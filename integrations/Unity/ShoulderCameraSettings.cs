using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace DeathWish.Client
{
    [Serializable]
    internal sealed class ShoulderCameraSettings
    {
        // At maximum height and distance, the arena side limit keeps the eye offset inside 8 m.
        private const float MIN_SIDE_METERS = 0f;
        private const float MAX_SIDE_METERS = 1.6f;
        private const float DEFAULT_SIDE_METERS = 0.9f;
        private const float ARENA_BODY_VIEWPORT_INSET = 0.16f;
        private const float ARENA_MIN_BODY_VIEWPORT_INSET = 0.09f;
        private const float MAX_ARENA_SIDE_METERS = 3.2f;
        private const float MIN_HEIGHT_METERS = 0.6f;
        private const float MAX_HEIGHT_METERS = 2.3f;
        private const float MIN_DISTANCE_METERS = 1f;
        private const float MAX_DISTANCE_METERS = 5f;
        private const string PREFIX = "DeathWish.Camera.";

        [SerializeField, Range(MIN_SIDE_METERS, MAX_SIDE_METERS)] private float _sideMeters = DEFAULT_SIDE_METERS;
        [SerializeField, Range(MIN_HEIGHT_METERS, MAX_HEIGHT_METERS)] private float _heightMeters = 1.55f;
        [SerializeField, Range(MIN_DISTANCE_METERS, MAX_DISTANCE_METERS)] private float _distanceMeters = 3.3f;
        [SerializeField] private InputField _sideInput = null;
        [SerializeField] private InputField _heightInput = null;
        [SerializeField] private InputField _distanceInput = null;

        public float SideMeters => _sideMeters;
        public float HeightMeters => _heightMeters;
        public float DistanceMeters => _distanceMeters;

        internal float GetArenaSideMeters(float frustumHalfWidthMeters)
        {
            float defaultShift = 1f - 2f * ARENA_BODY_VIEWPORT_INSET;
            float maxShift = 1f - 2f * ARENA_MIN_BODY_VIEWPORT_INSET;
            float shift = Mathf.Min(defaultShift * _sideMeters / DEFAULT_SIDE_METERS, maxShift);
            return Mathf.Min(frustumHalfWidthMeters * shift, MAX_ARENA_SIDE_METERS);
        }

        public void Initialize()
        {
            Bind(_sideInput, "SideMeters", _sideMeters, MIN_SIDE_METERS, MAX_SIDE_METERS, v => _sideMeters = v);
            Bind(_heightInput, "HeightMeters", _heightMeters, MIN_HEIGHT_METERS, MAX_HEIGHT_METERS, v => _heightMeters = v);
            Bind(_distanceInput, "DistanceMeters", _distanceMeters, MIN_DISTANCE_METERS, MAX_DISTANCE_METERS, v => _distanceMeters = v);
        }

        private static void Bind(InputField input, string key, float initial, float min, float max, Action<float> apply)
        {
            key = PREFIX + key;
            float value = PlayerPrefs.GetFloat(key, initial);
            value = float.IsNaN(value) || float.IsInfinity(value) ? initial : Mathf.Clamp(value, min, max);
            apply(value);
            if (input == null)
            {
                return;
            }
            input.SetTextWithoutNotify(value.ToString("0.##", CultureInfo.InvariantCulture));
            input.onEndEdit.AddListener(text =>
            {
                if (!input.wasCanceled && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) &&
                    !float.IsNaN(parsed) && !float.IsInfinity(parsed))
                {
                    value = Mathf.Clamp(parsed, min, max);
                    apply(value);
                    PlayerPrefs.SetFloat(key, value);
                    PlayerPrefs.Save();
                }
                input.SetTextWithoutNotify(value.ToString("0.##", CultureInfo.InvariantCulture));
            });
        }
    }
}
