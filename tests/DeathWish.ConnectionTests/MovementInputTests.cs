using DeathWish.MockClient.Core;
using DeathWish.Protocol;

internal static class MovementInputTests
{
    private const float TOLERANCE = .00001f;

    public static void Run()
    {
        foreach (float yaw in new[] { 44.9914f, 45, 134.9869f, 135, 225, 315, -45, float.MaxValue })
        {
            CheckInput(1, 1, yaw);
        }
        for (int step = 0; step <= 36000; step++)
        {
            float yaw = step / 100f;
            foreach (float strafe in new[] { -1f, 0, 1 })
            {
                foreach (float forward in new[] { -1f, 0, 1 })
                {
                    CheckInput(strafe, forward, yaw);
                }
            }
        }
        MoveInput forwardAtRight = MovementInput.FromLocalAxes(10, 0, 1, 90);
        Require(Math.Abs(forwardAtRight.X - 1) < TOLERANCE && Math.Abs(forwardAtRight.Z) < TOLERANCE,
            "Yaw maps forward into world X");
        MoveInput strafeAtRight = MovementInput.FromLocalAxes(10, 1, 0, 90);
        Require(Math.Abs(strafeAtRight.X) < TOLERANCE && Math.Abs(strafeAtRight.Z + 1) < TOLERANCE,
            "Yaw maps strafe into negative Z");
        MoveInput analog = MovementInput.FromLocalAxes(0, .3f, .4f, 34);
        Require(Math.Abs(Math.Sqrt(analog.X * analog.X + analog.Z * analog.Z) - .5) < TOLERANCE,
            "Analog magnitude preserved");
        Reject(() => MovementInput.FromLocalAxes(0, 2, 0, 0));
        Reject(() => MovementInput.FromLocalAxes(0, float.NaN, 0, 0));
        Reject(() => MovementInput.FromLocalAxes(0, 0, 1, float.PositiveInfinity));
        Reject(() => new MoveInput(0, 1.0000001f, 0).Validate());
        Console.WriteLine("PASS client rotated input: reproduced boundary angles, full yaw sweep, analog/stop, finite bounds and strict server validation");
    }

    private static void CheckInput(float strafe, float forward, float yaw)
    {
        MoveInput input = MovementInput.FromLocalAxes(uint.MaxValue, strafe, forward, yaw);
        byte[] frame = PacketCodec.EncodeMoveInput(input);
        MoveInput decoded = PacketCodec.DecodeMoveInput(frame[PacketCodec.HEADER_SIZE..]);
        Require(decoded.Sequence == uint.MaxValue && decoded.X == input.X && decoded.Z == input.Z,
            "Client input survives wire round trip");
        double expectedLength = Math.Min(1, Math.Sqrt(strafe * strafe + forward * forward));
        double actualLength = Math.Sqrt((double)input.X * input.X + (double)input.Z * input.Z);
        Require(Math.Abs(actualLength - expectedLength) < TOLERANCE, "Rotation preserves normalized magnitude");
    }

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException)
        {
            return;
        }
        throw new Exception("Invalid movement accepted");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
