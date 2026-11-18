using System.Numerics;

namespace DeathWish.Server;

/// <summary>A copied player state. Player IDs are positive, unique within one world run, and never reused in that run.</summary>
public readonly record struct PlayerState(long PlayerId, Guid SessionId, Vector3 Position)
{
    internal float VerticalVelocityMetersPerSecond { get; init; }
}
