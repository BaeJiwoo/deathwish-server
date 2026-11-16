using DeathWish.Protocol;
using DeathWish.Server;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;

internal static class SnapshotTests
{
    public static async Task RunAsync()
    {
        byte[] frame = SnapshotCodec.Encode(new WorldSnapshot(1, 2, new[] { new SnapshotPlayer(3, 1, -2, .5f) }));
        Require(Convert.ToHexString(frame) == "002207000000010000000000000002000100000000000000033F800000C00000003F000000", "Snapshot exact wire bytes");
        for (int split = 0; split <= frame.Length; split++)
        {
            var reader = new PacketReader();
            int received = 0;
            void Receive(MessageType type, byte[] payload)
            {
                WorldSnapshot snapshot = SnapshotCodec.Decode(payload);
                Require(type == MessageType.WorldSnapshot && snapshot.ServerTick == 2 && snapshot.Players[0].Y == -2, "Split snapshot");
                received++;
            }
            reader.Feed(frame.AsSpan(0, split), Receive);
            reader.Feed(frame.AsSpan(split), Receive);
            reader.Feed(frame.Concat(frame).ToArray(), Receive);
            Require(received == 3, "Combined snapshots");
        }
        var full = new WorldSnapshot(1, 1, Enumerable.Range(1, SnapshotCodec.MAX_PLAYERS)
            .Select(id => new SnapshotPlayer(id, 0, 0, 0)).ToArray());
        byte[] maximum = SnapshotCodec.Encode(full);
        new PacketReader().Feed(maximum, (_, payload) => Require(SnapshotCodec.Decode(payload).Players.Count == 1024, "Capacity snapshot"));
        Reject(() => SnapshotCodec.Decode(frame[3..^1]));
        Reject(() => SnapshotCodec.Encode(new WorldSnapshot(1, 1, new[] { new SnapshotPlayer(1, float.NaN, 0, 0) })));
        Reject(() => SnapshotCodec.Encode(new WorldSnapshot(1, 1, new[] { new SnapshotPlayer(1, 0, 0, 0), new SnapshotPlayer(1, 0, 0, 0) })));
        Require(SnapshotCodec.Decode(SnapshotCodec.Encode(new WorldSnapshot(1, 1, Array.Empty<SnapshotPlayer>()))[3..]).Players.Count == 0, "Empty snapshot");
        VerifyWorld();
        await VerifyCoalescingAsync();
        Console.WriteLine("PASS snapshots: wire, framing, capacity, membership, room isolation, movement, cadence and bounded coalescing");
    }

    private static void VerifyWorld()
    {
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions);
        var first = new EmbeddedChannel(new GameChannelHandler(sessions, rooms));
        var second = new EmbeddedChannel(new GameChannelHandler(sessions, rooms));
        var other = new EmbeddedChannel(new GameChannelHandler(sessions, rooms));
        try
        {
            first.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(1))));
            second.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(1))));
            other.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(2))));
            rooms.TryGetRoom(1, out var room);
            rooms.TryGetRoom(2, out var otherRoom);
            room!.World.Tick(1f / 30);
            otherRoom!.World.Tick(1f / 30);
            Require(Read(first)[2] == 4 && Read(second)[2] == 4 && Read(other)[2] == 4, "Reservation precedes ack");
            JoinAck ack = PacketCodec.DecodeJoinAck(Read(first)[3..]);
            Read(second);
            Read(other);
            first.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeMoveInput(new MoveInput(0, 1, 0))));
            room.World.Tick(1f / 30);
            Require(first.ReadOutbound<IByteBuffer>() == null, "No snapshot before interval");
            room.World.Tick(1f / 30);
            otherRoom.World.Tick(1f / 30);
            otherRoom.World.Tick(1f / 30);
            byte[] a = Read(first);
            Require(a.SequenceEqual(Read(second)), "Same room receives same tick and positions");
            WorldSnapshot snapshot = SnapshotCodec.Decode(a[3..]);
            Require(snapshot.ServerTick == 3 && snapshot.Players.Count == 2 &&
                Math.Abs(snapshot.Players.Single(p => p.PlayerId == ack.PlayerId).X - 1f / 3) < .0001f, "Snapshot follows movement");
            Require(SnapshotCodec.Decode(Read(other)[3..]).Players.Count == 1, "Room isolation");
            second.CloseAsync().GetAwaiter().GetResult();
            for (int i = 0; i < 3; i++)
            {
                room.World.Tick(1f / 30);
            }
            Require(SnapshotCodec.Decode(Read(first)[3..]).Players.Count == 1, "Full snapshot removes departed players");
        }
        finally
        {
            first.FinishAndReleaseAll();
            second.FinishAndReleaseAll();
            other.FinishAndReleaseAll();
        }
    }

    private static async Task VerifyCoalescingAsync()
    {
        var gate = new FirstWriteGate();
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions);
        var handler = new GameChannelHandler(sessions, rooms);
        var channel = new EmbeddedChannel(gate, handler);
        ClientSession session = handler.Session!;
        try
        {
            channel.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(1))));
            rooms.TryGetRoom(1, out var room);
            for (int i = 0; i < 90; i++)
            {
                room!.World.Tick(1f / 30);
            }
            Require(session.PendingFrames == 3 && channel.Active, "Blocked output retains response, ack and latest snapshot only");
            gate.Release();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (session.PendingFrames != 0)
            {
                channel.RunPendingTasks();
                await Task.Delay(1, deadline.Token);
            }
            Require(Read(channel)[2] == 4 && Read(channel)[2] == 5, "Coalescing preserves reliable admission ordering");
            Require(SnapshotCodec.Decode(Read(channel)[3..]).ServerTick == 90, "Only latest unsent snapshot delivered");
            Require(channel.ReadOutbound<IByteBuffer>() == null, "No obsolete snapshot backlog");
        }
        finally
        {
            gate.Release();
            channel.FinishAndReleaseAll();
        }
    }

    private sealed class FirstWriteGate : ChannelHandlerAdapter
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hasStarted;

        public override async Task WriteAsync(IChannelHandlerContext context, object message)
        {
            if (!_hasStarted)
            {
                _hasStarted = true;
                await _gate.Task;
            }
            await context.WriteAndFlushAsync(message);
        }

        public void Release() => _gate.TrySetResult();
    }

    private static byte[] Read(EmbeddedChannel channel)
    {
        IByteBuffer buffer = channel.ReadOutbound<IByteBuffer>() ?? throw new Exception("Missing snapshot output");
        try
        {
            byte[] bytes = new byte[buffer.ReadableBytes];
            buffer.ReadBytes(bytes);
            return bytes;
        }
        finally
        {
            buffer.Release();
        }
    }

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new Exception("Malformed snapshot accepted");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
