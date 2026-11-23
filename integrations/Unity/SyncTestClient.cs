using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using DeathWish.MockClient.Core;
using DeathWish.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace DeathWish.Client
{
    public sealed class SyncTestClient : MonoBehaviour
    {
        private const float INPUT_INTERVAL_SECONDS = 1f / 30f;
        private const float LOOK_SENSITIVITY = 2f;
        private const float DEGREES_PER_TURN = 360f;
        private const float GIZMO_RADIUS_METERS = 0.35f;
        private const float DEFAULT_PITCH_DEGREES = 8f;
        private const float MIN_PITCH_DEGREES = -65f;
        private const float MAX_PITCH_DEGREES = 75f;
        private const float CAMERA_CLEARANCE_METERS = 0.2f;
        private static readonly Vector3 TrailOriginLocalMeters = new Vector3(0.25f, 1.24f, 0.36f);
        private const string SHOULDER_PREF = "DeathWish.Camera.LeftShoulder";
        private const float FLOOR_SIZE_METERS = 100f;
        private const float LAB_WALL_OFFSET_METERS = 54f;
        private const float LAB_WALL_HEIGHT_METERS = 12f;
        private static readonly Color LocalAccent = new Color32(18, 78, 218, 255);
        private static readonly Color RemoteAccent = new Color32(255, 196, 24, 255);
        private const double SNAPSHOT_BLEND_SECONDS = 0.1;
        private const int TIMEOUT_SECONDS = 5;
        private const int MAX_PORT = 65535;

        [SerializeField] private InputField _hostInput = null;
        [SerializeField] private InputField _portInput = null;
        [SerializeField] private InputField _roomInput = null;
        [SerializeField] private Button _connectButton = null;
        [SerializeField] private Button _captureButton = null;
        [SerializeField] private Button _disconnectButton = null;
        [SerializeField] private Text _statusText = null;
        [SerializeField] private Text _sessionText = null;
        [SerializeField] private Text _positionText = null;
        [SerializeField] private Text _inputText = null;
        [SerializeField] private GameObject _connectionPanel = null;
        [SerializeField] private GameObject _aimHud = null;
        [SerializeField] private Text _shoulderLabel = null;
        [SerializeField] private Text _aimResult = null;
        [SerializeField] private ShoulderCameraSettings _shoulderCamera = new ShoulderCameraSettings();
        [SerializeField] private bool _showRaycastGizmos = true;

        private readonly Dictionary<long, Vector3> _positions = new Dictionary<long, Vector3>();
        private readonly Dictionary<long, Vector3> _blendStarts = new Dictionary<long, Vector3>();
        private double _blendStartSeconds;
        private bool _hasSnapshot;
        private readonly Dictionary<long, Transform> _markers = new Dictionary<long, Transform>();
        private readonly List<long> _departedPlayers = new List<long>();
        private Transform _viewCamera;
        private Camera _sceneCamera;
        private bool _isLeftShoulder;
        private GameObject _flatMap;
        private GameObject _physicsLab;
        private readonly List<Mesh> _labMeshes = new List<Mesh>();
        private Mesh _playerMesh;
        private Material _floorMaterial;
        private Material _wallMaterial;
        private Material _cautionMaterial;
        private Material _localMaterial;
        private Material _remoteMaterial;
        private ConnectionTestClient _client;
        private CancellationTokenSource _cancellation;
        private string _status = "PhysicsLab preview | Disconnected";
        private string _latestRayResult = string.Empty;
        private long _playerId;
        private long _lastTick;
        private uint _sequence;
        private float _yawDegrees;
        private float _pitchDegrees = DEFAULT_PITCH_DEGREES;
        private float _nextInputSeconds;
        private bool _isConnecting;
        private bool _isSending;
        private int _captureFrame;
        private const int MAX_BUFFERED_ACTIONS = 16;
        private readonly Queue<ClientShot?> _actions = new Queue<ClientShot?>();
        private readonly Queue<ClientShot> _sentShots = new Queue<ClientShot>();
        private RaycastFeedback _feedback;
        private bool _hasFocus;
        private bool _isConfigured;
        private int _generation;

        private void Awake()
        {
            _isConfigured = _hostInput != null && _portInput != null && _roomInput != null &&
                _connectButton != null && _captureButton != null && _disconnectButton != null &&
                _statusText != null && _sessionText != null && _positionText != null && _inputText != null;
            if (!_isConfigured)
            {
                Debug.LogError("SyncTestScene is missing Canvas references. Open the supplied scene and check SyncTestClient in the Inspector.");
                enabled = false;
                return;
            }

            _isLeftShoulder = PlayerPrefs.GetInt(SHOULDER_PREF, 0) != 0;
            _shoulderCamera.Initialize();
            UpdateShoulderLabel();
            Shader feedbackShader = Resources.Load<Shader>("ShotFeedback");
            if (feedbackShader != null && feedbackShader.isSupported)
            {
                _feedback = new RaycastFeedback(transform, feedbackShader);
            }
            _sceneCamera = Camera.main;
            Shader shader = Resources.Load<Shader>("SyncTestWorld");
            Mesh floorMesh = Resources.GetBuiltinResource<Mesh>("Plane.fbx");
            _playerMesh = Resources.GetBuiltinResource<Mesh>("Capsule.fbx");
            if (_sceneCamera != null && shader != null && shader.isSupported && floorMesh != null && _playerMesh != null)
            {
                _viewCamera = _sceneCamera.transform;
                CreateWorldView(shader, floorMesh);
                SelectMap(true);
            }
            else
            {
                Debug.LogWarning("Third-person view needs a MainCamera, built-in meshes and the SyncTestWorld shader.");
            }

            Application.runInBackground = true;
            _hasFocus = Application.isFocused;
            RefreshView();
        }

        /// <summary>Called by the scene's Connect button on Unity's main thread.</summary>
        public void Connect()
        {
            if (!_isConfigured || !isActiveAndEnabled || _client != null || _isConnecting)
            {
                return;
            }

            _ = ConnectAsync();
        }

        private async Task ConnectAsync()
        {
            string host = _hostInput.text.Trim();
            if (host.Length == 0 || !int.TryParse(_portInput.text, out int port) || port < 1 || port > MAX_PORT ||
                !int.TryParse(_roomInput.text, out int room) || room <= 0)
            {
                _status = "Enter a host, port (1-65535), and positive room ID.";
                RefreshView();
                return;
            }

            _isConnecting = true;
            int generation = ++_generation;
            _cancellation = new CancellationTokenSource();
            var client = new ConnectionTestClient();
            _client = client;
            _status = "Connecting...";
            RefreshView();
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(TIMEOUT_SECONDS));
                    await client.ConnectAsync(host, port, timeout.Token);
                    JoinRoomResponse response = await client.JoinRoomAsync(room, timeout.Token);
                    if (response.Result != RoomJoinResult.Success)
                    {
                        throw new InvalidOperationException("Join failed: " + response.Result);
                    }

                    JoinAck ack = await client.WaitForAdmissionAsync(timeout.Token);
                    if (generation != _generation)
                    {
                        return;
                    }

                    SelectMap(ack.IsPhysicsLab);
                    _playerId = ack.PlayerId;
                    _lastTick = ack.ServerTick;
                    _positions[_playerId] = new Vector3(ack.X, ack.Y, ack.Z);
                    _status = ack.IsPhysicsLab ? "Connected | PhysicsLab" : "Connected | Flat map";
                }
            }
            catch (Exception error)
            {
                if (generation == _generation)
                {
                    Disconnect();
                    _status = error.Message;
                    RefreshView();
                }
            }
            finally
            {
                if (generation == _generation)
                {
                    _isConnecting = false;
                    RefreshView();
                }
            }
        }

        private void Update()
        {
            if (_client == null)
            {
                return;
            }
            if (_client.Disconnected.IsCompleted)
            {
                string reason = _client.DisconnectError?.Message ?? "Server closed connection.";
                Disconnect();
                _status = reason;
                RefreshView();
                return;
            }
            if (_playerId == 0)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                ReleaseInput();
            }
            bool canControl = _hasFocus && Cursor.lockState == CursorLockMode.Locked;
            if (canControl)
            {
                if (Input.GetKeyDown(KeyCode.Q))
                {
                    SwapShoulder();
                }
                _yawDegrees = Mathf.Repeat(_yawDegrees + Input.GetAxisRaw("Mouse X") * LOOK_SENSITIVITY, DEGREES_PER_TURN);
                _pitchDegrees = Mathf.Clamp(_pitchDegrees - Input.GetAxisRaw("Mouse Y") * LOOK_SENSITIVITY,
                    MIN_PITCH_DEGREES, MAX_PITCH_DEGREES);
            }

            WorldSnapshot snapshot = _client.LatestSnapshot;
            if (snapshot != null && (!_hasSnapshot || snapshot.ServerTick > _lastTick))
            {
                ApplySnapshot(snapshot, Time.unscaledTimeAsDouble);
            }
            while (_client.TryReadRaycastResult(out string result))
            {
                _statusText.text = _status = result;
                _latestRayResult = result;
                if (_sentShots.Count > 0)
                {
                    ClientShot shot = _sentShots.Dequeue();
                    _feedback?.Show(shot, result, Time.unscaledTime);
                }
                Debug.Log(result);
            }
            RefreshTelemetry();

            // A synchronous send failure can disconnect. Do not read connection state afterward.
            if (!_isSending && (Time.unscaledTime >= _nextInputSeconds || _actions.Count > 0))
            {
                float strafe = canControl ? Axis(KeyCode.D, KeyCode.A) : 0;
                float forward = canControl ? Axis(KeyCode.W, KeyCode.S) : 0;
                _nextInputSeconds = Time.unscaledTime + INPUT_INTERVAL_SECONDS;
                _ = SendInputAsync(strafe, forward);
            }
        }

        // Continuations return to Unity's main thread; only one write can be in flight.
        private async Task SendInputAsync(float strafe, float forward)
        {
            ConnectionTestClient client = _client;
            CancellationTokenSource cancellation = _cancellation;
            if (client == null || cancellation == null)
            {
                return;
            }

            int generation = _generation;
            _isSending = true;
            try
            {
                MoveInput input = MovementInput.FromLocalAxes(_sequence++, strafe, forward, _yawDegrees);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(TIMEOUT_SECONDS));
                    bool hasAction = _actions.Count > 0;
                    ClientShot? shot = hasAction ? _actions.Dequeue() : null;
                    await client.SendMoveAsync(input, timeout.Token);
                    if (hasAction && generation == _generation)
                    {
                        if (shot.HasValue)
                        {
                            // TCP replies follow send order. Register before awaiting a possibly synchronous response.
                            _sentShots.Enqueue(shot.Value);
                            await client.SendRayAsync(shot.Value.Request, timeout.Token);
                        }
                        else
                        {
                            await client.SendJumpAsync(timeout.Token);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                if (generation == _generation)
                {
                    Disconnect();
                    _status = error.Message;
                    RefreshView();
                }
            }
            finally
            {
                if (generation == _generation)
                {
                    _isSending = false;
                }
            }
        }

        private void ApplySnapshot(WorldSnapshot snapshot, double nowSeconds)
        {
            // If a target arrives early, continue from the current blend to keep the path continuous.
            float fraction = BlendFraction(nowSeconds);
            foreach (var player in _positions)
            {
                _blendStarts[player.Key] = DisplayPosition(player.Key, player.Value, fraction);
            }
            _lastTick = snapshot.ServerTick;
            _hasSnapshot = true;
            _blendStartSeconds = nowSeconds;
            _positions.Clear();
            foreach (SnapshotPlayer player in snapshot.Players)
            {
                _positions.Add(player.PlayerId, new Vector3(player.X, player.Y, player.Z));
            }
        }

        private float BlendFraction(double nowSeconds)
        {
            return Mathf.Clamp01((float)((nowSeconds - _blendStartSeconds) / SNAPSHOT_BLEND_SECONDS));
        }

        private Vector3 DisplayPosition(long id, Vector3 target, float fraction)
        {
            return _blendStarts.TryGetValue(id, out Vector3 start) ? Vector3.Lerp(start, target, fraction) : target;
        }

        private void CreateWorldView(Shader shader, Mesh floorMesh)
        {
            _floorMaterial = new Material(shader) { color = new Color32(227, 235, 238, 255) };
            _floorMaterial.SetFloat("_Grid", 1f);
            _floorMaterial.SetFloat("_FloorHalfSize", FLOOR_SIZE_METERS / 2);
            _localMaterial = new Material(shader) { color = LocalAccent };
            _remoteMaterial = new Material(shader) { color = RemoteAccent };

            _flatMap = new GameObject("Flat map");
            _flatMap.transform.SetParent(transform, false);
            Vector3 meshSize = floorMesh.bounds.size;
            var floorScale = new Vector3(FLOOR_SIZE_METERS / meshSize.x, 1f, FLOOR_SIZE_METERS / meshSize.z);
            CreateVisual("Grid floor", floorMesh, _flatMap.transform, Vector3.zero, floorScale, _floorMaterial, true);

            Mesh panel = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            _wallMaterial = new Material(shader);
            _wallMaterial.SetFloat("_Wall", 1f);
            float span = 2 * LAB_WALL_OFFSET_METERS;
            float height = LAB_WALL_HEIGHT_METERS;
            foreach (float side in new[] { -LAB_WALL_OFFSET_METERS, LAB_WALL_OFFSET_METERS })
            {
                CreateVisual("Observation wall", panel, _flatMap.transform, new Vector3(0, height / 2, side),
                    new Vector3(span, height, 0.4f), _wallMaterial, true);
                CreateVisual("Observation wall", panel, _flatMap.transform, new Vector3(side, height / 2, 0),
                    new Vector3(0.4f, height, span), _wallMaterial, true);
            }
        }

        [DataContract]
        private sealed class LabData
        {
            [DataMember(Name = "objects", IsRequired = true)] public LabObject[] Objects { get; set; }
            [DataMember(Name = "caution")] public LabObject Caution { get; set; }
        }

        [DataContract]
        private sealed class LabObject
        {
            [DataMember(Name = "name", IsRequired = true)] public string Name { get; set; }
            [DataMember(Name = "vertices", IsRequired = true)] public float[][] Vertices { get; set; }
            [DataMember(Name = "triangles", IsRequired = true)] public int[][] Triangles { get; set; }
        }

        private void SelectMap(bool isPhysicsLab)
        {
            if (_flatMap == null)
            {
                return;
            }
            if (isPhysicsLab && _physicsLab == null)
            {
                TextAsset asset = Resources.Load<TextAsset>("PhysicsLab");
                if (asset == null)
                {
                    throw new InvalidOperationException("Missing PhysicsLab map asset.");
                }
                using (var stream = new MemoryStream(asset.bytes))
                {
                    var data = (LabData)new DataContractJsonSerializer(typeof(LabData)).ReadObject(stream);
                    _physicsLab = new GameObject("PhysicsLab");
                    _physicsLab.transform.SetParent(transform, false);
                    foreach (LabObject part in data.Objects)
                    {
                        Material material = part.Name == "Torus" ? _remoteMaterial : _floorMaterial;
                        CreateLabPart(part, material, true);
                    }
                    if (data.Caution != null)
                    {
                        _cautionMaterial = new Material(_floorMaterial);
                        _cautionMaterial.SetFloat("_Caution", 1f);
                        CreateLabPart(data.Caution, _cautionMaterial, false);
                    }
                }
            }
            _flatMap.SetActive(!isPhysicsLab);
            if (_physicsLab != null)
            {
                _physicsLab.SetActive(isPhysicsLab);
            }
        }

        private void CreateLabPart(LabObject part, Material material, bool blocksCamera)
        {
            var mesh = new Mesh { name = part.Name };
            _labMeshes.Add(mesh);
            mesh.vertices = Array.ConvertAll(part.Vertices, xyz => new Vector3(xyz[0], xyz[1], xyz[2]));
            var indices = new int[part.Triangles.Length * 3];
            for (int i = 0; i < part.Triangles.Length; i++)
            {
                // The export uses AC x AB; Unity draws AB x AC.
                int[] triangle = part.Triangles[i];
                int offset = i * 3;
                indices[offset] = triangle[0];
                indices[offset + 1] = triangle[2];
                indices[offset + 2] = triangle[1];
            }
            mesh.triangles = indices;
            mesh.RecalculateNormals();
            CreateVisual(part.Name, mesh, _physicsLab.transform, Vector3.zero, Vector3.one, material, blocksCamera);
        }

        private void LateUpdate()
        {
            _feedback?.Update(Time.unscaledTime);
            RenderWorld(Time.unscaledTimeAsDouble);
            if (_playerId == 0 || !_hasFocus || Cursor.lockState != CursorLockMode.Locked ||
                Time.frameCount == _captureFrame)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Space) && _actions.Count < MAX_BUFFERED_ACTIONS)
            {
                _actions.Enqueue(null);
            }
            if (Input.GetMouseButtonDown(0) && _sentShots.Count + _actions.Count < MAX_BUFFERED_ACTIONS &&
                _viewCamera != null && _markers.TryGetValue(_playerId, out Transform marker))
            {
                Vector3 offset = _viewCamera.position - marker.position;
                Vector3 forward = _sceneCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f)).direction;
                var request = new CameraRay(new System.Numerics.Vector3(offset.x, offset.y, offset.z),
                    new System.Numerics.Vector3(forward.x, forward.y, forward.z));
                _actions.Enqueue(new ClientShot(request, new Ray(_viewCamera.position, forward),
                    marker.TransformPoint(TrailOriginLocalMeters)));
            }
        }

        private void RenderWorld(double nowSeconds)
        {
            if (_viewCamera == null)
            {
                return;
            }

            _departedPlayers.Clear();
            foreach (var marker in _markers)
            {
                if (!_positions.ContainsKey(marker.Key))
                {
                    RemoveMarker(marker.Value);
                    _departedPlayers.Add(marker.Key);
                }
            }
            foreach (long playerId in _departedPlayers)
            {
                _markers.Remove(playerId);
                _blendStarts.Remove(playerId);
            }

            float fraction = BlendFraction(nowSeconds);
            foreach (var player in _positions)
            {
                bool isLocalPlayer = player.Key == _playerId;
                if (!_markers.TryGetValue(player.Key, out Transform marker))
                {
                    marker = new GameObject(isLocalPlayer ? $"Me {player.Key}" : $"Player {player.Key}").transform;
                    marker.SetParent(transform, false);
                    CreateVisual("Body", _playerMesh, marker, Vector3.up * 0.9f,
                        new Vector3(0.65f, 0.9f, 0.65f), isLocalPlayer ? _localMaterial : _remoteMaterial);
                    _markers.Add(player.Key, marker);
                }

                marker.position = DisplayPosition(player.Key, player.Value, fraction);
                if (isLocalPlayer)
                {
                    marker.rotation = Quaternion.Euler(0f, _yawDegrees, 0f);
                }
            }

            if (_playerId == 0)
            {
                var overview = new Vector3(15f, 32f, 22f);
                var target = new Vector3(-5f, 2f, -5f);
                _viewCamera.SetPositionAndRotation(overview, Quaternion.LookRotation(target - overview));
                return;
            }
            Vector3 position = _markers.TryGetValue(_playerId, out Transform localMarker) ? localMarker.position : Vector3.zero;
            Quaternion yaw = Quaternion.Euler(0f, _yawDegrees, 0f);
            Quaternion orbit = Quaternion.Euler(_pitchDegrees, _yawDegrees, 0f);
            Vector3 pivot = position + Vector3.up * _shoulderCamera.HeightMeters;
            float side = _isLeftShoulder ? -1f : 1f;
            Vector3 boom = orbit * Vector3.back * _shoulderCamera.DistanceMeters +
                yaw * Vector3.right * (side * _shoulderCamera.SideMeters);
            if (Physics.SphereCast(pivot, CAMERA_CLEARANCE_METERS, boom.normalized, out RaycastHit hit,
                boom.magnitude, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                boom = boom.normalized * hit.distance;
            }
            _viewCamera.SetPositionAndRotation(pivot + boom, orbit);
        }

        public void SwapShoulder()
        {
            _isLeftShoulder = !_isLeftShoulder;
            PlayerPrefs.SetInt(SHOULDER_PREF, _isLeftShoulder ? 1 : 0);
            PlayerPrefs.Save();
            UpdateShoulderLabel();
        }

        private void UpdateShoulderLabel()
        {
            if (_shoulderLabel != null)
            {
                _shoulderLabel.text = _isLeftShoulder ? "Shoulder: Left [Q]" : "Shoulder: Right [Q]";
            }
        }

        private static void CreateVisual(string name, Mesh mesh, Transform parent,
            Vector3 offsetMeters, Vector3 scale, Material material, bool blocksCamera = false)
        {
            var visual = new GameObject(name);
            visual.transform.SetParent(parent, false);
            visual.transform.localPosition = offsetMeters;
            visual.transform.localScale = scale;
            visual.AddComponent<MeshFilter>().sharedMesh = mesh;
            visual.AddComponent<MeshRenderer>().sharedMaterial = material;
            if (blocksCamera)
            {
                visual.AddComponent<MeshCollider>().sharedMesh = mesh;
            }
        }

        private static void RemoveMarker(Transform marker)
        {
            marker.gameObject.SetActive(false);
            Destroy(marker.gameObject);
        }

        /// <summary>Called by the scene's Capture button on Unity's main thread.</summary>
        public void CaptureInput()
        {
            if (_playerId == 0 || !isActiveAndEnabled)
            {
                return;
            }

            UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);
            _captureFrame = Time.frameCount;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _nextInputSeconds = 0;
            RefreshTelemetry();
        }

        private void RefreshView()
        {
            if (!_isConfigured || !isActiveAndEnabled)
            {
                return;
            }

            bool canConnect = _client == null && !_isConnecting;
            _hostInput.interactable = canConnect;
            _portInput.interactable = canConnect;
            _roomInput.interactable = canConnect;
            _connectButton.interactable = canConnect;
            _captureButton.interactable = _playerId != 0;
            _disconnectButton.interactable = _client != null;
            _statusText.text = _status;
            RefreshTelemetry();
        }

        private void RefreshTelemetry()
        {
            if (!_isConfigured || !isActiveAndEnabled)
            {
                return;
            }

            _sessionText.text = $"Player ID: {_playerId}    Tick: {_lastTick}    Players: {_positions.Count}";
            Vector3 position = Vector3.zero;
            _positionText.text = _playerId != 0 && _positions.TryGetValue(_playerId, out position)
                ? "My server position (m)\n" + FormatPosition(position)
                : "My server position (m)\nX: --    Y: --    Z: --";
            bool canControl = _hasFocus && Cursor.lockState == CursorLockMode.Locked && _playerId != 0;
            if (_aimHud != null && _connectionPanel != null)
            {
                _aimHud.SetActive(canControl);
                _connectionPanel.SetActive(!canControl);
                if (canControl && _aimResult != null)
                {
                    _aimResult.text = FormatPosition(position) + "\n" + _latestRayResult;
                }
            }
            _inputText.text = string.Format(CultureInfo.InvariantCulture, "Input: {0}    Yaw: {1:F1} deg",
                canControl ? "WASD / Space jump / LMB ray / Esc" : "Released", _yawDegrees);
        }

        private void OnDrawGizmos()
        {
#if UNITY_EDITOR
            if (Camera.current == null || Camera.current.cameraType != CameraType.SceneView)
            {
                return;
            }

            Color previousColor = Gizmos.color;
            if (_showRaycastGizmos)
            {
                _feedback?.DrawGizmos(Time.unscaledTime);
            }
            foreach (var player in _positions)
            {
                bool isLocalPlayer = player.Key == _playerId;
                Gizmos.color = isLocalPlayer ? LocalAccent : RemoteAccent;
                Gizmos.DrawWireSphere(player.Value, GIZMO_RADIUS_METERS);
                Gizmos.DrawLine(player.Value - Vector3.right * GIZMO_RADIUS_METERS, player.Value + Vector3.right * GIZMO_RADIUS_METERS);
                Gizmos.DrawLine(player.Value - Vector3.forward * GIZMO_RADIUS_METERS, player.Value + Vector3.forward * GIZMO_RADIUS_METERS);
                string identity = isLocalPlayer ? "ME " : "PLAYER ";
                UnityEditor.Handles.Label(player.Value + Vector3.up * (GIZMO_RADIUS_METERS * 2),
                    identity + player.Key + "\n" + FormatPosition(player.Value));
            }
            Gizmos.color = previousColor;
#endif
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            _hasFocus = hasFocus;
            if (!hasFocus)
            {
                ReleaseInput();
            }
        }

        private void ReleaseInput()
        {
            _actions.Clear();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _nextInputSeconds = 0;
            RefreshTelemetry();
        }

        private void OnEnable()
        {
            RefreshView();
        }

        private void OnDisable()
        {
            Disconnect();
        }

        private void OnDestroy()
        {
            _feedback?.Dispose();
            foreach (Mesh mesh in _labMeshes)
            {
                Destroy(mesh);
            }
            Destroy(_floorMaterial);
            Destroy(_wallMaterial);
            Destroy(_cautionMaterial);
            Destroy(_localMaterial);
            Destroy(_remoteMaterial);
        }

        /// <summary>Cancels the active connection and clears snapshot markers on Unity's main thread.</summary>
        public void Disconnect()
        {
            if (_physicsLab != null)
            {
                SelectMap(true);
            }
            ++_generation;
            CancellationTokenSource cancellation = _cancellation;
            ConnectionTestClient client = _client;
            _cancellation = null;
            _client = null;
            _isConnecting = false;
            _isSending = false;
            _playerId = 0;
            _lastTick = 0;
            _hasSnapshot = false;
            _sequence = 0;
            _yawDegrees = 0;
            _pitchDegrees = DEFAULT_PITCH_DEGREES;
            _positions.Clear();
            _blendStarts.Clear();
            _latestRayResult = string.Empty;
            _sentShots.Clear();
            _feedback?.Clear();
            foreach (Transform marker in _markers.Values)
            {
                RemoveMarker(marker);
            }
            _markers.Clear();
            cancellation?.Cancel();
            client?.Dispose();
            cancellation?.Dispose();
            ReleaseInput();
            _status = "PhysicsLab preview | Disconnected";
            RefreshView();
        }

        private static string FormatPosition(Vector3 position)
        {
            return string.Format(CultureInfo.InvariantCulture, "X: {0:F3}    Y: {1:F3}    Z: {2:F3}",
                position.x, position.y, position.z);
        }

        private static float Axis(KeyCode positive, KeyCode negative)
        {
            return (Input.GetKey(positive) ? 1 : 0) - (Input.GetKey(negative) ? 1 : 0);
        }
    }
}
