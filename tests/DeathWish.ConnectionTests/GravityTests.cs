using System.Numerics;
using DeathWish.Server;

internal static class GravityTests
{
    internal static void Run()
    {
        var rooms = new RoomManager(new SessionManager(), 1, 1, 1, gravityMetersPerSecondSquared: 8);
        using var physics = rooms.GetWorlds().Single().CreatePhysicsSimulation();
        var player = physics.AddPlayer(new Vector3(2, 10, 3));
        physics.Step(0.25f);
        var first = physics.GetMotion(player);
        physics.Step(0.25f);
        var second = physics.GetMotion(player);

        using var weightless = new GameWorld(2, gravityMetersPerSecondSquared: 0).CreatePhysicsSimulation();
        var drifting = weightless.AddPlayer(second.FootPositionMeters, second.VerticalVelocityMetersPerSecond);
        weightless.Step(0.25f);
        var coasting = weightless.GetMotion(drifting);
        const float POSITION_TOLERANCE_METERS = 0.00001f;
        if (Vector3.Distance(first.FootPositionMeters, new Vector3(2, 9.5f, 3)) > POSITION_TOLERANCE_METERS ||
            Vector3.Distance(second.FootPositionMeters, new Vector3(2, 8.5f, 3)) > POSITION_TOLERANCE_METERS ||
            Vector3.Distance(coasting.FootPositionMeters, new Vector3(2, 7.5f, 3)) > POSITION_TOLERANCE_METERS ||
            first.VerticalVelocityMetersPerSecond != -2 || second.VerticalVelocityMetersPerSecond != -4 ||
            coasting.VerticalVelocityMetersPerSecond != -4)
        {
            throw new Exception($"Unexpected free fall or coasting. First {first}, second {second}, coasting {coasting}.");
        }

        Console.WriteLine("PASS configured room gravity and coasting at zero gravity through Bepu");
    }
}
