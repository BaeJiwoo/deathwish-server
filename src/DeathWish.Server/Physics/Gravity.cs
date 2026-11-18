namespace DeathWish.Server.Physics;

internal static class Gravity
{
    /// <summary>Advances free fall by one tick using the downward acceleration magnitude and the new velocity.</summary>
    internal static PlayerState Apply(PlayerState player, float accelerationMetersPerSecondSquared, float deltaTimeSeconds)
    {
        float velocityMetersPerSecond = player.VerticalVelocityMetersPerSecond - accelerationMetersPerSecondSquared * deltaTimeSeconds;
        var position = player.Position;
        position.Y += velocityMetersPerSecond * deltaTimeSeconds;

        return player with { Position = position, VerticalVelocityMetersPerSecond = velocityMetersPerSecond };
    }
}
