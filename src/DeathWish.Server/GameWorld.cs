using System.Numerics;
using DeathWish.Protocol;

namespace DeathWish.Server;

public sealed class GameWorld
{
    private const double SNAPSHOT_INTERVAL_SECONDS = 0.1;
    internal const float MOVEMENT_SPEED_METERS_PER_SECOND = 5f;
    internal const int DEFAULT_ADMISSION_CAPACITY = 1024;
    internal const int DEFAULT_MAX_COMMANDS_PER_TICK = 128;

    private readonly Dictionary<WorldAdmission, PlayerState> _players = new();
    private readonly List<(WorldAdmission Admission, PlayerState Player)> _movementUpdates = new();
    private readonly int _maxCommandsPerTick;
    private PlayerState[] _publishedPlayers = Array.Empty<PlayerState>();
    private long _lastPlayerId;
    private long _currentTick;
    private bool _stopped;
    private double _snapshotElapsedSeconds;

    public int RoomId { get; }
    public long CurrentTick => Interlocked.Read(ref _currentTick);
    public int PlayerCount => Volatile.Read(ref _publishedPlayers).Length;
    internal WorldCommandQueue Commands { get; }

    internal GameWorld(int roomId, int admissionCapacity = DEFAULT_ADMISSION_CAPACITY,
        int maxCommandsPerTick = DEFAULT_MAX_COMMANDS_PER_TICK, TimeProvider? timeProvider = null)
    {
        if (roomId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(roomId));
        }
        if (maxCommandsPerTick < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCommandsPerTick));
        }
        RoomId = roomId;
        _maxCommandsPerTick = maxCommandsPerTick;
        Commands = new WorldCommandQueue(admissionCapacity, timeProvider);
    }

    /// <summary>Returns a defensive copy of the player states published after the last completed update.</summary>
    public PlayerState[] GetPlayers() => Volatile.Read(ref _publishedPlayers).ToArray();

    /// <summary>Runs one synchronous update using a finite, positive delta in seconds. Only the owning world loop may call this method.</summary>
    internal void Tick(float deltaTimeSeconds)
    {
        if (!float.IsFinite(deltaTimeSeconds) || deltaTimeSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaTimeSeconds));
        }
        if (_stopped)
        {
            throw new InvalidOperationException("The world has stopped.");
        }
        long completedTick = checked(_currentTick + 1);

        bool changed = false;
        for (int count = 0; count < _maxCommandsPerTick && Commands.TryDequeue(out WorldCommand command); count++)
        {
            WorldAdmission admission = command.Admission;
            switch (command.Type)
            {
                case WorldCommandType.Join:
                    if (admission.IsDeparted || admission.Session.IsDisconnected)
                    {
                        break;
                    }
                    long playerId = checked(_lastPlayerId + 1);
                    _players.Add(admission, new PlayerState(playerId, admission.Session.SessionId, Vector3.Zero));
                    _lastPlayerId = playerId;
                    changed = true;
                    admission.Session.Send(PacketCodec.EncodeJoinAck(new JoinAck(RoomId, playerId, completedTick, 0, 0, 0)));
                    break;
                case WorldCommandType.Leave:
                    changed |= _players.Remove(admission);
                    Commands.Release(admission);
                    break;
                default:
                    throw new InvalidOperationException("Unknown world command.");
            }
        }

        // Lifecycle commands run first; movement never adds commands to the bounded Join/Leave queue.
        changed |= UpdateMovement(deltaTimeSeconds);
        if (changed)
        {
            Volatile.Write(ref _publishedPlayers, _players.Values.ToArray());
        }

        _snapshotElapsedSeconds += deltaTimeSeconds;
        if (_snapshotElapsedSeconds >= SNAPSHOT_INTERVAL_SECONDS)
        {
            _snapshotElapsedSeconds %= SNAPSHOT_INTERVAL_SECONDS;
            BroadcastSnapshot(completedTick);
        }

        // Publish completion after simulation work; readers do not acquire ownership of world state.
        Interlocked.Exchange(ref _currentTick, completedTick);
    }

    private void BroadcastSnapshot(long serverTick)
    {
        var players = _players.Where(pair => !pair.Key.IsDeparted && !pair.Key.Session.IsDisconnected)
            .Select(pair => new SnapshotPlayer(pair.Value.PlayerId, pair.Value.Position.X,
                pair.Value.Position.Y, pair.Value.Position.Z)).OrderBy(player => player.PlayerId).ToArray();
        byte[] frame = SnapshotCodec.Encode(new WorldSnapshot(RoomId, serverTick, players));
        foreach (WorldAdmission admission in _players.Keys)
        {
            if (!admission.IsDeparted && !admission.Session.IsDisconnected)
            {
                admission.Session.SendSnapshot(frame);
            }
        }
    }

    private bool UpdateMovement(float deltaTimeSeconds)
    {
        _movementUpdates.Clear();
        foreach ((WorldAdmission admission, PlayerState player) in _players)
        {
            if (admission.IsDeparted || admission.Session.IsDisconnected)
            {
                continue;
            }

            Vector3 direction = admission.Movement.GetDirection();
            if (direction == Vector3.Zero)
            {
                continue;
            }

            Vector3 position = player.Position + direction * MOVEMENT_SPEED_METERS_PER_SECOND * deltaTimeSeconds;
            _movementUpdates.Add((admission, player with { Position = position }));
        }

        foreach ((WorldAdmission admission, PlayerState player) in _movementUpdates)
        {
            _players[admission] = player;
        }
        return _movementUpdates.Count != 0;
    }

    // Only the world loop performs terminal cleanup, after its last update or on failure.
    internal void Shutdown()
    {
        Commands.Complete();
        _players.Clear();
        _movementUpdates.Clear();
        Volatile.Write(ref _publishedPlayers, Array.Empty<PlayerState>());
        _stopped = true;
    }
}
