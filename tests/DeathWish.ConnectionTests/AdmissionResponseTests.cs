using DeathWish.Protocol;
using DeathWish.Server;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;

static class AdmissionResponseTests
{
    public static async Task RunAsync()
    {
        byte[] expected = Convert.FromHexString("00200500000001000000000000000200000000000000033F800000C00000003F000000");
        byte[] encoded = PacketCodec.EncodeJoinAck(new JoinAck(1, 2, 3, 1, -2, .5f));
        Require(encoded.SequenceEqual(expected), "JoinAck exact big-endian bytes");
        for (int split = 0; split <= expected.Length; split++)
        {
            var reader = new PacketReader();
            int count = 0;
            void Receive(MessageType type, byte[] payload)
            {
                JoinAck ack = PacketCodec.DecodeJoinAck(payload);
                Require(type == MessageType.JoinAck && ack.RoomId == 1 && ack.PlayerId == 2 &&
                    ack.ServerTick == 3 && ack.X == 1 && ack.Y == -2 && ack.Z == .5f, "Fragmented admission decode");
                count++;
            }
            reader.Feed(expected.AsSpan(0, split), Receive);
            reader.Feed(expected.AsSpan(split), Receive);
            reader.Feed(expected.Concat(expected).ToArray(), Receive);
            Require(count == 3, "Combined admission frames");
        }
        foreach (int size in new[] { 0, 31, 33 })
        {
            try { PacketCodec.DecodeJoinAck(new byte[size]); throw new Exception("Invalid length accepted"); }
            catch (InvalidDataException) { }
        }
        foreach (var ack in new[] { new JoinAck(0, 1, 1, 0, 0, 0), new JoinAck(1, 0, 1, 0, 0, 0),
            new JoinAck(1, 1, 0, 0, 0, 0), new JoinAck(1, 1, 1, float.NaN, 0, 0) })
        {
            try { PacketCodec.EncodeJoinAck(ack); throw new Exception("Invalid state accepted"); }
            catch (InvalidDataException) { }
        }

        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions);
        var handler = new GameChannelHandler(sessions, rooms);
        var channel = new EmbeddedChannel(handler);
        try
        {
            channel.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(1))));
            Require(rooms.TryGetRoom(1, out var room), "Room exists");
            room!.World.Tick(1f / 30);
            Require(Read(channel)[2] == (byte)MessageType.JoinRoomResponse, "Reservation precedes admission");
            JoinAck ack = PacketCodec.DecodeJoinAck(Read(channel)[3..]);
            Require(ack.PlayerId == room.World.GetPlayers()[0].PlayerId && ack.ServerTick == 1 && ack.X == 0, "World admission state");
            channel.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(1))));
            room.World.Tick(1f / 30);
            Require(Read(channel)[2] == 4 && channel.ReadOutbound<IByteBuffer>() is null, "Duplicate join does not repeat JoinAck");
            channel.WriteInbound(Unpooled.WrappedBuffer(expected));
            channel.RunPendingTasks();
            Require(!channel.Active, "Client cannot send JoinAck");
            room.World.Tick(1f / 30);
            Require(room.World.PlayerCount == 0, "Invalid direction removes player");
        }
        finally { channel.FinishAndReleaseAll(); }

        await CheckBlockedOutput(false);
        await CheckBlockedOutput(true);
        CheckLengthDecoder();
        Console.WriteLine("PASS JoinAck bytes, framing, validation, world ordering, output bounds and write timeout");
    }

    private static void CheckLengthDecoder()
    {
        byte[] request = PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(1));
        for (int split = 1; split < request.Length; split++)
        {
            var sessions = new SessionManager();
            var channel = new EmbeddedChannel(new LengthFieldBasedFrameDecoder(4099, 0, 2, 1, 0),
                new GameChannelHandler(sessions, new RoomManager(sessions)));
            try
            {
                channel.WriteInbound(Unpooled.WrappedBuffer(request[..split]));
                Require(channel.ReadOutbound<IByteBuffer>() is null, "Length decoder waits for complete frame");
                channel.WriteInbound(Unpooled.WrappedBuffer(request[split..].Concat(request).ToArray()));
                Require(Read(channel)[2] == 4 && Read(channel)[2] == 4, "Length decoder handles split and combined frames");
                channel.WriteInbound(Unpooled.WrappedBuffer(new byte[] { 0x10, 0x01, 1 }));
                channel.RunPendingTasks();
                Require(!channel.Active && sessions.Count == 0, "Oversized length closes production pipeline");
            }
            finally { channel.FinishAndReleaseAll(); }
        }
    }

    private static async Task CheckBlockedOutput(bool timeout)
    {
        var stalled = new StalledWrite();
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions);
        var handler = new GameChannelHandler(sessions, rooms);
        var channel = new EmbeddedChannel(stalled, handler);
        try
        {
            ClientSession session = handler.Session!;
            session.WriteTimeout = timeout ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromSeconds(5);
            channel.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(1))));
            rooms.TryGetRoom(1, out var room);
            room!.World.Tick(1f / 30);
            Require(room.World.PlayerCount == 1 && session.PendingFrames == 2, "Stalled write does not block tick");
            if (!timeout)
            {
                for (int i = 2; i < ClientSession.MAX_PENDING_FRAMES; i++) Require(session.Send(new byte[3]), "Queue within limit");
                Require(!session.Send(new byte[3]), "Overflow rejected");
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (channel.Active)
            {
                channel.RunPendingTasks();
                await Task.Delay(1, deadline.Token);
            }
            room.World.Tick(1f / 30);
            Require(sessions.Count == 0 && room.World.PlayerCount == 0, "Slow connection cleanup");
            stalled.Complete();
            while (session.PendingFrames != 0) await Task.Delay(1, deadline.Token);
        }
        finally { stalled.Complete(); channel.FinishAndReleaseAll(); }
    }

    private static byte[] Read(EmbeddedChannel channel)
    {
        IByteBuffer buffer = channel.ReadOutbound<IByteBuffer>() ?? throw new Exception("Missing output");
        try { byte[] bytes = new byte[buffer.ReadableBytes]; buffer.ReadBytes(bytes); return bytes; }
        finally { buffer.Release(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class StalledWrite : ChannelHandlerAdapter
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IByteBuffer? _buffer;
        public override Task WriteAsync(IChannelHandlerContext context, object message)
        {
            _buffer = (IByteBuffer)message;
            return _completion.Task;
        }
        internal void Complete()
        {
            Interlocked.Exchange(ref _buffer, null)?.Release();
            _completion.TrySetResult();
        }
    }
}
