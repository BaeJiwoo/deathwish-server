using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace DeathWish.Protocol;

public readonly struct CameraRay
{
    private const float MAX_CAMERA_OFFSET_METERS = 8f;
    public Vector3 Offset { get; }
    public Vector3 Direction { get; }

    public CameraRay(Vector3 offset, Vector3 direction)
    {
        float length = direction.Length();
        if (!(offset.LengthSquared() <= MAX_CAMERA_OFFSET_METERS * MAX_CAMERA_OFFSET_METERS) ||
            !(length >= 0.99f && length <= 1.01f))
        {
            throw new InvalidDataException("Invalid camera offset or direction.");
        }
        Offset = offset;
        Direction = direction / length;
    }
}

/// <summary>A single remote shot at world simulation time, on the same clock as an aimed gait snapshot.</summary>
public readonly struct ShotFired
{
    public long PlayerId { get; }
    public long ServerTimeMicroseconds { get; }

    public ShotFired(long playerId, long serverTimeMicroseconds)
    {
        PlayerId = playerId;
        ServerTimeMicroseconds = serverTimeMicroseconds;
    }

    internal void Validate()
    {
        if (PlayerId <= 0 || ServerTimeMicroseconds <= 0)
        {
            throw new InvalidDataException("Invalid shot player ID or simulation time.");
        }
    }
}

public static class ActionCodec
{
    private const int VECTOR_SIZE = 3 * sizeof(float);
    private const int RAY_SIZE = 2 * VECTOR_SIZE;

    private const int SHOT_EVENT_SIZE = 2 * sizeof(long);

    public static byte[] EncodeShotFired(ShotFired shot)
    {
        shot.Validate();
        var frame = new byte[PacketCodec.HEADER_SIZE + SHOT_EVENT_SIZE];
        frame[1] = SHOT_EVENT_SIZE;
        frame[2] = (byte)MessageType.ShotFired;
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(PacketCodec.HEADER_SIZE), shot.PlayerId);
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(PacketCodec.HEADER_SIZE + sizeof(long)), shot.ServerTimeMicroseconds);
        return frame;
    }

    public static ShotFired DecodeShotFired(byte[] payload)
    {
        if (payload.Length != SHOT_EVENT_SIZE)
        {
            throw new InvalidDataException("Invalid shot event length.");
        }
        var shot = new ShotFired(BinaryPrimitives.ReadInt64BigEndian(payload),
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(sizeof(long))));
        shot.Validate();
        return shot;
    }

    public static byte[] EncodeJump() => new byte[] { 0, 0, (byte)MessageType.Jump };

    public static byte[] EncodeRay(CameraRay ray)
    {
        var validated = new CameraRay(ray.Offset, ray.Direction);
        var frame = new byte[PacketCodec.HEADER_SIZE + RAY_SIZE];
        frame[1] = RAY_SIZE;
        frame[2] = (byte)MessageType.CameraRay;
        WriteVector(frame.AsSpan(PacketCodec.HEADER_SIZE), validated.Offset);
        WriteVector(frame.AsSpan(PacketCodec.HEADER_SIZE + VECTOR_SIZE), validated.Direction);
        return frame;
    }

    public static CameraRay DecodeRay(byte[] payload)
    {
        if (payload.Length != RAY_SIZE)
        {
            throw new InvalidDataException("Invalid ray length.");
        }
        return new CameraRay(ReadVector(payload), ReadVector(payload.AsSpan(VECTOR_SIZE)));
    }

    public static byte[] EncodeResult(string message)
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(message);
        if (payload.Length > PacketCodec.MAX_PAYLOAD_LENGTH)
        {
            throw new ArgumentOutOfRangeException(nameof(message));
        }
        var frame = new byte[PacketCodec.HEADER_SIZE + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)payload.Length);
        frame[2] = (byte)MessageType.RaycastResult;
        payload.CopyTo(frame, PacketCodec.HEADER_SIZE);
        return frame;
    }

    private static void WriteVector(Span<byte> bytes, Vector3 value)
    {
        WriteFloat(bytes, value.X);
        WriteFloat(bytes.Slice(sizeof(float)), value.Y);
        WriteFloat(bytes.Slice(2 * sizeof(float)), value.Z);
    }

    private static void WriteFloat(Span<byte> bytes, float value) =>
        BinaryPrimitives.WriteInt32BigEndian(bytes, BitConverter.SingleToInt32Bits(value));

    private static Vector3 ReadVector(ReadOnlySpan<byte> bytes) =>
        new Vector3(ReadFloat(bytes), ReadFloat(bytes.Slice(sizeof(float))), ReadFloat(bytes.Slice(2 * sizeof(float))));

    private static float ReadFloat(ReadOnlySpan<byte> bytes) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes));
}
