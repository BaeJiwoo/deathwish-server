using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace DeathWish.Server.Physics;

/// <summary>Room-local simulation. Its owner must use it on one world thread and dispose it after the last step.</summary>
internal sealed class PlayerPhysicsSimulation : IDisposable
{
    internal const float PLAYER_WIDTH_METERS = 0.65f;
    internal const float PLAYER_HEIGHT_METERS = 1.8f;
    internal const float PLAYER_DEPTH_METERS = 0.65f;
    private const float FOOT_BEVEL_INSET_METERS = 0.08f;
    private const float FOOT_BEVEL_HEIGHT_METERS = 0.03f;
    private const int SOLVER_ITERATIONS = 8;
    private const int SUBSTEPS = 4;
    private const float DISABLED_SLEEP_THRESHOLD = -1;

    private readonly BufferPool _pool = new();
    private readonly Simulation _simulation;
    private readonly TypedIndex _playerShape;
    private readonly float _centerHeightMeters;

    public PlayerPhysicsSimulation(IReadOnlyList<Collider> staticColliders, float gravityMetersPerSecondSquared)
    {
        _simulation = Simulation.Create(_pool, new PlayerContactCallbacks(),
            new GravityIntegrator(gravityMetersPerSecondSquared), new SolveDescription(SOLVER_ITERATIONS, SUBSTEPS));
        _playerShape = _simulation.Shapes.Add(CreatePlayerHull(out _centerHeightMeters));

        foreach (Collider collider in staticColliders)
        {
            collider.RegisterStatic(_simulation);
        }
    }

    public BodyHandle AddPlayer(Vector3 footPositionMeters, float verticalVelocityMetersPerSecond = 0)
    {
        Vector3 center = footPositionMeters + Vector3.UnitY * _centerHeightMeters;
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

    public void Step(float deltaTimeSeconds)
    {
        _simulation.Timestep(deltaTimeSeconds);
    }

    public (Vector3 FootPositionMeters, float VerticalVelocityMetersPerSecond) GetMotion(BodyHandle handle)
    {
        BodyReference body = _simulation.Bodies.GetBodyReference(handle);
        return (body.Pose.Position - Vector3.UnitY * _centerHeightMeters, body.Velocity.Linear.Y);
    }

    private ConvexHull CreatePlayerHull(out float centerHeightMeters)
    {
        float halfWidth = PLAYER_WIDTH_METERS / 2;
        float halfDepth = PLAYER_DEPTH_METERS / 2;
        Span<Vector3> corners = stackalloc Vector3[12];
        int index = 0;
        // The inset sole keeps the body's vertical leading face off the toe of an ascending triangle.
        for (int x = -1; x <= 1; x += 2)
        {
            for (int z = -1; z <= 1; z += 2)
            {
                corners[index++] = new Vector3(x * (halfWidth - FOOT_BEVEL_INSET_METERS), 0, z * (halfDepth - FOOT_BEVEL_INSET_METERS));
                corners[index++] = new Vector3(x * halfWidth, FOOT_BEVEL_HEIGHT_METERS, z * halfDepth);
                corners[index++] = new Vector3(x * halfWidth, PLAYER_HEIGHT_METERS, z * halfDepth);
            }
        }

        var hull = new ConvexHull(corners, _pool, out Vector3 center);
        centerHeightMeters = center.Y;
        return hull;
    }

    public void Dispose()
    {
        _simulation.Dispose();
        _pool.Clear();
    }
}
