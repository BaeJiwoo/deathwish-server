using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace DeathWish.Protocol;

public static class MatchCodec
{
    public const int MAX_NICKNAME_SCALARS = 16;
    private const int MAX_NICKNAME_BYTES = MAX_NICKNAME_SCALARS * 4;
    private const int JOIN_EXTENSION_SIZE = 2;
    private const byte JOIN_EXTENSION_MARKER = 0;
    private const byte JOIN_ARENA_SHOTS_FLAG = 1;
    private const int MATCH_TICK_OFFSET = sizeof(int);
    private const int MATCH_PHASE_OFFSET = MATCH_TICK_OFFSET + sizeof(long);
    private const int MATCH_WINNER_OFFSET = MATCH_PHASE_OFFSET + sizeof(byte);
    private const int RED_SCORE_OFFSET = MATCH_WINNER_OFFSET + sizeof(byte);
    private const int BLUE_SCORE_OFFSET = RED_SCORE_OFFSET + sizeof(int);
    private const int PLAYER_COUNT_OFFSET = BLUE_SCORE_OFFSET + sizeof(int);
    private const int STATE_HEADER_SIZE = PLAYER_COUNT_OFFSET + sizeof(byte);
    private const int PLAYER_TEAM_OFFSET = sizeof(long);
    private const int PLAYER_HEALTH_OFFSET = PLAYER_TEAM_OFFSET + sizeof(byte);
    private const int PLAYER_KILLS_OFFSET = PLAYER_HEALTH_OFFSET + sizeof(byte);
    private const int PLAYER_DEATHS_OFFSET = PLAYER_KILLS_OFFSET + sizeof(int);
    private const int PLAYER_ASSISTS_OFFSET = PLAYER_DEATHS_OFFSET + sizeof(int);
    private const int PLAYER_PING_OFFSET = PLAYER_ASSISTS_OFFSET + sizeof(int);
    private const int PLAYER_FIXED_SIZE = PLAYER_PING_OFFSET + sizeof(int);
    private const int ATTACKER_ID_OFFSET = MATCH_TICK_OFFSET + sizeof(long);
    private const int VICTIM_ID_OFFSET = ATTACKER_ID_OFFSET + sizeof(long);
    private const int KILL_HEADER_SIZE = VICTIM_ID_OFFSET + sizeof(long);
    private const int SHOT_SHOOTER_ID_OFFSET = MATCH_TICK_OFFSET + sizeof(long);
    private const int SHOT_ORIGIN_OFFSET = SHOT_SHOOTER_ID_OFFSET + sizeof(long);
    private const int SHOT_END_OFFSET = SHOT_ORIGIN_OFFSET + ActionCodec.VECTOR_SIZE;
    private const int SHOT_OUTCOME_OFFSET = SHOT_END_OFFSET + ActionCodec.VECTOR_SIZE;
    private const int SHOT_PAYLOAD_SIZE = SHOT_OUTCOME_OFFSET + sizeof(byte);

    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Trims surrounding whitespace and validates a nickname of 1 to 16 Unicode scalar values.</summary>
    public static string NormalizeNickname(string nickname)
    {
        if (nickname is null)
        {
            throw new ArgumentNullException(nameof(nickname));
        }

        string normalized = nickname.Trim();
        ValidateNickname(normalized);
        return normalized;
    }

    public static byte[] EncodeJoinRequest(int roomId, string nickname)
    {
        return EncodeJoinRequest(roomId, nickname, false);
    }

    public static byte[] EncodeJoinRequest(int roomId, string nickname, bool receiveArenaShots)
    {
        ValidateRoomId(roomId);
        string normalized = NormalizeNickname(nickname);
        int nicknameOffset = sizeof(int) + (receiveArenaShots ? JOIN_EXTENSION_SIZE : 0);
        var frame = CreateFrame(MessageType.JoinMatchRequest, nicknameOffset + Utf8.GetByteCount(normalized));
        Span<byte> payload = frame.AsSpan(PacketCodec.HEADER_SIZE);
        BinaryPrimitives.WriteInt32BigEndian(payload, roomId);
        if (receiveArenaShots)
        {
            payload[sizeof(int)] = JOIN_EXTENSION_MARKER;
            payload[sizeof(int) + 1] = JOIN_ARENA_SHOTS_FLAG;
        }
        Utf8.GetBytes(normalized.AsSpan(), payload.Slice(nicknameOffset));
        return frame;
    }

    public static JoinMatchRequest DecodeJoinRequest(byte[] payload)
    {
        RequireLength(payload, sizeof(int) + 1, sizeof(int) + JOIN_EXTENSION_SIZE + MAX_NICKNAME_BYTES);
        int roomId = BinaryPrimitives.ReadInt32BigEndian(payload);
        ValidateRoomId(roomId);
        ReadOnlySpan<byte> name = payload.AsSpan(sizeof(int));
        // NUL is forbidden in a nickname, so it distinguishes the optional join extension.
        bool receiveArenaShots = name[0] == JOIN_EXTENSION_MARKER;
        if (receiveArenaShots)
        {
            if (name.Length <= JOIN_EXTENSION_SIZE || name[1] != JOIN_ARENA_SHOTS_FLAG)
            {
                throw new InvalidDataException("Invalid match join extension.");
            }
            name = name.Slice(JOIN_EXTENSION_SIZE);
        }
        if (name.Length > MAX_NICKNAME_BYTES)
        {
            throw new InvalidDataException("Nickname is too long.");
        }
        return new JoinMatchRequest(roomId, DecodeNickname(name), receiveArenaShots);
    }

    public static byte[] EncodeState(MatchState state)
    {
        ValidateState(state);
        int payloadSize = STATE_HEADER_SIZE;
        foreach (MatchPlayer player in state.Players)
        {
            payloadSize += PLAYER_FIXED_SIZE + 1 + Utf8.GetByteCount(player.Nickname);
        }

        var frame = CreateFrame(MessageType.MatchState, payloadSize);
        Span<byte> payload = frame.AsSpan(PacketCodec.HEADER_SIZE);
        WriteMatchIdentity(payload, state.RoomId, state.ServerTick);
        payload[MATCH_PHASE_OFFSET] = (byte)state.Phase;
        payload[MATCH_WINNER_OFFSET] = (byte)(state.Winner ?? 0);
        BinaryPrimitives.WriteInt32BigEndian(payload.Slice(RED_SCORE_OFFSET), state.RedScore);
        BinaryPrimitives.WriteInt32BigEndian(payload.Slice(BLUE_SCORE_OFFSET), state.BlueScore);
        payload[PLAYER_COUNT_OFFSET] = (byte)state.Players.Count;

        Span<byte> remaining = payload.Slice(STATE_HEADER_SIZE);
        foreach (MatchPlayer player in state.Players)
        {
            BinaryPrimitives.WriteInt64BigEndian(remaining, player.PlayerId);
            remaining[PLAYER_TEAM_OFFSET] = (byte)player.Team;
            remaining[PLAYER_HEALTH_OFFSET] = (byte)player.Health;
            BinaryPrimitives.WriteInt32BigEndian(remaining.Slice(PLAYER_KILLS_OFFSET), player.Kills);
            BinaryPrimitives.WriteInt32BigEndian(remaining.Slice(PLAYER_DEATHS_OFFSET), player.Deaths);
            BinaryPrimitives.WriteInt32BigEndian(remaining.Slice(PLAYER_ASSISTS_OFFSET), player.Assists);
            BinaryPrimitives.WriteInt32BigEndian(remaining.Slice(PLAYER_PING_OFFSET), player.PingMs);
            remaining = remaining.Slice(PLAYER_FIXED_SIZE);
            WriteNickname(ref remaining, player.Nickname);
        }
        return frame;
    }

    public static MatchState DecodeState(byte[] payload)
    {
        RequireLength(payload, STATE_HEADER_SIZE,
            STATE_HEADER_SIZE + MatchState.MAX_PLAYERS * (PLAYER_FIXED_SIZE + 1 + MAX_NICKNAME_BYTES));
        int count = payload[PLAYER_COUNT_OFFSET];
        if (count > MatchState.MAX_PLAYERS)
        {
            throw new InvalidDataException("Too many match players.");
        }

        var players = new MatchPlayer[count];
        ReadOnlySpan<byte> remaining = payload.AsSpan(STATE_HEADER_SIZE);
        for (int index = 0; index < players.Length; index++)
        {
            if (remaining.Length < PLAYER_FIXED_SIZE)
            {
                throw new InvalidDataException("Incomplete match player.");
            }

            ReadOnlySpan<byte> fields = remaining.Slice(0, PLAYER_FIXED_SIZE);
            remaining = remaining.Slice(PLAYER_FIXED_SIZE);
            string nickname = ReadNickname(ref remaining);
            players[index] = new MatchPlayer(BinaryPrimitives.ReadInt64BigEndian(fields), nickname,
                (TeamId)fields[PLAYER_TEAM_OFFSET], fields[PLAYER_HEALTH_OFFSET],
                BinaryPrimitives.ReadInt32BigEndian(fields.Slice(PLAYER_KILLS_OFFSET)),
                BinaryPrimitives.ReadInt32BigEndian(fields.Slice(PLAYER_DEATHS_OFFSET)),
                BinaryPrimitives.ReadInt32BigEndian(fields.Slice(PLAYER_ASSISTS_OFFSET)),
                BinaryPrimitives.ReadInt32BigEndian(fields.Slice(PLAYER_PING_OFFSET)));
        }
        RequireEnd(remaining);

        byte winner = payload[MATCH_WINNER_OFFSET];
        var state = new MatchState(BinaryPrimitives.ReadInt32BigEndian(payload),
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(MATCH_TICK_OFFSET)),
            (MatchPhase)payload[MATCH_PHASE_OFFSET],
            BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(RED_SCORE_OFFSET)),
            BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(BLUE_SCORE_OFFSET)),
            winner == 0 ? null : (TeamId)winner, players);
        ValidateState(state);
        return state;
    }

    public static byte[] EncodeKill(KillEvent kill)
    {
        ValidateKill(kill);
        int payloadSize = KILL_HEADER_SIZE + 2 + Utf8.GetByteCount(kill.AttackerNickname) + Utf8.GetByteCount(kill.VictimNickname);
        var frame = CreateFrame(MessageType.KillEvent, payloadSize);
        Span<byte> payload = frame.AsSpan(PacketCodec.HEADER_SIZE);
        WriteMatchIdentity(payload, kill.RoomId, kill.ServerTick);
        BinaryPrimitives.WriteInt64BigEndian(payload.Slice(ATTACKER_ID_OFFSET), kill.AttackerId);
        BinaryPrimitives.WriteInt64BigEndian(payload.Slice(VICTIM_ID_OFFSET), kill.VictimId);
        Span<byte> remaining = payload.Slice(KILL_HEADER_SIZE);
        WriteNickname(ref remaining, kill.AttackerNickname);
        WriteNickname(ref remaining, kill.VictimNickname);
        return frame;
    }

    public static KillEvent DecodeKill(byte[] payload)
    {
        RequireLength(payload, KILL_HEADER_SIZE + 4, KILL_HEADER_SIZE + 2 * (1 + MAX_NICKNAME_BYTES));
        ReadOnlySpan<byte> remaining = payload.AsSpan(KILL_HEADER_SIZE);
        string attacker = ReadNickname(ref remaining);
        string victim = ReadNickname(ref remaining);
        RequireEnd(remaining);

        var kill = new KillEvent(BinaryPrimitives.ReadInt32BigEndian(payload),
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(MATCH_TICK_OFFSET)),
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(ATTACKER_ID_OFFSET)), attacker,
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(VICTIM_ID_OFFSET)), victim);
        ValidateKill(kill);
        return kill;
    }

    public static byte[] EncodeShot(ArenaShot shot)
    {
        ValidateShot(shot);
        var frame = CreateFrame(MessageType.ArenaShot, SHOT_PAYLOAD_SIZE);
        Span<byte> payload = frame.AsSpan(PacketCodec.HEADER_SIZE);
        WriteMatchIdentity(payload, shot.RoomId, shot.ServerTick);
        BinaryPrimitives.WriteInt64BigEndian(payload.Slice(SHOT_SHOOTER_ID_OFFSET), shot.ShooterPlayerId);
        ActionCodec.WriteVector(payload.Slice(SHOT_ORIGIN_OFFSET), shot.OriginMeters);
        ActionCodec.WriteVector(payload.Slice(SHOT_END_OFFSET), shot.EndMeters);
        payload[SHOT_OUTCOME_OFFSET] = (byte)shot.Outcome;
        return frame;
    }

    public static ArenaShot DecodeShot(byte[] payload)
    {
        RequireLength(payload, SHOT_PAYLOAD_SIZE, SHOT_PAYLOAD_SIZE);
        var shot = new ArenaShot(BinaryPrimitives.ReadInt32BigEndian(payload),
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(MATCH_TICK_OFFSET)),
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(SHOT_SHOOTER_ID_OFFSET)),
            ActionCodec.ReadVector(payload.AsSpan(SHOT_ORIGIN_OFFSET)), ActionCodec.ReadVector(payload.AsSpan(SHOT_END_OFFSET)),
            (ArenaShotOutcome)payload[SHOT_OUTCOME_OFFSET]);
        ValidateShot(shot);
        return shot;
    }

    private static void ValidateShot(ArenaShot shot)
    {
        ValidateMatchIdentity(shot.RoomId, shot.ServerTick);
        if (shot.ShooterPlayerId <= 0 || !IsFinite(shot.OriginMeters) || !IsFinite(shot.EndMeters) ||
            shot.Outcome is not (ArenaShotOutcome.Miss or ArenaShotOutcome.Terrain or ArenaShotOutcome.PlayerContact or ArenaShotOutcome.Damage))
        {
            throw new InvalidDataException("Invalid arena shot.");
        }
    }

    private static bool IsFinite(Vector3 point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

    private static void ValidateState(MatchState state)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        ValidateMatchIdentity(state.RoomId, state.ServerTick);
        bool hasValidWinner = state.Phase == MatchPhase.Finished
            ? state.Winner is TeamId.Red or TeamId.Blue
            : state.Winner is null;
        if (state.Phase is not (MatchPhase.Waiting or MatchPhase.Playing or MatchPhase.Finished) || !hasValidWinner ||
            state.RedScore < 0 || state.BlueScore < 0 || state.Players.Count > MatchState.MAX_PLAYERS)
        {
            throw new InvalidDataException("Invalid match state header.");
        }

        var playerIds = new HashSet<long>();
        int redPlayers = 0;
        int bluePlayers = 0;
        foreach (MatchPlayer player in state.Players)
        {
            bool hasValidStats = player.Health >= 0 && player.Health <= MatchState.MAX_HEALTH &&
                player.Kills >= 0 && player.Deaths >= 0 && player.Assists >= 0 &&
                player.PingMs >= MatchState.UNKNOWN_PING_MS;
            if (player.PlayerId <= 0 || !playerIds.Add(player.PlayerId) || !hasValidStats ||
                player.Team is not (TeamId.Red or TeamId.Blue))
            {
                throw new InvalidDataException("Invalid match player.");
            }
            ValidateNickname(player.Nickname);
            if (player.Team == TeamId.Red)
            {
                redPlayers++;
            }
            else
            {
                bluePlayers++;
            }
        }
        if (redPlayers > MatchState.MAX_TEAM_SIZE || bluePlayers > MatchState.MAX_TEAM_SIZE)
        {
            throw new InvalidDataException("Too many players on a team.");
        }
    }

    private static void ValidateKill(KillEvent kill)
    {
        ValidateMatchIdentity(kill.RoomId, kill.ServerTick);
        if (kill.AttackerId <= 0 || kill.VictimId <= 0 || kill.AttackerId == kill.VictimId)
        {
            throw new InvalidDataException("Invalid kill participants.");
        }
        ValidateNickname(kill.AttackerNickname);
        ValidateNickname(kill.VictimNickname);
    }

    private static void ValidateMatchIdentity(int roomId, long serverTick)
    {
        ValidateRoomId(roomId);
        if (serverTick <= 0)
        {
            throw new InvalidDataException("Match tick must be positive.");
        }
    }

    private static void ValidateRoomId(int roomId)
    {
        if (roomId <= 0)
        {
            throw new InvalidDataException("Room ID must be positive.");
        }
    }

    private static void ValidateNickname(string nickname)
    {
        if (string.IsNullOrEmpty(nickname) || char.IsWhiteSpace(nickname[0]) || char.IsWhiteSpace(nickname[nickname.Length - 1]))
        {
            throw new InvalidDataException("Enter a nickname without surrounding whitespace.");
        }

        int scalarCount = 0;
        int offset = 0;
        while (offset < nickname.Length)
        {
            char character = nickname[offset];
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(nickname, offset);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate || character is '<' or '>')
            {
                throw new InvalidDataException("Nickname contains an unsupported character.");
            }

            scalarCount++;
            if (scalarCount > MAX_NICKNAME_SCALARS)
            {
                throw new InvalidDataException("Use at most 16 characters for the nickname.");
            }
            offset += char.IsSurrogatePair(nickname, offset) ? 2 : 1;
        }
    }

    private static byte[] CreateFrame(MessageType type, int payloadSize)
    {
        var frame = new byte[PacketCodec.HEADER_SIZE + payloadSize];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)payloadSize);
        frame[2] = (byte)type;
        return frame;
    }

    private static void WriteMatchIdentity(Span<byte> payload, int roomId, long serverTick)
    {
        BinaryPrimitives.WriteInt32BigEndian(payload, roomId);
        BinaryPrimitives.WriteInt64BigEndian(payload.Slice(MATCH_TICK_OFFSET), serverTick);
    }

    private static void WriteNickname(ref Span<byte> remaining, string nickname)
    {
        int length = Utf8.GetBytes(nickname.AsSpan(), remaining.Slice(1));
        remaining[0] = (byte)length;
        remaining = remaining.Slice(1 + length);
    }

    private static string ReadNickname(ref ReadOnlySpan<byte> remaining)
    {
        if (remaining.Length < 1 || remaining[0] < 1 || remaining[0] > MAX_NICKNAME_BYTES || remaining.Length <= remaining[0])
        {
            throw new InvalidDataException("Invalid nickname length.");
        }

        int length = remaining[0];
        string nickname = DecodeNickname(remaining.Slice(1, length));
        remaining = remaining.Slice(1 + length);
        return nickname;
    }

    private static string DecodeNickname(ReadOnlySpan<byte> bytes)
    {
        string nickname;
        try
        {
            nickname = Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Nickname is not valid UTF-8.", exception);
        }
        ValidateNickname(nickname);
        return nickname;
    }

    private static void RequireLength(byte[] payload, int minimum, int maximum)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }
        if (payload.Length < minimum || payload.Length > maximum)
        {
            throw new InvalidDataException("Invalid match payload length.");
        }
    }

    private static void RequireEnd(ReadOnlySpan<byte> remaining)
    {
        if (!remaining.IsEmpty)
        {
            throw new InvalidDataException("Unexpected trailing match data.");
        }
    }
}
