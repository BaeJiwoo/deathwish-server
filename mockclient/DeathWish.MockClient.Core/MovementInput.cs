using System;
using DeathWish.Protocol;

namespace DeathWish.MockClient.Core;

public static class MovementInput
{
    private const double DEGREES_PER_TURN = 360;
    private const double DEGREES_PER_HALF_TURN = 180;

    /// <summary>Converts local axes in [-1, 1] to a bounded XZ input using finite yaw in degrees. Preserves sub-unit analog input.</summary>
    public static MoveInput FromLocalAxes(uint sequence, float strafe, float forward, float yawDegrees)
    {
        new MoveInput(sequence, strafe, forward).Validate();
        if (float.IsNaN(yawDegrees) || float.IsInfinity(yawDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(yawDegrees));
        }

        double radians = (yawDegrees % DEGREES_PER_TURN) * Math.PI / DEGREES_PER_HALF_TURN;
        double length = Math.Max(1, Math.Sqrt((double)strafe * strafe + (double)forward * forward));
        double sin = Math.Sin(radians);
        double cos = Math.Cos(radians);
        double x = (strafe * cos + forward * sin) / length;
        double z = (forward * cos - strafe * sin) / length;

        // Rotation rounding must not turn legitimate input into a protocol violation.
        return new MoveInput(sequence, (float)Math.Clamp(x, -1, 1), (float)Math.Clamp(z, -1, 1));
    }
}
