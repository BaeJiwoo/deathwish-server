using System.Buffers.Binary;
using System.Net;
using System.Numerics;
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
        byte[] gaitJoin = PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(ROOM_ID, SnapshotMode.Gait));
        Require(gaitJoin.SequenceEqual(new byte[] { 0, 5, 3, 0, 0, 0, 1, 2 }) &&
            PacketCodec.DecodeJoinRoomRequest(gaitJoin[PacketCodec.HEADER_SIZE..]).Mode == SnapshotMode.Gait,
            "Compact gait is explicitly requested with its own mode byte");
        animatedJoin[^1] = 5;
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
        VerifyGaitWire(snapshot.Positions);
        VerifyAimWire(snapshot.Positions);
        VerifyShotWire();
        Console.WriteLine("PASS animation wire opt-in, full 1024-player frame, fragmentation, immutable poses and malformed payloads");
    }

    private static void VerifyGaitWire(WorldSnapshot positions)
    {
        var rows = positions.Players.Select(p => new GaitPlayer(p.PlayerId, -12_345, true, 0xffff1234u)).ToArray();
        var snapshot = new GaitWorldSnapshot(positions, 300_000, rows);
        rows[0] = default;
        byte[] frame = GaitSnapshotCodec.Encode(snapshot);
        Require(frame.Length == 3 + 22 + 27 * SnapshotCodec.MAX_PLAYERS &&
            frame.Length == PacketCodec.HEADER_SIZE + GaitSnapshotCodec.MAX_PAYLOAD_LENGTH &&
            frame[^7..].SequenceEqual(new byte[] { 0xcf, 0xc7, 1, 0xff, 0xff, 0x12, 0x34 }),
            "The full compact frame carries seven big-endian gait bytes per player");
        var reader = new PacketReader();
        reader.Feed(frame.AsSpan(0, frame.Length - 1), (_, _) => throw new Exception("Incomplete gait frame dispatched"));
        GaitWorldSnapshot? decoded = null;
        reader.Feed(frame.AsSpan(frame.Length - 1), (type, payload) =>
        {
            Require(type == MessageType.GaitWorldSnapshot, "Compact snapshots use type 18");
            decoded = GaitSnapshotCodec.Decode(payload);
        });
        Require(decoded != null && decoded.Players[0].IsWalking && decoded.Players[^1].CyclePhase == 0xffff1234u &&
            decoded.Players[^1].PlayerId == positions.Players[^1].PlayerId &&
            decoded.ServerTimeMicroseconds == snapshot.ServerTimeMicroseconds &&
            decoded.Positions.Players.SequenceEqual(positions.Players), "Gait and full position roster round trip together");

        byte[] body = frame[PacketCodec.HEADER_SIZE..];
        RequireInvalid(() => GaitSnapshotCodec.Decode(body[..^1]), "Truncated gait");
        body[^5] = 2;
        RequireInvalid(() => GaitSnapshotCodec.Decode(body), "Reserved gait state");
        body[^5] = 0;
        BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(body.Length - 7), short.MinValue);
        RequireInvalid(() => GaitSnapshotCodec.Decode(body), "Invalid gait heading");
        RequireInvalid(() => GaitSnapshotCodec.Encode(new GaitWorldSnapshot(positions, 1, rows)), "Mismatched gait roster");
        Console.WriteLine("PASS compact gait handshake, 1024-player frame, fragmentation, immutable roster and validation");
    }

    private static void VerifyAimWire(WorldSnapshot positions)
    {
        var aim = new AimAngles(12_345, -4_567);
        byte[] input = PacketCodec.EncodeAimedMoveInput(new MoveInput(0x01020304, 1, -.5f), aim);
        Require(input.SequenceEqual(Convert.FromHexString("001013010203043F800000BF0000003039EE29")),
            "Aimed input has the sequenced movement prefix and signed camera yaw and pitch");
        byte[] payload = input[PacketCodec.HEADER_SIZE..];
        MoveInput movement = PacketCodec.DecodeAimedMoveInput(payload, out AimAngles decodedAim);
        Require(movement.Sequence == 0x01020304 && movement.X == 1 && movement.Z == -.5f &&
            decodedAim.Equals(aim), "Camera and motion round trip in one frame");
        RequireInvalid(() => PacketCodec.DecodeMoveInput(payload), "Legacy movement rejects trailing bytes");
        RequireInvalid(() => PacketCodec.DecodeAimedMoveInput(payload[..^1], out _), "Truncated aimed input");
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(payload.Length - 4), 18_001);
        RequireInvalid(() => PacketCodec.DecodeAimedMoveInput(payload, out _), "Yaw beyond a signed half turn");

        byte[] join = PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(ROOM_ID, SnapshotMode.AimedGait));
        Require(join[^1] == 3 && PacketCodec.DecodeJoinRoomRequest(join[PacketCodec.HEADER_SIZE..]).Mode == SnapshotMode.AimedGait,
            "Mode 3 opts into camera snapshots");
        var rows = positions.Players.Select(p => new GaitPlayer(p.PlayerId, 9000, false, 123u, aim)).ToArray();
        byte[] frame = GaitSnapshotCodec.Encode(new GaitWorldSnapshot(positions, 300_000, rows), true);
        Require(frame.Length == PacketCodec.HEADER_SIZE + GaitSnapshotCodec.MAX_AIMED_PAYLOAD_LENGTH &&
            frame[^4..].SequenceEqual(Convert.FromHexString("3039EE29")), "Full roster has four additional aim bytes per player");
        var reader = new PacketReader();
        reader.Feed(frame.AsSpan(0, frame.Length - 1), (_, _) => throw new Exception("Incomplete aim snapshot dispatched"));
        reader.Feed(frame.AsSpan(frame.Length - 1), (type, bytes) =>
        {
            GaitWorldSnapshot snapshot = GaitSnapshotCodec.Decode(bytes, true);
            Require(type == MessageType.AimedGaitWorldSnapshot && snapshot.Players.Count == positions.Players.Count &&
                snapshot.Players[^1].Aim.Equals(aim) && snapshot.Players[^1].YawCentidegrees == 9000,
                "Travel heading and camera angles decode independently from a split maximum frame");
        });
        payload = frame[PacketCodec.HEADER_SIZE..];
        RequireInvalid(() => GaitSnapshotCodec.Decode(payload), "Legacy gait rejects aimed snapshot");
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(payload.Length - 2), -9_001);
        RequireInvalid(() => GaitSnapshotCodec.Decode(payload, true), "Out-of-range pitch rejected on receive");
        Console.WriteLine("PASS atomic camera and movement input, maximum aim roster, angle limits and legacy framing");
    }

    private static void VerifyShotWire()
    {
        byte[] join = PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(ROOM_ID, SnapshotMode.AimedGaitWithShots));
        Require(join[^1] == 4 && PacketCodec.DecodeJoinRoomRequest(join[PacketCodec.HEADER_SIZE..]).Mode == SnapshotMode.AimedGaitWithShots,
            "Mode 4 opts into shots and aimed gait");
        var shot = new ShotFired(0x0102030405060708, 0x1020304050607080);
        byte[] frame = ActionCodec.EncodeShotFired(shot);
        Require(frame.SequenceEqual(Convert.FromHexString("00101501020304050607081020304050607080")), "Shot wire layout");
        var reader = new PacketReader();
        reader.Feed(frame.AsSpan(0, frame.Length - 1), (_, _) => throw new Exception("Incomplete shot dispatched"));
        reader.Feed(frame.AsSpan(frame.Length - 1), (type, payload) =>
        {
            Require(type == MessageType.ShotFired && ActionCodec.DecodeShotFired(payload).Equals(shot), "Split shot round trip");
        });
        byte[] body = frame[PacketCodec.HEADER_SIZE..];
        RequireInvalid(() => ActionCodec.DecodeShotFired(body[..^1]), "Incomplete shot");
        RequireInvalid(() => ActionCodec.EncodeShotFired(default), "Invalid shooter ID");
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(sizeof(long)), 0);
        RequireInvalid(() => ActionCodec.DecodeShotFired(body), "Invalid shot clock");
        Console.WriteLine("PASS shot subscription, exact wire bytes, framing and validation");
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
            using var gait = new ConnectionTestClient();
            await actor.ConnectAsync("127.0.0.1", port, token);
            await legacy.ConnectAsync("127.0.0.1", port, token);
            await gait.ConnectAsync("127.0.0.1", port, token);
            Require((await actor.JoinAnimatedRoomAsync(ROOM_ID, token)).Result == RoomJoinResult.Success &&
                (await legacy.JoinRoomAsync(ROOM_ID, token)).Result == RoomJoinResult.Success &&
                (await gait.JoinGaitRoomAsync(ROOM_ID, token)).Result == RoomJoinResult.Success, "Three snapshot modes join the same room");

            await StepAsync(clock);
            JoinAck admission = await actor.WaitForAdmissionAsync(token);
            await legacy.WaitForAdmissionAsync(token);
            await gait.WaitForAdmissionAsync(token);
            await WaitForTickAsync(gait, admission.ServerTick, token);
            Require(gait.LatestGaitSnapshot!.Players.All(p => !p.IsWalking && p.CyclePhase == 0), "All gait clocks start at rest");
            await WaitForTickAsync(actor, admission.ServerTick, token);
            AnimatedWorldSnapshot stand = actor.LatestAnimatedSnapshot!;
            Require(stand.Players.Single(player => player.PlayerId == admission.PlayerId)
                .BonePitchCentidegrees.All(pitch => pitch == 0), "The server sends stand on admission");

            await actor.SendMoveAsync(new MoveInput(1, 1, 0), token);
            await actor.EchoAsync("movement parsed", token);
            await StepAsync(clock);
            await WaitForTickAsync(actor, stand.Positions.ServerTick + 1, token);
            await WaitForTickAsync(legacy, stand.Positions.ServerTick + 1, token);
            await WaitForTickAsync(gait, stand.Positions.ServerTick + 1, token);

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

            GaitWorldSnapshot moving = gait.LatestGaitSnapshot!;
            GaitPlayer movingActor = moving.Players.Single(p => p.PlayerId == admission.PlayerId);
            double expectedCycle = (animatedFeet.X - admission.X) / GameWorld.MOVEMENT_SPEED_METERS_PER_SECOND;
            Require(moving.Positions.Players.SequenceEqual(walk.Positions.Players) &&
                moving.ServerTimeMicroseconds == walk.ServerTimeMicroseconds && movingActor.IsWalking &&
                movingActor.YawCentidegrees == pose.YawCentidegrees &&
                Math.Abs(movingActor.CyclePhase - expectedCycle * GaitPlayer.PHASE_UNITS_PER_CYCLE) <= 1 &&
                gait.LatestAnimatedSnapshot == null && actor.LatestGaitSnapshot == null,
                "A gait client receives the same movement, clock and facing, with phase based on accepted travel");

            await actor.SendMoveAsync(new MoveInput(2, 0, 0), token);
            await actor.EchoAsync("stop parsed", token);
            await StepAsync(clock);
            await WaitForTickAsync(actor, walk.Positions.ServerTick + 1, token);
            Require(actor.LatestAnimatedSnapshot!.Players.Single(player => player.PlayerId == admission.PlayerId)
                .BonePitchCentidegrees.All(pitch => pitch == 0), "Releasing movement returns to the server's standing pose");
            await WaitForTickAsync(gait, walk.Positions.ServerTick + 1, token);
            GaitPlayer stopped = gait.LatestGaitSnapshot!.Players.Single(p => p.PlayerId == admission.PlayerId);
            Require(!stopped.IsWalking && stopped.CyclePhase == movingActor.CyclePhase,
                "Stopping freezes the server phase while the Animator can return to idle");
            Console.WriteLine("PASS live gait, bone-pose and legacy TCP admission, shared positions, accepted phase and stopping");
            await VerifyAimTcpAsync(actor, admission.PlayerId, gait, clock, port, token);
        }
        finally
        {
            stop.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task VerifyAimTcpAsync(ConnectionTestClient actor, long actorId, ConnectionTestClient gait,
        ManualTimeProvider clock, int port, CancellationToken token)
    {
        var idleAim = new AimAngles(-17_999, 3_100);
        await actor.SendAimedMoveAsync(new MoveInput(3, 0, 0), idleAim, token);
        await actor.SendAimedMoveAsync(new MoveInput(2, -1, 0), default, token);
        await actor.EchoAsync("old and new aim parsed", token);
        await StepAsync(clock);

        using var observer = new ConnectionTestClient();
        await observer.ConnectAsync("127.0.0.1", port, token);
        await observer.JoinAimedGaitRoomAsync(ROOM_ID, token);
        await StepAsync(clock);
        JoinAck observerAdmission = await observer.WaitForAdmissionAsync(token);
        await WaitForTickAsync(observer, observerAdmission.ServerTick, token);
        GaitPlayer SeenActor() => observer.LatestGaitSnapshot!.Players.Single(p => p.PlayerId == actorId);
        var idle = SeenActor();
        Require(!idle.IsWalking && idle.Aim.Equals(idleAim) && idle.YawCentidegrees == 9000,
            "Late join immediately receives stationary facing without letting stale aim rewind it");

        var strafeAim = new AimAngles(-9_000, -4_000);
        await actor.SendAimedMoveAsync(new MoveInput(4, 1, 0), strafeAim, token);
        await actor.EchoAsync("strafe parsed", token);
        await StepAsync(clock);
        await WaitForTickAsync(observer, observerAdmission.ServerTick + 1, token);
        await WaitForTickAsync(gait, observerAdmission.ServerTick + 1, token);
        GaitPlayer strafe = SeenActor();
        var legacy = gait.LatestGaitSnapshot!.Players.Single(p => p.PlayerId == actorId);
        Require(strafe.IsWalking && strafe.CyclePhase > idle.CyclePhase && strafe.YawCentidegrees == 9000 &&
            strafe.Aim.Equals(strafeAim) && legacy.Aim.Equals(new AimAngles(9000, 0)),
            "Travel and camera face opposite directions while original gait observers retain their heading");

        await actor.SendMoveAsync(new MoveInput(5, 0, 0), token);
        await actor.EchoAsync("legacy stop parsed", token);
        for (int i = 0; i < 4; i++)
        {
            await StepAsync(clock);
        }
        await WaitForTickAsync(observer, observerAdmission.ServerTick + 5, token);
        GaitPlayer stopped = SeenActor();
        Require(!stopped.IsWalking && stopped.Aim.Equals(strafeAim) && stopped.CyclePhase == strafe.CyclePhase,
            "Legacy input and expired movement keep the last accepted aim intact");
        Console.WriteLine("PASS live camera aim while stationary and strafing, stale sequence, late join, expiry and legacy observers");
        await VerifyShotsTcpAsync(actor, observer, clock, port, token);
    }

    private static async Task VerifyShotsTcpAsync(ConnectionTestClient oldShooter, ConnectionTestClient oldObserver,
        ManualTimeProvider clock, int port, CancellationToken token)
    {
        using var shooter = new ConnectionTestClient();
        using var observer = new ConnectionTestClient();
        using var otherRoom = new ConnectionTestClient();

        async Task Subscribe(ConnectionTestClient client, int roomId)
        {
            await client.ConnectAsync("127.0.0.1", port, token);
            Require((await client.JoinShootingRoomAsync(roomId, token)).Result == RoomJoinResult.Success, "Shot observer admission");
        }

        await Subscribe(shooter, ROOM_ID);
        await Subscribe(observer, ROOM_ID);
        await Subscribe(otherRoom, ROOM_ID + 1);
        await StepAsync(clock);
        JoinAck shooterAck = await shooter.WaitForAdmissionAsync(token);
        await observer.WaitForAdmissionAsync(token);
        await otherRoom.WaitForAdmissionAsync(token);
        var hit = new CameraRay(Vector3.UnitY, -Vector3.UnitY);
        var miss = new CameraRay(Vector3.UnitY, Vector3.UnitY);
        await shooter.SendRayAsync(hit, token);
        await shooter.SendRayAsync(miss, token);
        await shooter.EchoAsync("both rays queued", token);

        await StepAsync(clock);
        await StepAsync(clock);
        long finalTick = shooterAck.ServerTick + 2;
        foreach (var client in new[] { shooter, observer, otherRoom, oldObserver })
        {
            await WaitForTickAsync(client, finalTick, token);
        }
        long latestTime = observer.LatestGaitSnapshot!.ServerTimeMicroseconds;
        Require(observer.TryReadShotFired(out ShotFired first) && observer.TryReadShotFired(out ShotFired second) &&
            first.PlayerId == shooterAck.PlayerId && second.PlayerId == first.PlayerId &&
            first.ServerTimeMicroseconds == latestTime - TICK_MICROSECONDS && second.ServerTimeMicroseconds == latestTime &&
            !observer.TryReadShotFired(out _), "A hit and a miss survive coalesced snapshots as two timed events");
        Require(!shooter.TryReadShotFired(out _) && !otherRoom.TryReadShotFired(out _) && !oldObserver.TryReadShotFired(out _),
            "No shot echo, cross-room broadcast or unsolicited message on a legacy aim connection");
        Require(shooter.TryReadRaycastResult(out string? hitReport) && hitReport!.Contains("hit terrain") &&
            shooter.TryReadRaycastResult(out string? missReport) && missReport == "Raycast miss" &&
            !observer.TryReadRaycastResult(out _), "The original shooter alone receives the authoritative hit and miss results");

        using var late = new ConnectionTestClient();
        await Subscribe(late, ROOM_ID);
        await StepAsync(clock);
        JoinAck lateAck = await late.WaitForAdmissionAsync(token);
        await WaitForTickAsync(late, lateAck.ServerTick, token);
        Require(!late.TryReadShotFired(out _), "Late observers do not replay earlier shots");
        await oldShooter.SendRayAsync(miss, token);
        await oldShooter.EchoAsync("older shooter ray queued", token);
        await StepAsync(clock);
        long oldShooterId = (await oldShooter.WaitForAdmissionAsync(token)).PlayerId;
        foreach (var client in new[] { shooter, observer, late })
        {
            await WaitForTickAsync(client, lateAck.ServerTick + 1, token);
            Require(client.TryReadShotFired(out ShotFired shot) && shot.PlayerId == oldShooterId &&
                !client.TryReadShotFired(out _), "Each subscribed observer sees a legacy shooter's new shot exactly once");
        }
        Console.WriteLine("PASS live shot events: hits/misses, repeated shots, no self echo, private results, room isolation, late join and mixed modes");
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
