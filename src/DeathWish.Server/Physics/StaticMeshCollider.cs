using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace DeathWish.Server.Physics;

internal sealed class StaticMeshCollider : Collider
{
    private readonly Triangle[] _localTriangles;
    protected override Vector3 HalfSizeMeters { get; }

    internal StaticMeshCollider(Vector3 centerMeters, Vector3 halfSizeMeters, Triangle[] localTriangles)
        : base(centerMeters)
    {
        HalfSizeMeters = halfSizeMeters;
        _localTriangles = localTriangles;
    }

    internal override void RegisterStatic(Simulation simulation)
    {
        // Each simulation owns its mesh buffers and BVH until the room's pool is released.
        simulation.BufferPool.Take(_localTriangles.Length, out Buffer<Triangle> triangles);
        for (int i = 0; i < _localTriangles.Length; i++)
        {
            triangles[i] = _localTriangles[i];
        }

        TypedIndex shape = simulation.Shapes.Add(new Mesh(triangles, Vector3.One, simulation.BufferPool));
        simulation.Statics.Add(new StaticDescription(CenterWorldPositionMeters, Quaternion.Identity, shape));
    }
}
