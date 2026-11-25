using System.Diagnostics.CodeAnalysis;
using DeathWish.Protocol;

namespace DeathWish.Server;

public sealed class RoomManager
{
    private readonly object _sync = new();
    private readonly SessionManager _sessions;
    private readonly Dictionary<int, Room> _rooms;
    private readonly Dictionary<Guid, Membership> _membership = new();
    private bool _accepting = true;

    public RoomManager(SessionManager sessions, int roomCount = ServerOptions.DefaultRoomCount)
        : this(sessions, roomCount, GameWorld.DEFAULT_ADMISSION_CAPACITY, GameWorld.DEFAULT_MAX_COMMANDS_PER_TICK) { }

    internal RoomManager(SessionManager sessions, int roomCount, int admissionCapacity, int maxCommandsPerTick,
        TimeProvider? timeProvider = null, float gravityMetersPerSecondSquared = ServerOptions.DEFAULT_GRAVITY_METERS_PER_SECOND_SQUARED,
        int? teamDeathmatchRoomId = null, int teamDeathmatchTeamSize = ServerOptions.DEFAULT_TDM_TEAM_SIZE,
        int? trainingRoomId = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (roomCount < 1) throw new ArgumentOutOfRangeException(nameof(roomCount));
        ServerOptions.ValidateArenaRoomIds(roomCount, teamDeathmatchRoomId, trainingRoomId);

        _sessions = sessions;
        _rooms = new Dictionary<int, Room>(roomCount);
        for (int index = 0; index < roomCount; index++)
        {
            int roomId = index + 1;
            var mapId = WorldMapId.Flat;
            bool isTrainingRoom = roomId == trainingRoomId;
            if (roomId == teamDeathmatchRoomId || isTrainingRoom)
            {
                mapId = WorldMapId.TeamDeathmatch;
            }
            else if (roomId == roomCount)
            {
                mapId = WorldMapId.PhysicsLab;
            }

            _rooms.Add(roomId, new Room(roomId, _sync, admissionCapacity, maxCommandsPerTick, timeProvider,
                gravityMetersPerSecondSquared, mapId, teamDeathmatchTeamSize, isTrainingRoom));
        }
    }

    public bool TryGetRoom(int roomId, [NotNullWhen(true)] out Room? room)
        => _rooms.TryGetValue(roomId, out room);

    internal GameWorld[] GetWorlds()
        => _rooms.Values.OrderBy(room => room.RoomId).Select(room => room.World).ToArray();

    public int? GetRoomId(Guid sessionId)
    {
        lock (_sync) return _membership.TryGetValue(sessionId, out var membership) ? membership.Room.RoomId : null;
    }

    public RoomJoinResult Join(Guid sessionId, int roomId) => Join(sessionId, roomId, null);

    internal RoomJoinResult Join(Guid sessionId, int roomId, string? nickname, bool receiveArenaShots = false)
    {
        if (nickname is not null)
        {
            nickname = MatchCodec.NormalizeNickname(nickname);
        }

        // Membership and room contents change together; disconnect cleanup uses the same lock.
        lock (_sync)
        {
            if (!_sessions.TryGetSession(sessionId, out var session) || session.IsDisconnected)
                return RoomJoinResult.SessionUnavailable;
            if (!_accepting) return RoomJoinResult.WorldUnavailable;
            if (!_rooms.TryGetValue(roomId, out var room))
                return RoomJoinResult.RoomNotFound;
            if (!room.World.Commands.IsAccepting) return RoomJoinResult.WorldUnavailable;
            if (_membership.TryGetValue(sessionId, out var membership))
                return membership.Room == room ? RoomJoinResult.Success : RoomJoinResult.AlreadyInAnotherRoom;
            if (!room.World.IsAcceptingMatchPlayers)
            {
                return RoomJoinResult.WorldUnavailable;
            }
            if (room.World.MapId == WorldMapId.TeamDeathmatch && nickname is null)
            {
                return RoomJoinResult.NicknameRequired;
            }
            if (!room.World.Commands.TryEnqueueJoin(session, out var admission, nickname, receiveArenaShots))
            {
                return RoomJoinResult.WorldUnavailable;
            }

            room.Add(sessionId);
            _membership.Add(sessionId, new Membership(room, admission));
            return RoomJoinResult.Success;
        }
    }

    /// <summary>Associates input with the connection's current admission. Does not mutate player state.</summary>
    internal bool SubmitMovement(Guid sessionId, MoveInput input)
    {
        input.Validate();
        return GetActiveMailbox(sessionId)?.TrySetInput(input) ?? false;
    }

    /// <summary>Returns the connection's current mailbox. Departure closes it before a later admission can reuse the session.</summary>
    internal MovementMailbox? GetActiveMailbox(Guid sessionId) => GetActiveAdmission(sessionId)?.Movement;

    /// <summary>Associates a probe reply with the current admission. Replies outside an arena are ignored.</summary>
    internal void SubmitLatencyReply(Guid sessionId, long token)
    {
        GetActiveAdmission(sessionId)?.Latency?.RecordReply(token);
    }

    private WorldAdmission? GetActiveAdmission(Guid sessionId)
    {
        lock (_sync)
        {
            if (!_accepting || !_membership.TryGetValue(sessionId, out var membership) ||
                membership.Admission.IsDeparted || membership.Admission.Session.IsDisconnected)
            {
                return null;
            }

            return membership.Admission;
        }
    }

    public bool Leave(Guid sessionId)
    {
        lock (_sync)
        {
            if (!_membership.Remove(sessionId, out var membership)) return false;

            membership.Room.World.Commands.EnqueueLeave(membership.Admission);
            membership.Room.Remove(sessionId);
            return true;
        }
    }

    internal void StopAdmission()
    {
        lock (_sync) _accepting = false;
    }

    private sealed record Membership(Room Room, WorldAdmission Admission);
}
