using UnityEngine;

namespace DeathWish.Client
{
    internal sealed class ArenaWeaponVisual
    {
        private const float FLASH_SECONDS = 0.09f;
        private const float RECOIL_SECONDS = 0.14f;
        private const float RECOIL_DISTANCE_METERS = 0.07f;
        private static readonly Vector3 MuzzleOffsetMeters = new Vector3(0, 0.025f, 0.67f);
        internal static readonly Vector3 RightMountMeters = new Vector3(0.55f, 1.17f, 0.28f);

        private readonly Transform _root;
        private readonly GameObject _flash;
        private float _firedAtSeconds = float.NegativeInfinity;
        private Vector3 _shotDirection;

        internal bool IsVisible
        {
            get => _root != null && _root.gameObject.activeInHierarchy;
            set
            {
                if (_root != null)
                {
                    _root.gameObject.SetActive(value);
                }
            }
        }

        internal Vector3 MuzzlePosition => _root.TransformPoint(MuzzleOffsetMeters);

        internal ArenaWeaponVisual(Transform parent, string name, Mesh box, Material housing, Material accent,
            Material flash)
        {
            _root = new GameObject(name).transform;
            _root.SetParent(parent, false);
            CreatePart("Receiver", box, _root, Vector3.zero, new Vector3(0.24f, 0.23f, 0.53f), housing);
            CreatePart("Barrel", box, _root, new Vector3(0, 0.025f, 0.43f), new Vector3(0.13f, 0.13f, 0.38f), housing);
            CreatePart("Muzzle collar", box, _root, new Vector3(0, 0.025f, 0.615f), new Vector3(0.17f, 0.17f, 0.11f), accent);
            CreatePart("Top rail", box, _root, new Vector3(0, 0.13f, 0.02f), new Vector3(0.18f, 0.05f, 0.40f), accent);
            CreatePart("Side panel", box, _root, new Vector3(0.122f, 0.015f, 0.05f), new Vector3(0.025f, 0.095f, 0.31f), accent);
            CreatePart("Grip", box, _root, new Vector3(0, -0.17f, -0.11f), new Vector3(0.14f, 0.2f, 0.16f), housing);
            CreatePart("Mount", box, _root, new Vector3(-0.14f, -0.13f, -0.14f),
                new Vector3(0.27f, 0.105f, 0.30f), housing);
            _flash = new GameObject("Muzzle flash");
            _flash.transform.SetParent(_root, false);
            _flash.transform.localPosition = MuzzleOffsetMeters;
            CreatePart("Flash core", box, _flash.transform, new Vector3(0, 0, 0.07f),
                new Vector3(0.09f, 0.09f, 0.20f), flash);
            CreatePart("Flash flare", box, _flash.transform, new Vector3(0, 0, 0.03f),
                new Vector3(0.21f, 0.045f, 0.08f), flash);
            _flash.SetActive(false);
        }

        internal void Fire(float nowSeconds, Vector3 direction)
        {
            _firedAtSeconds = nowSeconds;
            _shotDirection = direction;
            _flash.SetActive(true);
        }

        internal bool TryGetFiringDirection(float nowSeconds, out Vector3 direction)
        {
            direction = _shotDirection;
            return nowSeconds - _firedAtSeconds < RECOIL_SECONDS && direction.sqrMagnitude > 0;
        }

        internal void Pose(Vector3 mountMeters, float pitchDegrees, float scale, float nowSeconds)
        {
            float ageSeconds = nowSeconds - _firedAtSeconds;
            float recoil = Mathf.Clamp01(1f - ageSeconds / RECOIL_SECONDS);
            var rotation = Quaternion.Euler(pitchDegrees - 5f * recoil, 0, 0);
            _root.localRotation = rotation;
            _root.localPosition = mountMeters - rotation * Vector3.forward * (RECOIL_DISTANCE_METERS * scale * recoil);
            _root.localScale = Vector3.one * scale;
            _flash.SetActive(ageSeconds < FLASH_SECONDS);
        }

        internal void Reset()
        {
            _firedAtSeconds = float.NegativeInfinity;
            if (_flash != null)
            {
                _flash.SetActive(false);
            }
        }

        internal void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root.gameObject);
            }
        }

        private static void CreatePart(string name, Mesh mesh, Transform parent, Vector3 offsetMeters,
            Vector3 scale, Material material)
        {
            var part = new GameObject(name);
            part.transform.SetParent(parent, false);
            part.transform.localPosition = offsetMeters;
            part.transform.localScale = scale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            part.AddComponent<MeshRenderer>().sharedMaterial = material;
        }
    }
}
