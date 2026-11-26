using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using UnityEngine;

namespace DeathWish.Client
{
    internal sealed class ShooterAimView
    {
        private const string CLIP_RESOURCE = "ShooterAimPose";
        private const string RIFLE_RESOURCE = "HeldRifle";
        private const float PALM_FRACTION = 0.72f;
        internal const double RECOIL_DURATION_SECONDS = 0.24;
        private const double RECOIL_PEAK_SECONDS = 0.035;
        private const float INITIAL_RECOIL_DEGREES = 4f;
        private const float PEAK_RECOIL_DEGREES = 10f;
        private static AimClip _clip;
        private static bool _hasLoadedClip;

        private readonly Transform _feet;
        private readonly Transform[] _bones;
        private readonly Quaternion[] _bindRelativeToFeet;

        private readonly Transform _grip;
        private readonly Transform _support;
        private readonly Transform _upperArm;
        private readonly Transform _forearm;
        private readonly Transform _leftHand;
        private readonly Transform _leftKnuckle;

        private Vector3 _recoilAxis;
        private float _appliedRecoilDegrees;

        internal Transform Muzzle { get; }

        private ShooterAimView(Transform feet, Transform[] bones, Dictionary<string, Transform> byName,
            Transform grip, Transform weapon)
        {
            _feet = feet;
            _bones = bones;
            _grip = grip;
            _support = weapon.Find("SupportHand");
            Muzzle = weapon.Find("Muzzle");
            _upperArm = byName["LeftArm"];
            _forearm = byName["LeftForeArm"];
            _leftHand = byName["LeftHand"];
            _leftKnuckle = byName["LeftHandIndex1"];
            _bindRelativeToFeet = new Quaternion[bones.Length];
            var inverseRig = Quaternion.Inverse(feet.rotation);
            for (int index = 0; index < bones.Length; index++)
            {
                _bindRelativeToFeet[index] = inverseRig * bones[index].rotation;
            }
        }

        internal static ShooterAimView TryCreate(Transform model, Transform feet)
        {
            if (!_hasLoadedClip)
            {
                _clip = LoadClip();
                _hasLoadedClip = true;
            }
            if (_clip == null)
            {
                return null;
            }

            var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform bone in model.GetComponentsInChildren<Transform>(true))
            {
                string name = bone.name;
                byName[name.Substring(name.LastIndexOf(':') + 1)] = bone;
            }
            var bones = new Transform[_clip.BoneNames.Length];
            for (int index = 0; index < bones.Length; index++)
            {
                if (!byName.TryGetValue(_clip.BoneNames[index], out bones[index]))
                {
                    Debug.LogWarning($"Missing shooter aim bone '{_clip.BoneNames[index]}' on {model.name}.");
                    return null;
                }
            }

            var prefab = Resources.Load<GameObject>(RIFLE_RESOURCE);
            if (prefab == null || prefab.transform.Find("Muzzle") == null || prefab.transform.Find("SupportHand") == null)
            {
                Debug.LogWarning("HeldRifle requires Muzzle and SupportHand sockets.");
                return null;
            }
            Transform hand = byName["RightHand"];
            var grip = new GameObject("Grip").transform;
            grip.SetParent(hand, false);
            grip.position = Vector3.Lerp(hand.position, byName["RightHandIndex1"].position, PALM_FRACTION);

            // FBX bone scales can carry the cm-to-m conversion. Keep the prop in world meters.
            Vector3 scale = grip.lossyScale;
            grip.localScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
            Transform weapon = UnityEngine.Object.Instantiate(prefab, grip, false).transform;
            weapon.name = "Weapon";
            return new ShooterAimView(feet, bones, byName, grip, weapon);
        }

        internal void Apply(double serverTimeSeconds, Quaternion aimRotation, double shotAgeSeconds)
        {
            int frames = _clip.FrameCount;
            double sample = serverTimeSeconds % _clip.DurationSeconds * frames / _clip.DurationSeconds;
            int firstFrame = (int)sample;
            int secondFrame = (firstFrame + 1) % frames;
            float fraction = (float)(sample - firstFrame);
            Quaternion rigRotation = _feet.rotation;

            // The bake stores parent-first bind deltas in character space, including the wrist twist.
            for (int index = 0; index < _bones.Length; index++)
            {
                Quaternion delta = Quaternion.Slerp(_clip.ReadRotation(firstFrame, index),
                    _clip.ReadRotation(secondFrame, index), fraction);
                _bones[index].rotation = rigRotation * delta * _bindRelativeToFeet[index];
            }

            Vector3 leftPalm = Vector3.Lerp(_leftHand.position, _leftKnuckle.position, PALM_FRACTION);
            Transform spine = _bones[0];
            spine.rotation = Quaternion.FromToRotation(leftPalm - _grip.position,
                aimRotation * _support.localPosition) * spine.rotation;
            _grip.rotation = aimRotation;
            PlaceSupportHand();

            // Recoil is additive to ShooterPack's sampled upper body. Both hands and their rifle
            // move about the spine together, after the support-hand constraint has been solved.
            _recoilAxis = aimRotation * Vector3.right;
            _appliedRecoilDegrees = 0f;
            ApplyRecoil(shotAgeSeconds);
        }

        internal void ApplyRecoil(double ageSeconds)
        {
            float recoilDegrees = 0f;
            if (ageSeconds >= 0 && ageSeconds < RECOIL_DURATION_SECONDS)
            {
                if (ageSeconds < RECOIL_PEAK_SECONDS)
                {
                    recoilDegrees = Mathf.SmoothStep(INITIAL_RECOIL_DEGREES, PEAK_RECOIL_DEGREES,
                        (float)(ageSeconds / RECOIL_PEAK_SECONDS));
                }
                else
                {
                    recoilDegrees = Mathf.SmoothStep(PEAK_RECOIL_DEGREES, 0f,
                        (float)((ageSeconds - RECOIL_PEAK_SECONDS) / (RECOIL_DURATION_SECONDS - RECOIL_PEAK_SECONDS)));
                }
            }
            if (recoilDegrees != _appliedRecoilDegrees)
            {
                Transform spine = _bones[0];
                spine.rotation = Quaternion.AngleAxis(_appliedRecoilDegrees - recoilDegrees, _recoilAxis) * spine.rotation;
                _appliedRecoilDegrees = recoilDegrees;
            }
        }

        private void PlaceSupportHand()
        {
            // Put the palm on the rifle socket while preserving the clip's wrist twist and bent elbow.
            Quaternion wristRotation = _leftHand.rotation;
            Vector3 palm = Vector3.Lerp(_leftHand.position, _leftKnuckle.position, PALM_FRACTION);
            Vector3 targetWrist = _support.position + (_leftHand.position - palm);
            Vector3 shoulder = _upperArm.position;
            Vector3 upper = _forearm.position - shoulder;
            Vector3 lower = _leftHand.position - _forearm.position;
            Vector3 reach = targetWrist - shoulder;
            Vector3 direction = reach.normalized;
            float distance = Mathf.Clamp(reach.magnitude, Mathf.Abs(upper.magnitude - lower.magnitude),
                upper.magnitude + lower.magnitude);
            if (distance <= Mathf.Epsilon)
            {
                return;
            }
            float along = (upper.sqrMagnitude - lower.sqrMagnitude + distance * distance) / (2f * distance);
            float bend = Mathf.Sqrt(Mathf.Max(0f, upper.sqrMagnitude - along * along));
            Vector3 bendDirection = Vector3.ProjectOnPlane(upper, direction).normalized;
            Vector3 desiredUpper = direction * along + bendDirection * bend;
            _upperArm.rotation = Quaternion.FromToRotation(upper, desiredUpper) * _upperArm.rotation;
            _forearm.rotation = Quaternion.FromToRotation(_leftHand.position - _forearm.position,
                shoulder + direction * distance - _forearm.position) * _forearm.rotation;
            _leftHand.rotation = wristRotation;
        }

        private static AimClip LoadClip()
        {
            var asset = Resources.Load<TextAsset>(CLIP_RESOURCE);
            if (asset == null)
            {
                Debug.LogWarning("Missing ShooterAimPose resource. Bob will use the replicated walk pose.");
                return null;
            }
            try
            {
                using var stream = new MemoryStream(asset.bytes);
                var clip = (AimClip)new DataContractJsonSerializer(typeof(AimClip)).ReadObject(stream);
                if (clip != null && clip.IsValid())
                {
                    return clip;
                }
            }
            catch (SerializationException exception)
            {
                Debug.LogWarning($"Could not read ShooterAimPose: {exception.Message}");
                return null;
            }
            Debug.LogWarning("Invalid ShooterAimPose bake. Bob will use the replicated walk pose.");
            return null;
        }

        [DataContract]
        private sealed class AimClip
        {
            [DataMember(Name = "boneNames", IsRequired = true)] public string[] BoneNames { get; set; }
            [DataMember(Name = "durationSeconds", IsRequired = true)] public double DurationSeconds { get; set; }
            [DataMember(Name = "rotations", IsRequired = true)] public float[] Rotations { get; set; }
            internal int FrameCount => Rotations.Length / 4 / BoneNames.Length;

            internal bool IsValid()
            {
                return BoneNames != null && BoneNames.Length > 0 && BoneNames[0] == "Spine" &&
                    Array.IndexOf(BoneNames, "RightHand") >= 0 &&
                    Rotations != null && Rotations.Length % (BoneNames.Length * 4) == 0 && FrameCount > 1 &&
                    DurationSeconds > 0 && !double.IsInfinity(DurationSeconds);
            }

            internal Quaternion ReadRotation(int frame, int bone)
            {
                int offset = (frame * BoneNames.Length + bone) * 4;
                return new Quaternion(Rotations[offset], Rotations[offset + 1], Rotations[offset + 2], Rotations[offset + 3]);
            }
        }
    }
}
