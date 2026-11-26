using System;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO;

namespace DeathWish.Protocol;

public readonly struct GaitPlayer
{
    public const int PHASE_UNITS_PER_CYCLE = 1 << 16;

    public long PlayerId { get; }
    public short YawCentidegrees { get; }
    public AimAngles Aim { get; }
    public bool IsWalking { get; }
    /// <summary>Cumulative cycles in unsigned 16.16 fixed point; unsigned differences handle rollover.</summary>
    public uint CyclePhase { get; }

    public GaitPlayer(long playerId, short yawCentidegrees, bool isWalking, uint cyclePhase)
        : this(playerId, yawCentidegrees, isWalking, cyclePhase, new AimAngles(yawCentidegrees, 0))
    {
    }

    public GaitPlayer(long playerId, short yawCentidegrees, bool isWalking, uint cyclePhase, AimAngles aim)
    {
        Aim = aim;
        PlayerId = playerId;
        YawCentidegrees = yawCentidegrees;
        IsWalking = isWalking;
        CyclePhase = cyclePhase;
    }
}

public sealed class GaitWorldSnapshot
{
    public WorldSnapshot Positions { get; }
    public long ServerTimeMicroseconds { get; }
    public ReadOnlyCollection<GaitPlayer> Players { get; }

    public GaitWorldSnapshot(WorldSnapshot positions, long serverTimeMicroseconds, GaitPlayer[] players)
    {
        Positions = positions;
        ServerTimeMicroseconds = serverTimeMicroseconds;
        Players = Array.AsReadOnly((GaitPlayer[])players.Clone());
    }
}

public static class GaitSnapshotCodec
{
    private const int PLAYER_SIZE = sizeof(short) + sizeof(byte) + sizeof(uint);
    private const int MAX_YAW_CENTIDEGREES = 18_000;
    public const int MAX_PAYLOAD_LENGTH = SnapshotCodec.MAX_PAYLOAD_LENGTH + sizeof(long) +
        SnapshotCodec.MAX_PLAYERS * PLAYER_SIZE;

    public const int MAX_AIMED_PAYLOAD_LENGTH = MAX_PAYLOAD_LENGTH + SnapshotCodec.MAX_PLAYERS * AimAngles.WIRE_SIZE;

    public static byte[] Encode(GaitWorldSnapshot snapshot) => Encode(snapshot, false);

    public static byte[] Encode(GaitWorldSnapshot snapshot, bool includesAim)
    {
        SnapshotCodec.Validate(snapshot.Positions);
        int count = snapshot.Positions.Players.Count;
        if (snapshot.ServerTimeMicroseconds <= 0 || count != snapshot.Players.Count)
        {
            throw new InvalidDataException("Invalid gait snapshot metadata.");
        }
        int positionSize = SnapshotCodec.HEADER_SIZE + count * SnapshotCodec.PLAYER_SIZE;
        int playerSize = includesAim ? PLAYER_SIZE + AimAngles.WIRE_SIZE : PLAYER_SIZE;
        int payloadSize = positionSize + sizeof(long) + count * playerSize;
        var frame = new byte[PacketCodec.HEADER_SIZE + payloadSize];
        BinaryPrimitives.WriteUInt16BigEndian(frame, checked((ushort)payloadSize));
        frame[2] = (byte)(includesAim ? MessageType.AimedGaitWorldSnapshot : MessageType.GaitWorldSnapshot);

        Span<byte> payload = frame.AsSpan(PacketCodec.HEADER_SIZE);
        SnapshotCodec.WritePayload(payload.Slice(0, positionSize), snapshot.Positions);
        BinaryPrimitives.WriteInt64BigEndian(payload.Slice(positionSize), snapshot.ServerTimeMicroseconds);
        Span<byte> records = payload.Slice(positionSize + sizeof(long));
        for (int index = 0; index < count; index++)
        {
            GaitPlayer gait = snapshot.Players[index];
            if (gait.PlayerId != snapshot.Positions.Players[index].PlayerId)
            {
                throw new InvalidDataException("Gait records must follow the position roster.");
            }
            ValidateYaw(gait.YawCentidegrees);
            BinaryPrimitives.WriteInt16BigEndian(records, gait.YawCentidegrees);
            records[sizeof(short)] = gait.IsWalking ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32BigEndian(records.Slice(sizeof(short) + sizeof(byte)), gait.CyclePhase);
            if (includesAim)
            {
                gait.Aim.Write(records.Slice(PLAYER_SIZE));
            }
            records = records.Slice(playerSize);
        }
        return frame;
    }

    public static GaitWorldSnapshot Decode(byte[] payload) => Decode(payload, false);

    public static GaitWorldSnapshot Decode(byte[] payload, bool includesAim)
    {
        if (payload.Length < SnapshotCodec.HEADER_SIZE + sizeof(long))
        {
            throw new InvalidDataException("Incomplete gait snapshot header.");
        }
        int count = SnapshotCodec.ReadPlayerCount(payload);
        int positionSize = SnapshotCodec.HEADER_SIZE + count * SnapshotCodec.PLAYER_SIZE;
        int playerSize = includesAim ? PLAYER_SIZE + AimAngles.WIRE_SIZE : PLAYER_SIZE;
        if (count > SnapshotCodec.MAX_PLAYERS || payload.Length != positionSize + sizeof(long) + count * playerSize)
        {
            throw new InvalidDataException("Invalid gait snapshot length.");
        }
        WorldSnapshot positions = SnapshotCodec.DecodePayload(payload.AsSpan(0, positionSize));
        long time = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(positionSize));
        if (time <= 0)
        {
            throw new InvalidDataException("Invalid gait snapshot clock.");
        }
        var players = new GaitPlayer[count];
        ReadOnlySpan<byte> records = payload.AsSpan(positionSize + sizeof(long));
        for (int index = 0; index < count; index++)
        {
            short yaw = BinaryPrimitives.ReadInt16BigEndian(records);
            ValidateYaw(yaw);
            byte walking = records[sizeof(short)];
            if (walking > 1)
            {
                throw new InvalidDataException("Invalid gait state.");
            }
            uint phase = BinaryPrimitives.ReadUInt32BigEndian(records.Slice(sizeof(short) + sizeof(byte)));
            AimAngles aim = includesAim ? AimAngles.Read(records.Slice(PLAYER_SIZE)) : new AimAngles(yaw, 0);
            players[index] = new GaitPlayer(positions.Players[index].PlayerId, yaw, walking == 1, phase, aim);
            records = records.Slice(playerSize);
        }
        return new GaitWorldSnapshot(positions, time, players);
    }

    private static void ValidateYaw(short yawCentidegrees)
    {
        if (yawCentidegrees < -MAX_YAW_CENTIDEGREES || yawCentidegrees > MAX_YAW_CENTIDEGREES)
        {
            throw new InvalidDataException("Invalid gait heading.");
        }
    }
}
