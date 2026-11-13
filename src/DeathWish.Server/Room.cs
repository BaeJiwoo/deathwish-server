namespace DeathWish.Server;

public sealed class Room
{
    private readonly object _sync;
    private readonly HashSet<Guid> _members = new();

    public int RoomId { get; }
    public int MemberCount { get { lock (_sync) return _members.Count; } }

    internal Room(int roomId, object sync)
    {
        RoomId = roomId;
        _sync = sync;
    }

    /// <summary>Returns a copy of the member IDs at the time of the call.</summary>
    public Guid[] GetMemberIds()
    {
        lock (_sync) return _members.ToArray();
    }

    // Only RoomManager mutates membership while holding the shared lock.
    internal void Add(Guid sessionId) => _members.Add(sessionId);
    internal void Remove(Guid sessionId) => _members.Remove(sessionId);
}
