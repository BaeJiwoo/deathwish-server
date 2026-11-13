using System.Diagnostics.CodeAnalysis;
using DeathWish.Protocol;

namespace DeathWish.Server;

public sealed class RoomManager
{
    private readonly object _sync = new();
    private readonly SessionManager _sessions;
    private readonly Dictionary<int, Room> _rooms;
    private readonly Dictionary<Guid, Room> _membership = new();

    public RoomManager(SessionManager sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
        _rooms = new Dictionary<int, Room>
        {
            [1] = new Room(1, _sync),
            [2] = new Room(2, _sync)
        };
    }

    public bool TryGetRoom(int roomId, [NotNullWhen(true)] out Room? room)
        => _rooms.TryGetValue(roomId, out room);

    public int? GetRoomId(Guid sessionId)
    {
        lock (_sync) return _membership.TryGetValue(sessionId, out var room) ? room.RoomId : null;
    }

    public RoomJoinResult Join(Guid sessionId, int roomId)
    {
        // Membership and room contents change together; disconnect cleanup uses the same lock.
        lock (_sync)
        {
            if (!_sessions.TryGetSession(sessionId, out var session) || session.IsDisconnected)
                return RoomJoinResult.SessionUnavailable;
            if (!_rooms.TryGetValue(roomId, out var room))
                return RoomJoinResult.RoomNotFound;
            if (_membership.TryGetValue(sessionId, out var currentRoom))
                return currentRoom == room ? RoomJoinResult.Success : RoomJoinResult.AlreadyInAnotherRoom;

            room.Add(sessionId);
            _membership.Add(sessionId, room);
            return RoomJoinResult.Success;
        }
    }

    public bool Leave(Guid sessionId)
    {
        lock (_sync)
        {
            if (!_membership.Remove(sessionId, out var room)) return false;

            room.Remove(sessionId);
            return true;
        }
    }
}
