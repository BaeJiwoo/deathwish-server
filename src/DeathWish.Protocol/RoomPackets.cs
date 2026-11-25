namespace DeathWish.Protocol;

public readonly struct JoinAck
{
    public int RoomId { get; }
    public long PlayerId { get; }
    public long ServerTick { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }

    public bool IsPhysicsLab { get; }

    public JoinAck(int roomId, long playerId, long serverTick, float x, float y, float z)
        : this(roomId, playerId, serverTick, x, y, z, false)
    {
    }

    public JoinAck(int roomId, long playerId, long serverTick, float x, float y, float z, bool isPhysicsLab)
    {
        IsPhysicsLab = isPhysicsLab;
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
    WorldUnavailable = 4
}

public readonly struct JoinRoomRequest
{
    public int RoomId { get; }
    public bool ReceivesAnimation { get; }

    public JoinRoomRequest(int roomId) : this(roomId, false)
    {
    }

    public JoinRoomRequest(int roomId, bool receivesAnimation)
    {
        RoomId = roomId;
        ReceivesAnimation = receivesAnimation;
    }
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
