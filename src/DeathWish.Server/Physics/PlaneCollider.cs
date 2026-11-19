using System.Numerics;

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
}
