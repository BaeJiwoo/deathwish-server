using System;
using System.Buffers.Binary;
using System.IO;

namespace DeathWish.Protocol;

public static class PingCodec
{
    private const int TOKEN_SIZE = sizeof(long);

    /// <summary>Encodes a server probe carrying a positive, connection-local token.</summary>
    public static byte[] EncodeProbe(long token) => Encode(MessageType.LatencyProbe, token);

    /// <summary>Encodes the client's echo of the server probe token.</summary>
    public static byte[] EncodeReply(long token) => Encode(MessageType.LatencyReply, token);

    public static long DecodeProbe(byte[] payload) => Decode(payload);

    public static long DecodeReply(byte[] payload) => Decode(payload);

    private static byte[] Encode(MessageType type, long token)
    {
        ValidateToken(token);
        var frame = new byte[PacketCodec.HEADER_SIZE + TOKEN_SIZE];
        BinaryPrimitives.WriteUInt16BigEndian(frame, TOKEN_SIZE);
        frame[2] = (byte)type;
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(PacketCodec.HEADER_SIZE), token);
        return frame;
    }

    private static long Decode(byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }
        if (payload.Length != TOKEN_SIZE)
        {
            throw new InvalidDataException("Invalid latency token length.");
        }

        long token = BinaryPrimitives.ReadInt64BigEndian(payload);
        ValidateToken(token);
        return token;
    }

    private static void ValidateToken(long token)
    {
        if (token <= 0)
        {
            throw new InvalidDataException("Latency token must be positive.");
        }
    }
}
