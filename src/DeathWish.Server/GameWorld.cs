using System.Numerics;

namespace DeathWish.Server;

public sealed class GameWorld
{
    internal const int DefaultAdmissionCapacity = 1024;
    internal const int DefaultMaxCommandsPerTick = 128;

    private readonly Dictionary<WorldAdmission, PlayerState> _players = new();
    private readonly int _maxCommandsPerTick;
    private PlayerState[] _publishedPlayers = Array.Empty<PlayerState>();
    private long _lastPlayerId;
    private long _currentTick;
    private bool _stopped;

    public int RoomId { get; }
    public long CurrentTick => Interlocked.Read(ref _currentTick);
    public int PlayerCount => Volatile.Read(ref _publishedPlayers).Length;
    internal WorldCommandQueue Commands { get; }

    internal GameWorld(int roomId, int admissionCapacity = DefaultAdmissionCapacity,
        int maxCommandsPerTick = DefaultMaxCommandsPerTick)
    {
        if (roomId < 1) throw new ArgumentOutOfRangeException(nameof(roomId));
        if (maxCommandsPerTick < 1) throw new ArgumentOutOfRangeException(nameof(maxCommandsPerTick));
        RoomId = roomId;
        _maxCommandsPerTick = maxCommandsPerTick;
        Commands = new WorldCommandQueue(admissionCapacity);
    }

    /// <summary>Returns a defensive copy of the player states published after the last completed update.</summary>
    public PlayerState[] GetPlayers() => Volatile.Read(ref _publishedPlayers).ToArray();

    /// <summary>Runs one synchronous update using a finite, positive delta in seconds. Only the owning world loop may call this method.</summary>
    internal void Tick(float deltaTime)
    {
        if (!float.IsFinite(deltaTime) || deltaTime <= 0) throw new ArgumentOutOfRangeException(nameof(deltaTime));
        if (_stopped) throw new InvalidOperationException("The world has stopped.");
        long completedTick = checked(_currentTick + 1);

        bool changed = false;
        for (int count = 0; count < _maxCommandsPerTick && Commands.TryDequeue(out WorldCommand command); count++)
        {
            WorldAdmission admission = command.Admission;
            switch (command.Type)
            {
                case WorldCommandType.Join:
                    if (admission.IsDeparted || admission.Session.IsDisconnected) break;
                    long playerId = checked(_lastPlayerId + 1);
                    _players.Add(admission, new PlayerState(playerId, admission.Session.SessionId, Vector3.Zero));
                    _lastPlayerId = playerId;
                    changed = true;
                    break;
                case WorldCommandType.Leave:
                    changed |= _players.Remove(admission);
                    Commands.Release(admission);
                    break;
                default:
                    throw new InvalidOperationException("Unknown world command.");
            }
        }

        if (changed) Volatile.Write(ref _publishedPlayers, _players.Values.ToArray());

        // Publish completion after simulation work; readers do not acquire ownership of world state.
        Interlocked.Exchange(ref _currentTick, completedTick);
    }

    // Only the world loop performs terminal cleanup, after its last update or on failure.
    internal void Shutdown()
    {
        Commands.Complete();
        _players.Clear();
        Volatile.Write(ref _publishedPlayers, Array.Empty<PlayerState>());
        _stopped = true;
    }
}
