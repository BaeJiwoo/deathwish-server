using System.Numerics;
using DeathWish.Protocol;
using DeathWish.Server;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;

internal static class AnimationWorldTests
{
    private const float TICK_SECONDS = 1f / 30;
    private const int TICKS_PER_SNAPSHOT = 3;
    private const long SNAPSHOT_INTERVAL_MICROSECONDS = 100_000;
    private const float POSE_TOLERANCE_METERS = 0.001f;
    private const short EAST_YAW_CENTIDEGREES = 9_000;

    internal static void Run()
    {
        VerifyWorldSnapshots();
        VerifyBlockedWalking();
        Console.WriteLine("PASS server animation: opt-in and legacy snapshots, movement/idle, phase, time, OBBs, wall and departure");
    }

    private static void VerifyWorldSnapshots()
    {
        var clock = new ManualTimeProvider();
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions, 2, 8, 8, clock);
        GameWorld world = rooms.GetWorlds()[0];
        var animated = new EmbeddedChannel(new GameChannelHandler(sessions, rooms));
        var legacy = new EmbeddedChannel(new GameChannelHandler(sessions, rooms));
        try
        {
            JoinAck first = Join(animated, world, SnapshotMode.BonePoses);
            JoinAck second = Join(legacy, world, SnapshotMode.Positions);
            world.Tick(TICK_SECONDS);
            AnimatedWorldSnapshot initial = ReadAnimated(animated);
            VerifyLegacy(ReadPosition(legacy), initial.Positions);
            AnimatedPlayer idle = initial.Players.Single(player => player.PlayerId == first.PlayerId);
            AnimatedPlayer legacyIdle = initial.Players.Single(player => player.PlayerId == second.PlayerId);
            Require(idle.BonePitchCentidegrees.All(pitch => pitch == 0) &&
                legacyIdle.BonePitchCentidegrees.All(pitch => pitch == 0), "Both players initially stand idle");
            Require(initial.RigId == AnimationRig.RIG_ID && initial.ServerTimeMicroseconds == SNAPSHOT_INTERVAL_MICROSECONDS,
                "Animated snapshots identify the rig and use simulation time in microseconds");

            SendMovement(animated, sequence: 0, x: 1f, z: 0f);
            Tick(world, TICKS_PER_SNAPSHOT);
            AnimatedWorldSnapshot walking = ReadAnimated(animated);
            VerifyLegacy(ReadPosition(legacy), walking.Positions);
            AnimatedPlayer moving = walking.Players.Single(player => player.PlayerId == first.PlayerId);
            SnapshotPlayer foot = walking.Positions.Players.Single(player => player.PlayerId == first.PlayerId);
            Require(MathF.Abs(foot.X - first.X - 0.5f) < POSE_TOLERANCE_METERS &&
                moving.YawCentidegrees == EAST_YAW_CENTIDEGREES && moving.BonePitchCentidegrees.Any(pitch => pitch != 0),
                "After Bepu movement, the animated actor faces and walks east");
            Require(walking.Players.Single(player => player.PlayerId == second.PlayerId).BonePitchCentidegrees.All(pitch => pitch == 0),
                "An observer has an independent pose");
            Require(walking.ServerTimeMicroseconds - initial.ServerTimeMicroseconds == SNAPSHOT_INTERVAL_MICROSECONDS,
                "Wall-clock changes are not needed to advance animation snapshot time");
            VerifyWorldPose(world, moving, foot);

            SendMovement(animated, sequence: 1, x: 0f, z: 0f);
            Tick(world, TICKS_PER_SNAPSHOT);
            AnimatedWorldSnapshot stopped = ReadAnimated(animated);
            VerifyLegacy(ReadPosition(legacy), stopped.Positions);
            AnimatedPlayer settled = stopped.Players.Single(player => player.PlayerId == first.PlayerId);
            Require(settled.YawCentidegrees == EAST_YAW_CENTIDEGREES && settled.BonePitchCentidegrees.All(pitch => pitch == 0),
                "Zero input produces the standing pose and retains the last movement yaw");

            SendMovement(animated, sequence: 2, x: 1f, z: 0f);
            Tick(world, TICKS_PER_SNAPSHOT);
            AnimatedWorldSnapshot resumed = ReadAnimated(animated);
            VerifyLegacy(ReadPosition(legacy), resumed.Positions);
            AnimatedPlayer resumedActor = resumed.Players.Single(player => player.PlayerId == first.PlayerId);
            SnapshotPlayer resumedFoot = resumed.Positions.Players.Single(player => player.PlayerId == first.PlayerId);
            Span<short> expectedPitches = stackalloc short[AnimationRig.BONE_COUNT];
            float traveledMeters = resumedFoot.X - first.X;
            AnimationRig.SampleWalk(traveledMeters / GameWorld.MOVEMENT_SPEED_METERS_PER_SECOND, true, expectedPitches);
            Require(resumedActor.BonePitchCentidegrees.SequenceEqual(expectedPitches.ToArray()),
                "After resuming, the phase matches total movement time without counting idle time");

            clock.Advance(TimeSpan.FromMilliseconds(MovementMailbox.INPUT_TIMEOUT_MS));
            Tick(world, TICKS_PER_SNAPSHOT);
            AnimatedWorldSnapshot expired = ReadAnimated(animated);
            VerifyLegacy(ReadPosition(legacy), expired.Positions);
            Require(expired.Players.Single(player => player.PlayerId == first.PlayerId).BonePitchCentidegrees.All(pitch => pitch == 0),
                "Expired input cannot keep advancing the walk");

            legacy.CloseAsync().GetAwaiter().GetResult();
            Tick(world, TICKS_PER_SNAPSHOT);
            AnimatedWorldSnapshot remaining = ReadAnimated(animated);
            Require(remaining.Players.Count == 1 && remaining.Players[0].PlayerId == first.PlayerId,
                "Departed actors are removed from the full animated snapshot");
            var absentPose = new AnimationBonePose[AnimationRig.BONE_COUNT];
            Require(!world.TryGetPlayerAnimationPose(second.PlayerId, absentPose), "Departed actors no longer have world hitboxes");
        }
        finally
        {
            animated.FinishAndReleaseAll();
            legacy.FinishAndReleaseAll();
            world.Shutdown();
        }
    }

    private static void VerifyBlockedWalking()
    {
        var clock = new ManualTimeProvider();
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions, 1, 8, 8, clock);
        GameWorld world = rooms.GetWorlds()[0];
        var channel = new EmbeddedChannel(new GameChannelHandler(sessions, rooms));
        var observer = new EmbeddedChannel(new GameChannelHandler(sessions, rooms));
        try
        {
            JoinAck actor = Join(channel, world, SnapshotMode.BonePoses);
            Join(observer, world, SnapshotMode.Gait);
            SendMovement(channel, sequence: 0, x: 0f, z: 1f);
            Tick(world, 11);
            ReadLatestAnimated(channel);

            // Approach the east wall along the ground beyond the ring.
            SendMovement(channel, sequence: 1, x: 1f, z: 0f);
            Tick(world, 180);
            AnimatedWorldSnapshot snapshot = ReadLatestAnimated(channel);
            Require(snapshot.Positions.Players.Single(p => p.PlayerId == actor.PlayerId).X is > 24.55f and < 24.75f,
                "The actor reaches the PhysicsLab east wall");
            AnimatedPlayer settled = snapshot.Players.Single(p => p.PlayerId == actor.PlayerId);
            Require(settled.YawCentidegrees == EAST_YAW_CENTIDEGREES && settled.BonePitchCentidegrees.All(pitch => pitch == 0),
                "Actual displacement stops the walk when persistent input pushes against a wall");
            GaitPlayer held = ReadLatestGait(observer).Players.Single(p => p.PlayerId == actor.PlayerId);
            Tick(world, TICKS_PER_SNAPSHOT);
            GaitPlayer later = ReadLatestGait(observer).Players.Single(p => p.PlayerId == actor.PlayerId);
            Require(!held.IsWalking && !later.IsWalking && held.CyclePhase > GaitPlayer.PHASE_UNITS_PER_CYCLE &&
                held.CyclePhase == later.CyclePhase && held.YawCentidegrees == EAST_YAW_CENTIDEGREES,
                "The gait clock counts complete cycles and holds phase and heading while blocked");
        }
        finally
        {
            observer.FinishAndReleaseAll();
            channel.FinishAndReleaseAll();
            world.Shutdown();
        }
    }

    private static void VerifyWorldPose(GameWorld world, AnimatedPlayer player, SnapshotPlayer foot)
    {
        Span<AnimationBonePose> actual = stackalloc AnimationBonePose[AnimationRig.BONE_COUNT];
        Span<AnimationBonePose> expected = stackalloc AnimationBonePose[AnimationRig.BONE_COUNT];
        var position = new Vector3(foot.X, foot.Y, foot.Z);
        AnimationRig.GetWorldPose(position, player.YawCentidegrees, player.BonePitchCentidegrees.ToArray(), expected);
        Require(world.TryGetPlayerAnimationPose(player.PlayerId, actual),
            "An active player has a server-computed world pose and oriented hitboxes");
        for (int index = 0; index < actual.Length; index++)
        {
            Require(Vector3.Distance(actual[index].Position, expected[index].Position) < POSE_TOLERANCE_METERS &&
                Vector3.Distance(actual[index].BoxCenter, expected[index].BoxCenter) < POSE_TOLERANCE_METERS &&
                actual[index].BoxSize == expected[index].BoxSize &&
                MathF.Abs(Quaternion.Dot(actual[index].Rotation, expected[index].Rotation)) > 0.9999f,
                "Server hitboxes and poses match decoded snapshot transforms");
        }
        Require(Vector3.Distance(Vector3.Transform(Vector3.UnitZ, actual[0].Rotation), Vector3.UnitX) < POSE_TOLERANCE_METERS,
            "The server root basis rotates +Z forward to +X when walking east");
    }

    private static JoinAck Join(EmbeddedChannel channel, GameWorld world, SnapshotMode mode)
    {
        var request = new JoinRoomRequest(world.RoomId, mode);
        channel.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeJoinRoomRequest(request)));
        byte[] response = ReadFrame(channel);
        Require(response[2] == (byte)MessageType.JoinRoomResponse &&
            PacketCodec.DecodeJoinRoomResponse(response[PacketCodec.HEADER_SIZE..]).Result == RoomJoinResult.Success,
            "The room reserves both legacy and animated admissions before the world acknowledgment");
        world.Tick(TICK_SECONDS);
        byte[] ack = ReadFrame(channel);
        Require(ack[2] == (byte)MessageType.JoinAck, "Animation opt-in preserves JoinAck order and type");
        return PacketCodec.DecodeJoinAck(ack[PacketCodec.HEADER_SIZE..]);
    }

    private static void Tick(GameWorld world, int count)
    {
        for (int index = 0; index < count; index++)
        {
            world.Tick(TICK_SECONDS);
        }
    }

    private static void SendMovement(EmbeddedChannel channel, uint sequence, float x, float z) =>
        channel.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeMoveInput(new MoveInput(sequence, x, z))));

    private static AnimatedWorldSnapshot ReadAnimated(EmbeddedChannel channel) =>
        DecodeAnimated(ReadFrame(channel));

    private static AnimatedWorldSnapshot ReadLatestAnimated(EmbeddedChannel channel)
    {
        AnimatedWorldSnapshot latest = ReadAnimated(channel);
        while (channel.ReadOutbound<IByteBuffer>() is { } buffer)
        {
            latest = DecodeAnimated(CopyFrame(buffer));
        }
        return latest;
    }

    private static GaitWorldSnapshot ReadLatestGait(EmbeddedChannel channel)
    {
        byte[] frame = ReadFrame(channel);
        while (channel.ReadOutbound<IByteBuffer>() is { } buffer)
        {
            frame = CopyFrame(buffer);
        }
        Require(frame[2] == (byte)MessageType.GaitWorldSnapshot, "A compact observer receives gait frames");
        return GaitSnapshotCodec.Decode(frame[PacketCodec.HEADER_SIZE..]);
    }

    private static AnimatedWorldSnapshot DecodeAnimated(byte[] frame)
    {
        Require(frame[2] == (byte)MessageType.AnimatedWorldSnapshot,
            "An opted-in client receives only the single combined position and pose frame");
        return AnimatedSnapshotCodec.Decode(frame[PacketCodec.HEADER_SIZE..]);
    }

    private static WorldSnapshot ReadPosition(EmbeddedChannel channel)
    {
        byte[] frame = ReadFrame(channel);
        Require(frame[2] == (byte)MessageType.WorldSnapshot, "A legacy client receives the unchanged snapshot type");
        return SnapshotCodec.Decode(frame[PacketCodec.HEADER_SIZE..]);
    }

    private static void VerifyLegacy(WorldSnapshot legacy, WorldSnapshot animated)
    {
        Require(legacy.RoomId == animated.RoomId && legacy.ServerTick == animated.ServerTick &&
            legacy.Players.SequenceEqual(animated.Players), "Both wire formats carry the same tick and player positions");
    }

    private static byte[] ReadFrame(EmbeddedChannel channel) =>
        CopyFrame(channel.ReadOutbound<IByteBuffer>() ?? throw new InvalidOperationException("Missing animation test frame."));

    private static byte[] CopyFrame(IByteBuffer buffer)
    {
        try
        {
            var frame = new byte[buffer.ReadableBytes];
            buffer.ReadBytes(frame);
            return frame;
        }
        finally
        {
            buffer.Release();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
