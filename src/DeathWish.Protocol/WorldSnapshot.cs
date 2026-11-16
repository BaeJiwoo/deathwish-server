using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.IO;

namespace DeathWish.Protocol;

public readonly struct SnapshotPlayer
{
    public long PlayerId { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }

    public SnapshotPlayer(long playerId, float x, float y, float z)
    {
        PlayerId = playerId;
        X = x;
        Y = y;
        Z = z;
    }
}

public sealed class WorldSnapshot
{
    public int RoomId { get; }
    public long ServerTick { get; }
    public ReadOnlyCollection<SnapshotPlayer> Players { get; }

    public WorldSnapshot(int roomId, long serverTick, SnapshotPlayer[] players)
    {
        RoomId = roomId;
        ServerTick = serverTick;
        Players = Array.AsReadOnly((SnapshotPlayer[])players.Clone());
    }
}

public static class SnapshotCodec
{
    private const int ROOM_ID_OFFSET = 0;
    private const int SERVER_TICK_OFFSET = 4;
    private const int PLAYER_COUNT_OFFSET = 12;
    private const int PLAYER_ID_OFFSET = 0;
    private const int POSITION_X_OFFSET = 8;
    private const int POSITION_Y_OFFSET = 12;
    private const int POSITION_Z_OFFSET = 16;
    public const int MAX_PLAYERS = 1024;
    public const int HEADER_SIZE = 14;
    public const int PLAYER_SIZE = 20;
    public const int MAX_PAYLOAD_LENGTH = HEADER_SIZE + MAX_PLAYERS * PLAYER_SIZE;

    public static byte[] Encode(WorldSnapshot snapshot)
    {
        Validate(snapshot);
        int length = HEADER_SIZE + snapshot.Players.Count * PLAYER_SIZE;
        byte[] frame = new byte[PacketCodec.HEADER_SIZE + length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)length);
        frame[2] = (byte)MessageType.WorldSnapshot;
        Span<byte> payload = frame.AsSpan(PacketCodec.HEADER_SIZE);
        BinaryPrimitives.WriteInt32BigEndian(payload.Slice(ROOM_ID_OFFSET), snapshot.RoomId);
        BinaryPrimitives.WriteInt64BigEndian(payload.Slice(SERVER_TICK_OFFSET), snapshot.ServerTick);
        BinaryPrimitives.WriteUInt16BigEndian(payload.Slice(PLAYER_COUNT_OFFSET), (ushort)snapshot.Players.Count);
        int offset = HEADER_SIZE;
        foreach (SnapshotPlayer player in snapshot.Players)
        {
            BinaryPrimitives.WriteInt64BigEndian(payload.Slice(offset + PLAYER_ID_OFFSET), player.PlayerId);
            WriteFloat(payload.Slice(offset + POSITION_X_OFFSET), player.X);
            WriteFloat(payload.Slice(offset + POSITION_Y_OFFSET), player.Y);
            WriteFloat(payload.Slice(offset + POSITION_Z_OFFSET), player.Z);
            offset += PLAYER_SIZE;
        }
        return frame;
    }

    public static WorldSnapshot Decode(byte[] payload)
    {
        if (payload.Length < HEADER_SIZE)
        {
            throw new InvalidDataException("Incomplete snapshot header.");
        }
        int count = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(PLAYER_COUNT_OFFSET));
        if (count > MAX_PLAYERS || payload.Length != HEADER_SIZE + count * PLAYER_SIZE)
        {
            throw new InvalidDataException("Invalid snapshot length.");
        }
        var players = new SnapshotPlayer[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = payload.AsSpan(HEADER_SIZE + i * PLAYER_SIZE);
            players[i] = new SnapshotPlayer(BinaryPrimitives.ReadInt64BigEndian(entry.Slice(PLAYER_ID_OFFSET)),
                ReadFloat(entry.Slice(POSITION_X_OFFSET)), ReadFloat(entry.Slice(POSITION_Y_OFFSET)), ReadFloat(entry.Slice(POSITION_Z_OFFSET)));
        }
        var snapshot = new WorldSnapshot(BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(ROOM_ID_OFFSET)),
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(SERVER_TICK_OFFSET)), players);
        Validate(snapshot);
        return snapshot;
    }

    private static void Validate(WorldSnapshot snapshot)
    {
        if (snapshot.RoomId <= 0 || snapshot.ServerTick <= 0 || snapshot.Players.Count > MAX_PLAYERS)
        {
            throw new InvalidDataException("Invalid snapshot header.");
        }
        var ids = new HashSet<long>();
        foreach (SnapshotPlayer player in snapshot.Players)
        {
            if (player.PlayerId <= 0 || !ids.Add(player.PlayerId) ||
                !IsFinite(player.X) || !IsFinite(player.Y) || !IsFinite(player.Z))
            {
                throw new InvalidDataException("Invalid snapshot player.");
            }
        }
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static void WriteFloat(Span<byte> bytes, float value) =>
        BinaryPrimitives.WriteInt32BigEndian(bytes, BitConverter.SingleToInt32Bits(value));
    private static float ReadFloat(ReadOnlySpan<byte> bytes) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes));
}
