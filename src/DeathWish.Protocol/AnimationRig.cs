using System;
using System.Collections.Generic;
using System.Numerics;

namespace DeathWish.Protocol;

public readonly struct AnimationBoneDefinition
{
    public string Name { get; }
    public int ParentIndex { get; }
    public Vector3 LocalPosition { get; }
    public Vector3 BoxOffset { get; }
    public Vector3 BoxSize { get; }

    internal AnimationBoneDefinition(string name, int parentIndex, Vector3 localPosition, Vector3 boxOffset, Vector3 boxSize)
    {
        Name = name;
        ParentIndex = parentIndex;
        LocalPosition = localPosition;
        BoxOffset = boxOffset;
        BoxSize = boxSize;
    }
}

public readonly struct AnimationBonePose
{
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public Vector3 BoxCenter { get; }
    public Vector3 BoxSize { get; }

    internal AnimationBonePose(Vector3 position, Quaternion rotation, Vector3 boxCenter, Vector3 boxSize)
    {
        Position = position;
        Rotation = rotation;
        BoxCenter = boxCenter;
        BoxSize = boxSize;
    }
}

/// <summary>DeathWish presentation rig and the ShooterPack forward walk sampled in the shared coordinate system.</summary>
public static class AnimationRig
{
    public const uint RIG_ID = 1;
    public const int BONE_COUNT = 11;
    public const int WALK_SAMPLE_RATE_HZ = 60;
    public const float WALK_DURATION_SECONDS = 1f;
    private const int WALK_SAMPLE_COUNT = (int)(WALK_SAMPLE_RATE_HZ * WALK_DURATION_SECONDS);
    private const float RADIANS_PER_CENTIDEGREE = MathF.PI / 18000f;

    // Parent-before-child order is also the wire order. Version the rig ID when the bind data changes.
    private static readonly IReadOnlyList<AnimationBoneDefinition> Definitions = Array.AsReadOnly(new[]
    {
        new AnimationBoneDefinition("Root", -1, Vector3.Zero, Vector3.Zero, Vector3.Zero),
        new AnimationBoneDefinition("Torso", 0, new Vector3(0, 0.92f, 0),
            new Vector3(0, 0.275f, 0), new Vector3(0.38f, 0.53f, 0.24f)),
        new AnimationBoneDefinition("Head", 1, new Vector3(0, 0.56f, 0),
            new Vector3(0, 0.16f, 0), new Vector3(0.305f, 0.32f, 0.27f)),
        new AnimationBoneDefinition("LeftUpperArm", 1, new Vector3(-0.255f, 0.48f, 0),
            new Vector3(0, -0.14f, 0), new Vector3(0.14f, 0.28f, 0.15f)),
        new AnimationBoneDefinition("LeftForearm", 3, new Vector3(0, -0.28f, 0),
            new Vector3(0, -0.1375f, 0), new Vector3(0.115f, 0.275f, 0.13f)),
        new AnimationBoneDefinition("RightUpperArm", 1, new Vector3(0.255f, 0.48f, 0),
            new Vector3(0, -0.14f, 0), new Vector3(0.14f, 0.28f, 0.15f)),
        new AnimationBoneDefinition("RightForearm", 5, new Vector3(0, -0.28f, 0),
            new Vector3(0, -0.1375f, 0), new Vector3(0.115f, 0.275f, 0.13f)),
        new AnimationBoneDefinition("LeftThigh", 0, new Vector3(-0.108f, 0.92f, 0),
            new Vector3(0, -0.24f, 0), new Vector3(0.185f, 0.48f, 0.19f)),
        new AnimationBoneDefinition("LeftShin", 7, new Vector3(0, -0.48f, 0),
            new Vector3(0, -0.22f, 0.03f), new Vector3(0.145f, 0.44f, 0.22f)),
        new AnimationBoneDefinition("RightThigh", 0, new Vector3(0.108f, 0.92f, 0),
            new Vector3(0, -0.24f, 0), new Vector3(0.185f, 0.48f, 0.19f)),
        new AnimationBoneDefinition("RightShin", 9, new Vector3(0, -0.48f, 0),
            new Vector3(0, -0.22f, 0.03f), new Vector3(0.145f, 0.44f, 0.22f))
    });

    public static IReadOnlyList<AnimationBoneDefinition> Bones => Definitions;

    /// <summary>Writes exactly BONE_COUNT signed X-axis pitches into the caller's span. Phase is finite seconds and wraps each second.</summary>
    public static void SampleWalk(double phaseSeconds, bool isWalking, Span<short> pitchCentidegrees)
    {
        if (pitchCentidegrees.Length < BONE_COUNT)
        {
            throw new ArgumentException("The destination must hold all bone pitches.", nameof(pitchCentidegrees));
        }
        if (double.IsNaN(phaseSeconds) || double.IsInfinity(phaseSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(phaseSeconds));
        }
        if (!isWalking)
        {
            pitchCentidegrees.Slice(0, BONE_COUNT).Clear();
            return;
        }

        double secondsInCycle = phaseSeconds % WALK_DURATION_SECONDS;
        if (secondsInCycle < 0)
        {
            secondsInCycle += WALK_DURATION_SECONDS;
        }

        double sample = secondsInCycle * WALK_SAMPLE_RATE_HZ;
        int first = Math.Min((int)sample, WALK_SAMPLE_COUNT - 1);
        int second = (first + 1) % WALK_SAMPLE_COUNT;
        double fraction = sample - first;
        ReadOnlySpan<short> samples = AnimationWalkClip.Samples;
        for (int index = 0; index < BONE_COUNT; index++)
        {
            short start = samples[first * BONE_COUNT + index];
            short end = samples[second * BONE_COUNT + index];
            pitchCentidegrees[index] = (short)Math.Round(start + (end - start) * fraction, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>Evaluates a complete pose at the supplied world foot position in meters and signed Y-axis yaw in centidegrees. The caller owns both spans.</summary>
    public static void GetWorldPose(Vector3 feet, short yawCentidegrees, ReadOnlySpan<short> pitchCentidegrees,
        Span<AnimationBonePose> bones)
    {
        if (pitchCentidegrees.Length < BONE_COUNT)
        {
            throw new ArgumentException("All bone pitches are required.", nameof(pitchCentidegrees));
        }
        if (bones.Length < BONE_COUNT)
        {
            throw new ArgumentException("The destination must hold all bones.", nameof(bones));
        }

        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawCentidegrees * RADIANS_PER_CENTIDEGREE);
        for (int index = 0; index < BONE_COUNT; index++)
        {
            AnimationBoneDefinition definition = Definitions[index];
            Vector3 parentPosition = definition.ParentIndex < 0 ? feet : bones[definition.ParentIndex].Position;
            Quaternion parentRotation = definition.ParentIndex < 0 ? yaw : bones[definition.ParentIndex].Rotation;
            var pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitchCentidegrees[index] * RADIANS_PER_CENTIDEGREE);

            Vector3 position = parentPosition + Vector3.Transform(definition.LocalPosition, parentRotation);
            var rotation = Quaternion.Normalize(parentRotation * pitch);
            Vector3 boxCenter = position + Vector3.Transform(definition.BoxOffset, rotation);
            bones[index] = new AnimationBonePose(position, rotation, boxCenter, definition.BoxSize);
        }
    }
}
