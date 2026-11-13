using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace DeathWish.Protocol;

public enum MessageType : byte
{
    EchoRequest = 1,
    EchoResponse = 2,
    JoinRoomRequest = 3,
    JoinRoomResponse = 4
}

public static class PacketCodec
{
    public const int HeaderSize = 3;
    public const int MaxPayloadLength = 4096;
    private static readonly UTF8Encoding Encoding = new UTF8Encoding(false, true);

    public static byte[] EncodeEcho(MessageType type, string message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        if (type != MessageType.EchoRequest && type != MessageType.EchoResponse)
            throw new ArgumentException("Expected an Echo message type.", nameof(type));
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

    public static byte[] EncodeJoinRoomRequest(JoinRoomRequest request)
    {
        ValidateRoomId(request.RoomId);
        byte[] frame = new byte[HeaderSize + 4];
        frame[1] = 4;
        frame[2] = (byte)MessageType.JoinRoomRequest;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(HeaderSize, 4), request.RoomId);
        return frame;
    }

    public static JoinRoomRequest DecodeJoinRoomRequest(byte[] payload)
    {
        ValidatePayloadLength(payload, 4);
        int roomId = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan());
        ValidateRoomId(roomId);
        return new JoinRoomRequest(roomId);
    }

    public static byte[] EncodeJoinRoomResponse(JoinRoomResponse response)
    {
        ValidateRoomId(response.RoomId);
        ValidateRoomJoinResult(response.Result);
        byte[] frame = new byte[HeaderSize + 5];
        frame[1] = 5;
        frame[2] = (byte)MessageType.JoinRoomResponse;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(HeaderSize, 4), response.RoomId);
        frame[HeaderSize + 4] = (byte)response.Result;
        return frame;
    }

    public static JoinRoomResponse DecodeJoinRoomResponse(byte[] payload)
    {
        ValidatePayloadLength(payload, 5);
        int roomId = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
        var result = (RoomJoinResult)payload[4];
        ValidateRoomId(roomId);
        ValidateRoomJoinResult(result);
        return new JoinRoomResponse(roomId, result);
    }

    private static void ValidatePayloadLength(byte[] payload, int expected)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (payload.Length != expected) throw new InvalidDataException($"Expected {expected} payload bytes.");
    }

    private static void ValidateRoomId(int roomId)
    {
        if (roomId <= 0) throw new InvalidDataException("Room ID must be positive.");
    }

    private static void ValidateRoomJoinResult(RoomJoinResult result)
    {
        if (result != RoomJoinResult.Success && result != RoomJoinResult.SessionUnavailable &&
            result != RoomJoinResult.RoomNotFound && result != RoomJoinResult.AlreadyInAnotherRoom)
            throw new InvalidDataException("Unknown room join result.");
    }

    internal static void ValidateType(MessageType type)
    {
        if (type != MessageType.EchoRequest && type != MessageType.EchoResponse &&
            type != MessageType.JoinRoomRequest && type != MessageType.JoinRoomResponse)
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
