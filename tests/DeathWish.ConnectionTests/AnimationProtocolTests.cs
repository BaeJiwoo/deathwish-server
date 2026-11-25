using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DeathWish.MockClient.Core;
using DeathWish.Protocol;
using DeathWish.Server;

internal static class AnimationProtocolTests
{
    private const int ROOM_ID = 1;
    private const int TICK_RATE_HZ = 10;
    private const long TICK_MICROSECONDS = 100_000;

    internal static void Run()
    {
        byte[] legacyJoin = PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(ROOM_ID));
        Require(legacyJoin.SequenceEqual(new byte[] { 0, 4, 3, 0, 0, 0, 1 }), "Legacy join bytes stay unchanged");
        byte[] animatedJoin = PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(ROOM_ID, true));
        Require(animatedJoin.SequenceEqual(new byte[] { 0, 5, 3, 0, 0, 0, 1, 1 }), "Animated join opts in explicitly");
        Require(PacketCodec.DecodeJoinRoomRequest(animatedJoin[PacketCodec.HEADER_SIZE..]).ReceivesAnimation,
            "The optional join flag round trips");
        animatedJoin[^1] = 2;
        RequireInvalid(() => PacketCodec.DecodeJoinRoomRequest(animatedJoin[PacketCodec.HEADER_SIZE..]), "Unknown feature flag");

        var rows = new SnapshotPlayer[SnapshotCodec.MAX_PLAYERS];
        var animation = new AnimatedPlayer[rows.Length];
        var angles = new short[AnimationRig.BONE_COUNT];
        angles[^1] = -1700;
        for (int index = 0; index < rows.Length; index++)
        {
            rows[index] = new SnapshotPlayer(index + 1, index, 0.25f, -index);
            animation[index] = new AnimatedPlayer(index + 1, -9000, angles);
        }
        angles[^1] = 0;

        var snapshot = new AnimatedWorldSnapshot(new WorldSnapshot(ROOM_ID, 3, rows), 300_000, animation);
        byte[] frame = AnimatedSnapshotCodec.Encode(snapshot);
        Require(frame.Length == PacketCodec.HEADER_SIZE + AnimatedSnapshotCodec.MAX_PAYLOAD_LENGTH &&
            frame.Length <= ushort.MaxValue, "All position slots fit in one bounded animation snapshot");
        AnimatedWorldSnapshot? decoded = null;
        var reader = new PacketReader();
        reader.Feed(frame.AsSpan(0, 13), (_, _) => throw new Exception("The fragmented frame completed too early"));
        reader.Feed(frame.AsSpan(13), (type, payload) =>
        {
            Require(type == MessageType.AnimatedWorldSnapshot, "The animated snapshot type survives framing");
            decoded = AnimatedSnapshotCodec.Decode(payload);
        });
        Require(decoded != null && decoded.RigId == AnimationRig.RIG_ID && decoded.ServerTimeMicroseconds == 300_000 &&
            decoded.Positions.Players[^1].Z == -1023 && decoded.Players[^1].PlayerId == rows.Length &&
            decoded.Players[^1].YawCentidegrees == -9000 && decoded.Players[^1].BonePitchCentidegrees[^1] == -1700,
            "Full snapshot preserves positions, clock, immutable poses and rig identity");

        byte[] body = frame[PacketCodec.HEADER_SIZE..];
        RequireInvalid(() => AnimatedSnapshotCodec.Decode(body[..^1]), "Truncated pose data");
        BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(body.Length - sizeof(short)), short.MaxValue);
        RequireInvalid(() => AnimatedSnapshotCodec.Decode(body), "Angles outside one signed half turn");
        var mismatched = new AnimatedWorldSnapshot(new WorldSnapshot(ROOM_ID, 3, [rows[0]]), 300_000, [animation[1]]);
        RequireInvalid(() => AnimatedSnapshotCodec.Encode(mismatched), "Position and pose roster order must match");
        Console.WriteLine("PASS animation wire opt-in, full 1024-player frame, fragmentation, immutable poses and malformed payloads");
    }

    internal static async Task RunTcpAsync()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        int port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();

        var clock = new ManualTimeProvider();
        using var stop = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        var server = new GameServer(new ServerOptions(port: port, tickRate: TICK_RATE_HZ), clock);
        Task serverTask = server.RunAsync(stop.Token);
        try
        {
            await clock.NextDelayAsync();
            using var actor = new ConnectionTestClient();
            using var legacy = new ConnectionTestClient();
            await actor.ConnectAsync("127.0.0.1", port, token);
            await legacy.ConnectAsync("127.0.0.1", port, token);
            Require((await actor.JoinAnimatedRoomAsync(ROOM_ID, token)).Result == RoomJoinResult.Success &&
                (await legacy.JoinRoomAsync(ROOM_ID, token)).Result == RoomJoinResult.Success, "Both versions join the same room");

            await StepAsync(clock);
            JoinAck admission = await actor.WaitForAdmissionAsync(token);
            await legacy.WaitForAdmissionAsync(token);
            await WaitForTickAsync(actor, admission.ServerTick, token);
            AnimatedWorldSnapshot stand = actor.LatestAnimatedSnapshot!;
            Require(stand.Players.Single(player => player.PlayerId == admission.PlayerId)
                .BonePitchCentidegrees.All(pitch => pitch == 0), "The server sends stand on admission");

            await actor.SendMoveAsync(new MoveInput(1, 1, 0), token);
            await actor.EchoAsync("movement parsed", token);
            await StepAsync(clock);
            await WaitForTickAsync(actor, stand.Positions.ServerTick + 1, token);
            await WaitForTickAsync(legacy, stand.Positions.ServerTick + 1, token);

            AnimatedWorldSnapshot walk = actor.LatestAnimatedSnapshot!;
            AnimatedPlayer pose = walk.Players.Single(player => player.PlayerId == admission.PlayerId);
            SnapshotPlayer animatedFeet = walk.Positions.Players.Single(player => player.PlayerId == admission.PlayerId);
            SnapshotPlayer legacyFeet = legacy.LatestSnapshot!.Players.Single(player => player.PlayerId == admission.PlayerId);
            Require(walk.ServerTimeMicroseconds - stand.ServerTimeMicroseconds == TICK_MICROSECONDS &&
                pose.YawCentidegrees == 9000 && pose.BonePitchCentidegrees.Any(pitch => pitch != 0),
                "Actual server movement produces a timed walk pose facing +X");
            Require(animatedFeet.X > admission.X && animatedFeet.X == legacyFeet.X &&
                animatedFeet.Y == legacyFeet.Y && animatedFeet.Z == legacyFeet.Z && legacy.LatestAnimatedSnapshot == null,
                "Legacy and animated sessions receive the same authoritative movement in separate formats");

            await actor.SendMoveAsync(new MoveInput(2, 0, 0), token);
            await actor.EchoAsync("stop parsed", token);
            await StepAsync(clock);
            await WaitForTickAsync(actor, walk.Positions.ServerTick + 1, token);
            Require(actor.LatestAnimatedSnapshot!.Players.Single(player => player.PlayerId == admission.PlayerId)
                .BonePitchCentidegrees.All(pitch => pitch == 0), "Releasing movement returns to the server's standing pose");
            Console.WriteLine("PASS live animation and legacy TCP admission, shared positions, walk yaw and stopping");
        }
        finally
        {
            stop.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task StepAsync(ManualTimeProvider clock)
    {
        clock.Advance(TimeSpan.FromSeconds(1d / TICK_RATE_HZ));
        await clock.NextDelayAsync();
    }

    private static async Task WaitForTickAsync(ConnectionTestClient client, long tick, CancellationToken token)
    {
        while ((client.LatestSnapshot?.ServerTick ?? 0) < tick)
        {
            await Task.Delay(1, token);
        }
    }

    private static void RequireInvalid(Action action, string description)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new Exception(description);
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
        {
            throw new Exception(description);
        }
    }
}
