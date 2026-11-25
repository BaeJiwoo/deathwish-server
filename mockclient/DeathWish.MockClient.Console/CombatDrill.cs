using System.Numerics;
using DeathWish.Protocol;

internal static class CombatDrill
{
    internal const int ROUTE_NUMBER = 4;
    private const float FIRST_POST_X_METERS = 7.15f;
    private const float POST_SPACING_METERS = 0.95f;
    private const float POST_Z_METERS = 3.8f;
    private const float ZONE_MIN_X_METERS = 6.6f;
    private const float ZONE_MAX_X_METERS = 11.45f;
    private const float ZONE_ABS_Z_METERS = 4.25f;
    private const float ZONE_GROUND_HEIGHT_METERS = 0.35f;
    private const float AIM_HEIGHT_METERS = 1.24f;

    internal static Vector2 GetPost(TeamId team, int slot)
    {
        float zMeters = team == TeamId.Red ? -POST_Z_METERS : POST_Z_METERS;
        return new Vector2(FIRST_POST_X_METERS + slot * POST_SPACING_METERS, zMeters);
    }

    internal static bool Contains(Vector3 position) =>
        position.X is >= ZONE_MIN_X_METERS and <= ZONE_MAX_X_METERS &&
        MathF.Abs(position.Z) <= ZONE_ABS_Z_METERS && MathF.Abs(position.Y) <= ZONE_GROUND_HEIGHT_METERS;

    internal static CameraRay? AimAtClosestEnemy(Vector3 foot, TeamId team, MatchState match, WorldSnapshot snapshot)
    {
        float nearestDistanceSquared = float.PositiveInfinity;
        Vector3? closestOffset = null;
        foreach (SnapshotPlayer player in snapshot.Players)
        {
            var position = new Vector3(player.X, player.Y, player.Z);
            Vector3 offset = position - foot;
            float distanceSquared = offset.LengthSquared();
            if (distanceSquared <= 0 || distanceSquared >= nearestDistanceSquared || !Contains(position))
            {
                continue;
            }
            if (!match.Players.Any(entry => entry.PlayerId == player.PlayerId && entry.Team != team && entry.Health > 0))
            {
                continue;
            }

            nearestDistanceSquared = distanceSquared;
            closestOffset = offset;
        }

        if (closestOffset is not Vector3 aim)
        {
            return null;
        }
        return new CameraRay(Vector3.UnitY * AIM_HEIGHT_METERS, Vector3.Normalize(aim));
    }
}
