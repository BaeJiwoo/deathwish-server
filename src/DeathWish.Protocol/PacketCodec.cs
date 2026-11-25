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
    JoinRoomResponse = 4,
    JoinAck = 5,
    MoveInput = 6,
    WorldSnapshot = 7,
    Jump = 8,
    CameraRay = 9,
    RaycastResult = 10,
    AnimatedWorldSnapshot = 17
}

public static class PacketCodec
{
    private const int JOIN_ACK_BASE_SIZE = 32;
    private const int JOIN_ROOM_ID_SIZE = sizeof(int);
    private const byte ANIMATED_JOIN_FLAG = 1;
    private const byte PHYSICS_LAB_MAP_ID = 1;
    private const int MOVE_INPUT_PAYLOAD_SIZE = 12;
    private const int MOVE_SEQUENCE_OFFSET = 0;
    private const int MOVE_X_OFFSET = 4;
    private const int MOVE_Z_OFFSET = 8;
    public const int HEADER_SIZE = 3;
    public const int MAX_PAYLOAD_LENGTH = 4096;
    private static readonly UTF8Encoding Encoding = new UTF8Encoding(false, true);

    public static byte[] EncodeEcho(MessageType type, string message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        if (type != MessageType.EchoRequest && type != MessageType.EchoResponse)
            throw new ArgumentException("Expected an Echo message type.", nameof(type));
        int length = Encoding.GetByteCount(message);
        if (length > MAX_PAYLOAD_LENGTH) throw new ArgumentException("Payload exceeds 4096 UTF-8 bytes.", nameof(message));

        byte[] frame = new byte[HEADER_SIZE + length];
        frame[0] = (byte)(length >> 8);
        frame[1] = (byte)length;
        frame[2] = (byte)type;
        Encoding.GetBytes(message, 0, message.Length, frame, HEADER_SIZE);
        return frame;
    }

    public static string DecodeEcho(byte[] payload) => Encoding.GetString(payload);

    public static byte[] EncodeMoveInput(MoveInput input)
    {
        input.Validate();
        byte[] frame = new byte[HEADER_SIZE + MOVE_INPUT_PAYLOAD_SIZE];
        frame[1] = MOVE_INPUT_PAYLOAD_SIZE;
        frame[2] = (byte)MessageType.MoveInput;
        Span<byte> payload = frame.AsSpan(HEADER_SIZE);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(MOVE_SEQUENCE_OFFSET), input.Sequence);
        BinaryPrimitives.WriteInt32BigEndian(payload.Slice(MOVE_X_OFFSET), BitConverter.SingleToInt32Bits(input.X));
        BinaryPrimitives.WriteInt32BigEndian(payload.Slice(MOVE_Z_OFFSET), BitConverter.SingleToInt32Bits(input.Z));
        return frame;
    }

    public static MoveInput DecodeMoveInput(byte[] payload)
    {
        ValidatePayloadLength(payload, MOVE_INPUT_PAYLOAD_SIZE);
        var input = new MoveInput(BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(MOVE_SEQUENCE_OFFSET)),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(MOVE_X_OFFSET))),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(MOVE_Z_OFFSET))));
        input.Validate();
        return input;
    }

    public static byte[] EncodeJoinRoomRequest(JoinRoomRequest request)
    {
        ValidateRoomId(request.RoomId);
        int length = request.ReceivesAnimation ? JOIN_ROOM_ID_SIZE + 1 : JOIN_ROOM_ID_SIZE;
        byte[] frame = new byte[HEADER_SIZE + length];
        frame[1] = (byte)length;
        frame[2] = (byte)MessageType.JoinRoomRequest;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(HEADER_SIZE, JOIN_ROOM_ID_SIZE), request.RoomId);
        if (request.ReceivesAnimation)
        {
            frame[HEADER_SIZE + JOIN_ROOM_ID_SIZE] = ANIMATED_JOIN_FLAG;
        }
        return frame;
    }

    public static JoinRoomRequest DecodeJoinRoomRequest(byte[] payload)
    {
        if (payload == null)
        {
            throw new ArgumentNullException(nameof(payload));
        }
        bool receivesAnimation = payload.Length == JOIN_ROOM_ID_SIZE + 1 && payload[JOIN_ROOM_ID_SIZE] == ANIMATED_JOIN_FLAG;
        ValidatePayloadLength(payload, receivesAnimation ? JOIN_ROOM_ID_SIZE + 1 : JOIN_ROOM_ID_SIZE);
        int roomId = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan());
        ValidateRoomId(roomId);
        return new JoinRoomRequest(roomId, receivesAnimation);
    }

    public static byte[] EncodeJoinRoomResponse(JoinRoomResponse response)
    {
        ValidateRoomId(response.RoomId);
        ValidateRoomJoinResult(response.Result);
        byte[] frame = new byte[HEADER_SIZE + 5];
        frame[1] = 5;
        frame[2] = (byte)MessageType.JoinRoomResponse;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(HEADER_SIZE, 4), response.RoomId);
        frame[HEADER_SIZE + 4] = (byte)response.Result;
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

    public static byte[] EncodeJoinAck(JoinAck ack)
    {
        ValidateJoinAck(ack);
        int payloadSize = ack.IsPhysicsLab ? JOIN_ACK_BASE_SIZE + 1 : JOIN_ACK_BASE_SIZE;
        byte[] frame = new byte[HEADER_SIZE + payloadSize];
        frame[1] = (byte)payloadSize;
        if (ack.IsPhysicsLab)
        {
            frame[HEADER_SIZE + JOIN_ACK_BASE_SIZE] = PHYSICS_LAB_MAP_ID;
        }
        frame[2] = (byte)MessageType.JoinAck;
        Span<byte> payload = frame.AsSpan(HEADER_SIZE);
        BinaryPrimitives.WriteInt32BigEndian(payload, ack.RoomId);
        BinaryPrimitives.WriteInt64BigEndian(payload.Slice(4), ack.PlayerId);
        BinaryPrimitives.WriteInt64BigEndian(payload.Slice(12), ack.ServerTick);
        BinaryPrimitives.WriteInt32BigEndian(payload.Slice(20), BitConverter.SingleToInt32Bits(ack.X));
        BinaryPrimitives.WriteInt32BigEndian(payload.Slice(24), BitConverter.SingleToInt32Bits(ack.Y));
        BinaryPrimitives.WriteInt32BigEndian(payload.Slice(28), BitConverter.SingleToInt32Bits(ack.Z));
        return frame;
    }

    public static JoinAck DecodeJoinAck(byte[] payload)
    {
        if (payload == null)
        {
            throw new ArgumentNullException(nameof(payload));
        }
        bool isPhysicsLab = payload.Length == JOIN_ACK_BASE_SIZE + 1 && payload[JOIN_ACK_BASE_SIZE] == PHYSICS_LAB_MAP_ID;
        ValidatePayloadLength(payload, isPhysicsLab ? JOIN_ACK_BASE_SIZE + 1 : JOIN_ACK_BASE_SIZE);
        var ack = new JoinAck(BinaryPrimitives.ReadInt32BigEndian(payload),
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(4)),
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(12)),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(20))),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(24))),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(28))), isPhysicsLab);
        ValidateJoinAck(ack);
        return ack;
    }

    private static void ValidateJoinAck(JoinAck ack)
    {
        ValidateRoomId(ack.RoomId);
        if (ack.PlayerId <= 0 || ack.ServerTick <= 0 ||
            float.IsNaN(ack.X) || float.IsInfinity(ack.X) ||
            float.IsNaN(ack.Y) || float.IsInfinity(ack.Y) ||
            float.IsNaN(ack.Z) || float.IsInfinity(ack.Z))
            throw new InvalidDataException("Invalid world admission state.");
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
            result != RoomJoinResult.RoomNotFound && result != RoomJoinResult.AlreadyInAnotherRoom &&
            result != RoomJoinResult.WorldUnavailable)
            throw new InvalidDataException("Unknown room join result.");
    }

    internal static void ValidateType(MessageType type)
    {
        if (type != MessageType.EchoRequest && type != MessageType.EchoResponse &&
            type != MessageType.JoinRoomRequest && type != MessageType.JoinRoomResponse && type != MessageType.JoinAck &&
            type != MessageType.MoveInput && type != MessageType.WorldSnapshot && type != MessageType.Jump &&
            type != MessageType.CameraRay && type != MessageType.RaycastResult && type != MessageType.AnimatedWorldSnapshot)
            throw new InvalidDataException("Unknown message type.");
    }
}

/// <summary>Connection-owned decoder. Call sequentially. Callback receives an independently owned payload.</summary>
public sealed class PacketReader
{
    private readonly byte[] _header = new byte[PacketCodec.HEADER_SIZE];
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

                _type = (MessageType)_header[2];
                PacketCodec.ValidateType(_type);
                int maximum = _type switch
                {
                    MessageType.WorldSnapshot => SnapshotCodec.MAX_PAYLOAD_LENGTH,
                    MessageType.AnimatedWorldSnapshot => AnimatedSnapshotCodec.MAX_PAYLOAD_LENGTH,
                    _ => PacketCodec.MAX_PAYLOAD_LENGTH
                };
                if (length > maximum)
                {
                    throw new InvalidDataException("Payload exceeds message limit.");
                }
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
