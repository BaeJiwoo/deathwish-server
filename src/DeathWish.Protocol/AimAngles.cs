using System;
using System.Buffers.Binary;
using System.IO;

namespace DeathWish.Protocol;

/// <summary>World-space view direction in hundredths of a degree. Zero faces +Z, positive yaw turns toward +X and positive pitch looks down.</summary>
public readonly struct AimAngles
{
    internal const int WIRE_SIZE = 2 * sizeof(short);
    private const int HALF_TURN_CENTIDEGREES = 18_000;
    private const int QUARTER_TURN_CENTIDEGREES = 9_000;

    public short YawCentidegrees { get; }
    public short PitchCentidegrees { get; }

    public AimAngles(short yawCentidegrees, short pitchCentidegrees)
    {
        if (yawCentidegrees < -HALF_TURN_CENTIDEGREES || yawCentidegrees > HALF_TURN_CENTIDEGREES ||
            pitchCentidegrees < -QUARTER_TURN_CENTIDEGREES || pitchCentidegrees > QUARTER_TURN_CENTIDEGREES)
        {
            throw new InvalidDataException("Camera aim is outside the world-space yaw or pitch range.");
        }
        YawCentidegrees = yawCentidegrees;
        PitchCentidegrees = pitchCentidegrees;
    }

    internal void Write(Span<byte> record)
    {
        BinaryPrimitives.WriteInt16BigEndian(record, YawCentidegrees);
        BinaryPrimitives.WriteInt16BigEndian(record.Slice(sizeof(short)), PitchCentidegrees);
    }

    internal static AimAngles Read(ReadOnlySpan<byte> record) =>
        new AimAngles(BinaryPrimitives.ReadInt16BigEndian(record), BinaryPrimitives.ReadInt16BigEndian(record.Slice(sizeof(short))));
}
