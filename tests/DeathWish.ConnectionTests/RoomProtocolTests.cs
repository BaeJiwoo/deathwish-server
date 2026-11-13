using DeathWish.Protocol;

static class RoomProtocolTests
{
    public static void Run()
    {
        byte[] request = PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(0x01020304));
        Require(request.SequenceEqual(new byte[] { 0, 4, 3, 1, 2, 3, 4 }), "Request wire layout");
        foreach (RoomJoinResult result in Enum.GetValues<RoomJoinResult>())
        {
            byte[] response = PacketCodec.EncodeJoinRoomResponse(new JoinRoomResponse(0x01020304, result));
            Require(response.SequenceEqual(new byte[] { 0, 5, 4, 1, 2, 3, 4, (byte)result }), "Response wire layout");
            JoinRoomResponse decoded = PacketCodec.DecodeJoinRoomResponse(response[3..]);
            Require(decoded.RoomId == 0x01020304 && decoded.Result == result, "Result round trip");
            VerifyFraming(response, MessageType.JoinRoomResponse);
        }
        VerifyFraming(request, MessageType.JoinRoomRequest);

        foreach (int roomId in new[] { 1, int.MaxValue })
        {
            byte[] encoded = PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(roomId));
            Require(PacketCodec.DecodeJoinRoomRequest(encoded[3..]).RoomId == roomId, "Room ID boundary");
        }

        byte[] combined = request.Concat(PacketCodec.EncodeEcho(MessageType.EchoRequest, ""))
            .Concat(PacketCodec.EncodeJoinRoomResponse(new JoinRoomResponse(1, RoomJoinResult.Success))).ToArray();
        var reader = new PacketReader();
        var types = new List<MessageType>();
        reader.Feed(combined, (type, _) => types.Add(type));
        Require(types.SequenceEqual(new[] { MessageType.JoinRoomRequest, MessageType.EchoRequest, MessageType.JoinRoomResponse }) &&
            !reader.HasPartialFrame, "Combined mixed frames");

        foreach (int length in new[] { 0, 3, 5 })
            Reject<InvalidDataException>(() => PacketCodec.DecodeJoinRoomRequest(new byte[length]));
        foreach (int length in new[] { 0, 4, 6 })
            Reject<InvalidDataException>(() => PacketCodec.DecodeJoinRoomResponse(new byte[length]));
        foreach (int roomId in new[] { 0, -1, int.MinValue })
        {
            Reject<InvalidDataException>(() => PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(roomId)));
            Reject<InvalidDataException>(() => PacketCodec.EncodeJoinRoomResponse(new JoinRoomResponse(roomId, RoomJoinResult.Success)));
            byte[] payload = new byte[5];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload, roomId);
            Reject<InvalidDataException>(() => PacketCodec.DecodeJoinRoomRequest(payload[..4]));
            Reject<InvalidDataException>(() => PacketCodec.DecodeJoinRoomResponse(payload));
        }
        foreach (byte result in new byte[] { 4, 255 })
        {
            Reject<InvalidDataException>(() => PacketCodec.EncodeJoinRoomResponse(new JoinRoomResponse(1, (RoomJoinResult)result)));
            Reject<InvalidDataException>(() => PacketCodec.DecodeJoinRoomResponse(new byte[] { 0, 0, 0, 1, result }));
        }
        Reject<ArgumentNullException>(() => PacketCodec.DecodeJoinRoomRequest(null!));
        Reject<ArgumentNullException>(() => PacketCodec.DecodeJoinRoomResponse(null!));
        Reject<ArgumentException>(() => PacketCodec.EncodeEcho(MessageType.JoinRoomRequest, "invalid"));
        Reject<ArgumentException>(() => PacketCodec.EncodeEcho(MessageType.JoinRoomResponse, "invalid"));
        Console.WriteLine("PASS Room protocol wire bytes, framing, boundaries, and invalid payloads");
    }

    private static void VerifyFraming(byte[] frame, MessageType expectedType)
    {
        for (int split = 1; split < frame.Length; split++)
        {
            var reader = new PacketReader();
            int count = 0;
            void Receive(MessageType type, byte[] payload)
            {
                Require(type == expectedType && payload.SequenceEqual(frame[3..]), "Framed payload");
                count++;
            }
            reader.Feed(frame.AsSpan(0, split), Receive);
            Require(count == 0 && reader.HasPartialFrame, "Partial frame waits");
            reader.Feed(frame.AsSpan(split), Receive);
            Require(count == 1 && !reader.HasPartialFrame, "Split frame completes once");
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
