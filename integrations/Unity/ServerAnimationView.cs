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

        internal void Add(AnimatedWorldSnapshot snapshot, double receivedAtSeconds)
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
                    receivedAtSeconds - _lastReceivedAtSeconds > MAX_FRAME_GAP_SECONDS ||
                    snapshot.RigId != latest.RigId)
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
        internal uint RigId { get; }

        internal AnimationFrame(AnimatedWorldSnapshot snapshot, double serverTimeSeconds)
        {
            ServerTimeSeconds = serverTimeSeconds;
            RigId = snapshot.RigId;
            _players = new Dictionary<long, AnimationFramePlayer>(snapshot.Players.Count);

            // The wire codec reads position and pose from the same ordered player records.
            for (int index = 0; index < snapshot.Players.Count; index++)
            {
                SnapshotPlayer position = snapshot.Positions.Players[index];
                AnimatedPlayer pose = snapshot.Players[index];
                _players.Add(position.PlayerId, new AnimationFramePlayer(position, pose));
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
        internal AnimatedPlayer Pose { get; }

        internal AnimationFramePlayer(SnapshotPlayer position, AnimatedPlayer pose)
        {
            Position = new Vector3(position.X, position.Y, position.Z);
            Pose = pose;
        }
    }

    internal readonly struct AnimationRenderSample
    {
        private readonly AnimationFrame _previous;
        private readonly AnimationFrame _next;

        internal float Fraction { get; }

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
        private const float CENTIDEGREES_PER_DEGREE = 100f;
        private readonly Transform _root;
        private readonly Transform[] _bones;

        internal ServerCharacterView(Transform marker, Mesh boxMesh, Material material)
        {
            _root = new GameObject("ServerRig").transform;
            _root.SetParent(marker, false);
            _bones = new Transform[AnimationRig.Bones.Count];
            for (int index = 0; index < _bones.Length; index++)
            {
                AnimationBoneDefinition definition = AnimationRig.Bones[index];
                var bone = new GameObject(definition.Name).transform;
                bone.SetParent(definition.ParentIndex < 0 ? _root : _bones[definition.ParentIndex], false);
                bone.localPosition = ToUnityVector(definition.LocalPosition);
                _bones[index] = bone;

                if (definition.BoxSize == System.Numerics.Vector3.Zero)
                {
                    continue;
                }

                var box = new GameObject(definition.Name + "Box");
                box.transform.SetParent(bone, false);
                box.transform.localPosition = ToUnityVector(definition.BoxOffset);
                box.transform.localScale = ToUnityVector(definition.BoxSize);
                box.AddComponent<MeshFilter>().sharedMesh = boxMesh;
                box.AddComponent<MeshRenderer>().sharedMaterial = material;
            }
        }

        internal void Apply(AnimatedPlayer previous, AnimatedPlayer next, float fraction)
        {
            // Keep the parent marker's yaw for camera input and the local shot trail.
            _root.rotation = Quaternion.Slerp(
                Quaternion.Euler(0f, previous.YawCentidegrees / CENTIDEGREES_PER_DEGREE, 0f),
                Quaternion.Euler(0f, next.YawCentidegrees / CENTIDEGREES_PER_DEGREE, 0f), fraction);
            for (int index = 0; index < _bones.Length; index++)
            {
                float previousPitch = previous.BonePitchCentidegrees[index] / CENTIDEGREES_PER_DEGREE;
                float nextPitch = next.BonePitchCentidegrees[index] / CENTIDEGREES_PER_DEGREE;
                _bones[index].localRotation = Quaternion.Slerp(
                    Quaternion.Euler(previousPitch, 0f, 0f), Quaternion.Euler(nextPitch, 0f, 0f), fraction);
            }
        }

        private static Vector3 ToUnityVector(System.Numerics.Vector3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }
    }
}
