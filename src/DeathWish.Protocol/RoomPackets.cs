namespace DeathWish.Protocol;

public readonly struct JoinAck
{
    public int RoomId { get; }
    public long PlayerId { get; }
    public long ServerTick { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }

    public JoinAck(int roomId, long playerId, long serverTick, float x, float y, float z)
    {
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
