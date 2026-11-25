using System;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO;

namespace DeathWish.Protocol;

public readonly struct AnimatedPlayer
{
    public long PlayerId { get; }
    public short YawCentidegrees { get; }
    public ReadOnlyCollection<short> BonePitchCentidegrees { get; }

    public AnimatedPlayer(long playerId, short yawCentidegrees, short[] bonePitchCentidegrees)
    {
        PlayerId = playerId;
        YawCentidegrees = yawCentidegrees;
        BonePitchCentidegrees = Array.AsReadOnly((short[])bonePitchCentidegrees.Clone());
    }
}

public sealed class AnimatedWorldSnapshot
{
    public WorldSnapshot Positions { get; }
    public long ServerTimeMicroseconds { get; }
    public uint RigId { get; }
    public ReadOnlyCollection<AnimatedPlayer> Players { get; }

    public AnimatedWorldSnapshot(WorldSnapshot positions, long serverTimeMicroseconds, AnimatedPlayer[] players,
        uint rigId = AnimationRig.RIG_ID)
    {
        Positions = positions;
        ServerTimeMicroseconds = serverTimeMicroseconds;
        RigId = rigId;
        Players = Array.AsReadOnly((AnimatedPlayer[])players.Clone());
    }
}

public static class AnimatedSnapshotCodec
{
    private const int POSE_SIZE = (1 + AnimationRig.BONE_COUNT) * sizeof(short);
    private const int METADATA_SIZE = sizeof(uint) + sizeof(long);
    private const int MAX_ANGLE_CENTIDEGREES = 18_000;
    public const int MAX_PAYLOAD_LENGTH = SnapshotCodec.MAX_PAYLOAD_LENGTH + METADATA_SIZE +
        SnapshotCodec.MAX_PLAYERS * POSE_SIZE;

    public static byte[] Encode(AnimatedWorldSnapshot snapshot)
    {
        SnapshotCodec.Validate(snapshot.Positions);
        Validate(snapshot);
        int positionSize = SnapshotCodec.HEADER_SIZE + snapshot.Positions.Players.Count * SnapshotCodec.PLAYER_SIZE;
        int payloadSize = positionSize + METADATA_SIZE + snapshot.Players.Count * POSE_SIZE;
        var frame = new byte[PacketCodec.HEADER_SIZE + payloadSize];
        BinaryPrimitives.WriteUInt16BigEndian(frame, checked((ushort)payloadSize));
        frame[2] = (byte)MessageType.AnimatedWorldSnapshot;

        Span<byte> payload = frame.AsSpan(PacketCodec.HEADER_SIZE);
        SnapshotCodec.WritePayload(payload.Slice(0, positionSize), snapshot.Positions);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(positionSize), snapshot.RigId);
        BinaryPrimitives.WriteInt64BigEndian(payload.Slice(positionSize + sizeof(uint)), snapshot.ServerTimeMicroseconds);
        int offset = positionSize + METADATA_SIZE;
        foreach (AnimatedPlayer player in snapshot.Players)
        {
            BinaryPrimitives.WriteInt16BigEndian(payload.Slice(offset), player.YawCentidegrees);
            offset += sizeof(short);
            foreach (short pitch in player.BonePitchCentidegrees)
            {
                BinaryPrimitives.WriteInt16BigEndian(payload.Slice(offset), pitch);
                offset += sizeof(short);
            }
        }
        return frame;
    }

    public static AnimatedWorldSnapshot Decode(byte[] payload)
    {
        if (payload.Length < SnapshotCodec.HEADER_SIZE + METADATA_SIZE)
        {
            throw new InvalidDataException("Incomplete animated snapshot header.");
        }

        int count = SnapshotCodec.ReadPlayerCount(payload);
        int positionSize = SnapshotCodec.HEADER_SIZE + count * SnapshotCodec.PLAYER_SIZE;
        if (count > SnapshotCodec.MAX_PLAYERS || payload.Length != positionSize + METADATA_SIZE + count * POSE_SIZE)
        {
            throw new InvalidDataException("Invalid animated snapshot length.");
        }

        WorldSnapshot positions = SnapshotCodec.DecodePayload(payload.AsSpan(0, positionSize));
        uint rigId = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(positionSize));
        long time = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(positionSize + sizeof(uint)));
        var players = new AnimatedPlayer[count];
        var pitches = new short[AnimationRig.BONE_COUNT];
        int offset = positionSize + METADATA_SIZE;
        for (int index = 0; index < count; index++)
        {
            short yaw = BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(offset));
            offset += sizeof(short);
            for (int bone = 0; bone < pitches.Length; bone++)
            {
                pitches[bone] = BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(offset));
                offset += sizeof(short);
            }
            players[index] = new AnimatedPlayer(positions.Players[index].PlayerId, yaw, pitches);
        }

        var snapshot = new AnimatedWorldSnapshot(positions, time, players, rigId);
        Validate(snapshot);
        return snapshot;
    }

    private static void Validate(AnimatedWorldSnapshot snapshot)
    {
        if (snapshot.ServerTimeMicroseconds <= 0 || snapshot.RigId == 0 ||
            snapshot.Players.Count != snapshot.Positions.Players.Count)
        {
            throw new InvalidDataException("Invalid animated snapshot metadata.");
        }
        for (int index = 0; index < snapshot.Players.Count; index++)
        {
            AnimatedPlayer player = snapshot.Players[index];
            if (player.PlayerId != snapshot.Positions.Players[index].PlayerId ||
                player.BonePitchCentidegrees?.Count != AnimationRig.BONE_COUNT ||
                !IsValidAngle(player.YawCentidegrees))
            {
                throw new InvalidDataException("Invalid animated snapshot player.");
            }
            foreach (short pitch in player.BonePitchCentidegrees!)
            {
                if (!IsValidAngle(pitch))
                {
                    throw new InvalidDataException("Invalid animated snapshot pitch.");
                }
            }
        }
    }

    private static bool IsValidAngle(short centidegrees) =>
        centidegrees >= -MAX_ANGLE_CENTIDEGREES && centidegrees <= MAX_ANGLE_CENTIDEGREES;
}
