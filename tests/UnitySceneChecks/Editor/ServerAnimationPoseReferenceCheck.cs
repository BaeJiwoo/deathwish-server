using System;
using DeathWish.Protocol;
using UnityEngine;
using NumericsVector3 = System.Numerics.Vector3;

internal static class ServerAnimationPoseReferenceCheck
{
    private const float POSITION_TOLERANCE_METERS = 0.0001f;
    private const float ROTATION_TOLERANCE_DEGREES = 0.05f;

    internal static void Verify(Transform marker, Vector3 expectedFeet, short yawCentidegrees,
        short[] bonePitchCentidegrees)
    {
        Transform rig = marker.Find("ServerRig");
        Require(rig != null && rig.gameObject.activeInHierarchy, "The matching animation rig is visible.");
        Require(rig.GetComponentsInChildren<Animator>(true).Length == 0 &&
            rig.GetComponentsInChildren<Animation>(true).Length == 0, "The Unity rig needs no runtime Animator.");
        Require(rig.GetComponentsInChildren<Collider>(true).Length == 0,
            "The replicated boxes are display geometry without Unity colliders.");

        var reference = new AnimationBonePose[AnimationRig.BONE_COUNT];
        AnimationRig.GetWorldPose(new NumericsVector3(expectedFeet.x, expectedFeet.y, expectedFeet.z),
            yawCentidegrees, bonePitchCentidegrees, reference);
        var rendered = new Transform[AnimationRig.BONE_COUNT];
        for (int index = 0; index < rendered.Length; index++)
        {
            AnimationBoneDefinition definition = AnimationRig.Bones[index];
            Transform parent = definition.ParentIndex < 0 ? rig : rendered[definition.ParentIndex];
            Transform bone = parent.Find(definition.Name);
            Require(bone != null, $"The joint {definition.Name} follows the shared parent order.");
            rendered[index] = bone;

            AnimationBonePose pose = reference[index];
            var rotation = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
            VerifyTransform(bone, ToUnity(pose.Position), rotation);
            if (definition.BoxSize == NumericsVector3.Zero)
            {
                continue;
            }

            Transform box = bone.Find(definition.Name + "Box");
            Require(box != null && box.GetComponent<MeshRenderer>() != null,
                $"The body part {definition.Name} has visible box geometry.");
            VerifyTransform(box, ToUnity(pose.BoxCenter), rotation);
            Require(Vector3.Distance(box.lossyScale, ToUnity(pose.BoxSize)) <= POSITION_TOLERANCE_METERS,
                $"The displayed {definition.Name} box uses the server OBB dimensions.");
        }
    }

    private static void VerifyTransform(Transform actual, Vector3 position, Quaternion rotation)
    {
        Require(Vector3.Distance(actual.position, position) <= POSITION_TOLERANCE_METERS,
            $"{actual.name} is at {actual.position}, expected {position}.");
        Require(Quaternion.Angle(actual.rotation, rotation) <= ROTATION_TOLERANCE_DEGREES,
            $"{actual.name} does not match the server reference rotation.");
    }

    private static Vector3 ToUnity(NumericsVector3 value) => new Vector3(value.X, value.Y, value.Z);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
