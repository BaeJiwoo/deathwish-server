using System.Numerics;

namespace DeathWish.Server.Physics;

internal abstract class Collider
{
    public Vector3 CenterWorldPositionMeters { get; private set; }

    protected abstract Vector3 HalfSizeMeters { get; }

    protected Collider(Vector3 centerWorldPositionMeters)
    {
        SetCenterWorldPosition(centerWorldPositionMeters);
    }

    /// <summary>Places the collider at a finite world position. Once attached to a world, only its world loop may move it.</summary>
    public void SetCenterWorldPosition(Vector3 centerWorldPositionMeters)
    {
        bool isFinite = float.IsFinite(centerWorldPositionMeters.X) &&
            float.IsFinite(centerWorldPositionMeters.Y) && float.IsFinite(centerWorldPositionMeters.Z);
        if (!isFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(centerWorldPositionMeters));
        }

        CenterWorldPositionMeters = centerWorldPositionMeters;
    }

    /// <summary>Returns the axis-aligned bounds in world coordinates, in meters.</summary>
    public (Vector3 MinMeters, Vector3 MaxMeters) GetBounds()
    {
        Vector3 halfSizeMeters = HalfSizeMeters;
        return (CenterWorldPositionMeters - halfSizeMeters, CenterWorldPositionMeters + halfSizeMeters);
    }
}
