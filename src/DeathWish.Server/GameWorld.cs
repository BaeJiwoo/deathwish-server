namespace DeathWish.Server;

public sealed class GameWorld
{
    private long _currentTick;

    public int RoomId { get; }
    public long CurrentTick => Interlocked.Read(ref _currentTick);

    internal GameWorld(int roomId)
    {
        if (roomId < 1) throw new ArgumentOutOfRangeException(nameof(roomId));
        RoomId = roomId;
    }

    /// <summary>Runs one synchronous update using a finite, positive delta in seconds. Only the owning world loop may call this method.</summary>
    internal void Tick(float deltaTime)
    {
        if (!float.IsFinite(deltaTime) || deltaTime <= 0) throw new ArgumentOutOfRangeException(nameof(deltaTime));

        // Publish completion after simulation work; readers do not acquire ownership of world state.
        Interlocked.Exchange(ref _currentTick, checked(_currentTick + 1));
    }
}
