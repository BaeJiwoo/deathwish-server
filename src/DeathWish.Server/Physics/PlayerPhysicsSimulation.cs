using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace DeathWish.Server.Physics;

/// <summary>Room-local simulation. Its owner must use it on one world thread and dispose it after the last step.</summary>
internal sealed class PlayerPhysicsSimulation : IDisposable
{
    private const float PLAYER_WIDTH_METERS = 0.65f;
    private const float PLAYER_HEIGHT_METERS = 1.8f;
    private const float PLAYER_DEPTH_METERS = 0.65f;
    private const int SOLVER_ITERATIONS = 8;
    private const int SUBSTEPS = 4;
    private const float DISABLED_SLEEP_THRESHOLD = -1;

    private readonly BufferPool _pool = new();
    private readonly Simulation _simulation;
    private readonly TypedIndex _playerShape;

    public PlayerPhysicsSimulation(IReadOnlyList<Collider> staticColliders, float gravityMetersPerSecondSquared)
    {
        _simulation = Simulation.Create(_pool, new PlayerContactCallbacks(),
            new GravityIntegrator(gravityMetersPerSecondSquared), new SolveDescription(SOLVER_ITERATIONS, SUBSTEPS));
        _playerShape = _simulation.Shapes.Add(new Box(PLAYER_WIDTH_METERS, PLAYER_HEIGHT_METERS, PLAYER_DEPTH_METERS));

        foreach (Collider collider in staticColliders)
        {
            collider.RegisterStatic(_simulation);
        }
    }

    public BodyHandle AddPlayer(Vector3 footPositionMeters, float verticalVelocityMetersPerSecond = 0)
    {
        Vector3 center = footPositionMeters + Vector3.UnitY * (PLAYER_HEIGHT_METERS / 2);
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
        return (body.Pose.Position - Vector3.UnitY * (PLAYER_HEIGHT_METERS / 2), body.Velocity.Linear.Y);
    }

    public void Dispose()
    {
        _simulation.Dispose();
        _pool.Clear();
    }
}
