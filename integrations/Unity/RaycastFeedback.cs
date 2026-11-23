using System;
using System.Globalization;
using System.Text.RegularExpressions;
using DeathWish.Protocol;
using UnityEngine;

namespace DeathWish.Client
{
    internal readonly struct ClientShot
    {
        public readonly CameraRay Request;
        public readonly Ray Aim;
        public readonly Vector3 TrailStart;

        public ClientShot(CameraRay request, Ray aim, Vector3 trailStart)
        {
            Request = request;
            Aim = aim;
            TrailStart = trailStart;
        }
    }

    internal sealed class RaycastFeedback
    {
        private const int HISTORY_SIZE = 16;
        private const int RING_SEGMENTS = 20;
        private const float MISS_RANGE_METERS = 100f;
        private const float TRAVEL_SECONDS = 0.07f;
        private const float TRAIL_SECONDS = 0.17f;
        private const float IMPACT_SECONDS = 0.24f;
        private const float GIZMO_SECONDS = 4f;
        private const string NUMBER = @"(-?\d+\.\d{2})";
        private static readonly Regex HitReport = new Regex(
            @"\ARaycast hit (terrain|player \d+) at \(" + NUMBER + ", " + NUMBER + ", " + NUMBER +
            @"\), " + NUMBER + @" m\z", RegexOptions.CultureInvariant);
        private static readonly Color TrailColor = new Color(1f, 0.83f, 0.34f);
        private static readonly Color TerrainColor = new Color(1f, 0.55f, 0.12f);
        private static readonly Color PlayerColor = new Color(1f, 0.2f, 0.32f);
        private readonly Transform _parent;
        private readonly Material _material;
        private readonly Effect[] _history = new Effect[HISTORY_SIZE];
        private int _next;

        private sealed class Effect
        {
            public LineRenderer Trail;
            public LineRenderer Impact;
            public Vector3 Origin;
            public Vector3 Start;
            public Vector3 End;
            public Color HitColor;
            public bool HasHit;
            public bool IsVisible;
            public float StartSeconds;
            public float GizmoUntil = float.NegativeInfinity;
        }

        public RaycastFeedback(Transform parent, Shader shader)
        {
            _parent = parent;
            _material = new Material(shader);
        }

        // A hit report includes the authoritative endpoint and distance; a miss only acknowledges the saved aim.
        internal static bool TryResolve(ClientShot shot, string report, out Vector3 origin, out Vector3 end,
            out bool hasHit, out bool hitPlayer)
        {
            origin = shot.Aim.origin;
            end = shot.Aim.GetPoint(MISS_RANGE_METERS);
            hasHit = false;
            hitPlayer = false;
            if (report == "Raycast miss")
            {
                return true;
            }

            Match match = HitReport.Match(report ?? string.Empty);
            if (!match.Success || !ReadNumber(match.Groups[2], out float x) || !ReadNumber(match.Groups[3], out float y) ||
                !ReadNumber(match.Groups[4], out float z) || !ReadNumber(match.Groups[5], out float distance) ||
                distance < 0 || distance > MISS_RANGE_METERS)
            {
                return false;
            }
            end = new Vector3(x, y, z);
            origin = end - shot.Aim.direction * distance;
            hasHit = true;
            hitPlayer = match.Groups[1].Value.StartsWith("player ", StringComparison.Ordinal);
            return true;
        }

        private static bool ReadNumber(Group group, out float value)
        {
            return float.TryParse(group.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !float.IsInfinity(value) && !float.IsNaN(value);
        }

        public void Show(ClientShot shot, string report, float now)
        {
            if (!TryResolve(shot, report, out Vector3 origin, out Vector3 end, out bool hasHit, out bool hitPlayer))
            {
                return;
            }
            Effect effect = _history[_next];
            if (effect == null)
            {
                effect = new Effect { Trail = MakeLine("BulletTrail", 2), Impact = MakeLine("HitEffect", RING_SEGMENTS) };
                effect.Impact.useWorldSpace = false;
                effect.Impact.loop = true;
                for (int i = 0; i < RING_SEGMENTS; i++)
                {
                    float angle = 2f * Mathf.PI * i / RING_SEGMENTS;
                    effect.Impact.SetPosition(i, new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0));
                }
                _history[_next] = effect;
            }
            _next = (_next + 1) % HISTORY_SIZE;
            effect.Start = shot.TrailStart;
            effect.Origin = origin;
            effect.End = end;
            effect.HasHit = hasHit;
            effect.HitColor = hitPlayer ? PlayerColor : TerrainColor;
            effect.StartSeconds = now;
            effect.GizmoUntil = now + GIZMO_SECONDS;
            effect.IsVisible = true;
            effect.Impact.transform.SetPositionAndRotation(end - shot.Aim.direction * 0.025f,
                Quaternion.LookRotation(shot.Aim.direction));
            Animate(effect, 0f);
        }

        private LineRenderer MakeLine(string name, int count)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(_parent, false);
            var line = obj.AddComponent<LineRenderer>();
            line.sharedMaterial = _material;
            line.useWorldSpace = true;
            line.positionCount = count;
            line.enabled = false;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        public void Update(float now)
        {
            foreach (Effect effect in _history)
            {
                if (effect != null && effect.IsVisible)
                {
                    Animate(effect, now - effect.StartSeconds);
                }
            }
        }

        private static void Animate(Effect effect, float age)
        {
            effect.Trail.enabled = age < TRAIL_SECONDS;
            if (effect.Trail.enabled)
            {
                float head = Mathf.Clamp01((age + 0.01f) / TRAVEL_SECONDS);
                float tail = Mathf.InverseLerp(0.035f, TRAIL_SECONDS, age);
                effect.Trail.SetPosition(0, Vector3.Lerp(effect.Start, effect.End, tail));
                effect.Trail.SetPosition(1, Vector3.Lerp(effect.Start, effect.End, head));
                effect.Trail.startWidth = 0.018f;
                effect.Trail.endWidth = 0.055f;
                Color color = TrailColor;
                color.a = 1f - tail;
                effect.Trail.endColor = color;
                color.a *= 0.3f;
                effect.Trail.startColor = color;
            }
            float impactAge = age - TRAVEL_SECONDS;
            effect.Impact.enabled = effect.HasHit && impactAge >= 0f && impactAge < IMPACT_SECONDS;
            if (effect.Impact.enabled)
            {
                float t = impactAge / IMPACT_SECONDS;
                effect.Impact.transform.localScale = Vector3.one * Mathf.Lerp(0.07f, 0.34f, t);
                Color color = effect.HitColor;
                color.a = 1f - t;
                effect.Impact.startColor = color;
                effect.Impact.endColor = color;
                effect.Impact.widthMultiplier = Mathf.Lerp(0.055f, 0.015f, t);
            }
            effect.IsVisible = age < (effect.HasHit ? TRAVEL_SECONDS + IMPACT_SECONDS : TRAIL_SECONDS);
        }

        public void DrawGizmos(float now)
        {
            foreach (Effect effect in _history)
            {
                if (effect == null || now > effect.GizmoUntil)
                {
                    continue;
                }
                Gizmos.color = effect.HasHit ? effect.HitColor : Color.cyan;
                Gizmos.DrawLine(effect.Origin, effect.End);
                Gizmos.DrawWireSphere(effect.Origin, 0.06f);
                if (effect.HasHit)
                {
                    Gizmos.DrawWireSphere(effect.End, 0.13f);
                }
            }
        }

        public void Clear()
        {
            foreach (Effect effect in _history)
            {
                if (effect != null)
                {
                    effect.Trail.enabled = false;
                    effect.Impact.enabled = false;
                    effect.IsVisible = false;
                    effect.GizmoUntil = float.NegativeInfinity;
                }
            }
        }

        public void Dispose()
        {
            foreach (Effect effect in _history)
            {
                if (effect?.Trail != null)
                {
                    UnityEngine.Object.Destroy(effect.Trail.gameObject);
                }
                if (effect?.Impact != null)
                {
                    UnityEngine.Object.Destroy(effect.Impact.gameObject);
                }
            }
            UnityEngine.Object.Destroy(_material);
        }
    }
}
