using System.Numerics;
using DeathWish.Protocol;

internal static class AnimationRigTests
{
    private const float POSITION_TOLERANCE_METERS = 0.0001f;
    private const short QUARTER_TURN_CENTIDEGREES = 9000;
    private const int HEAD_INDEX = 2;
    private const int TORSO_INDEX = 1;
    private const int LEFT_THIGH_INDEX = 7;
    private const int LEFT_SHIN_INDEX = 8;
    private const int RIGHT_THIGH_INDEX = 9;
    private const int RIGHT_SHIN_INDEX = 10;
    private const int SOURCE_RATE_HZ = 30;
    private const int SOURCE_PITCH_TOLERANCE_CENTIDEGREES = 10;

    internal static void Run()
    {
        VerifyWalkCycle();
        VerifyWorldPose();
        Console.WriteLine("PASS ShooterPack forward walk, standing pose, cycle interpolation, hierarchy, root yaw, and ten oriented boxes");
    }

    private static void VerifyWalkCycle()
    {
        var opening = new short[AnimationRig.BONE_COUNT];
        var halfCycle = new short[AnimationRig.BONE_COUNT];
        var wrapped = new short[AnimationRig.BONE_COUNT];
        AnimationRig.SampleWalk(0, true, opening);
        AnimationRig.SampleWalk(0.5, true, halfCycle);
        // Measured from hip, knee and ankle positions in walk forward.fbx, in the rig's sagittal plane.
        // The source frame numbers are one-based. Shin pitch is relative to its thigh.
        VerifySourceLegs(sourceFrame: 8, leftThigh: -5354, leftShin: 4946, rightThigh: 428, rightShin: 3958);
        VerifySourceLegs(sourceFrame: 24, leftThigh: 837, leftShin: 5168, rightThigh: -5153, rightShin: 3261);
        AnimationRig.SampleWalk(1, true, wrapped);
        Require(wrapped.SequenceEqual(opening), "The one-second FBX cycle wraps to its first pose.");
        AnimationRig.SampleWalk(-0.5, true, wrapped);
        Require(wrapped.SequenceEqual(halfCycle), "A negative phase wraps to the same positive phase.");

        double secondsPerSample = 1d / AnimationRig.WALK_SAMPLE_RATE_HZ;
        AnimationRig.SampleWalk(secondsPerSample, true, wrapped);
        var between = new short[AnimationRig.BONE_COUNT];
        AnimationRig.SampleWalk(secondsPerSample / 2, true, between);
        VerifyMidpoint(opening, between, wrapped);
        AnimationRig.SampleWalk(1 - secondsPerSample, true, wrapped);
        AnimationRig.SampleWalk(1 - secondsPerSample / 2, true, between);
        VerifyMidpoint(wrapped, between, opening);

        var destination = Enumerable.Repeat((short)-1, AnimationRig.BONE_COUNT + 1).ToArray();
        AnimationRig.SampleWalk(0.75, false, destination);
        Require(destination.Take(AnimationRig.BONE_COUNT).All(pitch => pitch == 0) && destination[^1] == -1,
            "Idle writes the neutral pose and leaves unrelated destination elements untouched.");
    }

    private static void VerifySourceLegs(int sourceFrame, int leftThigh, int leftShin, int rightThigh, int rightShin)
    {
        Span<short> pose = stackalloc short[AnimationRig.BONE_COUNT];
        AnimationRig.SampleWalk((sourceFrame - 1d) / SOURCE_RATE_HZ, true, pose);
        Require(Math.Abs(pose[LEFT_THIGH_INDEX] - leftThigh) <= SOURCE_PITCH_TOLERANCE_CENTIDEGREES &&
            Math.Abs(pose[LEFT_SHIN_INDEX] - leftShin) <= SOURCE_PITCH_TOLERANCE_CENTIDEGREES &&
            Math.Abs(pose[RIGHT_THIGH_INDEX] - rightThigh) <= SOURCE_PITCH_TOLERANCE_CENTIDEGREES &&
            Math.Abs(pose[RIGHT_SHIN_INDEX] - rightShin) <= SOURCE_PITCH_TOLERANCE_CENTIDEGREES,
            $"Both legs must match the projected FBX pose at source frame {sourceFrame}.");
    }

    private static void VerifyMidpoint(short[] start, short[] midpoint, short[] end)
    {
        for (int index = 0; index < AnimationRig.BONE_COUNT; index++)
        {
            Require(Math.Abs(2 * midpoint[index] - start[index] - end[index]) <= 1,
                $"Bone {index} must interpolate halfway between adjacent samples to the nearest centidegree.");
        }
    }

    private static void VerifyWorldPose()
    {
        var feet = new Vector3(3f, 4f, -2f);
        var pitches = new short[AnimationRig.BONE_COUNT];
        var bones = new AnimationBonePose[AnimationRig.BONE_COUNT];
        AnimationRig.GetWorldPose(feet, 0, pitches, bones);
        Require(bones[0].Position == feet && bones[0].BoxSize == Vector3.Zero, "The root is anchored at the server foot position.");
        Require(AnimationRig.Bones.Count == AnimationRig.BONE_COUNT &&
            AnimationRig.Bones.Select((bone, index) => bone.ParentIndex < index).All(isOrdered => isOrdered),
            "All rig members use parent-before-child order.");
        Require(bones.Count(bone => bone.BoxSize != Vector3.Zero) == AnimationRig.BONE_COUNT - 1,
            "The drawing and hitbox rig has one box per articulated body part.");
        float bottom = bones.Skip(1).Min(bone => bone.BoxCenter.Y - bone.BoxSize.Y / 2);
        float top = bones.Skip(1).Max(bone => bone.BoxCenter.Y + bone.BoxSize.Y / 2);
        Require(Math.Abs(bottom - feet.Y) < POSITION_TOLERANCE_METERS &&
            Math.Abs(top - feet.Y - 1.8f) < POSITION_TOLERANCE_METERS, "The standing rig is foot-aligned and 1.8 m tall.");

        pitches[TORSO_INDEX] = QUARTER_TURN_CENTIDEGREES;
        AnimationRig.GetWorldPose(feet, QUARTER_TURN_CENTIDEGREES, pitches, bones);
        Require(Vector3.Distance(bones[HEAD_INDEX].Position, feet + new Vector3(0.56f, 0.92f, 0)) <
            POSITION_TOLERANCE_METERS, "Parent X pitch and world Y yaw transform the child's attachment.");
        Require(Vector3.Distance(bones[HEAD_INDEX].BoxCenter, feet + new Vector3(0.72f, 0.92f, 0)) <
            POSITION_TOLERANCE_METERS, "The head box follows the combined rotation.");
        Require(Vector3.Distance(Vector3.Transform(Vector3.UnitY, bones[HEAD_INDEX].Rotation), Vector3.UnitX) <
            POSITION_TOLERANCE_METERS, "The box axes preserve both parent rotation and world yaw.");

        AnimationRig.SampleWalk(0.25, true, pitches);
        AnimationRig.GetWorldPose(feet, 0, pitches, bones);
        Require(bones[0].Position == feet && Math.Abs(bones[LEFT_THIGH_INDEX].BoxCenter.Z - feet.Z) > 0.02f,
            "The walk animates the limbs without root translation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
