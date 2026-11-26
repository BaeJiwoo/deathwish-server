using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

public static class BakeShooterLocomotion
{
    private const string WALK_FBX = "Assets/walk forward.fbx";
    private const string BOB_FBX = "Assets/bob_rigged.fbx";
    private const int SAMPLE_RATE_HZ = 30;
    // ServerCharacterView places the FBX model origin this far above the server's foot marker.
    private const float BOB_ORIGIN_HEIGHT_METERS = 0.9514f;
    private const float MAX_KEY_ERROR_METERS = 0.001f;
    private const float MAX_INTERPOLATED_ERROR_METERS = 0.01f;

    private static readonly string[] BonePaths = {
        "mixamorig:Hips",
        "mixamorig:Hips/mixamorig:LeftUpLeg",
        "mixamorig:Hips/mixamorig:LeftUpLeg/mixamorig:LeftLeg",
        "mixamorig:Hips/mixamorig:LeftUpLeg/mixamorig:LeftLeg/mixamorig:LeftFoot",
        "mixamorig:Hips/mixamorig:LeftUpLeg/mixamorig:LeftLeg/mixamorig:LeftFoot/mixamorig:LeftToeBase",
        "mixamorig:Hips/mixamorig:RightUpLeg",
        "mixamorig:Hips/mixamorig:RightUpLeg/mixamorig:RightLeg",
        "mixamorig:Hips/mixamorig:RightUpLeg/mixamorig:RightLeg/mixamorig:RightFoot",
        "mixamorig:Hips/mixamorig:RightUpLeg/mixamorig:RightLeg/mixamorig:RightFoot/mixamorig:RightToeBase"
    };

    public static void Run()
    {
        // Match the client model import: Bob supplies his bind pose, not an embedded FBX animation.
        var bobImporter = (ModelImporter)AssetImporter.GetAtPath(BOB_FBX);
        bobImporter.animationType = ModelImporterAnimationType.Generic;
        bobImporter.importAnimation = false;
        bobImporter.SaveAndReimport();
        var importer = (ModelImporter)AssetImporter.GetAtPath(WALK_FBX);
        importer.animationType = ModelImporterAnimationType.Generic;
        importer.importAnimation = true;
        importer.resampleCurves = true;
        importer.animationCompression = ModelImporterAnimationCompression.Off;
        importer.SaveAndReimport();
        var original = AssetDatabase.LoadAllAssetsAtPath(WALK_FBX).OfType<AnimationClip>()
            .Single(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal));
        var source = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(WALK_FBX));
        var bob = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(BOB_FBX));
        try
        {
            Transform[] sourceBones = Bind(source);
            Transform[] bobBones = Bind(bob);
            if (Mathf.Abs(original.length - 1f) > 0.0001f || original.frameRate != SAMPLE_RATE_HZ)
            {
                throw new InvalidOperationException("Expected a one-second walk sampled at 30 Hz.");
            }
            var standing = new Samples(2);
            for (int i = 0; i < 2; i++)
            {
                standing.Set(i, bobBones, bobBones[0].localPosition);
            }
            WriteClip("ShooterStand", standing);

            original.SampleAnimation(source, 0f);
            Vector3 start = sourceBones[0].position;
            original.SampleAnimation(source, original.length);
            Vector3 travel = Vector3.ProjectOnPlane(sourceBones[0].position - start, Vector3.up);
            var walk = new Samples(SAMPLE_RATE_HZ + 1);
            for (int frame = 0; frame < walk.Count; frame++)
            {
                float time = frame / (float)SAMPLE_RATE_HZ;
                original.SampleAnimation(source, time);
                Vector3 translation = RemovedTravel(start, travel, time);
                walk.Set(frame, sourceBones, sourceBones[0].position - translation);
            }
            AnimationClip baked = WriteClip("ShooterWalk", walk);
            Verify(original, source, sourceBones, baked, bob, bobBones, start, travel);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(source);
            UnityEngine.Object.DestroyImmediate(bob);
        }
    }

    private static Vector3 RemovedTravel(Vector3 start, Vector3 travel, float phase)
    {
        // Remove only net XZ travel. Retain pelvic sway, its original height above ground, and bobbing.
        return Vector3.ProjectOnPlane(start, Vector3.up) + travel * phase + Vector3.up * BOB_ORIGIN_HEIGHT_METERS;
    }

    private static Transform[] Bind(GameObject model)
    {
        return BonePaths.Select(path => model.transform.Find(path) ??
            throw new InvalidOperationException($"Missing joint {path} in {model.name}")).ToArray();
    }

    private sealed class Samples
    {
        internal readonly Vector3[] Hips;
        internal readonly Quaternion[,] Rotations;
        internal int Count => Hips.Length;

        internal Samples(int count)
        {
            Hips = new Vector3[count];
            Rotations = new Quaternion[count, BonePaths.Length];
        }

        internal void Set(int frame, Transform[] bones, Vector3 hipPosition)
        {
            Hips[frame] = hipPosition;
            for (int bone = 0; bone < bones.Length; bone++)
            {
                // These FBXs have the same leg hierarchy and bone-local axes. Verify() checks the
                // resulting endpoint positions on the actual destination skeleton after baking.
                Quaternion q = bones[bone].localRotation;
                if (frame > 0 && Quaternion.Dot(Rotations[frame - 1, bone], q) < 0f)
                {
                    q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                }
                Rotations[frame, bone] = q;
            }
        }
    }

    private static AnimationClip WriteClip(string name, Samples samples)
    {
        var clip = new AnimationClip {name = name, frameRate = SAMPLE_RATE_HZ};
        var bindings = new List<EditorCurveBinding>();
        var curves = new List<AnimationCurve>();
        void AddCurve(int bone, string property, Func<int, float> value)
        {
            var keys = new Keyframe[samples.Count];
            for (int frame = 0; frame < keys.Length; frame++)
            {
                keys[frame] = new Keyframe(frame / (float)(keys.Length - 1), value(frame));
            }
            var curve = new AnimationCurve(keys);
            for (int frame = 0; frame < keys.Length; frame++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, frame, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, frame, AnimationUtility.TangentMode.Linear);
            }
            bindings.Add(EditorCurveBinding.FloatCurve(BonePaths[bone], typeof(Transform), property));
            curves.Add(curve);
        }

        for (int bone = 0; bone < BonePaths.Length; bone++)
        {
            for (int axis = 0; axis < 4; axis++)
            {
                AddCurve(bone, "m_LocalRotation." + "xyzw"[axis], f => samples.Rotations[f, bone][axis]);
            }
        }
        for (int axis = 0; axis < 3; axis++)
        {
            AddCurve(0, "m_LocalPosition." + "xyz"[axis], f => samples.Hips[f][axis]);
        }
        AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
        clip.EnsureQuaternionContinuity();
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = true;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
        string path = "Assets/" + name + ".anim";
        AssetDatabase.CreateAsset(clip, path);
        AssetDatabase.SaveAssets();
        return AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
    }

    private static void Verify(AnimationClip sourceClip, GameObject source, Transform[] sourceBones,
        AnimationClip baked, GameObject bob, Transform[] bobBones, Vector3 start, Vector3 travel)
    {
        float maxKeyError = 0f;
        float maxError = 0f;
        var animator = bob.GetComponent<Animator>();
        if (animator == null)
        {
            animator = bob.AddComponent<Animator>();
        }
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        var graph = PlayableGraph.Create("Check native ShooterWalk clip");
        try
        {
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var motion = AnimationClipPlayable.Create(graph, baked);
            AnimationPlayableOutput.Create(graph, "Bob", animator).SetSourcePlayable(motion);
            graph.Play();
            // The source is right at its next stride at t=1, whereas the looping output can wrap to zero.
            for (int frame = 0; frame < SAMPLE_RATE_HZ * 2; frame++)
            {
                float time = frame / (float)(SAMPLE_RATE_HZ * 2);
                sourceClip.SampleAnimation(source, time);
                motion.SetTime(time);
                graph.Evaluate(0f);
                Vector3 translation = RemovedTravel(start, travel, time);
                for (int i = 0; i < bobBones.Length; i++)
                {
                    float error = Vector3.Distance(sourceBones[i].position - translation, bobBones[i].position);
                    maxError = Mathf.Max(maxError, error);
                    if (frame % 2 == 0)
                    {
                        maxKeyError = Mathf.Max(maxKeyError, error);
                    }
                }
            }
        }
        finally
        {
            graph.Destroy();
        }
        if (maxError > MAX_INTERPOLATED_ERROR_METERS || maxKeyError > MAX_KEY_ERROR_METERS)
        {
            throw new InvalidOperationException($"Walk retarget joint error is {maxError:F5} m (source keys {maxKeyError:F5} m).");
        }
        Debug.Log($"Walk verified at 60 phases: maximum joint error {maxError * 1000:F3} mm (source keys {maxKeyError * 1000:F3} mm); removed travel {travel.magnitude:F3} m.");
    }
}
