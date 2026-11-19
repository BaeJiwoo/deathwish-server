using System.Numerics;
using DeathWish.Protocol;
using DeathWish.Server;
using DeathWish.Server.Physics;
using DotNetty.Transport.Channels.Embedded;

internal static class PhysicsLabTests
{
    private const float TICK_SECONDS = 1f / 30;
    private const float LANDING_TOLERANCE_METERS = 0.015f;

    internal static void Run()
    {
        var sessions = new SessionManager();
        const int ROOM_COUNT = 5;
        var rooms = new RoomManager(sessions, ROOM_COUNT, 4, 4);
        GameWorld[] worlds = rooms.GetWorlds();
        GameWorld lab = worlds[^1];
        Require(worlds.Take(ROOM_COUNT - 1).All(world => world.Colliders.Count == 1 && world.Colliders[0] is PlaneCollider), "Flat room selection");
        Require(lab.Colliders.Count == 7 && lab.Colliders.All(collider => collider is StaticMeshCollider),
            "Seven static mesh objects and no fallback floor");

        var handler = new GameChannelHandler(sessions, rooms);
        var channel = new EmbeddedChannel(handler);
        try
        {
            Require(rooms.Join(handler.Session!.SessionId, ROOM_COUNT) == RoomJoinResult.Success, "Join lab room");
            for (int tick = 0; tick < 60; tick++)
            {
                lab.Tick(TICK_SECONDS);
                Require(lab.GetPlayers().Single().Position.Length() < LANDING_TOLERANCE_METERS,
                    "Spawn stays at origin without penetration recovery drift");
            }

            VerifyMeshContacts(lab);
        }
        finally
        {
            channel.FinishAndReleaseAll();
            lab.Shutdown();
        }
        Console.WriteLine("PASS selected PhysicsLab world: admission, floor, platform, opening, ring passage and rim collision");
    }

    private static void VerifyMeshContacts(GameWorld lab)
    {
        using var physics = lab.CreatePhysicsSimulation();
        var floor = physics.AddPlayer(new Vector3(10, 4, 5), -100);
        var platform = physics.AddPlayer(new Vector3(-12, 9, -18));
        var opening = physics.AddPlayer(new Vector3(-17, 9, -18));
        var throughRing = physics.AddPlayer(new Vector3(0, 0, -3));
        var intoRim = physics.AddPlayer(new Vector3(3.6f, 0, -3));
        for (int tick = 0; tick < 90; tick++)
        {
            physics.SetHorizontalVelocity(throughRing, 0, 2);
            physics.SetHorizontalVelocity(intoRim, 0, 2);
            physics.Step(TICK_SECONDS);
            Require(physics.GetMotion(floor).FootPositionMeters.Y >= -LANDING_TOLERANCE_METERS, "Fast floor landing");
            Require(physics.GetMotion(platform).FootPositionMeters.Y >= 6 - LANDING_TOLERANCE_METERS, "Platform holds player");
            Require(physics.GetMotion(intoRim).FootPositionMeters.Z < 0, "Front rim blocks passage");
        }
        float platformHeight = physics.GetMotion(platform).FootPositionMeters.Y;
        float openingHeight = physics.GetMotion(opening).FootPositionMeters.Y;
        float rimZ = physics.GetMotion(intoRim).FootPositionMeters.Z;
        Require(MathF.Abs(physics.GetMotion(floor).FootPositionMeters.Y) < LANDING_TOLERANCE_METERS, "Floor Y = 0");
        Require(MathF.Abs(platformHeight - 6) < LANDING_TOLERANCE_METERS, $"Platform height {platformHeight}");
        Require(MathF.Abs(openingHeight) < LANDING_TOLERANCE_METERS, $"Drop through the hole to the floor: {openingHeight}");
        Require(physics.GetMotion(throughRing).FootPositionMeters.Z > 2.9f, "Ring opening traversable");
        Require(rimZ > -2 && rimZ < 0, $"Player reached and was stopped by the ring: {rimZ}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
