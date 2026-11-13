using DeathWish.Protocol;
using DeathWish.Server;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;

static class RoomHandlerTests
{
    public static void Run()
    {
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions);
        var handler = new GameChannelHandler(sessions, rooms);
        var channel = new EmbeddedChannel(handler);
        var otherHandler = new GameChannelHandler(sessions, rooms);
        var other = new EmbeddedChannel(otherHandler);
        try
        {
            SendJoin(channel, 99, RoomJoinResult.RoomNotFound);
            Require(channel.Active && rooms.GetRoomId(handler.Session!.SessionId) is null, "Admission failure preserves connection");
            SendJoin(channel, 1, RoomJoinResult.Success);
            SendJoin(channel, 1, RoomJoinResult.Success);
            SendJoin(channel, 2, RoomJoinResult.AlreadyInAnotherRoom);
            SendJoin(other, 1, RoomJoinResult.Success);
            Require(rooms.TryGetRoom(1, out var room) && room.MemberCount == 2, "Requests use their own connection sessions");

            byte[] combined = PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(2))
                .Concat(PacketCodec.EncodeEcho(MessageType.EchoRequest, "after join"))
                .Concat(PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(1))).ToArray();
            channel.WriteInbound(Unpooled.WrappedBuffer(combined));
            ExpectJoin(channel, 2, RoomJoinResult.AlreadyInAnotherRoom);
            Require(ReadFrame(channel).SequenceEqual(PacketCodec.EncodeEcho(MessageType.EchoResponse, "after join")), "Echo response order");
            ExpectJoin(channel, 1, RoomJoinResult.Success);
            Require(channel.ReadOutbound<IByteBuffer>() is null, "One response per request");
        }
        finally
        {
            channel.FinishAndReleaseAll();
            other.FinishAndReleaseAll();
        }
        if (!rooms.TryGetRoom(1, out var emptyRoom)) throw new Exception("Missing test room");
        Require(sessions.Count == 0 && emptyRoom.MemberCount == 0, "Disconnect cleans admitted sessions");

        byte[] request = PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(1));
        for (int split = 1; split < request.Length; split++)
        {
            var fragmented = new EmbeddedChannel(new GameChannelHandler(sessions, rooms));
            try
            {
                fragmented.WriteInbound(Unpooled.WrappedBuffer(request[..split]));
                Require(fragmented.ReadOutbound<IByteBuffer>() is null, "No response to partial join");
                fragmented.WriteInbound(Unpooled.WrappedBuffer(request[split..]));
                ExpectJoin(fragmented, 1, RoomJoinResult.Success);
            }
            finally { fragmented.FinishAndReleaseAll(); }
        }

        foreach (byte[] invalid in new[] {
            new byte[] { 0, 0, 3 }, new byte[] { 0, 3, 3, 0, 0, 1 },
            new byte[] { 0, 5, 3, 0, 0, 0, 1, 0 },
            new byte[] { 0, 4, 3, 0, 0, 0, 0 },
            new byte[] { 0, 4, 3, 255, 255, 255, 255 },
            PacketCodec.EncodeJoinRoomResponse(new JoinRoomResponse(1, RoomJoinResult.Success)) })
        {
            var invalidChannel = new EmbeddedChannel(new GameChannelHandler(sessions, rooms));
            try
            {
                SendJoin(invalidChannel, 1, RoomJoinResult.Success);
                invalidChannel.WriteInbound(Unpooled.WrappedBuffer(invalid));
                invalidChannel.RunPendingTasks();
                Require(!invalidChannel.Active && sessions.Count == 0 && emptyRoom.MemberCount == 0, "Malformed join closes and cleans membership");
                Require(invalidChannel.ReadOutbound<IByteBuffer>() is null, "Malformed join has no success response");
            }
            finally { invalidChannel.FinishAndReleaseAll(); }
        }

        var failedWrite = new EmbeddedChannel(new FailWriteHandler(), new GameChannelHandler(sessions, rooms));
        try
        {
            failedWrite.WriteInbound(Unpooled.WrappedBuffer(request));
            failedWrite.RunPendingTasks();
            Require(!failedWrite.Active && sessions.Count == 0 && emptyRoom.MemberCount == 0, "Failed response write closes and cleans membership");
        }
        finally { failedWrite.FinishAndReleaseAll(); }
        Console.WriteLine("PASS Room request dispatch, admission results, ordering, fragmentation, invalid messages, and write failure cleanup");
    }

    private static void SendJoin(EmbeddedChannel channel, int roomId, RoomJoinResult result)
    {
        channel.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(roomId))));
        ExpectJoin(channel, roomId, result);
    }

    private static void ExpectJoin(EmbeddedChannel channel, int roomId, RoomJoinResult result)
    {
        byte[] expected = { 0, 5, 4, (byte)(roomId >> 24), (byte)(roomId >> 16), (byte)(roomId >> 8), (byte)roomId, (byte)result };
        Require(ReadFrame(channel).SequenceEqual(expected), "Exact admission response");
    }

    private static byte[] ReadFrame(EmbeddedChannel channel)
    {
        IByteBuffer buffer = channel.ReadOutbound<IByteBuffer>() ?? throw new Exception("Missing response");
        try
        {
            byte[] bytes = new byte[buffer.ReadableBytes];
            buffer.ReadBytes(bytes);
            return bytes;
        }
        finally { buffer.Release(); }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }

    private sealed class FailWriteHandler : ChannelHandlerAdapter
    {
        public override Task WriteAsync(IChannelHandlerContext context, object message)
        {
            ((IByteBuffer)message).Release();
            return Task.FromException(new IOException("Simulated response write failure."));
        }
    }
}
