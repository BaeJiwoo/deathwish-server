using System.Buffers.Binary;
using System.Numerics;
using DeathWish.MockClient.Core;
using DeathWish.Protocol;
using DeathWish.Server;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels.Embedded;

static class MovementTests
{
    private const float DELTA_SECONDS = 1f / 30;
    private const float POSITION_TOLERANCE = .0001f;

    public static async Task RunAsync()
    {
        VerifyProtocol();
        VerifyFrequencyIndependence();
        VerifyDirectionAndStop();
        VerifyExpiryAndSequence();
        VerifyLifecycle();
        VerifyPendingDeparture();
        VerifyHandler();
        await VerifyConcurrentInputAsync();
        using var client = new ConnectionTestClient();
        await ExpectAsync<InvalidOperationException>(() => client.SendMoveAsync(new MoveInput(0, 1, 0), default));
        await ExpectAsync<InvalidDataException>(() => client.SendMoveAsync(new MoveInput(0, float.NaN, 0), default));
        Console.WriteLine("PASS movement wire bytes, normalization, frequency independence, expiry, sequence wrap, lifecycle, isolation and concurrency");
    }

    private static void VerifyProtocol()
    {
        byte[] expected = Convert.FromHexString("000C06010203043F800000BF000000");
        Require(PacketCodec.EncodeMoveInput(new MoveInput(0x01020304, 1, -.5f)).SequenceEqual(expected), "Exact movement wire bytes");
        for (int split = 0; split <= expected.Length; split++)
        {
            var reader = new PacketReader();
            int received = 0;
            void Receive(MessageType type, byte[] payload)
            {
                MoveInput input = PacketCodec.DecodeMoveInput(payload);
                Require(type == MessageType.MoveInput && input.Sequence == 0x01020304 && input.X == 1 && input.Z == -.5f,
                    "Fragmented movement decode");
                received++;
            }
            reader.Feed(expected.AsSpan(0, split), Receive);
            reader.Feed(expected.AsSpan(split), Receive);
            reader.Feed(expected.Concat(expected).ToArray(), Receive);
            Require(received == 3, "Combined movement decode");
        }

        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1.01f, -1.01f })
        {
            Expect<InvalidDataException>(() => PacketCodec.EncodeMoveInput(new MoveInput(0, invalid, 0)));
            Expect<InvalidDataException>(() => PacketCodec.EncodeMoveInput(new MoveInput(0, 0, invalid)));
            foreach (int offset in new[] { 4, 8 })
            {
                byte[] payload = expected[PacketCodec.HEADER_SIZE..];
                BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(offset), BitConverter.SingleToInt32Bits(invalid));
                Expect<InvalidDataException>(() => PacketCodec.DecodeMoveInput(payload));
            }
        }
        foreach (int length in new[] { 0, 11, 13 })
        {
            Expect<InvalidDataException>(() => PacketCodec.DecodeMoveInput(new byte[length]));
        }
        Require(MoveInput.IsNewerSequence(0, uint.MaxValue), "Sequence wraps forward");
        Require(!MoveInput.IsNewerSequence(uint.MaxValue, 0), "Old wrapped sequence rejected");
        Require(!MoveInput.IsNewerSequence(0x80000000, 0), "Half-range ambiguity rejected");
        Require(!MoveInput.IsNewerSequence(0, 0), "Duplicate rejected");
    }

    private static void VerifyFrequencyIndependence()
    {
        using var sparse = new Fixture();
        using var flooded = new Fixture();
        uint sequence = 0;
        for (int tick = 0; tick < 30; tick++)
        {
            sparse.Clock.Advance(TimeSpan.FromSeconds(DELTA_SECONDS));
            flooded.Clock.Advance(TimeSpan.FromSeconds(DELTA_SECONDS));
            if (tick % 3 == 0)
            {
                Require(sparse.Send(new MoveInput((uint)tick, 1, 0)), "Sparse input accepted");
            }
            for (int packet = 0; packet < 100; packet++)
            {
                Require(flooded.Send(new MoveInput(sequence++, 1, 0)), "Burst input accepted");
            }
            Require(flooded.World.Commands.PendingCount == 0, "Movement does not grow lifecycle queue");
            sparse.World.Tick(DELTA_SECONDS);
            flooded.World.Tick(DELTA_SECONDS);
        }
        Near(sparse.Position, new Vector3(5, 0, 0), "Speed uses tick duration");
        Near(flooded.Position, sparse.Position, "Packet frequency cannot increase speed");
    }

    private static void VerifyDirectionAndStop()
    {
        using var fixture = new Fixture();
        PlayerState[] before = fixture.World.GetPlayers();
        fixture.Send(new MoveInput(0, 1, 1));
        Near(fixture.Position, Vector3.Zero, "Network input does not mutate position");
        fixture.World.Tick(DELTA_SECONDS);
        Near(fixture.Position, Vector3.Normalize(new Vector3(1, 0, 1)) * (5 * DELTA_SECONDS), "Diagonal speed capped");
        Near(before[0].Position, Vector3.Zero, "Published states remain immutable");

        Vector3 previous = fixture.Position;
        fixture.Send(new MoveInput(1, .5f, 0));
        fixture.World.Tick(DELTA_SECONDS);
        Near(fixture.Position - previous, new Vector3(2.5f * DELTA_SECONDS, 0, 0), "Analog magnitude retained");
        fixture.Send(new MoveInput(2, 0, 0));
        previous = fixture.Position;
        fixture.World.Tick(DELTA_SECONDS);
        Near(fixture.Position, previous, "Zero direction stops next tick");
    }

    private static void VerifyExpiryAndSequence()
    {
        using var fixture = new Fixture();
        Require(fixture.Send(new MoveInput(uint.MaxValue, 1, 0)), "First arbitrary sequence accepted");
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(249));
        Require(!fixture.Send(new MoveInput(uint.MaxValue, -1, 0)), "Duplicate does not refresh age");
        fixture.World.Tick(DELTA_SECONDS);
        Vector3 previous = fixture.Position;
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        fixture.World.Tick(DELTA_SECONDS);
        Near(fixture.Position, previous, "Input expires at exactly 250ms");
        Require(!fixture.Send(new MoveInput(uint.MaxValue - 1, -1, 0)), "Old input cannot revive expired movement");
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.World.Tick(DELTA_SECONDS);
        Near(fixture.Position, previous, "Long stall does not replay stale movement");

        Require(fixture.Send(new MoveInput(0, 0, -1)), "Fresh wrapped input resumes movement");
        Require(!fixture.Send(new MoveInput(0x80000000, 1, 0)), "Ambiguous jump ignored");
        fixture.World.Tick(DELTA_SECONDS);
        Near(fixture.Position - previous, new Vector3(0, 0, -5 * DELTA_SECONDS), "Latest valid direction retained");
    }

    private static void VerifyLifecycle()
    {
        using var fixture = new Fixture(admit: false);
        Require(!fixture.Send(new MoveInput(0, 1, 0)), "Unjoined input ignored");
        fixture.Rooms.Join(fixture.SessionId, 1);
        for (uint sequence = 0; sequence < 1000; sequence++)
        {
            fixture.Send(new MoveInput(sequence, 1, 0));
        }
        Require(fixture.World.Commands.PendingCount == 1, "Pending admission keeps one latest movement value");
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(250));
        fixture.World.Tick(DELTA_SECONDS);
        Near(fixture.Position, Vector3.Zero, "Delayed admission does not freshen input");

        fixture.Send(new MoveInput(1000, 1, 0));
        fixture.World.Tick(DELTA_SECONDS);
        fixture.Rooms.Leave(fixture.SessionId);
        Require(!fixture.Send(new MoveInput(1001, 1, 0)), "Departed input ignored");
        fixture.Rooms.Join(fixture.SessionId, 1);
        fixture.World.Tick(DELTA_SECONDS);
        Near(fixture.Position, Vector3.Zero, "Rejoin does not inherit movement");
        Require(fixture.Send(new MoveInput(0, -1, 0)), "New admission resets sequence history");
        fixture.World.Shutdown();
        Require(!fixture.Send(new MoveInput(1, 1, 0)) && fixture.World.PlayerCount == 0, "Shutdown closes movement mailbox");
    }

    private static void VerifyHandler()
    {
        using var fixture = new Fixture();
        var otherHandler = new GameChannelHandler(fixture.Sessions, fixture.Rooms);
        var otherChannel = new EmbeddedChannel(otherHandler);
        try
        {
            fixture.Rooms.Join(otherHandler.Session!.SessionId, 1);
            fixture.World.Tick(DELTA_SECONDS);
            Vector3 otherStart = fixture.World.GetPlayers().Single(player => player.SessionId == otherHandler.Session.SessionId).Position;
            byte[] frame = PacketCodec.EncodeMoveInput(new MoveInput(0, 0, 1));
            fixture.Channel.WriteInbound(Unpooled.WrappedBuffer(frame[..5]));
            fixture.World.Tick(DELTA_SECONDS);
            Near(fixture.Position, Vector3.Zero, "Partial input waits in production decoder");
            fixture.Channel.WriteInbound(Unpooled.WrappedBuffer(frame[5..]));
            fixture.World.Tick(DELTA_SECONDS);
            Near(fixture.Position, new Vector3(0, 0, 5 * DELTA_SECONDS), "Input applies to sender");
            Near(fixture.World.GetPlayers().Single(player => player.SessionId == otherHandler.Session.SessionId).Position,
                otherStart, "Other player in same room remains unaffected");

            byte[] malformed = (byte[])frame.Clone();
            BinaryPrimitives.WriteInt32BigEndian(malformed.AsSpan(7), BitConverter.SingleToInt32Bits(float.NaN));
            fixture.Channel.WriteInbound(Unpooled.WrappedBuffer(malformed));
            fixture.Channel.RunPendingTasks();
            Require(!fixture.Channel.Active, "Malformed direction disconnects");
            fixture.World.Tick(DELTA_SECONDS);
            Require(fixture.World.PlayerCount == 1 && fixture.World.GetPlayers()[0].SessionId == otherHandler.Session.SessionId,
                "Invalid movement cleans only its sender");
        }
        finally
        {
            otherChannel.FinishAndReleaseAll();
        }
    }

    private static void VerifyPendingDeparture()
    {
        using var fixture = new Fixture(maxCommandsPerTick: 1);
        fixture.Send(new MoveInput(0, 1, 0));
        fixture.World.Tick(DELTA_SECONDS);
        Vector3 position = fixture.Position;
        var otherHandler = new GameChannelHandler(fixture.Sessions, fixture.Rooms);
        var otherChannel = new EmbeddedChannel(otherHandler);
        try
        {
            fixture.Rooms.Join(otherHandler.Session!.SessionId, 1);
            fixture.Rooms.Leave(fixture.SessionId);
            fixture.World.Tick(DELTA_SECONDS);
            Near(fixture.Position, position, "Pending Leave stops movement before command consumption");
            Require(fixture.World.Commands.PendingCount == 1, "Leave remains queued behind admission");
            fixture.World.Tick(DELTA_SECONDS);
            Require(fixture.World.GetPlayers().All(player => player.SessionId != fixture.SessionId), "Departure removes player");
        }
        finally
        {
            otherChannel.FinishAndReleaseAll();
        }
    }

    private static async Task VerifyConcurrentInputAsync()
    {
        using var fixture = new Fixture();
        await Task.WhenAll(Task.Run(() =>
        {
            for (uint sequence = 0; sequence < 1000; sequence++)
            {
                fixture.Send(new MoveInput(sequence, 1, -1));
            }
        }), Task.Run(() =>
        {
            for (int tick = 0; tick < 100; tick++)
            {
                fixture.World.Tick(DELTA_SECONDS);
            }
        }));
        Require(fixture.Send(new MoveInput(1000, 0, 0)), "Newest stop accepted after concurrent writes");
        Vector3 position = fixture.Position;
        fixture.World.Tick(DELTA_SECONDS);
        Vector3 stopped = fixture.Position;
        Require(MathF.Abs(stopped.X - position.X) < POSITION_TOLERANCE && MathF.Abs(stopped.Z - position.Z) < POSITION_TOLERANCE,
            "No torn or queued horizontal movement after stop");
        Require(fixture.World.Commands.PendingCount == 0, "Concurrent movement does not queue work");
    }

    private static void Near(Vector3 actual, Vector3 expected, string message)
    {
        Require(Vector3.Distance(actual, expected) < POSITION_TOLERANCE, $"{message}: {actual} != {expected}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private sealed class Fixture : IDisposable
    {
        public ManualTimeProvider Clock { get; } = new();
        public SessionManager Sessions { get; } = new();
        public RoomManager Rooms { get; }
        public GameWorld World => Rooms.GetWorlds()[0];
        public EmbeddedChannel Channel { get; }
        public Guid SessionId { get; }
        public Vector3 Position => World.GetPlayers().Single(player => player.SessionId == SessionId).Position;

        public Fixture(bool admit = true, int maxCommandsPerTick = 4)
        {
            Rooms = new RoomManager(Sessions, 2, 4, maxCommandsPerTick, Clock);
            var handler = new GameChannelHandler(Sessions, Rooms);
            Channel = new EmbeddedChannel(new LengthFieldBasedFrameDecoder(
                PacketCodec.HEADER_SIZE + PacketCodec.MAX_PAYLOAD_LENGTH, 0, 2, 1, 0), handler);
            SessionId = handler.Session!.SessionId;
            if (admit)
            {
                Rooms.Join(SessionId, 1);
                World.Tick(DELTA_SECONDS);
            }
        }

        public bool Send(MoveInput input) => Rooms.SubmitMovement(SessionId, input);

        public void Dispose()
        {
            Channel.FinishAndReleaseAll();
            foreach (GameWorld world in Rooms.GetWorlds())
            {
                world.Shutdown();
            }
        }
    }
}
