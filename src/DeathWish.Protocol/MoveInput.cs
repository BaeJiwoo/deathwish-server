using System;
using System.IO;

namespace DeathWish.Protocol;

public readonly struct MoveInput
{
    private const uint SEQUENCE_HALF_RANGE = 0x80000000;

    public uint Sequence { get; }
    public float X { get; }
    public float Z { get; }

    public MoveInput(uint sequence, float x, float z)
    {
        Sequence = sequence;
        X = x;
        Z = z;
    }

    public void Validate()
    {
        if (float.IsNaN(X) || float.IsInfinity(X) || X < -1 || X > 1 ||
            float.IsNaN(Z) || float.IsInfinity(Z) || Z < -1 || Z > 1)
        {
            throw new InvalidDataException("Movement components must be finite and within [-1, 1].");
        }
    }

    public static bool IsNewerSequence(uint candidate, uint previous)
    {
        uint distance = unchecked(candidate - previous);
        return distance != 0 && distance < SEQUENCE_HALF_RANGE;
    }
}
