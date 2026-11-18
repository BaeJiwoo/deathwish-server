using System.Numerics;
using DeathWish.Server;
using DeathWish.Server.Physics;

internal static class GravityTests
{
    internal static void Run()
    {
        var player = new PlayerState(1, Guid.NewGuid(), new Vector3(2, 10, 3));
        PlayerState first = Gravity.Apply(player, 8, 0.25f);
        PlayerState second = Gravity.Apply(first, 8, 0.25f);
        PlayerState coasting = Gravity.Apply(second, 0, 0.25f);

        if (first != (player with { Position = new Vector3(2, 9.5f, 3), VerticalVelocityMetersPerSecond = -2 }) ||
            second != (player with { Position = new Vector3(2, 8.5f, 3), VerticalVelocityMetersPerSecond = -4 }) ||
            coasting != (second with { Position = new Vector3(2, 7.5f, 3) }))
        {
            throw new Exception("Expected velocity-first free fall and constant velocity at zero gravity.");
        }

        Console.WriteLine("PASS gravity updates vertical velocity before height and preserves horizontal position");
    }
}
