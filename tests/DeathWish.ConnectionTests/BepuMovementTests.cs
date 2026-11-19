using System.Numerics;
using DeathWish.Server;
using DeathWish.Server.Physics;

internal static class BepuMovementTests
{
    private const float STEP_SECONDS = 1f / 30;
    private const float POSITION_TOLERANCE_METERS = 0.01f;

    internal static void Run()
    {
        var world = new GameWorld(1);
        using var physics = world.CreatePhysicsSimulation();
        var walker = physics.AddPlayer(new Vector3(45, 0, 0));
        var falling = physics.AddPlayer(new Vector3(1, 4, 0), -200);
        var edge = physics.AddPlayer(new Vector3(50.2f, 0, 0));
        var unsupported = physics.AddPlayer(new Vector3(50.5f, 0, 0));
        for (int tick = 0; tick < 60; tick++)
        {
            physics.Step(STEP_SECONDS);
            float fallHeight = physics.GetMotion(falling).FootPositionMeters.Y;
            Require(fallHeight >= -POSITION_TOLERANCE_METERS, $"Fast fall penetrated the floor at tick {tick}, Y={fallHeight}");
            Require(MathF.Abs(physics.GetMotion(edge).FootPositionMeters.Y) < POSITION_TOLERANCE_METERS,
                "A partly supported player remains upright on the edge");
        }
        Require(MathF.Abs(physics.GetMotion(falling).FootPositionMeters.Y) < POSITION_TOLERANCE_METERS,
            "Fast fall settles at the floor surface");
        Require(physics.GetMotion(unsupported).FootPositionMeters.Y < -1, "The finite floor allows unsupported falls");

        physics.SetHorizontalVelocity(walker, GameWorld.MOVEMENT_SPEED_METERS_PER_SECOND, 0);
        for (int tick = 0; tick < 36; tick++)
        {
            physics.Step(STEP_SECONDS);
        }
        Vector3 walked = physics.GetMotion(walker).FootPositionMeters;
        Require(MathF.Abs(walked.X - 51) < POSITION_TOLERANCE_METERS && walked.Y < 0, "Walk across the floor and off its edge");
        physics.SetHorizontalVelocity(walker, 0, 0);
        physics.Step(STEP_SECONDS);
        Vector3 stopped = physics.GetMotion(walker).FootPositionMeters;
        Require(MathF.Abs(stopped.X - walked.X) < POSITION_TOLERANCE_METERS && stopped.Y < walked.Y,
            "Releasing horizontal input preserves falling velocity");
        physics.RemovePlayer(walker);

        using var weightless = new GameWorld(2, gravityMetersPerSecondSquared: 0).CreatePhysicsSimulation();
        var floating = weightless.AddPlayer(new Vector3(0, 3, 0));
        weightless.Step(STEP_SECONDS);
        Require(weightless.GetMotion(floating).FootPositionMeters.Y == 3, "Zero gravity preserves height");
        Console.WriteLine("PASS Bepu server movement, high-speed landing, partial edge support, finite floor and zero gravity");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
