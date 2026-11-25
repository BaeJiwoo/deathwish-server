using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using BepuUtilities.Memory;
using DeathWish.Protocol;

namespace DeathWish.Server.Physics;

/// <summary>Room-local simulation. Its owner must use it on one world thread and dispose it after the last step.</summary>
internal sealed class PlayerPhysicsSimulation : IDisposable
{
    internal const float PLAYER_DIAMETER_METERS = 0.65f;
    internal const float PLAYER_WIDTH_METERS = PLAYER_DIAMETER_METERS;
    internal const float PLAYER_HEIGHT_METERS = 1.8f;
    internal const float PLAYER_DEPTH_METERS = PLAYER_DIAMETER_METERS;
    private const float PLAYER_RADIUS_METERS = PLAYER_DIAMETER_METERS / 2;
    private const float PLAYER_CENTER_HEIGHT_METERS = PLAYER_HEIGHT_METERS / 2;
    private const int SOLVER_ITERATIONS = 8;
    private const int SUBSTEPS = 4;
    private const float DISABLED_SLEEP_THRESHOLD = -1;

    internal const float RAY_RANGE_METERS = 100f;
    private const float GROUND_PROBE_START_METERS = 0.12f;
    private const float GROUND_TOLERANCE_METERS = 0.035f;
    private const float MIN_SUPPORT_NORMAL_Y = 0.6f;
    private const float MAX_SLOPE_ASSIST_VERTICAL_SPEED_METERS_PER_SECOND = 1f;
    private const float MAX_SLOPE_PROBE_RISE_METERS = 0.35f;
    private const float SLOPE_CLEARANCE_METERS = 0.002f;
    private const float SLOPE_STEP_TOLERANCE_METERS = 0.015f;
    private const float MIN_SLOPE_DESCENT_SPEED_METERS_PER_SECOND = 0.05f;
    private const float MAX_SLOPE_POP_METERS = 0.12f;
    private const float SLOPE_NORMAL_ALIGNMENT = 0.999f;
    private const float SOLE_PROBE_RADIUS_METERS = PLAYER_RADIUS_METERS - 0.005f;
    private const float SOLE_PROBE_DIAGONAL_METERS = SOLE_PROBE_RADIUS_METERS * 0.70710678f;
    private const float SHOT_ORIGIN_HEIGHT_METERS = 1.24f;
    private const float SHOT_SURFACE_ALLOWANCE_METERS = 0.02f;

    private static readonly Vector3[] SupportOffsets =
    [
        Vector3.Zero,
        new(-SOLE_PROBE_RADIUS_METERS, 0, 0), new(SOLE_PROBE_RADIUS_METERS, 0, 0),
        new(0, 0, -SOLE_PROBE_RADIUS_METERS), new(0, 0, SOLE_PROBE_RADIUS_METERS),
        new(-SOLE_PROBE_DIAGONAL_METERS, 0, -SOLE_PROBE_DIAGONAL_METERS),
        new(-SOLE_PROBE_DIAGONAL_METERS, 0, SOLE_PROBE_DIAGONAL_METERS),
        new(SOLE_PROBE_DIAGONAL_METERS, 0, -SOLE_PROBE_DIAGONAL_METERS),
        new(SOLE_PROBE_DIAGONAL_METERS, 0, SOLE_PROBE_DIAGONAL_METERS)
    ];

    private readonly BufferPool _pool = new();
    private readonly Simulation _simulation;
    private readonly TypedIndex _playerShape;
    private readonly List<SlopeDescent> _slopeDescents = new();

    public PlayerPhysicsSimulation(IReadOnlyList<Collider> staticColliders, float gravityMetersPerSecondSquared)
    {
        _simulation = Simulation.Create(_pool, new PlayerContactCallbacks(),
            new GravityIntegrator(gravityMetersPerSecondSquared), new SolveDescription(SOLVER_ITERATIONS, SUBSTEPS));
        _playerShape = _simulation.Shapes.Add(new Cylinder(PLAYER_RADIUS_METERS, PLAYER_HEIGHT_METERS));

        foreach (Collider collider in staticColliders)
        {
            collider.RegisterStatic(_simulation);
        }
    }

    public BodyHandle AddPlayer(Vector3 footPositionMeters, float verticalVelocityMetersPerSecond = 0)
    {
        Vector3 center = footPositionMeters + Vector3.UnitY * PLAYER_CENTER_HEIGHT_METERS;
        // Zero inverse angular inertia keeps the player upright even with partial support.
        var inertia = new BodyInertia { InverseMass = 1 };
        var collidable = new CollidableDescription(_playerShape, ContinuousDetection.Continuous());
        return _simulation.Bodies.Add(BodyDescription.CreateDynamic(center,
            new BodyVelocity(Vector3.UnitY * verticalVelocityMetersPerSecond), inertia, collidable, DISABLED_SLEEP_THRESHOLD));
    }

    public void RemovePlayer(BodyHandle handle)
    {
        _simulation.Bodies.Remove(handle);
    }

    public void SetHorizontalVelocity(BodyHandle handle, float xMetersPerSecond, float zMetersPerSecond)
    {
        BodyReference body = _simulation.Bodies.GetBodyReference(handle);
        body.Velocity.Linear.X = xMetersPerSecond;
        body.Velocity.Linear.Z = zMetersPerSecond;
    }

    /// <summary>Checks support below the player's feet. Called by the world loop.</summary>
    internal bool HasGroundSupport(BodyHandle player) => FindGroundSupport(player, GetMotion(player).FootPositionMeters).Hit.HasValue;

    private ClosestHit FindGroundSupport(BodyHandle player, Vector3 foot)
    {
        foreach (Vector3 offset in SupportOffsets)
        {
            ClosestHit support = ProbeGround(player, foot + offset);
            if (support.Hit.HasValue)
            {
                return support;
            }
        }
        return default;
    }

    /// <summary>Applies the jump impulse to a supported player. Called by the world loop.</summary>
    internal void ApplyJumpImpulse(BodyHandle player, float gravityMetersPerSecondSquared, float deltaTimeSeconds)
    {
        BodyReference body = _simulation.Bodies.GetBodyReference(player);
        // Account for the first velocity integration in the discrete ballistic trajectory.
        float launchSpeedMetersPerSecond = MathF.Sqrt(2 * gravityMetersPerSecondSquared * PLAYER_HEIGHT_METERS);
        float deltaSpeedMetersPerSecond = launchSpeedMetersPerSecond +
            gravityMetersPerSecondSquared * deltaTimeSeconds / 2 - body.Velocity.Linear.Y;
        body.ApplyLinearImpulse(Vector3.UnitY * (deltaSpeedMetersPerSecond / body.LocalInertia.InverseMass));
    }

    /// <summary>Casts from the player's feet plus the camera offset. Called by the world loop.</summary>
    internal (CollidableReference? Target, float DistanceMeters, Vector3 PointMeters) CastCameraRay(
        BodyHandle player, CameraRay ray)
    {
        Vector3 origin = GetMotion(player).FootPositionMeters + ray.Offset;
        return CastRay(player, origin, ray.Direction, RAY_RANGE_METERS);
    }

    /// <summary>Uses the camera for aim and the server body's shot origin for collision. Called by the world loop.</summary>
    internal ArenaShotTrace CastArenaShot(BodyHandle player, CameraRay ray)
    {
        Vector3 foot = GetMotion(player).FootPositionMeters;
        Vector3 shotOrigin = foot + Vector3.UnitY * SHOT_ORIGIN_HEIGHT_METERS;
        Vector3 cameraOrigin = foot + ray.Offset;
        Vector3 cameraDelta = cameraOrigin - shotOrigin;
        float cameraDistanceMeters = cameraDelta.Length();
        if (cameraDistanceMeters > 0f)
        {
            var occlusion = new ClosestHit(player, groundOnly: false, terrainOnly: true);
            _simulation.RayCast(shotOrigin, cameraDelta / cameraDistanceMeters, cameraDistanceMeters, ref occlusion);
            if (occlusion.Hit.HasValue)
            {
                // There is no accepted line of fire when the camera cannot see the body origin.
                return new ArenaShotTrace(shotOrigin, shotOrigin, null, 0f);
            }
        }

        var (cameraTarget, _, cameraPoint) = CastRay(player, cameraOrigin, ray.Direction, RAY_RANGE_METERS);
        Vector3 aimPoint = cameraTarget.HasValue ? cameraPoint : cameraOrigin + ray.Direction * RAY_RANGE_METERS;
        Vector3 shotDelta = aimPoint - shotOrigin;
        float aimDistanceMeters = shotDelta.Length();
        if (aimDistanceMeters <= 0f)
        {
            return new ArenaShotTrace(shotOrigin, shotOrigin, null, 0f);
        }

        // Allow the authoritative ray to cross the surface located by the camera ray.
        float shotRangeMeters = MathF.Min(aimDistanceMeters + SHOT_SURFACE_ALLOWANCE_METERS, RAY_RANGE_METERS);
        Vector3 shotDirection = shotDelta / aimDistanceMeters;
        var (target, distanceMeters, pointMeters) = CastRay(player, shotOrigin, shotDirection, shotRangeMeters);
        Vector3 endMeters = target.HasValue ? pointMeters : shotOrigin + shotDirection * shotRangeMeters;
        return new ArenaShotTrace(shotOrigin, endMeters, target, distanceMeters);
    }

    internal readonly record struct ArenaShotTrace(Vector3 OriginMeters, Vector3 EndMeters, CollidableReference? Target,
        float DistanceMeters);

    private (CollidableReference? Target, float DistanceMeters, Vector3 PointMeters) CastRay(
        BodyHandle player, Vector3 origin, Vector3 direction, float rangeMeters)
    {
        var query = new ClosestHit(player, groundOnly: false);
        _simulation.RayCast(origin, direction, rangeMeters, ref query);
        return (query.Hit, query.DistanceMeters, origin + direction * query.DistanceMeters);
    }

    private struct ClosestHit(BodyHandle player, bool groundOnly, bool terrainOnly = false) : IRayHitHandler
    {
        internal CollidableReference? Hit;
        internal float DistanceMeters;
        internal Vector3 Normal;

        public bool AllowTest(CollidableReference collidable) =>
            collidable.Mobility == CollidableMobility.Static ||
            (!groundOnly && !terrainOnly && collidable.BodyHandle != player);

        public bool AllowTest(CollidableReference collidable, int childIndex) => AllowTest(collidable);

        public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal,
            CollidableReference collidable, int childIndex)
        {
            if (groundOnly && normal.Y < MIN_SUPPORT_NORMAL_Y)
            {
                return;
            }
            maximumT = t;
            DistanceMeters = t;
            Normal = normal;
            Hit = collidable;
        }
    }

    public void Step(float deltaTimeSeconds)
    {
        ref BodySet activeBodies = ref _simulation.Bodies.ActiveSet;
        for (int index = 0; index < activeBodies.Count; index++)
        {
            BodyHandle player = activeBodies.IndexToHandle[index];
            var body = _simulation.Bodies.GetBodyReference(player);
            Vector3 velocity = body.Velocity.Linear;
            var horizontal = new Vector3(velocity.X, 0, velocity.Z);
            if (horizontal == Vector3.Zero)
            {
                continue;
            }

            Vector3 foot = body.Pose.Position - Vector3.UnitY * PLAYER_CENTER_HEIGHT_METERS;
            AssistSlopeAscent(body, foot, horizontal, velocity.Y, deltaTimeSeconds);
            TrackSlopeDescent(player, foot, horizontal, velocity.Y);
        }
        _simulation.Timestep(deltaTimeSeconds);

        foreach (SlopeDescent descent in _slopeDescents)
        {
            CorrectSlopeDescent(descent);
        }
        _slopeDescents.Clear();
    }

    private void AssistSlopeAscent(BodyReference body, Vector3 foot, Vector3 horizontal, float verticalMetersPerSecond,
        float deltaTimeSeconds)
    {
        if (MathF.Abs(verticalMetersPerSecond) > MAX_SLOPE_ASSIST_VERTICAL_SPEED_METERS_PER_SECOND)
        {
            return;
        }

        // A cylinder's flat rim can stop on a rising triangle before the solver resolves its upper face.
        float lookAheadSeconds = PLAYER_RADIUS_METERS / horizontal.Length() + deltaTimeSeconds;
        ClosestHit ahead = ProbeGround(body.Handle, foot + horizontal * lookAheadSeconds, MAX_SLOPE_PROBE_RISE_METERS);
        if (!ahead.Hit.HasValue)
        {
            return;
        }

        float rampSpeedMetersPerSecond = -Vector3.Dot(ahead.Normal, horizontal) / ahead.Normal.Y;
        float riseMeters = MAX_SLOPE_PROBE_RISE_METERS - ahead.DistanceMeters;
        float maximumRiseMeters = rampSpeedMetersPerSecond * deltaTimeSeconds + SLOPE_STEP_TOLERANCE_METERS;
        if (rampSpeedMetersPerSecond > 0 && riseMeters > 0 && riseMeters <= maximumRiseMeters &&
            FindGroundSupport(body.Handle, foot).Hit.HasValue)
        {
            // Lift before CCD sees the rim at the seam. Gravity still integrates the vertical velocity.
            body.Pose.Position.Y += riseMeters + SLOPE_CLEARANCE_METERS;
        }
    }

    private void TrackSlopeDescent(BodyHandle player, Vector3 foot, Vector3 horizontal, float verticalMetersPerSecond)
    {
        if (verticalMetersPerSecond > -MIN_SLOPE_DESCENT_SPEED_METERS_PER_SECOND)
        {
            return;
        }

        ClosestHit support = FindGroundSupport(player, foot);
        float downwardComponent = Vector3.Dot(support.Normal, horizontal);
        if (support.Hit is not CollidableReference surface || downwardComponent <= 0)
        {
            return;
        }

        float descentMetersPerSecond = downwardComponent / support.Normal.Y;
        if (verticalMetersPerSecond < -descentMetersPerSecond - MAX_SLOPE_ASSIST_VERTICAL_SPEED_METERS_PER_SECOND)
        {
            return;
        }

        var normalXZ = new Vector3(support.Normal.X, 0, support.Normal.Z);
        Vector3 uphillRimOffset = -Vector3.Normalize(normalXZ) * PLAYER_RADIUS_METERS;
        Vector3 rim = foot + uphillRimOffset;
        ClosestHit rimSupport = ProbeGround(player, rim, SLOPE_STEP_TOLERANCE_METERS);
        if (IsSameSlope(rimSupport, surface.StaticHandle, support.Normal))
        {
            rim.Y += SLOPE_STEP_TOLERANCE_METERS - rimSupport.DistanceMeters;
            _slopeDescents.Add(new SlopeDescent(player, surface.StaticHandle, support.Normal, rim, uphillRimOffset));
        }
    }

    private void CorrectSlopeDescent(SlopeDescent descent)
    {
        var body = _simulation.Bodies.GetBodyReference(descent.Player);
        if (body.Velocity.Linear.Y <= 0)
        {
            return;
        }

        Vector3 foot = body.Pose.Position - Vector3.UnitY * PLAYER_CENTER_HEIGHT_METERS;
        Vector3 rim = foot + descent.UphillRimOffsetMeters;
        var offset = new Vector3(rim.X - descent.PriorRimMeters.X, 0, rim.Z - descent.PriorRimMeters.Z);
        rim.Y = descent.PriorRimMeters.Y - Vector3.Dot(offset, descent.Normal) / descent.Normal.Y;
        float liftMeters = foot.Y - rim.Y;
        if (liftMeters <= SLOPE_CLEARANCE_METERS || liftMeters > MAX_SLOPE_POP_METERS)
        {
            return;
        }

        ClosestHit support = ProbeGround(descent.Player, rim, SLOPE_STEP_TOLERANCE_METERS);
        if (!IsSameSlope(support, descent.Surface, descent.Normal))
        {
            return;
        }

        // A shared mesh edge can kick a cylinder upward. Correct only on the same supporting ramp face.
        float groundHeightMeters = rim.Y + SLOPE_STEP_TOLERANCE_METERS - support.DistanceMeters;
        if (MathF.Abs(groundHeightMeters - rim.Y) > SLOPE_STEP_TOLERANCE_METERS ||
            foot.Y - groundHeightMeters is <= 0 or > MAX_SLOPE_POP_METERS)
        {
            return;
        }

        body.Pose.Position.Y = groundHeightMeters + PLAYER_CENTER_HEIGHT_METERS;
        var horizontal = new Vector3(body.Velocity.Linear.X, 0, body.Velocity.Linear.Z);
        body.Velocity.Linear.Y = MathF.Min(0, -Vector3.Dot(support.Normal, horizontal) / support.Normal.Y);
        body.UpdateBounds();
    }

    private static bool IsSameSlope(ClosestHit hit, StaticHandle surface, Vector3 normal) =>
        hit.Hit is CollidableReference collidable && collidable.StaticHandle == surface &&
        Vector3.Dot(hit.Normal, normal) >= SLOPE_NORMAL_ALIGNMENT;

    private readonly record struct SlopeDescent(BodyHandle Player, StaticHandle Surface, Vector3 Normal, Vector3 PriorRimMeters,
        Vector3 UphillRimOffsetMeters);

    private ClosestHit ProbeGround(BodyHandle player, Vector3 position, float startHeightMeters = GROUND_PROBE_START_METERS)
    {
        var query = new ClosestHit(player, groundOnly: true);
        Vector3 start = position + Vector3.UnitY * startHeightMeters;
        _simulation.RayCast(start, -Vector3.UnitY, startHeightMeters + GROUND_TOLERANCE_METERS, ref query);
        return query;
    }

    public (Vector3 FootPositionMeters, float VerticalVelocityMetersPerSecond) GetMotion(BodyHandle handle)
    {
        BodyReference body = _simulation.Bodies.GetBodyReference(handle);
        return (body.Pose.Position - Vector3.UnitY * PLAYER_CENTER_HEIGHT_METERS, body.Velocity.Linear.Y);
    }

    public void Dispose()
    {
        _simulation.Dispose();
        _pool.Clear();
    }
}
