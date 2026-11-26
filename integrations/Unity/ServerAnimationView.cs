using System;
using System.Collections.Generic;
using DeathWish.Protocol;
using UnityEngine;

namespace DeathWish.Client
{
    internal sealed class ServerAnimationTimeline
    {
        private const int MAX_FRAMES = 32;
        private const double RENDER_DELAY_SECONDS = 0.1;
        private const double MAX_FRAME_GAP_SECONDS = 1;
        private const double MICROSECONDS_PER_SECOND = 1_000_000;

        private readonly List<AnimationFrame> _frames = new List<AnimationFrame>();
        private double _clockOffsetSeconds;
        private double _lastReceivedAtSeconds;
        private double _lastRenderTimeSeconds = double.NegativeInfinity;

        internal void Add(GaitWorldSnapshot snapshot, double receivedAtSeconds)
        {
            double serverTimeSeconds = snapshot.ServerTimeMicroseconds / MICROSECONDS_PER_SECOND;
            if (_frames.Count > 0)
            {
                AnimationFrame latest = _frames[_frames.Count - 1];
                if (serverTimeSeconds <= latest.ServerTimeSeconds)
                {
                    return;
                }

                if (serverTimeSeconds - latest.ServerTimeSeconds > MAX_FRAME_GAP_SECONDS ||
                    receivedAtSeconds - _lastReceivedAtSeconds > MAX_FRAME_GAP_SECONDS)
                {
                    Clear();
                }
            }

            double clockOffsetSeconds = serverTimeSeconds - receivedAtSeconds;
            _clockOffsetSeconds = _frames.Count == 0
                ? clockOffsetSeconds : Math.Max(_clockOffsetSeconds, clockOffsetSeconds);
            _frames.Add(new AnimationFrame(snapshot, serverTimeSeconds));
            _lastReceivedAtSeconds = receivedAtSeconds;
            if (_frames.Count > MAX_FRAMES)
            {
                _frames.RemoveAt(0);
            }
        }

        internal bool TrySample(double nowSeconds, out AnimationRenderSample sample)
        {
            sample = default;
            if (_frames.Count == 0)
            {
                return false;
            }

            double delayedTimeSeconds = nowSeconds + _clockOffsetSeconds - RENDER_DELAY_SECONDS;
            double renderTimeSeconds = Math.Min(_frames[_frames.Count - 1].ServerTimeSeconds,
                Math.Max(_lastRenderTimeSeconds, delayedTimeSeconds));
            _lastRenderTimeSeconds = renderTimeSeconds;
            while (_frames.Count > 2 && _frames[1].ServerTimeSeconds <= renderTimeSeconds)
            {
                _frames.RemoveAt(0);
            }

            AnimationFrame previous = _frames[0];
            AnimationFrame next = _frames[_frames.Count > 1 ? 1 : 0];
            double durationSeconds = next.ServerTimeSeconds - previous.ServerTimeSeconds;
            float fraction = durationSeconds > 0
                ? Mathf.Clamp01((float)((renderTimeSeconds - previous.ServerTimeSeconds) / durationSeconds)) : 0f;
            sample = new AnimationRenderSample(previous, next, fraction);
            return true;
        }

        internal double GetShotStartSeconds(long serverTimeMicroseconds)
        {
            // Nominal start on the delayed pose clock. Actual playback uses unscaled local time.
            return serverTimeMicroseconds / MICROSECONDS_PER_SECOND - _clockOffsetSeconds + RENDER_DELAY_SECONDS;
        }

        internal void Clear()
        {
            _frames.Clear();
            _lastRenderTimeSeconds = double.NegativeInfinity;
        }
    }

    internal sealed class AnimationFrame
    {
        private readonly Dictionary<long, AnimationFramePlayer> _players;

        internal double ServerTimeSeconds { get; }

        internal AnimationFrame(GaitWorldSnapshot snapshot, double serverTimeSeconds)
        {
            ServerTimeSeconds = serverTimeSeconds;
            _players = new Dictionary<long, AnimationFramePlayer>(snapshot.Players.Count);

            // Position and gait are paired by the roster order in one complete snapshot.
            for (int index = 0; index < snapshot.Players.Count; index++)
            {
                SnapshotPlayer position = snapshot.Positions.Players[index];
                GaitPlayer gait = snapshot.Players[index];
                _players.Add(position.PlayerId, new AnimationFramePlayer(position, gait));
            }
        }

        internal bool TryGetPlayer(long id, out AnimationFramePlayer player)
        {
            return _players.TryGetValue(id, out player);
        }
    }

    internal readonly struct AnimationFramePlayer
    {
        internal Vector3 Position { get; }
        internal GaitPlayer Gait { get; }

        internal AnimationFramePlayer(SnapshotPlayer position, GaitPlayer gait)
        {
            Position = new Vector3(position.X, position.Y, position.Z);
            Gait = gait;
        }
    }

    internal readonly struct AnimationRenderSample
    {
        private readonly AnimationFrame _previous;
        private readonly AnimationFrame _next;

        internal float Fraction { get; }
        internal double ServerTimeSeconds => _previous.ServerTimeSeconds +
            (_next.ServerTimeSeconds - _previous.ServerTimeSeconds) * Fraction;

        internal AnimationRenderSample(AnimationFrame previous, AnimationFrame next, float fraction)
        {
            _previous = previous;
            _next = next;
            Fraction = fraction;
        }

        internal bool TryGetPlayer(long id, out AnimationFramePlayer previous, out AnimationFramePlayer next)
        {
            bool existed = _previous.TryGetPlayer(id, out previous);
            bool exists = _next.TryGetPlayer(id, out next);
            if (!existed)
            {
                previous = next;
                return exists && Fraction >= 1f;
            }
            if (!exists)
            {
                next = previous;
            }
            return true;
        }
    }

    internal sealed class ServerCharacterView
    {
        private const string MODEL_RESOURCE = "bob_rigged";
        private const float PELVIS_HEIGHT_METERS = 0.9514f;
        private const float CENTIDEGREES_PER_DEGREE = 100f;
        private readonly Transform _root;
        private readonly Transform[] _fallbackBones;
        private readonly short[] _fallbackPitches;
        private readonly Transform _fallbackTorso;
        private readonly ShooterAimView _aim;
        private readonly ClipGaitAnimator _gaitAnimator;

        internal ServerCharacterView(Transform marker, Mesh boxMesh, Material material)
        {
            _root = new GameObject("ServerRig").transform;
            _root.SetParent(marker, false);
            var feet = new GameObject("Root").transform;
            feet.SetParent(_root, false);

            var prefab = Resources.Load<GameObject>(MODEL_RESOURCE);
            if (prefab != null)
            {
                GameObject model = UnityEngine.Object.Instantiate(prefab, feet, false);
                model.name = "Bob";
                model.transform.localPosition = Vector3.up * PELVIS_HEIGHT_METERS;
                var skin = model.GetComponentInChildren<SkinnedMeshRenderer>();
                if (skin != null)
                {
                    _gaitAnimator = ClipGaitAnimator.TryCreate(model);
                    if (_gaitAnimator != null)
                    {
                        skin.sharedMaterial = material;
                        _aim = ShooterAimView.TryCreate(model.transform, feet);
                        return;
                    }
                }
                model.SetActive(false);
                UnityEngine.Object.Destroy(model);
            }

            _fallbackBones = CreateBoxRig(feet, boxMesh, material);
            _fallbackPitches = new short[_fallbackBones.Length];
            _fallbackTorso = feet.Find("Torso");
        }

        internal bool TryGetMuzzlePosition(out Vector3 position)
        {
            position = default;
            if (_aim == null || !_aim.Muzzle.gameObject.activeInHierarchy)
            {
                return false;
            }
            position = _aim.Muzzle.position;
            return true;
        }

        internal void BeginLocalShot()
        {
            _aim?.ApplyRecoil(0d);
        }

        internal void Apply(GaitPlayer previous, GaitPlayer next, float fraction, double serverTimeSeconds,
            float aimYawDegrees, float aimPitchDegrees, double shotAgeSeconds)
        {
            _root.rotation = Quaternion.Euler(0f, aimYawDegrees, 0f);

            // Subtract before converting to float so the counter can wrap or cross multiple cycles.
            uint advance = unchecked(next.CyclePhase - previous.CyclePhase);
            const int UNITS = GaitPlayer.PHASE_UNITS_PER_CYCLE;
            double phaseUnits = previous.CyclePhase % UNITS + advance * (double)fraction;
            double phase = phaseUnits % UNITS / UNITS;
            float walkWeight = Mathf.Lerp(previous.IsWalking ? 1f : 0f, next.IsWalking ? 1f : 0f, fraction);

            if (_gaitAnimator != null)
            {
                _gaitAnimator.Sample(phase, walkWeight);
                _aim?.Apply(serverTimeSeconds, Quaternion.Euler(aimPitchDegrees, aimYawDegrees, 0f), shotAgeSeconds);
                return;
            }

            AnimationRig.SampleWalk(phase * AnimationRig.WALK_DURATION_SECONDS, walkWeight > 0f, _fallbackPitches);
            for (int index = 0; index < _fallbackBones.Length; index++)
            {
                float pitch = walkWeight * _fallbackPitches[index] / CENTIDEGREES_PER_DEGREE;
                _fallbackBones[index].localRotation = Quaternion.Euler(pitch, 0f, 0f);
            }
            _fallbackTorso.localRotation *= Quaternion.Euler(aimPitchDegrees, 0f, 0f);
        }

        private static Transform[] CreateBoxRig(Transform feet, Mesh boxMesh, Material material)
        {
            var bones = new Transform[AnimationRig.Bones.Count];
            bones[0] = feet;
            for (int index = 1; index < bones.Length; index++)
            {
                AnimationBoneDefinition definition = AnimationRig.Bones[index];
                var bone = new GameObject(definition.Name).transform;
                bone.SetParent(bones[definition.ParentIndex], false);
                bone.localPosition = ToUnityVector(definition.LocalPosition);
                bones[index] = bone;

                var box = new GameObject(definition.Name + "Box");
                box.transform.SetParent(bone, false);
                box.transform.localPosition = ToUnityVector(definition.BoxOffset);
                box.transform.localScale = ToUnityVector(definition.BoxSize);
                box.AddComponent<MeshFilter>().sharedMesh = boxMesh;
                box.AddComponent<MeshRenderer>().sharedMaterial = material;
            }
            return bones;
        }

        private static Vector3 ToUnityVector(System.Numerics.Vector3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }
    }
}
