namespace DeathWish.Protocol;

public enum WorldMapId : byte
{
    Flat = 0,
    PhysicsLab = 1,
    TeamDeathmatch = 2
}

public readonly struct JoinAck
{
    public int RoomId { get; }
    public long PlayerId { get; }
    public long ServerTick { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }

    public WorldMapId MapId { get; }
    public bool IsPhysicsLab => MapId == WorldMapId.PhysicsLab;

    public JoinAck(int roomId, long playerId, long serverTick, float x, float y, float z)
        : this(roomId, playerId, serverTick, x, y, z, WorldMapId.Flat)
    {
    }

    public JoinAck(int roomId, long playerId, long serverTick, float x, float y, float z, bool isPhysicsLab)
        : this(roomId, playerId, serverTick, x, y, z, isPhysicsLab ? WorldMapId.PhysicsLab : WorldMapId.Flat)
    {
    }

    public JoinAck(int roomId, long playerId, long serverTick, float x, float y, float z, WorldMapId mapId)
    {
        MapId = mapId;
        RoomId = roomId;
        PlayerId = playerId;
        ServerTick = serverTick;
        X = x;
        Y = y;
        Z = z;
    }
}

public enum RoomJoinResult : byte
{
    Success = 0,
    SessionUnavailable = 1,
    RoomNotFound = 2,
    AlreadyInAnotherRoom = 3,
    WorldUnavailable = 4,
    NicknameRequired = 5
}

public readonly struct JoinRoomRequest
{
    public int RoomId { get; }

    public JoinRoomRequest(int roomId) => RoomId = roomId;
}

public readonly struct JoinRoomResponse
{
    public int RoomId { get; }
    public RoomJoinResult Result { get; }

    public JoinRoomResponse(int roomId, RoomJoinResult result)
    {
        RoomId = roomId;
        Result = result;
    }
}
