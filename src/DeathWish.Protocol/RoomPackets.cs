namespace DeathWish.Protocol;

public enum RoomJoinResult : byte
{
    Success = 0,
    SessionUnavailable = 1,
    RoomNotFound = 2,
    AlreadyInAnotherRoom = 3
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
