using System;
using System.IO;
using System.Text;

namespace DeathWish.Protocol;

public enum MessageType : byte
{
    EchoRequest = 1,
    EchoResponse = 2
}

public static class PacketCodec
{
    public const int HeaderSize = 3;
    public const int MaxPayloadLength = 4096;
    private static readonly UTF8Encoding Encoding = new UTF8Encoding(false, true);

    public static byte[] EncodeEcho(MessageType type, string message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        ValidateType(type);
        int length = Encoding.GetByteCount(message);
        if (length > MaxPayloadLength) throw new ArgumentException("Payload exceeds 4096 UTF-8 bytes.", nameof(message));

        byte[] frame = new byte[HeaderSize + length];
        frame[0] = (byte)(length >> 8);
        frame[1] = (byte)length;
        frame[2] = (byte)type;
        Encoding.GetBytes(message, 0, message.Length, frame, HeaderSize);
        return frame;
    }

    public static string DecodeEcho(byte[] payload) => Encoding.GetString(payload);

    internal static void ValidateType(MessageType type)
    {
        if (type != MessageType.EchoRequest && type != MessageType.EchoResponse)
            throw new InvalidDataException("Unknown message type.");
    }
}

/// <summary>Connection-owned decoder. Call sequentially. Callback receives an independently owned payload.</summary>
public sealed class PacketReader
{
    private readonly byte[] _header = new byte[PacketCodec.HeaderSize];
    private int _headerLength;
    private byte[]? _payload;
    private int _payloadLength;
    private MessageType _type;

    public bool HasPartialFrame => _headerLength != 0;

    public void Feed(ReadOnlySpan<byte> bytes, Action<MessageType, byte[]> onPacket)
    {
        while (!bytes.IsEmpty)
        {
            if (_headerLength < _header.Length)
            {
                int count = Math.Min(_header.Length - _headerLength, bytes.Length);
                bytes.Slice(0, count).CopyTo(_header.AsSpan(_headerLength));
                _headerLength += count;
                bytes = bytes.Slice(count);
                if (_headerLength < _header.Length) return;

                int length = (_header[0] << 8) | _header[1];
                if (length > PacketCodec.MaxPayloadLength) throw new InvalidDataException("Payload exceeds 4096 bytes.");
                _type = (MessageType)_header[2];
                PacketCodec.ValidateType(_type);
                _payload = new byte[length];
            }

            int copied = Math.Min(_payload!.Length - _payloadLength, bytes.Length);
            bytes.Slice(0, copied).CopyTo(_payload.AsSpan(_payloadLength));
            _payloadLength += copied;
            bytes = bytes.Slice(copied);
            if (_payloadLength != _payload.Length) return;

            byte[] payload = _payload;
            MessageType type = _type;
            _headerLength = 0;
            _payloadLength = 0;
            _payload = null;
            onPacket(type, payload);
        }
    }
}
