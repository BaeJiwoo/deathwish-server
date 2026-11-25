using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace DeathWish.Client
{
    [Serializable]
    internal sealed class ShoulderCameraSettings
    {
        // Even at maximum pitch and all three maxima, the camera stays inside the protocol's 8 m limit.
        private const float MIN_SIDE_METERS = 0f;
        private const float MAX_SIDE_METERS = 1.6f;
        private const float MIN_HEIGHT_METERS = 0.6f;
        private const float MAX_HEIGHT_METERS = 2.3f;
        private const float MIN_DISTANCE_METERS = 1f;
        private const float MAX_DISTANCE_METERS = 5f;
        private const string PREFIX = "DeathWish.Camera.";

        [SerializeField, Range(MIN_SIDE_METERS, MAX_SIDE_METERS)] private float _sideMeters = 0.9f;
        [SerializeField, Range(MIN_HEIGHT_METERS, MAX_HEIGHT_METERS)] private float _heightMeters = 1.55f;
        [SerializeField, Range(MIN_DISTANCE_METERS, MAX_DISTANCE_METERS)] private float _distanceMeters = 3.3f;
        [SerializeField] private InputField _sideInput = null;
        [SerializeField] private InputField _heightInput = null;
        [SerializeField] private InputField _distanceInput = null;

        public float SideMeters => _sideMeters;
        public float HeightMeters => _heightMeters;
        public float DistanceMeters => _distanceMeters;

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
