using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeathWish.Protocol;
using UnityEngine;

namespace DeathWish.Client.Editor
{
    internal static class ShooterAimSceneCheck
    {
        private const long PLAYER_ID = 71;
        private const int SAMPLE_RATE_HZ = 60;
        private const int SOURCE_FRAMES = 62;
        private const int SOURCE_RATE_HZ = 30;
        private const float MAX_PALM_ERROR_METERS = 0.01f;
        private const float MUZZLE_TIP_TOLERANCE_METERS = 0.015f;
        private const float MAX_LEG_ERROR_DEGREES = 0.1f;
        private const float MAX_BARREL_ERROR_DEGREES = 0.1f;
        private const float GROUND_TOLERANCE_METERS = 0.025f;
        private const float TRANSITION_GROUND_TOLERANCE_METERS = 0.08f;
        private const BindingFlags PRIVATE_INSTANCE = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly string[] RifleTextureProperties = { "_MainTex", "_BumpMap", "_MetallicGlossMap", "_EmissionMap" };
        private static readonly string[] LowerBones =
            { "Hips", "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase", "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase" };

        internal static void VerifyClipAt(Transform rig, string clipName, float phase)
        {
            var reference = UnityEngine.Object.Instantiate(Resources.Load<GameObject>("bob_rigged"));
            try
            {
                AnimationClip clip = Resources.Load<AnimationClip>(clipName);
                clip.SampleAnimation(reference, phase * clip.length);
                CompareLowerBody(rig.Find("Root/Bob").GetComponentsInChildren<Transform>(), reference.GetComponentsInChildren<Transform>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(reference);
            }
        }

        private static void CompareLowerBody(Transform[] actual, Transform[] reference)
        {
            foreach (string name in LowerBones)
            {
                Transform a = Bone(actual, name), b = Bone(reference, name);
                Require(Quaternion.Angle(a.localRotation, b.localRotation) <= MAX_LEG_ERROR_DEGREES &&
                    Vector3.Distance(a.localPosition, b.localPosition) <= 0.001f,
                    $"{name} did not match the local clip sample. Angle error {Quaternion.Angle(a.localRotation, b.localRotation):F2}°.");
            }
        }

        internal static void Verify(SyncTestClient client)
        {
            Type type = typeof(SyncTestClient);
            type.GetField("_usesServerAnimation", PRIVATE_INSTANCE).SetValue(client, true);
            type.GetField("_playerId", PRIVATE_INSTANCE).SetValue(client, PLAYER_ID);
            var apply = type.GetMethod("ApplyGaitSnapshot", PRIVATE_INSTANCE);
            var render = type.GetMethod("RenderWorld", PRIVATE_INSTANCE);
            var markers = (Dictionary<long, Transform>)type.GetField("_markers", PRIVATE_INSTANCE).GetValue(client);

            Transform rig = null, grip = null, muzzle = null, support = null, rightArm = null;
            Transform leftHand = null, leftKnuckle = null, rightHand = null, rightKnuckle = null;
            Quaternion firstArmRotation = default;
            float maxArmMotionDegrees = 0f;
            float maxAimErrorDegrees = 0f;
            float minFootHeightMeters = float.PositiveInfinity;
            float maxFootHeightMeters = float.NegativeInfinity;
            float standFootHeightMeters = 0f;
            float minBlendHeightMeters = float.PositiveInfinity;
            float maxBlendHeightMeters = float.NegativeInfinity;
            SkinnedMeshRenderer bodyRenderer = null;
            float maxSupportErrorMeters = 0f;
            float maxGripErrorMeters = 0f;
            var reference = UnityEngine.Object.Instantiate(Resources.Load<GameObject>("bob_rigged"));
            Transform[] referenceBones = reference.GetComponentsInChildren<Transform>();
            Transform[] actorBones = null;
            var walk = Resources.Load<AnimationClip>("ShooterWalk");
            var stand = Resources.Load<AnimationClip>("ShooterStand");
            int samplesPerCycle = SOURCE_FRAMES * SAMPLE_RATE_HZ / SOURCE_RATE_HZ;
            for (int sample = 0; sample <= 2 * samplesPerCycle; sample++)
            {
                double time = sample / (double)SAMPLE_RATE_HZ;
                bool walking = sample >= samplesPerCycle && sample < 2 * samplesPerCycle;
                int elapsedWalkSamples = Mathf.Clamp(sample - samplesPerCycle, 0, samplesPerCycle - 1);
                double elapsedWalkSeconds = elapsedWalkSamples / (double)SAMPLE_RATE_HZ;
                uint phase = (uint)(elapsedWalkSeconds * GaitPlayer.PHASE_UNITS_PER_CYCLE);
                var position = new SnapshotPlayer(PLAYER_ID, (float)elapsedWalkSeconds, 0f, 0f);
                float yawDegrees = (sample * 3.25f) % 360f;
                float pitchDegrees = Mathf.Lerp(-65f, 75f, Mathf.PingPong(sample / 60f, 1f));
                type.GetField("_yawDegrees", PRIVATE_INSTANCE).SetValue(client, yawDegrees);
                type.GetField("_pitchDegrees", PRIVATE_INSTANCE).SetValue(client, pitchDegrees);
                var positions = new WorldSnapshot(1, sample + 1, new[] { position });
                var pose = new GaitPlayer(PLAYER_ID, sample >= samplesPerCycle ? (short)9000 : (short)0, walking, phase);
                double serverTime = 1.0 + time;
                var snapshot = new GaitWorldSnapshot(positions, (long)Math.Round(serverTime * 1_000_000), new[] { pose });
                apply.Invoke(client, new object[] { snapshot, serverTime });
                if (sample == samplesPerCycle || sample == 2 * samplesPerCycle)
                {
                    double midpoint = serverTime + 0.1 - 0.5 / SAMPLE_RATE_HZ;
                    render.Invoke(client, new object[] { midpoint });
                    float footHeight = LowestVertexY(bodyRenderer) - rig.position.y;
                    minBlendHeightMeters = Mathf.Min(minBlendHeightMeters, footHeight);
                    maxBlendHeightMeters = Mathf.Max(maxBlendHeightMeters, footHeight);
                }
                render.Invoke(client, new object[] { serverTime + 0.101 });

                if (sample == 0)
                {
                    rig = markers[PLAYER_ID].Find("ServerRig");
                    var bob = rig.Find("Root/Bob");
                    Require(bob != null, "Shooter preview needs the imported Bob skin.");
                    bodyRenderer = bob.GetComponentInChildren<SkinnedMeshRenderer>();
                    Material body = bodyRenderer.sharedMaterial;
                    Require(body.shader.name == "Standard" && body.GetFloat("_Glossiness") == 0f &&
                        body.GetFloat("_Metallic") == 0f && body.GetTexture("_MainTex") == null,
                        "Bob must have a lit, single-color dielectric material at full roughness.");
                    Animator animator = bob.GetComponent<Animator>();
                    Require(animator != null && animator.hasBoundPlayables && !animator.applyRootMotion,
                        "Bob must play real clips through the Animator while the server controls root motion.");
                    actorBones = bob.GetComponentsInChildren<Transform>();
                    var bones = actorBones;
                    rightHand = Bone(bones, "RightHand");
                    rightKnuckle = Bone(bones, "RightHandIndex1");
                    grip = rightHand.Find("Grip");
                    VerifyWeapon(grip);
                    muzzle = grip.Find("Weapon/Muzzle");
                    support = grip.Find("Weapon/SupportHand");
                    leftHand = Bone(bones, "LeftHand");
                    leftKnuckle = Bone(bones, "LeftHandIndex1");
                    rightArm = Bone(bones, "RightArm");
                    firstArmRotation = rightArm.localRotation;
                }
                AnimationClip expected = walking ? walk : stand;
                float clipTime = walking ? (phase % GaitPlayer.PHASE_UNITS_PER_CYCLE / (float)GaitPlayer.PHASE_UNITS_PER_CYCLE) * walk.length : 0f;
                expected.SampleAnimation(reference, clipTime);
                CompareLowerBody(actorBones, referenceBones);
                if (sample == 0 || sample == samplesPerCycle || sample == 2 * samplesPerCycle || walking && sample % 10 == 0)
                {
                    float footHeight = LowestVertexY(bodyRenderer) - rig.position.y;
                    if (sample == 0)
                    {
                        standFootHeightMeters = footHeight;
                    }
                    minFootHeightMeters = Mathf.Min(minFootHeightMeters, footHeight);
                    maxFootHeightMeters = Mathf.Max(maxFootHeightMeters, footHeight);
                }

                Vector3 rightPalm = Vector3.Lerp(rightHand.position, rightKnuckle.position, 0.72f);
                Vector3 leftPalm = Vector3.Lerp(leftHand.position, leftKnuckle.position, 0.72f);
                Vector3 cameraForward = Quaternion.Euler(pitchDegrees, yawDegrees, 0f) * Vector3.forward;
                maxAimErrorDegrees = Mathf.Max(maxAimErrorDegrees, Vector3.Angle(muzzle.forward, cameraForward));
                maxGripErrorMeters = Mathf.Max(maxGripErrorMeters, Vector3.Distance(grip.position, rightPalm));
                maxSupportErrorMeters = Mathf.Max(maxSupportErrorMeters, Vector3.Distance(support.position, leftPalm));
                maxArmMotionDegrees = Mathf.Max(maxArmMotionDegrees, Quaternion.Angle(firstArmRotation, rightArm.localRotation));
            }

            Require(minFootHeightMeters >= -GROUND_TOLERANCE_METERS && maxFootHeightMeters <= GROUND_TOLERANCE_METERS,
                $"Rendered foot height relative to server floor was {minFootHeightMeters:F4} to {maxFootHeightMeters:F4} m.");
            // A pose crossfade between different planted feet can briefly move the sole farther than either endpoint.
            Require(minBlendHeightMeters >= -TRANSITION_GROUND_TOLERANCE_METERS &&
                maxBlendHeightMeters <= TRANSITION_GROUND_TOLERANCE_METERS,
                $"Blending between stand and walk moved the sole {minBlendHeightMeters:F4} to {maxBlendHeightMeters:F4} m from the floor.");
            Require(maxAimErrorDegrees < MAX_BARREL_ERROR_DEGREES,
                $"The weapon axis drifted {maxAimErrorDegrees:F3} degrees from the camera.");
            Require(maxGripErrorMeters <= MAX_PALM_ERROR_METERS && maxSupportErrorMeters <= MAX_PALM_ERROR_METERS,
                $"The hands drifted from the weapon sockets by {maxGripErrorMeters * 1000:F2} mm and {maxSupportErrorMeters * 1000:F2} mm.");
            Require(maxArmMotionDegrees > 0.1f, "The aiming loop must visibly change the upper arm pose.");
            UnityEngine.Object.DestroyImmediate(reference);
            Debug.Log($"PASS gait Animator: stand, forward walk and stop match clip pelvis, legs, ankles and toes. " +
                $"Stand {standFootHeightMeters:F4} m, foot height {minFootHeightMeters:F4} to {maxFootHeightMeters:F4} m, blend {minBlendHeightMeters:F4} to {maxBlendHeightMeters:F4} m, aim error {maxAimErrorDegrees:F3}°, grip/support error {maxGripErrorMeters * 1000:F2}/{maxSupportErrorMeters * 1000:F2} mm, arm motion {maxArmMotionDegrees:F2}°.");
        }

        internal static void VerifyWeapon(Transform grip)
        {
            Require(grip != null, "The weapon needs a grip on Bob's right hand.");
            Transform weapon = grip.Find("Weapon");
            Transform muzzle = grip.Find("Weapon/Muzzle");
            Require(weapon != null && muzzle != null && weapon.Find("SupportHand") != null && weapon.gameObject.activeInHierarchy &&
                grip.GetComponentsInChildren<Collider>(true).Length == 0,
                "The right hand must hold a visual weapon with a muzzle socket.");

            MeshRenderer[] renderers = weapon.GetComponentsInChildren<MeshRenderer>();
            Require(renderers.Length > 0, "The equipped weapon must have visible geometry.");
            float furthestForward = float.NegativeInfinity;
            foreach (MeshRenderer renderer in renderers)
            {
                Mesh mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                Require(renderer.enabled && !renderer.forceRenderingOff && mesh != null && mesh.vertexCount > 0,
                    $"Weapon part {renderer.name} must have visible geometry.");
                Material material = renderer.sharedMaterial;
                Require(material != null && material.shader.isSupported && material.shader.name != "Hidden/InternalErrorShader" &&
                    RifleTextureProperties.All(property => material.GetTexture(property) is Texture2D),
                    $"Weapon part {renderer.name} needs the rifle's base color, normal, metallic and emission maps.");
                Bounds bounds = mesh.bounds;
                Vector3 half = bounds.extents;
                Transform part = renderer.transform;
                Vector3 axis = muzzle.forward;
                float forwardExtent = Mathf.Abs(Vector3.Dot(axis, part.TransformVector(Vector3.right * half.x))) +
                    Mathf.Abs(Vector3.Dot(axis, part.TransformVector(Vector3.up * half.y))) +
                    Mathf.Abs(Vector3.Dot(axis, part.TransformVector(Vector3.forward * half.z)));
                float tipOffset = Vector3.Dot(axis, part.TransformPoint(bounds.center) - muzzle.position) + forwardExtent;
                furthestForward = Mathf.Max(furthestForward, tipOffset);
            }

            Require(Mathf.Abs(furthestForward) <= MUZZLE_TIP_TOLERANCE_METERS,
                $"The muzzle socket and the visible tip differ along the aim axis by {furthestForward * 1000:F1} mm.");
        }

        // Use bind poses to measure the displayed skin in world units, including the imported FBX scale.
        private static float LowestVertexY(SkinnedMeshRenderer renderer)
        {
            Mesh mesh = renderer.sharedMesh;
            Transform[] bones = renderer.bones;
            Matrix4x4[] matrices = mesh.bindposes;
            for (int index = 0; index < matrices.Length; index++)
            {
                matrices[index] = bones[index].localToWorldMatrix * matrices[index];
            }

            Vector3[] vertices = mesh.vertices;
            BoneWeight[] weights = mesh.boneWeights;
            float minY = float.PositiveInfinity;
            for (int index = 0; index < vertices.Length; index++)
            {
                Vector3 v = vertices[index];
                BoneWeight w = weights[index];
                float y = matrices[w.boneIndex0].MultiplyPoint3x4(v).y * w.weight0 +
                    matrices[w.boneIndex1].MultiplyPoint3x4(v).y * w.weight1 +
                    matrices[w.boneIndex2].MultiplyPoint3x4(v).y * w.weight2 +
                    matrices[w.boneIndex3].MultiplyPoint3x4(v).y * w.weight3;
                minY = Mathf.Min(minY, y);
            }
            return minY;
        }

        private static Transform Bone(Transform[] bones, string name)
        {
            return bones.Single(t => t.name == name || t.name.EndsWith(":" + name, StringComparison.Ordinal));
        }

        private static void Require(bool satisfied, string detail)
        {
            if (!satisfied)
            {
                throw new InvalidOperationException(detail);
            }
        }
    }
}
