using System.Numerics;
using BepuPhysics;
using DeathWish.Protocol;
using DeathWish.Server;
using DeathWish.Server.Physics;

internal static class TeamDeathmatchJumpTests
{
    private const float TICK_SECONDS = 1f / ServerOptions.DefaultTickRate;
    private const float LAND_POSITION_TOLERANCE_METERS = 0.02f;
    private const float LAND_VELOCITY_TOLERANCE_METERS_PER_SECOND = 0.1f;
    private const float RAISED_LANDING_METERS = 0.4f;
    private const float COURSE_X_METERS = -10.4f;
    private const float APPROACH_SPEED_METERS_PER_SECOND = 2.5f;
    private const float TARGET_POSITION_TOLERANCE_METERS = 0.005f;
    private const float TAKEOFF_MARGIN_METERS = 0.45f;
    private const float SOUTH_ENTRY_Z_METERS = -14.6f;
    private const float NORTH_ENTRY_Z_METERS = 14.6f;
    private const int RUN_UP_TICKS = 6;
    private const int MAX_FLIGHT_TICKS = ServerOptions.DefaultTickRate * 3;
    private const int MAX_APPROACH_TICKS = ServerOptions.DefaultTickRate * 5;
    private const int MAX_SETTLE_TICKS = ServerOptions.DefaultTickRate;

    private static readonly JumpPlatform[] Platforms =
    [
        new(-12, -10, 0.75f),
        new(-7.9f, -5.9f, 1),
        new(-2.9f, -0.9f, 1.35f),
        new(3.1f, 5.1f, 1.15f),
        new(9.75f, 11.75f, 1.4f)
    ];

    internal static void Run()
    {
        VerifyRunningJump(0);
        VerifyRunningJump(RAISED_LANDING_METERS);
        var arena = new GameWorld(1, mapId: WorldMapId.TeamDeathmatch);
        VerifyCourse(arena, Platforms, direction: 1);
        VerifyCourse(arena, Platforms.Reverse().ToArray(), direction: -1);
        VerifyMissedJumpFallsToFloor(arena);
    }

    private static void VerifyRunningJump(float landingHeightMeters)
    {
        var world = new GameWorld(1);
        IReadOnlyList<Collider> colliders = world.Colliders;
        if (landingHeightMeters > 0)
        {
            var raisedLanding = new BoxCollider(new Vector3(0, landingHeightMeters / 2, 8.5f),
                new Vector3(6f, landingHeightMeters, 10f));
            colliders = [.. colliders, raisedLanding];
        }

        using var physics = new PlayerPhysicsSimulation(colliders, ServerOptions.DEFAULT_GRAVITY_METERS_PER_SECOND_SQUARED);
        var player = physics.AddPlayer(Vector3.Zero);
        for (int tick = 0; tick < RUN_UP_TICKS; tick++)
        {
            physics.SetHorizontalVelocity(player, 0, GameWorld.MOVEMENT_SPEED_METERS_PER_SECOND);
            physics.Step(TICK_SECONDS);
        }

        Vector3 launch = physics.GetMotion(player).FootPositionMeters;
        Require(physics.HasGroundSupport(player), $"Running jump starts grounded: {launch}");
        physics.SetHorizontalVelocity(player, 0, GameWorld.MOVEMENT_SPEED_METERS_PER_SECOND);
        physics.ApplyJumpImpulse(player, ServerOptions.DEFAULT_GRAVITY_METERS_PER_SECOND_SQUARED, TICK_SECONDS);

        float apexMeters = 0;
        for (int ticks = 1; ticks <= MAX_FLIGHT_TICKS; ticks++)
        {
            physics.SetHorizontalVelocity(player, 0, GameWorld.MOVEMENT_SPEED_METERS_PER_SECOND);
            physics.Step(TICK_SECONDS);

            var (foot, verticalVelocityMetersPerSecond) = physics.GetMotion(player);
            apexMeters = MathF.Max(apexMeters, foot.Y - launch.Y);
            if (apexMeters > 0.5f && IsGrounded(physics, player, foot, verticalVelocityMetersPerSecond, landingHeightMeters))
            {
                float distanceMeters = foot.Z - launch.Z;
                Require(apexMeters is > 1.75f and < 1.85f && distanceMeters is > 5.3f and < 6.4f,
                    $"Expected default jump envelope at Y={landingHeightMeters:F2} m: range {distanceMeters:F3} m, apex {apexMeters:F3} m");
                Console.WriteLine($"PASS TeamDeathmatch running jump at +{landingHeightMeters:F2} m: range {distanceMeters:F3} m, apex {apexMeters:F3} m, airtime {ticks * TICK_SECONDS:F3} s ({ticks} ticks)");
                return;
            }
        }
        throw new Exception($"Running jump did not land at Y={landingHeightMeters:F2} m.");
    }

    private static void VerifyCourse(GameWorld arena, JumpPlatform[] route, int direction)
    {
        using var physics = arena.CreatePhysicsSimulation();
        float entryZ = direction > 0 ? SOUTH_ENTRY_Z_METERS : NORTH_ENTRY_Z_METERS;
        var player = physics.AddPlayer(new Vector3(COURSE_X_METERS, 0, entryZ));
        MoveTo(physics, player, route[0].CenterZ, direction);
        Require(SettleAtHeight(physics, player, route[0].HeightMeters),
            $"Player climbs the entry ramp to {route[0]}. {DescribeMotion(physics, player)}");

        for (int index = 1; index < route.Length; index++)
        {
            JumpPlatform current = route[index - 1];
            JumpPlatform destination = route[index];
            float leadingEdgeZ = direction > 0 ? current.MaxZ : current.MinZ;
            float takeoffZ = leadingEdgeZ - direction * TAKEOFF_MARGIN_METERS;
            MoveTo(physics, player, takeoffZ, direction);
            Require(IsGrounded(physics, player, current.HeightMeters), $"Jump {index} takes off from its source block.");
            JumpTo(physics, player, destination, direction);

            Vector3 landing = physics.GetMotion(player).FootPositionMeters;
            Require(landing.Z > destination.MinZ + 0.15f && landing.Z < destination.MaxZ - 0.15f &&
                MathF.Abs(landing.X - COURSE_X_METERS) < LAND_POSITION_TOLERANCE_METERS,
                $"Jump {index} lands inside its destination block at {landing}.");
        }

        float exitZ = direction > 0 ? NORTH_ENTRY_Z_METERS : SOUTH_ENTRY_Z_METERS;
        MoveTo(physics, player, exitZ, direction);
        Require(SettleAtHeight(physics, player, 0),
            $"Player descends the exit ramp toward Z={exitZ}. {DescribeMotion(physics, player)}");
        Console.WriteLine($"PASS TeamDeathmatch jump course: entry ramp, {route.Length - 1} successive grounded jumps and exit ramp toward Z={exitZ:F1} m");
    }

    private static void JumpTo(PlayerPhysicsSimulation physics, BodyHandle player, JumpPlatform destination, int direction)
    {
        Vector3 launch = physics.GetMotion(player).FootPositionMeters;
        physics.SetHorizontalVelocity(player, 0, direction * GameWorld.MOVEMENT_SPEED_METERS_PER_SECOND);
        physics.ApplyJumpImpulse(player, ServerOptions.DEFAULT_GRAVITY_METERS_PER_SECOND_SQUARED, TICK_SECONDS);

        bool isAirborne = false;
        for (int tick = 0; tick < MAX_FLIGHT_TICKS; tick++)
        {
            Vector3 oldFoot = physics.GetMotion(player).FootPositionMeters;
            // Releasing directional input in midair keeps the landing centered on the shorter jumps.
            float remainingZ = (destination.CenterZ - oldFoot.Z) * direction;
            float speed = Math.Clamp(remainingZ / TICK_SECONDS, 0, GameWorld.MOVEMENT_SPEED_METERS_PER_SECOND);
            physics.SetHorizontalVelocity(player, 0, speed * direction);
            physics.Step(TICK_SECONDS);

            var (foot, verticalVelocityMetersPerSecond) = physics.GetMotion(player);
            isAirborne |= foot.Y > launch.Y + 0.5f;
            if (isAirborne && IsGrounded(physics, player, foot, verticalVelocityMetersPerSecond, destination.HeightMeters))
            {
                physics.SetHorizontalVelocity(player, 0, 0);
                return;
            }
        }
        throw new Exception($"Jump from {launch} missed {destination}; final foot position {physics.GetMotion(player).FootPositionMeters}.");
    }

    private static void VerifyMissedJumpFallsToFloor(GameWorld arena)
    {
        using var physics = arena.CreatePhysicsSimulation();
        JumpPlatform source = Platforms[0];
        JumpPlatform destination = Platforms[1];
        var player = physics.AddPlayer(new Vector3(COURSE_X_METERS, source.HeightMeters, source.MaxZ - TAKEOFF_MARGIN_METERS));
        float gapCenterZ = (source.MaxZ + destination.MinZ) / 2;
        MoveTo(physics, player, gapCenterZ, direction: 1);
        var (foot, verticalVelocityMetersPerSecond) = physics.GetMotion(player);
        Require(!physics.HasGroundSupport(player) && foot.Y < source.HeightMeters && verticalVelocityMetersPerSecond < 0,
            $"Walking off the source block without jumping enters a free fall: {foot}.");
        Require(SettleAtHeight(physics, player, 0), $"A missed jump lands safely on the arena floor. {DescribeMotion(physics, player)}");
        Console.WriteLine("PASS TeamDeathmatch jump course: walking into the open gap falls to a supported floor landing");
    }

    private static bool SettleAtHeight(PlayerPhysicsSimulation physics, BodyHandle player, float heightMeters)
    {
        physics.SetHorizontalVelocity(player, 0, 0);
        for (int tick = 0; tick < MAX_SETTLE_TICKS; tick++)
        {
            if (IsGrounded(physics, player, heightMeters))
            {
                return true;
            }
            physics.Step(TICK_SECONDS);
        }
        return false;
    }

    private static string DescribeMotion(PlayerPhysicsSimulation physics, BodyHandle player)
    {
        var (foot, verticalVelocityMetersPerSecond) = physics.GetMotion(player);
        return $"Foot {foot}, vertical velocity {verticalVelocityMetersPerSecond:F3} m/s, support {physics.HasGroundSupport(player)}.";
    }

    private static void MoveTo(PlayerPhysicsSimulation physics, BodyHandle player, float targetZ, int direction)
    {
        for (int tick = 0; tick < MAX_APPROACH_TICKS; tick++)
        {
            float remainingZ = (targetZ - physics.GetMotion(player).FootPositionMeters.Z) * direction;
            if (remainingZ < TARGET_POSITION_TOLERANCE_METERS)
            {
                physics.SetHorizontalVelocity(player, 0, 0);
                return;
            }
            float speed = MathF.Min(remainingZ / TICK_SECONDS, APPROACH_SPEED_METERS_PER_SECOND);
            physics.SetHorizontalVelocity(player, 0, speed * direction);
            physics.Step(TICK_SECONDS);
        }
        throw new Exception($"Player did not reach Z={targetZ:F2} m. Current position is {physics.GetMotion(player).FootPositionMeters}.");
    }

    private static bool IsGrounded(PlayerPhysicsSimulation physics, BodyHandle player, float heightMeters)
    {
        var (foot, verticalVelocityMetersPerSecond) = physics.GetMotion(player);
        return IsGrounded(physics, player, foot, verticalVelocityMetersPerSecond, heightMeters);
    }

    private static bool IsGrounded(PlayerPhysicsSimulation physics, BodyHandle player, Vector3 foot,
        float verticalVelocityMetersPerSecond, float heightMeters) =>
        MathF.Abs(foot.Y - heightMeters) < LAND_POSITION_TOLERANCE_METERS &&
        MathF.Abs(verticalVelocityMetersPerSecond) < LAND_VELOCITY_TOLERANCE_METERS_PER_SECOND &&
        physics.HasGroundSupport(player);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private readonly record struct JumpPlatform(float MinZ, float MaxZ, float HeightMeters)
    {
        internal float CenterZ => (MinZ + MaxZ) / 2;
    }
}
