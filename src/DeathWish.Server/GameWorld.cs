using System.Numerics;
using BepuPhysics;
using DeathWish.Protocol;
using DeathWish.Server.Physics;

namespace DeathWish.Server;

public sealed class GameWorld
{
    private const double SNAPSHOT_INTERVAL_SECONDS = 0.1;
    private const float FLOOR_SIDE_LENGTH_METERS = 100f;
    internal const float MOVEMENT_SPEED_METERS_PER_SECOND = 10f;
    internal const int DEFAULT_ADMISSION_CAPACITY = 1024;
    internal const int DEFAULT_MAX_COMMANDS_PER_TICK = 128;

    private readonly Dictionary<WorldAdmission, WorldPlayer> _players = new();
    private PlayerPhysicsSimulation? _physics;
    private readonly int _maxCommandsPerTick;
    private readonly float _gravityMetersPerSecondSquared;
    private PlayerState[] _publishedPlayers = Array.Empty<PlayerState>();
    private long _lastPlayerId;
    private long _currentTick;
    private bool _stopped;
    private double _snapshotElapsedSeconds;

    public int RoomId { get; }
    public long CurrentTick => Interlocked.Read(ref _currentTick);
    public int PlayerCount => Volatile.Read(ref _publishedPlayers).Length;
    internal WorldCommandQueue Commands { get; }

    /// <summary>World-owned colliders. After startup only the owning world loop may access them.</summary>
    internal IReadOnlyList<Collider> Colliders { get; }

    internal GameWorld(int roomId, int admissionCapacity = DEFAULT_ADMISSION_CAPACITY,
        int maxCommandsPerTick = DEFAULT_MAX_COMMANDS_PER_TICK, TimeProvider? timeProvider = null,
        float gravityMetersPerSecondSquared = ServerOptions.DEFAULT_GRAVITY_METERS_PER_SECOND_SQUARED)
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
        _gravityMetersPerSecondSquared = gravityMetersPerSecondSquared;
        Commands = new WorldCommandQueue(admissionCapacity, timeProvider);
        Colliders = new List<Collider>
        {
            new PlaneCollider(Vector3.Zero, FLOOR_SIDE_LENGTH_METERS, FLOOR_SIDE_LENGTH_METERS)
        }.AsReadOnly();
    }

    /// <summary>Creates a simulation from the room's static geometry and gravity. The caller owns and disposes it.</summary>
    internal PlayerPhysicsSimulation CreatePhysicsSimulation() => new(Colliders, _gravityMetersPerSecondSquared);

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
                    _physics ??= CreatePhysicsSimulation();
                    BodyHandle body = _physics.AddPlayer(Vector3.Zero);
                    _players.Add(admission, new WorldPlayer(body, new PlayerState(playerId, admission.Session.SessionId, Vector3.Zero)));
                    _lastPlayerId = playerId;
                    changed = true;
                    admission.Session.Send(PacketCodec.EncodeJoinAck(new JoinAck(RoomId, playerId, completedTick, 0, 0, 0)));
                    break;
                case WorldCommandType.Leave:
                    if (_players.Remove(admission, out WorldPlayer? leaving))
                    {
                        _physics!.RemovePlayer(leaving.Body);
                        changed = true;
                    }
                    Commands.Release(admission);
                    break;
                default:
                    throw new InvalidOperationException("Unknown world command.");
            }
        }

        // Lifecycle commands run first; movement never adds commands to the bounded Join/Leave queue.
        changed |= StepPhysics(deltaTimeSeconds);
        if (changed)
        {
            Volatile.Write(ref _publishedPlayers, _players.Values.Select(player => player.State).ToArray());
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
            .Select(pair => pair.Value.State)
            .Select(state => new SnapshotPlayer(state.PlayerId, state.Position.X, state.Position.Y, state.Position.Z))
            .OrderBy(player => player.PlayerId).ToArray();
        byte[] frame = SnapshotCodec.Encode(new WorldSnapshot(RoomId, serverTick, players));
        foreach (WorldAdmission admission in _players.Keys)
        {
            if (!admission.IsDeparted && !admission.Session.IsDisconnected)
            {
                admission.Session.SendSnapshot(frame);
            }
        }
    }

    private bool StepPhysics(float deltaTimeSeconds)
    {
        if (_physics is null || _players.Count == 0)
        {
            return false;
        }

        foreach ((WorldAdmission admission, WorldPlayer player) in _players)
        {
            if (admission.IsDeparted || admission.Session.IsDisconnected)
            {
                continue;
            }

            Vector3 velocity = admission.Movement.GetDirection() * MOVEMENT_SPEED_METERS_PER_SECOND;
            _physics.SetHorizontalVelocity(player.Body, velocity.X, velocity.Z);
        }

        _physics.Step(deltaTimeSeconds);
        bool changed = false;
        foreach ((WorldAdmission admission, WorldPlayer player) in _players)
        {
            if (admission.IsDeparted || admission.Session.IsDisconnected)
            {
                continue;
            }

            Vector3 position = _physics.GetMotion(player.Body).FootPositionMeters;
            if (position != player.State.Position)
            {
                player.State = player.State with { Position = position };
                changed = true;
            }
        }
        return changed;
    }

    // Only the world loop performs terminal cleanup, after its last update or on failure.
    internal void Shutdown()
    {
        Commands.Complete();
        _players.Clear();
        _physics?.Dispose();
        _physics = null;
        Volatile.Write(ref _publishedPlayers, Array.Empty<PlayerState>());
        _stopped = true;
    }

    private sealed class WorldPlayer(BodyHandle body, PlayerState state)
    {
        internal BodyHandle Body { get; } = body;
        internal PlayerState State { get; set; } = state;
    }
}
