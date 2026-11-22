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

public static class ActionCodec
{
    private const int VECTOR_SIZE = 3 * sizeof(float);
    private const int RAY_SIZE = 2 * VECTOR_SIZE;

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
