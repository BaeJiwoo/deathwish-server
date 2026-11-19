using System.Numerics;

namespace DeathWish.Server.Physics;

/// <summary>An axis-aligned box with full dimensions in meters along X, Y and Z.</summary>
internal sealed class BoxCollider : Collider
{
    public Vector3 SizeMeters { get; }

    protected override Vector3 HalfSizeMeters => SizeMeters / 2;

    public BoxCollider(Vector3 centerWorldPositionMeters, Vector3 sizeMeters)
        : base(centerWorldPositionMeters)
    {
        if (!float.IsFinite(sizeMeters.X) || !float.IsFinite(sizeMeters.Y) || !float.IsFinite(sizeMeters.Z) ||
            sizeMeters.X <= 0 || sizeMeters.Y <= 0 || sizeMeters.Z <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeMeters));
        }

        SizeMeters = sizeMeters;
    }

}
