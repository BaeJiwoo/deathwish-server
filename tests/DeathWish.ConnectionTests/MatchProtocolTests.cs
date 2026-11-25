using System.Numerics;
using DeathWish.Protocol;

internal static class MatchProtocolTests
{
    internal static void Run()
    {
        VerifyNamedAdmission();
        VerifyState();
        VerifyKill();
        VerifyArenaShot();
        VerifyLatency();
        Console.WriteLine("PASS TeamDeathmatch protocol framing, nickname validation, state, kill and firing events, and latency");
    }

    private static void VerifyNamedAdmission()
    {
        byte[] frame = MatchCodec.EncodeJoinRequest(0x01020304, " A ");
        Require(frame.SequenceEqual(new byte[] { 0, 5, 11, 1, 2, 3, 4, 65 }), "Named join wire bytes");
        VerifyFraming(frame, MessageType.JoinMatchRequest);
        Require(!MatchCodec.DecodeJoinRequest(frame[PacketCodec.HEADER_SIZE..]).ReceiveArenaShots, "Legacy join does not opt in");

        byte[] optedIn = MatchCodec.EncodeJoinRequest(0x01020304, " A ", receiveArenaShots: true);
        Require(optedIn.SequenceEqual(new byte[] { 0, 7, 11, 1, 2, 3, 4, 0, 1, 65 }), "Arena shot opt-in wire bytes");
        VerifyFraming(optedIn, MessageType.JoinMatchRequest);
        JoinMatchRequest optedInRequest = MatchCodec.DecodeJoinRequest(optedIn[PacketCodec.HEADER_SIZE..]);
        Require(optedInRequest.ReceiveArenaShots && optedInRequest.Nickname == "A", "Named join enables arena shot events");
        Reject(() => MatchCodec.DecodeJoinRequest(new byte[] { 0, 0, 0, 1, 0, 1 }));
        Reject(() => MatchCodec.DecodeJoinRequest(new byte[] { 0, 0, 0, 1, 0, 2, 65 }));

        string emoji = char.ConvertFromUtf32(0x1F44B);
        string longest = string.Concat(Enumerable.Repeat(emoji, MatchCodec.MAX_NICKNAME_SCALARS));
        foreach (string nickname in new[] { "한 글", "별" + emoji, longest })
        {
            byte[] encoded = MatchCodec.EncodeJoinRequest(2, " " + nickname + " ");
            JoinMatchRequest decoded = MatchCodec.DecodeJoinRequest(encoded[PacketCodec.HEADER_SIZE..]);
            Require(decoded.RoomId == 2 && decoded.Nickname == nickname, "Converts Unicode nickname to canonical wire form");
        }

        foreach (string nickname in new[] { "", "  ", "A\nB", "A\u0000B", "<b>A</b>", "A\u202EB", "\uD800", longest + emoji })
        {
            Reject(() => MatchCodec.EncodeJoinRequest(1, nickname));
        }
        Reject(() => MatchCodec.EncodeJoinRequest(0, "valid"));
        Reject(() => MatchCodec.DecodeJoinRequest(new byte[] { 0, 0, 0, 1, 32, 65 }));
        Reject(() => MatchCodec.DecodeJoinRequest(new byte[] { 0, 0, 0, 1, 0xC0, 0x80 }));
    }

    private static void VerifyArenaShot()
    {
        var shot = new ArenaShot(2, 7, 9, new Vector3(1, -2, 3), new Vector3(4, 5, 6), ArenaShotOutcome.Damage);
        byte[] frame = MatchCodec.EncodeShot(shot);
        byte[] expected =
        {
            0, 45, 16, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 7, 0, 0, 0, 0, 0, 0, 0, 9,
            0x3F, 0x80, 0, 0, 0xC0, 0, 0, 0, 0x40, 0x40, 0, 0,
            0x40, 0x80, 0, 0, 0x40, 0xA0, 0, 0, 0x40, 0xC0, 0, 0, 3
        };
        Require(frame.SequenceEqual(expected), "Arena shot wire bytes");
        VerifyFraming(frame, MessageType.ArenaShot);

        byte[] payload = frame[PacketCodec.HEADER_SIZE..];
        ArenaShot decoded = MatchCodec.DecodeShot(payload);
        Require(decoded.RoomId == 2 && decoded.ServerTick == 7 && decoded.ShooterPlayerId == 9 &&
            decoded.OriginMeters == shot.OriginMeters && decoded.EndMeters == shot.EndMeters &&
            decoded.Outcome == ArenaShotOutcome.Damage, "Arena shot round trip");
        Reject(() => MatchCodec.DecodeShot(payload[..^1]));
        Reject(() => MatchCodec.DecodeShot(payload.Append((byte)0).ToArray()));
        payload[^1] = byte.MaxValue;
        Reject(() => MatchCodec.DecodeShot(payload));
        Reject(() => MatchCodec.EncodeShot(new ArenaShot(2, 7, 0, Vector3.Zero, Vector3.One, ArenaShotOutcome.Miss)));
        Reject(() => MatchCodec.EncodeShot(new ArenaShot(2, 7, 9, Vector3.Zero, new Vector3(float.NaN), ArenaShotOutcome.Miss)));
    }

    private static void VerifyState()
    {
        var original = new[]
        {
            new MatchPlayer(42, "공격", TeamId.Red, 75, 1, 2, 3, 8),
            new MatchPlayer(84, "Blue", TeamId.Blue, 0, 0, 1, 0, MatchState.UNKNOWN_PING_MS)
        };
        var state = new MatchState(2, 0x0102030405060708, MatchPhase.Finished, 10, 2, TeamId.Red, original);
        original[0] = default;
        byte[] frame = MatchCodec.EncodeState(state);
        VerifyFraming(frame, MessageType.MatchState);

        byte[] payload = frame[PacketCodec.HEADER_SIZE..];
        byte[] header = { 0, 0, 0, 2, 1, 2, 3, 4, 5, 6, 7, 8, 2, 1, 0, 0, 0, 10, 0, 0, 0, 2, 2 };
        Require(payload.AsSpan(0, header.Length).SequenceEqual(header), "Match state header wire bytes");

        MatchState decoded = MatchCodec.DecodeState(payload);
        Require(decoded.RoomId == 2 && decoded.ServerTick == state.ServerTick && decoded.Phase == MatchPhase.Finished &&
            decoded.RedScore == 10 && decoded.BlueScore == 2 && decoded.Winner == TeamId.Red, "Round trips match header");
        MatchPlayer player = decoded.Players[0];
        Require(player.PlayerId == 42 && player.Nickname == "공격" && player.Team == TeamId.Red && player.Health == 75 &&
            player.Kills == 1 && player.Deaths == 2 && player.Assists == 3 && player.PingMs == 8, "Round trips player state");
        Require(decoded.Players[1].Health == 0 && decoded.Players[1].PingMs == -1, "Accepts death and unknown ping");

        for (int size = 0; size < payload.Length; size++)
        {
            Reject(() => MatchCodec.DecodeState(payload[..size]));
        }
        Reject(() => MatchCodec.DecodeState(payload.Append((byte)0).ToArray()));

        byte[] noWinner = (byte[])payload.Clone();
        noWinner[13] = 0;
        Reject(() => MatchCodec.DecodeState(noWinner));
        Reject(() => MatchCodec.EncodeState(new MatchState(2, 1, MatchPhase.Playing, 0, 0,
            null, new[] { player, player })));
        Reject(() => MatchCodec.EncodeState(new MatchState(2, 1, MatchPhase.Waiting, 0, 0,
            null, Enumerable.Range(1, 6).Select(id => new MatchPlayer(id, "red", TeamId.Red, 100, 0, 0, 0, -1)).ToArray())));
        Reject(() => MatchCodec.EncodeState(new MatchState(2, 1, MatchPhase.Waiting, 0, 0,
            null, new[] { new MatchPlayer(1, "player", TeamId.Red, 101, 0, 0, 0, -1) })));
    }

    private static void VerifyKill()
    {
        var kill = new KillEvent(2, 7, 9, "공격", 10, "피해");
        byte[] frame = MatchCodec.EncodeKill(kill);
        VerifyFraming(frame, MessageType.KillEvent);

        byte[] payload = frame[PacketCodec.HEADER_SIZE..];
        byte[] header = { 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 7, 0, 0, 0, 0, 0, 0, 0, 9, 0, 0, 0, 0, 0, 0, 0, 10 };
        Require(payload.AsSpan(0, header.Length).SequenceEqual(header), "Kill header wire bytes");
        KillEvent decoded = MatchCodec.DecodeKill(payload);
        Require(decoded.RoomId == 2 && decoded.ServerTick == 7 && decoded.AttackerId == 9 && decoded.VictimId == 10 &&
            decoded.AttackerNickname == "공격" && decoded.VictimNickname == "피해", "Round trips kill event");
        Reject(() => MatchCodec.DecodeKill(payload[..^1]));
        Reject(() => MatchCodec.DecodeKill(payload.Append((byte)0).ToArray()));
        Reject(() => MatchCodec.EncodeKill(new KillEvent(2, 1, 5, "same", 5, "same")));
    }

    private static void VerifyLatency()
    {
        const long TOKEN = 0x0102030405060708;
        byte[] probe = PingCodec.EncodeProbe(TOKEN);
        byte[] reply = PingCodec.EncodeReply(TOKEN);
        Require(probe.SequenceEqual(new byte[] { 0, 8, 14, 1, 2, 3, 4, 5, 6, 7, 8 }), "Latency probe wire bytes");
        Require(reply.SequenceEqual(new byte[] { 0, 8, 15, 1, 2, 3, 4, 5, 6, 7, 8 }), "Latency reply wire bytes");
        VerifyFraming(probe, MessageType.LatencyProbe);
        VerifyFraming(reply, MessageType.LatencyReply);
        Require(PingCodec.DecodeProbe(probe[PacketCodec.HEADER_SIZE..]) == TOKEN, "Round trips latency probe");
        Require(PingCodec.DecodeReply(reply[PacketCodec.HEADER_SIZE..]) == TOKEN, "Round trips latency reply");
        Reject(() => PingCodec.EncodeProbe(0));
        Reject(() => PingCodec.EncodeReply(-1));
        Reject(() => PingCodec.DecodeProbe(new byte[7]));
        Reject(() => PingCodec.DecodeReply(new byte[8]));
        Reject(() => PingCodec.DecodeReply(new byte[9]));
    }

    private static void VerifyFraming(byte[] frame, MessageType expectedType)
    {
        var reader = new PacketReader();
        int received = 0;
        foreach (byte value in frame)
        {
            reader.Feed(new[] { value }, (type, payload) =>
            {
                Require(type == expectedType && payload.SequenceEqual(frame[PacketCodec.HEADER_SIZE..]), "Frame type and exact payload");
                received++;
            });
        }
        Require(received == 1 && !reader.HasPartialFrame, "One complete match frame");
    }

    private static void Reject(Action decode)
    {
        try
        {
            decode();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new Exception("Expected InvalidDataException.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
