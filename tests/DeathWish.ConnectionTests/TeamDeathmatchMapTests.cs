using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using DeathWish.Protocol;
using DeathWish.Server;
using DeathWish.Server.Physics;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;

internal static class TeamDeathmatchMapTests
{
    private const int SPAWN_COUNT = 10;
    private const int ARENA_ROOM_ID = 2;
    private const float TICK_SECONDS = 1f / 30;
    private const float POSITION_TOLERANCE_METERS = 0.02f;
    private const float TRAVERSAL_SPEED_METERS_PER_SECOND = 2;
    private const float ARENA_HALF_WIDTH_METERS = 16f;
    private const float ARENA_HALF_LENGTH_METERS = 19f;
    private const float EAST_ROUTE_X_METERS = 4.5f;
    private const float ARCH_FRONT_Z_METERS = -7.7f;
    private const float ARCH_REAR_Z_METERS = -6.8f;
    private const float ARCH_CROWN_HEIGHT_METERS = 2.72f;
    private const float ARCH_APPROACH_Z_METERS = -8.55f;
    private const float ARCH_JAMB_X_METERS = 5.98f;
    private const float ARCH_CAP_RAY_HEIGHT_METERS = 2.9f;
    private const float BRIDGE_START_Z_METERS = -6f;
    private const float BRIDGE_END_Z_METERS = 8f;
    private const float BRIDGE_DECK_HEIGHT_METERS = 2.2f;
    private const float BRIDGE_DECK_THICKNESS_METERS = 0.25f;
    private const float BRIDGE_HALF_WIDTH_METERS = 1.05f / 2;

    internal static void Run()
    {
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions, 3, 32, 32, teamDeathmatchRoomId: ARENA_ROOM_ID);
        GameWorld[] worlds = rooms.GetWorlds();
        Require(worlds.Select(world => world.MapId).SequenceEqual(new[]
            { WorldMapId.Flat, WorldMapId.TeamDeathmatch, WorldMapId.PhysicsLab }), "Opt-in arena retains flat and PhysicsLab rooms");

        var soleRoom = new RoomManager(new SessionManager(), 1, 32, 32, teamDeathmatchRoomId: 1);
        Require(soleRoom.GetWorlds().Single().MapId == WorldMapId.TeamDeathmatch, "Explicit arena takes precedence in the highest room");

        GameWorld world = worlds[ARENA_ROOM_ID - 1];
        MeshWorldMap map = MeshWorldMap.Load(WorldMapId.TeamDeathmatch);
        Require(world.Colliders.Count > 1 && world.Colliders.All(collider => collider is StaticMeshCollider),
            "Arena uses exported static meshes without a fallback plane");
        Require(map.Spawns.Count == SPAWN_COUNT && map.Spawns.GroupBy(spawn => spawn.TeamId).Count() == 2,
            "Map contains both teams' spawn markers");
        VerifyAdmission(sessions, rooms, world, map.Spawns);
        VerifyGeometry(world);
        VerifyExpandedPerimeter(world);
        VerifyArch(world);
        VerifyBridge(world);
        Console.WriteLine("PASS Blender team deathmatch map: room selection, ten spawn markers, capacity, wall and lane, terrain raycast");
    }

    private static void VerifyAdmission(SessionManager sessions, RoomManager rooms, GameWorld world, IReadOnlyList<MapSpawn> spawns)
    {
        GameChannelHandler[] handlers = Enumerable.Range(0, SPAWN_COUNT + 1)
            .Select(_ => new GameChannelHandler(sessions, rooms)).ToArray();
        EmbeddedChannel[] channels = handlers.Select(handler => new EmbeddedChannel(handler)).ToArray();
        try
        {
            byte[] legacyRequest = PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(ARENA_ROOM_ID));
            channels[0].WriteInbound(Unpooled.WrappedBuffer(legacyRequest));
            Require(ReadResult(channels[0]) == RoomJoinResult.NicknameRequired, "Arena requires a named join");

            for (int index = 0; index < channels.Length; index++)
            {
                byte[] request = MatchCodec.EncodeJoinRequest(ARENA_ROOM_ID, $"Player {index + 1}");
                channels[index].WriteInbound(Unpooled.WrappedBuffer(request));
            }

            for (int index = 0; index < SPAWN_COUNT; index++)
            {
                Require(ReadResult(channels[index]) == RoomJoinResult.Success, "Arena accepts each reserved spawn slot");
            }
            Require(ReadResult(channels[^1]) == RoomJoinResult.WorldUnavailable, "Arena refuses admission beyond authored spawns");

            world.Tick(TICK_SECONDS);
            var actualSpawns = new HashSet<Vector3>();
            for (int index = 0; index < SPAWN_COUNT; index++)
            {
                JoinAck ack = PacketCodec.DecodeJoinAck(ReadFrame(channels[index], MessageType.JoinAck));
                var position = new Vector3(ack.X, ack.Y, ack.Z);
                Require(ack.MapId == WorldMapId.TeamDeathmatch && actualSpawns.Add(position), "Arena ack selects a distinct spawn");
            }
            Require(actualSpawns.SetEquals(spawns.Select(spawn => spawn.Position)), "Every spawn comes from Blender");

            for (int tick = 0; tick < 60; tick++)
            {
                world.Tick(TICK_SECONDS);
            }
            Require(world.GetPlayers().All(player => actualSpawns.Any(spawn =>
                Vector3.Distance(player.Position, spawn) < POSITION_TOLERANCE_METERS)), "Players stand on clear authored spawn markers");

            Require(rooms.Leave(handlers[0].Session!.SessionId), "Arena releases a departed player");
            world.Tick(TICK_SECONDS);
            channels[^1].WriteInbound(Unpooled.WrappedBuffer(MatchCodec.EncodeJoinRequest(ARENA_ROOM_ID, "Replacement")));
            Require(ReadResult(channels[^1]) == RoomJoinResult.Success, "Arena admits replacement after departure");
            world.Tick(TICK_SECONDS);
            JoinAck replacement = PacketCodec.DecodeJoinAck(ReadFrame(channels[^1], MessageType.JoinAck));
            Require(world.GetPlayers().Count(player => Vector3.Distance(player.Position,
                new Vector3(replacement.X, replacement.Y, replacement.Z)) < POSITION_TOLERANCE_METERS) == 1,
                "Replacement occupies a clear marker");
        }
        finally
        {
            foreach (EmbeddedChannel channel in channels)
            {
                channel.FinishAndReleaseAll();
            }
            world.Shutdown();
        }
    }

    private static void VerifyGeometry(GameWorld world)
    {
        using var physics = world.CreatePhysicsSimulation();
        var intoCover = physics.AddPlayer(new Vector3(0, 0, -4));
        var throughLane = physics.AddPlayer(new Vector3(8, 0, -4));
        var camera = new CameraRay(Vector3.UnitY, Vector3.UnitZ);
        var (target, distanceMeters, _) = physics.CastCameraRay(intoCover, camera);
        Require(target?.Mobility == CollidableMobility.Static && MathF.Abs(distanceMeters - 2.5f) < POSITION_TOLERANCE_METERS,
            "Server raycast hits the center cover mesh");

        for (int tick = 0; tick < 180; tick++)
        {
            physics.SetHorizontalVelocity(intoCover, 0, 2);
            physics.SetHorizontalVelocity(throughLane, 0, 2);
            physics.Step(TICK_SECONDS);
        }

        Vector3 coverPosition = physics.GetMotion(intoCover).FootPositionMeters;
        Vector3 lanePosition = physics.GetMotion(throughLane).FootPositionMeters;
        Require(coverPosition.Z is > -2 and < -1.5f, "Center cover blocks movement");
        Require(lanePosition.Z > 7 && MathF.Abs(lanePosition.Y) < POSITION_TOLERANCE_METERS, "Open side lane supports traversal");
    }

    private static void VerifyArch(GameWorld world)
    {
        using var physics = world.CreatePhysicsSimulation();
        var runner = physics.AddPlayer(new Vector3(EAST_ROUTE_X_METERS, 0, ARCH_APPROACH_Z_METERS));
        var receiver = physics.AddPlayer(new Vector3(EAST_ROUTE_X_METERS, 0, ARCH_REAR_Z_METERS + 0.38f));

        var openingRay = new CameraRay(Vector3.UnitY, Vector3.UnitZ);
        var (openingHit, _, _) = physics.CastCameraRay(runner, openingRay);
        Require(openingHit?.Mobility == CollidableMobility.Dynamic && openingHit.Value.BodyHandle == receiver,
            "Ground-level ray reaches a player on the far side of the arch");

        var capRay = new CameraRay(Vector3.UnitY * ARCH_CAP_RAY_HEIGHT_METERS, Vector3.UnitZ);
        var (capHit, _, capPoint) = physics.CastCameraRay(runner, capRay);
        Require(capHit?.Mobility == CollidableMobility.Static &&
            MathF.Abs(capPoint.Z - ARCH_FRONT_Z_METERS) < POSITION_TOLERANCE_METERS,
            $"Upper ray hits the curved arch instead of passing through it: {capPoint}");

        physics.RemovePlayer(receiver);
        Advance(physics, runner, 0, TRAVERSAL_SPEED_METERS_PER_SECOND, 32);
        Vector3 clear = physics.GetMotion(runner).FootPositionMeters;
        Require(clear.Z > ARCH_REAR_Z_METERS + PlayerPhysicsSimulation.PLAYER_DEPTH_METERS / 2 &&
            MathF.Abs(clear.X - EAST_ROUTE_X_METERS) < POSITION_TOLERANCE_METERS &&
            MathF.Abs(clear.Y) < POSITION_TOLERANCE_METERS,
            $"The grounded player clears the arch opening: {clear}");

        var jamb = physics.AddPlayer(new Vector3(ARCH_JAMB_X_METERS, 0, ARCH_APPROACH_Z_METERS));
        Advance(physics, jamb, 0, TRAVERSAL_SPEED_METERS_PER_SECOND, 32);
        Vector3 contact = physics.GetMotion(jamb).FootPositionMeters;
        float jambContactZ = ARCH_FRONT_Z_METERS - PlayerPhysicsSimulation.PLAYER_DEPTH_METERS / 2;
        Require(MathF.Abs(contact.Z - jambContactZ) < POSITION_TOLERANCE_METERS,
            $"The jamb blocks motion outside the opening: {contact}");

        var jumper = physics.AddPlayer(new Vector3(EAST_ROUTE_X_METERS, 0, (ARCH_FRONT_Z_METERS + ARCH_REAR_Z_METERS) / 2));
        physics.ApplyJumpImpulse(jumper, ServerOptions.DEFAULT_GRAVITY_METERS_PER_SECOND_SQUARED, TICK_SECONDS);
        float apexMeters = 0;
        for (int tick = 0; tick < 60; tick++)
        {
            physics.Step(TICK_SECONDS);
            apexMeters = MathF.Max(apexMeters, physics.GetMotion(jumper).FootPositionMeters.Y);
        }
        float centerClearanceMeters = ARCH_CROWN_HEIGHT_METERS - PlayerPhysicsSimulation.PLAYER_HEIGHT_METERS;
        Require(apexMeters > centerClearanceMeters - 0.2f && apexMeters < centerClearanceMeters + 0.05f &&
            MathF.Abs(physics.GetMotion(jumper).FootPositionMeters.Y) < POSITION_TOLERANCE_METERS &&
            physics.HasGroundSupport(jumper),
            $"The curved underside limits a jump and the player lands below the arch: apex {apexMeters:F3} m");
        Console.WriteLine($"PASS TeamDeathmatch arch: walk through, jamb contact, opening/cap rays, underside jump limit {apexMeters:F3} m");
    }

    private static void VerifyExpandedPerimeter(GameWorld world)
    {
        using var physics = world.CreatePhysicsSimulation();
        var start = new Vector3(14f, 0f, 17f);
        var runner = physics.AddPlayer(start);
        (Vector3 Direction, Vector3 WallPoint)[] rays =
        [
            (Vector3.UnitX, new Vector3(ARENA_HALF_WIDTH_METERS, 1f, start.Z)),
            (-Vector3.UnitX, new Vector3(-ARENA_HALF_WIDTH_METERS, 1f, start.Z)),
            (Vector3.UnitZ, new Vector3(start.X, 1f, ARENA_HALF_LENGTH_METERS)),
            (-Vector3.UnitZ, new Vector3(start.X, 1f, -ARENA_HALF_LENGTH_METERS))
        ];
        foreach ((Vector3 direction, Vector3 wallPoint) in rays)
        {
            var ray = new CameraRay(Vector3.UnitY, direction);
            var (target, _, point) = physics.CastCameraRay(runner, ray);
            Require(target?.Mobility == CollidableMobility.Static &&
                Vector3.Distance(point, wallPoint) < POSITION_TOLERANCE_METERS,
                $"The unobstructed outer lane reaches the expanded boundary at {wallPoint}: {point}");
        }

        Advance(physics, runner, 0f, -TRAVERSAL_SPEED_METERS_PER_SECOND, 180);
        Vector3 lane = physics.GetMotion(runner).FootPositionMeters;
        Require(lane.Z < start.Z - 11f && MathF.Abs(lane.Y) < POSITION_TOLERANCE_METERS &&
            MathF.Abs(lane.X - start.X) < POSITION_TOLERANCE_METERS,
            $"The eastern extension supports ground movement: {lane}");

        Advance(physics, runner, TRAVERSAL_SPEED_METERS_PER_SECOND, 0f, 60);
        Vector3 contact = physics.GetMotion(runner).FootPositionMeters;
        float contactX = ARENA_HALF_WIDTH_METERS - PlayerPhysicsSimulation.PLAYER_DEPTH_METERS / 2;
        Require(MathF.Abs(contact.X - contactX) < POSITION_TOLERANCE_METERS,
            $"The expanded east wall blocks movement: {contact}");
        Console.WriteLine("PASS 32 x 38 m arena perimeter, expanded outer lane and east wall contact");
    }

    private static void VerifyBridge(GameWorld world)
    {
        using var physics = world.CreatePhysicsSimulation();
        var walker = physics.AddPlayer(new Vector3(EAST_ROUTE_X_METERS, 0, BRIDGE_START_Z_METERS - 0.5f));
        Advance(physics, walker, 0, TRAVERSAL_SPEED_METERS_PER_SECOND, 36);
        Vector3 ramp = physics.GetMotion(walker).FootPositionMeters;
        Require(ramp.Z > -4.35f && ramp.Y is > 0.75f and < 1.65f && physics.HasGroundSupport(walker),
            $"Player climbs the ramp with continuous floor contact: {ramp}");

        Advance(physics, walker, 0, TRAVERSAL_SPEED_METERS_PER_SECOND, 66);
        Vector3 deck = physics.GetMotion(walker).FootPositionMeters;
        Require(deck.Z > 0 && MathF.Abs(deck.X - EAST_ROUTE_X_METERS) < POSITION_TOLERANCE_METERS &&
            MathF.Abs(deck.Y - BRIDGE_DECK_HEIGHT_METERS) < POSITION_TOLERANCE_METERS && physics.HasGroundSupport(walker),
            $"Player crosses the ramp seam onto the narrow deck: {deck}");

        Advance(physics, walker, 0, TRAVERSAL_SPEED_METERS_PER_SECOND, 127);
        Vector3 farEnd = physics.GetMotion(walker).FootPositionMeters;
        Require(farEnd.Z > BRIDGE_END_Z_METERS + 0.25f &&
            MathF.Abs(farEnd.Y) < POSITION_TOLERANCE_METERS && physics.HasGroundSupport(walker),
            $"Player crosses the bridge and descends to the far end: {farEnd}");

        var below = physics.AddPlayer(new Vector3(EAST_ROUTE_X_METERS, 0, 0));
        var undersideRay = new CameraRay(Vector3.UnitY, Vector3.UnitY);
        var (undersideHit, _, underside) = physics.CastCameraRay(below, undersideRay);
        Require(undersideHit?.Mobility == CollidableMobility.Static &&
            MathF.Abs(underside.Y - (BRIDGE_DECK_HEIGHT_METERS - BRIDGE_DECK_THICKNESS_METERS)) < POSITION_TOLERANCE_METERS,
            $"The bridge underside blocks the upward ray from the floor: {underside}");

        var alongEdge = physics.AddPlayer(new Vector3(EAST_ROUTE_X_METERS, BRIDGE_DECK_HEIGHT_METERS, 0));
        float edgeX = EAST_ROUTE_X_METERS + BRIDGE_HALF_WIDTH_METERS;
        Advance(physics, alongEdge, TRAVERSAL_SPEED_METERS_PER_SECOND, 0, 9);
        Advance(physics, alongEdge, 0, 0, 12);
        Vector3 partialSupport = physics.GetMotion(alongEdge).FootPositionMeters;
        Require(partialSupport.X > edgeX && MathF.Abs(partialSupport.Y - BRIDGE_DECK_HEIGHT_METERS) < POSITION_TOLERANCE_METERS &&
            physics.HasGroundSupport(alongEdge), $"The player's overhanging footprint still supports its weight: {partialSupport}");

        Advance(physics, alongEdge, TRAVERSAL_SPEED_METERS_PER_SECOND, 0, 15);
        var fall = physics.GetMotion(alongEdge);
        Require(fall.FootPositionMeters.X > edgeX + PlayerPhysicsSimulation.PLAYER_WIDTH_METERS / 2 &&
            fall.FootPositionMeters.Y is > 0.2f and < BRIDGE_DECK_HEIGHT_METERS - 0.1f &&
            fall.VerticalVelocityMetersPerSecond < -0.5f && !physics.HasGroundSupport(alongEdge),
            $"Walking off the open side produces a free fall: {fall}");

        Advance(physics, alongEdge, 0, 0, 60);
        var landing = physics.GetMotion(alongEdge);
        Require(MathF.Abs(landing.FootPositionMeters.Y) < POSITION_TOLERANCE_METERS &&
            MathF.Abs(landing.FootPositionMeters.X - fall.FootPositionMeters.X) < POSITION_TOLERANCE_METERS &&
            MathF.Abs(landing.VerticalVelocityMetersPerSecond) < POSITION_TOLERANCE_METERS &&
            physics.HasGroundSupport(alongEdge), $"Player lands on the floor after releasing sideways input: {landing}");
        Console.WriteLine($"PASS TeamDeathmatch bridge: uphill and downhill seams, underside, partial support, fall from {fall.FootPositionMeters} and floor landing");
    }

    private static void Advance(PlayerPhysicsSimulation physics, BodyHandle player,
        float xMetersPerSecond, float zMetersPerSecond, int ticks)
    {
        for (int tick = 0; tick < ticks; tick++)
        {
            physics.SetHorizontalVelocity(player, xMetersPerSecond, zMetersPerSecond);
            physics.Step(TICK_SECONDS);
        }
        physics.SetHorizontalVelocity(player, 0, 0);
    }

    private static RoomJoinResult ReadResult(EmbeddedChannel channel)
        => PacketCodec.DecodeJoinRoomResponse(ReadFrame(channel, MessageType.JoinRoomResponse)).Result;

    private static byte[] ReadFrame(EmbeddedChannel channel, MessageType expectedType)
    {
        channel.RunPendingTasks();
        IByteBuffer buffer = channel.ReadOutbound<IByteBuffer>() ?? throw new Exception($"Missing {expectedType} frame.");
        try
        {
            var frame = new byte[buffer.ReadableBytes];
            buffer.ReadBytes(frame);
            Require(frame[2] == (byte)expectedType, $"Expected {expectedType} frame");
            return frame[PacketCodec.HEADER_SIZE..];
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
            throw new Exception(message);
        }
    }
}
