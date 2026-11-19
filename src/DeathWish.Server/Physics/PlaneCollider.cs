using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace DeathWish.Server.Physics;

/// <summary>A horizontal rectangle. Width runs along X and depth along Z, both in meters.</summary>
internal sealed class PlaneCollider : Collider
{
    public float WidthMeters { get; }
    public float DepthMeters { get; }

    protected override Vector3 HalfSizeMeters => new(WidthMeters / 2, 0, DepthMeters / 2);

    public PlaneCollider(Vector3 centerWorldPositionMeters, float widthMeters, float depthMeters)
        : base(centerWorldPositionMeters)
    {
        if (!float.IsFinite(widthMeters) || widthMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthMeters));
        }
        if (!float.IsFinite(depthMeters) || depthMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depthMeters));
        }

        WidthMeters = widthMeters;
        DepthMeters = depthMeters;
    }

    internal override void RegisterStatic(Simulation simulation)
    {
        var half = HalfSizeMeters;
        var a = new Vector3(-half.X, 0, -half.Z);
        var b = new Vector3(half.X, 0, -half.Z);
        var c = new Vector3(half.X, 0, half.Z);
        var d = new Vector3(-half.X, 0, half.Z);

        // Bepu's clockwise triangle winding exposes the top face of the floor.
        TypedIndex first = simulation.Shapes.Add(new Triangle(a, b, c));
        TypedIndex second = simulation.Shapes.Add(new Triangle(a, c, d));
        simulation.Statics.Add(new StaticDescription(CenterWorldPositionMeters, Quaternion.Identity, first));
        simulation.Statics.Add(new StaticDescription(CenterWorldPositionMeters, Quaternion.Identity, second));
    }
}
