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
        private const float CLOSE_CAMERA_DISTANCE_METERS = 1.65f;
        private const float PLAYER_HEIGHT_METERS = 1.8f;
        private const float PLAYER_DIAMETER_METERS = 0.65f;
        private const float ARENA_FIRE_INTERVAL_SECONDS = 0.23f;
        private const float SHOT_STATUS_SECONDS = 0.65f;
        private const float REMOTE_HEADING_THRESHOLD_METERS = 0.002f;
        private static readonly Vector3 DebugTrailOriginLocalMeters = new Vector3(0.25f, 1.24f, 0.36f);
        private const string SHOULDER_PREF = "DeathWish.Camera.LeftShoulder";
        private const float FLOOR_SIZE_METERS = 100f;
        private const float LAB_WALL_OFFSET_METERS = 54f;
        private const float LAB_WALL_HEIGHT_METERS = 12f;
        private static readonly Color LocalAccent = new Color32(18, 78, 218, 255);
        private static readonly Color RemoteAccent = new Color32(255, 196, 24, 255);
        private const double SNAPSHOT_BLEND_SECONDS = 0.1;
        private const int TIMEOUT_SECONDS = 5;
        private const int MAX_PORT = 65535;
        private const int PHYSICS_LAB_MAP_VERSION = 1;
        private const int TEAM_DEATHMATCH_MAP_VERSION = 2;

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
        private readonly Dictionary<long, ArenaWeaponVisual> _arenaWeapons = new Dictionary<long, ArenaWeaponVisual>();
        private ArenaWeaponVisual _cameraWeapon;
        private Transform _localBody;
        private readonly List<long> _departedPlayers = new List<long>();
        private readonly Dictionary<long, MatchPlayer> _matchPlayers = new Dictionary<long, MatchPlayer>();
        private readonly Dictionary<long, long> _respawnSnapshotTicks = new Dictionary<long, long>();
        private TeamDeathmatchHud _matchHud;
        private MatchState _matchState;
        private WorldMapId _mapId;
        private bool _isScoreboardOpen;
        private Transform _viewCamera;
        private Camera _sceneCamera;
        private bool _isLeftShoulder;
        private readonly Dictionary<WorldMapId, GameObject> _mapRoots = new Dictionary<WorldMapId, GameObject>();
        private readonly List<Mesh> _mapMeshes = new List<Mesh>();
        private MapSpawn[] _arenaSpawns = Array.Empty<MapSpawn>();
        private Mesh _playerMesh;
        private Mesh _boxMesh;
        private Material _floorMaterial;
        private Material _wallMaterial;
        private Material _cautionMaterial;
        private Material _arenaMaterial;
        private Material _localMaterial;
        private Material _remoteMaterial;
        private Material _redMaterial;
        private Material _blueMaterial;
        private Material _weaponMaterial;
        private Material _weaponAccentMaterial;
        private Material _muzzleFlashMaterial;
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
        private float _nextShotSeconds;
        private float _shotStatusUntilSeconds;
        private ArenaShotOutcome _shotOutcome;
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

            _matchHud = new TeamDeathmatchHud(_roomInput, _connectButton, _statusText);
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
            _playerMesh = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
            _boxMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (_sceneCamera != null && shader != null && shader.isSupported && floorMesh != null && _playerMesh != null &&
                _boxMesh != null)
            {
                _viewCamera = _sceneCamera.transform;
                CreateWorldView(shader, floorMesh);
                SelectMap(WorldMapId.PhysicsLab);
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
            string nickname = _matchHud.NicknameInput.text.Trim();
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
                    JoinRoomResponse response = nickname.Length > 0
                        ? await client.JoinMatchAsync(room, nickname, receiveArenaShots: true, timeout.Token)
                        : await client.JoinRoomAsync(room, timeout.Token);
                    if (response.Result != RoomJoinResult.Success)
                    {
                        string error = response.Result == RoomJoinResult.NicknameRequired
                            ? "Enter a nickname to join Team Deathmatch."
                            : "Join failed: " + response.Result;
                        throw new InvalidOperationException(error);
                    }

                    JoinAck ack = await client.WaitForAdmissionAsync(timeout.Token);
                    if (generation != _generation)
                    {
                        return;
                    }

                    SelectMap(ack.MapId);
                    _mapId = ack.MapId;
                    _playerId = ack.PlayerId;
                    _lastTick = ack.ServerTick;
                    var spawn = new Vector3(ack.X, ack.Y, ack.Z);
                    _positions[_playerId] = spawn;
                    if (_mapId == WorldMapId.TeamDeathmatch)
                    {
                        _yawDegrees = ArenaSpawnYaw(spawn);
                    }
                    _status = "Connected | " + MapName(ack.MapId);
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
            ReadMatchUpdates();
            SetScoreboardOpen(_hasFocus && Input.GetKey(KeyCode.Tab));
            bool canControl = _hasFocus && Cursor.lockState == CursorLockMode.Locked;
            if (canControl && !_isScoreboardOpen)
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
                _latestRayResult = result;
                if (_sentShots.Count > 0)
                {
                    ClientShot shot = _sentShots.Dequeue();
                    if (_mapId != WorldMapId.TeamDeathmatch)
                    {
                        _feedback?.Show(shot, result, Time.unscaledTime);
                    }
                }
                if (_mapId != WorldMapId.TeamDeathmatch)
                {
                    _statusText.text = _status = result;
                    Debug.Log(result);
                }
            }
            RefreshTelemetry();

            // A synchronous send failure can disconnect. Do not read connection state afterward.
            if (!_isSending && (Time.unscaledTime >= _nextInputSeconds || _actions.Count > 0))
            {
                canControl = canControl && CanPlay() && !_isScoreboardOpen;
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
                    if (hasAction && generation == _generation && CanPlay() && !_isScoreboardOpen)
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

        private void ReadMatchUpdates()
        {
            if (_mapId != WorldMapId.TeamDeathmatch)
            {
                return;
            }

            MatchState state = _client.LatestMatchState;
            if (state != null && state != _matchState)
            {
                ApplyMatchState(state);
            }
            while (_client.TryReadKillEvent(out KillEvent kill))
            {
                _matchHud.AddKill(kill, Time.unscaledTime);
            }
            while (_client.TryReadArenaShot(out ArenaShot shot))
            {
                ShowArenaShot(shot);
            }
            _matchHud.ExpireKills(Time.unscaledTime);
        }

        private void ShowArenaShot(ArenaShot shot)
        {
            float nowSeconds = Time.unscaledTime;
            Vector3 origin = new Vector3(shot.OriginMeters.X, shot.OriginMeters.Y, shot.OriginMeters.Z);
            Vector3 end = new Vector3(shot.EndMeters.X, shot.EndMeters.Y, shot.EndMeters.Z);
            Vector3 trailStart = origin;
            _arenaWeapons.TryGetValue(shot.ShooterPlayerId, out ArenaWeaponVisual weapon);
            if (shot.ShooterPlayerId == _playerId)
            {
                _shotOutcome = shot.Outcome;
                _shotStatusUntilSeconds = nowSeconds + SHOT_STATUS_SECONDS;
                if (_cameraWeapon?.IsVisible == true)
                {
                    weapon = _cameraWeapon;
                }
            }
            else if (weapon != null)
            {
                weapon.Fire(nowSeconds, (end - origin).normalized);
                PoseArenaWeapon(_markers[shot.ShooterPlayerId], weapon, false, nowSeconds);
            }

            if (weapon?.IsVisible == true)
            {
                trailStart = weapon.MuzzlePosition;
            }
            _feedback?.ShowArenaShot(origin, end, trailStart, shot.Outcome, nowSeconds);
        }

        private void ApplyMatchState(MatchState state)
        {
            foreach (MatchPlayer player in state.Players)
            {
                bool hasRespawned = player.Health > 0 && _matchPlayers.TryGetValue(player.PlayerId, out MatchPlayer prior) &&
                    (prior.Health == 0 || player.Deaths > prior.Deaths);
                if (hasRespawned)
                {
                    _blendStarts.Remove(player.PlayerId);
                    if (_lastTick < state.ServerTick)
                    {
                        _respawnSnapshotTicks[player.PlayerId] = state.ServerTick;
                    }
                    else if (player.PlayerId == _playerId && _positions.TryGetValue(_playerId, out Vector3 spawn))
                    {
                        _yawDegrees = ArenaSpawnYaw(spawn);
                    }
                }
            }
            _matchState = state;
            _matchPlayers.Clear();
            foreach (MatchPlayer player in state.Players)
            {
                _matchPlayers.Add(player.PlayerId, player);
            }
            if (_matchPlayers.TryGetValue(_playerId, out MatchPlayer self))
            {
                _matchHud.SetMatch(state, self);
                if (self.Health == 0 || state.Phase != MatchPhase.Playing)
                {
                    _actions.Clear();
                }
            }
        }

        private void SetScoreboardOpen(bool isOpen)
        {
            isOpen = isOpen && _mapId == WorldMapId.TeamDeathmatch && _matchState != null;
            if (isOpen == _isScoreboardOpen)
            {
                return;
            }

            _isScoreboardOpen = isOpen;
            if (isOpen)
            {
                _actions.Clear();
                _nextInputSeconds = 0;
            }
            _matchHud.SetScoreboardVisible(isOpen);
        }

        private bool CanPlay()
        {
            return _mapId != WorldMapId.TeamDeathmatch ||
                (_matchState?.Phase == MatchPhase.Playing && CanShowMatchPlayer(_playerId));
        }

        private bool CanShowMatchPlayer(long playerId)
        {
            return _matchPlayers.TryGetValue(playerId, out MatchPlayer player) && player.Health > 0 &&
                (!_respawnSnapshotTicks.TryGetValue(playerId, out long tick) || _lastTick >= tick);
        }

        private void ApplySnapshot(WorldSnapshot snapshot, double nowSeconds)
        {
            // If a target arrives early, continue from the current blend to keep the path continuous.
            float fraction = BlendFraction(nowSeconds);
            bool isLocalRespawn = _respawnSnapshotTicks.TryGetValue(_playerId, out long localSpawnTick) &&
                snapshot.ServerTick >= localSpawnTick;
            foreach (var player in _positions)
            {
                bool hasPlayerLeftMatch = _mapId == WorldMapId.TeamDeathmatch && _matchState != null &&
                    !_matchPlayers.ContainsKey(player.Key);
                bool hasRespawned =
                    _respawnSnapshotTicks.TryGetValue(player.Key, out long spawnTick) && snapshot.ServerTick >= spawnTick;
                if (hasPlayerLeftMatch || hasRespawned)
                {
                    _blendStarts.Remove(player.Key);
                    _respawnSnapshotTicks.Remove(player.Key);
                }
                else
                {
                    _blendStarts[player.Key] = DisplayPosition(player.Key, player.Value, fraction);
                }
            }
            _lastTick = snapshot.ServerTick;
            _hasSnapshot = true;
            _blendStartSeconds = nowSeconds;
            _positions.Clear();
            foreach (SnapshotPlayer player in snapshot.Players)
            {
                _positions.Add(player.PlayerId, new Vector3(player.X, player.Y, player.Z));
            }
            if (isLocalRespawn && _positions.TryGetValue(_playerId, out Vector3 spawn))
            {
                _yawDegrees = ArenaSpawnYaw(spawn);
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
            _cautionMaterial = new Material(_floorMaterial);
            _cautionMaterial.SetFloat("_Caution", 1f);
            _arenaMaterial = new Material(shader) { color = new Color32(162, 178, 190, 255) };
            _localMaterial = new Material(shader) { color = LocalAccent };
            _remoteMaterial = new Material(shader) { color = RemoteAccent };
            _redMaterial = new Material(shader) { color = TeamDeathmatchHud.RedAccent };
            _blueMaterial = new Material(shader) { color = TeamDeathmatchHud.BlueAccent };
            _weaponMaterial = MakeSolidMaterial(shader, new Color32(49, 64, 79, 255));
            _weaponAccentMaterial = MakeSolidMaterial(shader, new Color32(241, 165, 49, 255));
            _muzzleFlashMaterial = MakeSolidMaterial(shader, new Color32(255, 231, 128, 255));
            _cameraWeapon = CreateWeaponVisual(_viewCamera, "Close camera weapon");
            _cameraWeapon.IsVisible = false;

            var flatMap = new GameObject(MapName(WorldMapId.Flat));
            flatMap.transform.SetParent(transform, false);
            _mapRoots.Add(WorldMapId.Flat, flatMap);
            Vector3 meshSize = floorMesh.bounds.size;
            var floorScale = new Vector3(FLOOR_SIZE_METERS / meshSize.x, 1f, FLOOR_SIZE_METERS / meshSize.z);
            CreateVisual("Grid floor", floorMesh, flatMap.transform, Vector3.zero, floorScale, _floorMaterial, true);

            _wallMaterial = new Material(shader);
            _wallMaterial.SetFloat("_Wall", 1f);
            float span = 2 * LAB_WALL_OFFSET_METERS;
            float height = LAB_WALL_HEIGHT_METERS;
            foreach (float side in new[] { -LAB_WALL_OFFSET_METERS, LAB_WALL_OFFSET_METERS })
            {
                CreateVisual("Observation wall", _boxMesh, flatMap.transform, new Vector3(0, height / 2, side),
                    new Vector3(span, height, 0.4f), _wallMaterial, true);
                CreateVisual("Observation wall", _boxMesh, flatMap.transform, new Vector3(side, height / 2, 0),
                    new Vector3(0.4f, height, span), _wallMaterial, true);
            }
        }

        [DataContract]
        private sealed class MapData
        {
            [DataMember(Name = "version", IsRequired = true)] public int Version { get; set; }
            [DataMember(Name = "mapId")] public int MapId { get; set; }
            [DataMember(Name = "objects", IsRequired = true)] public MapObject[] Objects { get; set; }
            [DataMember(Name = "caution")] public MapObject Caution { get; set; }
            [DataMember(Name = "spawns")] public MapSpawn[] Spawns { get; set; }
        }

        [DataContract]
        private sealed class MapSpawn
        {
            [DataMember(Name = "position", IsRequired = true)] public float[] Position { get; set; }
            [DataMember(Name = "forward", IsRequired = true)] public float[] Forward { get; set; }
        }

        [DataContract]
        private sealed class MapObject
        {
            [DataMember(Name = "name", IsRequired = true)] public string Name { get; set; }
            [DataMember(Name = "vertices", IsRequired = true)] public float[][] Vertices { get; set; }
            [DataMember(Name = "triangles", IsRequired = true)] public int[][] Triangles { get; set; }
        }

        private static string MapName(WorldMapId mapId)
        {
            return mapId switch
            {
                WorldMapId.Flat => "Flat map",
                WorldMapId.PhysicsLab => "PhysicsLab",
                WorldMapId.TeamDeathmatch => "TeamDeathmatch",
                _ => throw new ArgumentOutOfRangeException(nameof(mapId))
            };
        }

        private void SelectMap(WorldMapId mapId)
        {
            if (_mapRoots.Count == 0)
            {
                return;
            }

            if (!_mapRoots.ContainsKey(mapId))
            {
                _mapRoots.Add(mapId, LoadMap(mapId));
            }
            foreach (var map in _mapRoots)
            {
                if (map.Value != null)
                {
                    map.Value.SetActive(map.Key == mapId);
                }
            }
        }

        private GameObject LoadMap(WorldMapId mapId)
        {
            string name = MapName(mapId);
            TextAsset asset = Resources.Load<TextAsset>(name);
            if (asset == null)
            {
                throw new InvalidOperationException("Missing map asset: " + name);
            }

            using var stream = new MemoryStream(asset.bytes);
            var data = (MapData)new DataContractJsonSerializer(typeof(MapData)).ReadObject(stream);
            bool isPhysicsLab = mapId == WorldMapId.PhysicsLab;
            bool isExpectedMap = isPhysicsLab
                ? data.Version == PHYSICS_LAB_MAP_VERSION
                : data.Version == TEAM_DEATHMATCH_MAP_VERSION && data.MapId == (int)mapId &&
                  data.Spawns != null && data.Spawns.Length > 0;
            if (!isExpectedMap || data.Objects == null || data.Objects.Length == 0)
            {
                throw new InvalidOperationException("Invalid map asset: " + name);
            }

            var root = new GameObject(name);
            root.SetActive(false);
            root.transform.SetParent(transform, false);
            var meshes = new List<Mesh>();
            try
            {
                foreach (MapObject part in data.Objects)
                {
                    Material material = _arenaMaterial;
                    if (isPhysicsLab)
                    {
                        material = part.Name == "Torus" ? _remoteMaterial : _floorMaterial;
                    }
                    CreateMapPart(part, root.transform, material, true, meshes);
                }
                if (isPhysicsLab && data.Caution != null)
                {
                    CreateMapPart(data.Caution, root.transform, _cautionMaterial, false, meshes);
                }
                if (!isPhysicsLab)
                {
                    _arenaSpawns = data.Spawns;
                }
                _mapMeshes.AddRange(meshes);
                return root;
            }
            catch
            {
                foreach (Mesh mesh in meshes)
                {
                    Destroy(mesh);
                }
                Destroy(root);
                throw;
            }
        }

        private float ArenaSpawnYaw(Vector3 position)
        {
            float nearestDistance = float.PositiveInfinity;
            float yaw = 0;
            foreach (MapSpawn spawn in _arenaSpawns)
            {
                var spawnPosition = new Vector3(spawn.Position[0], spawn.Position[1], spawn.Position[2]);
                float distance = (position - spawnPosition).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    yaw = Mathf.Atan2(spawn.Forward[0], spawn.Forward[2]) * Mathf.Rad2Deg;
                }
            }
            return yaw;
        }

        private static void CreateMapPart(MapObject part, Transform parent, Material material, bool blocksCamera, List<Mesh> meshes)
        {
            var mesh = new Mesh { name = part.Name };
            meshes.Add(mesh);
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
            CreateVisual(part.Name, mesh, parent, Vector3.zero, Vector3.one, material, blocksCamera);
        }

        private void LateUpdate()
        {
            _feedback?.Update(Time.unscaledTime);
            RenderWorld(Time.unscaledTimeAsDouble);
            if (_playerId == 0 || !_hasFocus || Cursor.lockState != CursorLockMode.Locked ||
                Time.frameCount == _captureFrame || !CanPlay() || _isScoreboardOpen)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Space) && _actions.Count < MAX_BUFFERED_ACTIONS)
            {
                _actions.Enqueue(null);
            }
            bool isArena = _mapId == WorldMapId.TeamDeathmatch;
            float nowSeconds = Time.unscaledTime;
            bool wantsToFire = isArena
                ? Input.GetMouseButton(0) && nowSeconds >= _nextShotSeconds
                : Input.GetMouseButtonDown(0);
            if (wantsToFire && _sentShots.Count + _actions.Count < MAX_BUFFERED_ACTIONS &&
                _viewCamera != null && _markers.TryGetValue(_playerId, out Transform marker))
            {
                Vector3 offset = _viewCamera.position - marker.position;
                Vector3 forward = _sceneCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f)).direction;
                var request = new CameraRay(new System.Numerics.Vector3(offset.x, offset.y, offset.z),
                    new System.Numerics.Vector3(forward.x, forward.y, forward.z));
                Vector3 trailStart = marker.TransformPoint(DebugTrailOriginLocalMeters);
                if (isArena && _arenaWeapons.TryGetValue(_playerId, out ArenaWeaponVisual weapon))
                {
                    _nextShotSeconds = nowSeconds + ARENA_FIRE_INTERVAL_SECONDS;
                    weapon.Fire(nowSeconds, forward);
                    _cameraWeapon.Fire(nowSeconds, forward);
                    trailStart = _cameraWeapon.IsVisible ? _cameraWeapon.MuzzlePosition : weapon.MuzzlePosition;
                }
                _actions.Enqueue(new ClientShot(request, new Ray(_viewCamera.position, forward), trailStart));
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
                _arenaWeapons.Remove(playerId);
                _blendStarts.Remove(playerId);
                _respawnSnapshotTicks.Remove(playerId);
                _matchHud.RemoveNameplate(playerId);
            }

            float fraction = BlendFraction(nowSeconds);
            bool isTeamDeathmatch = _mapId == WorldMapId.TeamDeathmatch;
            float side = _isLeftShoulder ? -1f : 1f;
            foreach (var player in _positions)
            {
                bool isLocalPlayer = player.Key == _playerId;
                _markers.TryGetValue(player.Key, out Transform marker);
                if (isTeamDeathmatch && !CanShowMatchPlayer(player.Key))
                {
                    if (marker != null)
                    {
                        marker.gameObject.SetActive(false);
                    }
                    _matchHud.HideNameplate(player.Key);
                    continue;
                }

                Vector3 display = DisplayPosition(player.Key, player.Value, fraction);
                if (marker == null)
                {
                    marker = CreatePlayerMarker(player.Key, player.Value, isLocalPlayer, isTeamDeathmatch);
                }
                Vector3 travel = display - marker.position;
                if (isTeamDeathmatch && !isLocalPlayer && marker.gameObject.activeSelf)
                {
                    travel.y = 0;
                    if (travel.sqrMagnitude > REMOTE_HEADING_THRESHOLD_METERS * REMOTE_HEADING_THRESHOLD_METERS)
                    {
                        marker.rotation = Quaternion.LookRotation(travel);
                    }
                }
                else if (isTeamDeathmatch && !isLocalPlayer)
                {
                    marker.rotation = Quaternion.Euler(0f, ArenaSpawnYaw(display), 0f);
                }
                marker.gameObject.SetActive(true);
                marker.position = display;
                if (isLocalPlayer)
                {
                    marker.rotation = Quaternion.Euler(0f, _yawDegrees, 0f);
                }
                if (isTeamDeathmatch && _arenaWeapons.TryGetValue(player.Key, out ArenaWeaponVisual weapon))
                {
                    PoseArenaWeapon(marker, weapon, isLocalPlayer, (float)nowSeconds);
                }
            }

            if (_playerId == 0)
            {
                var overview = new Vector3(15f, 32f, 22f);
                var target = new Vector3(-5f, 2f, -5f);
                _viewCamera.SetPositionAndRotation(overview, Quaternion.LookRotation(target - overview));
                return;
            }
            _positions.TryGetValue(_playerId, out Vector3 position);
            if (_markers.TryGetValue(_playerId, out Transform localMarker))
            {
                position = localMarker.position;
            }
            Quaternion yaw = Quaternion.Euler(0f, _yawDegrees, 0f);
            Quaternion orbit = Quaternion.Euler(_pitchDegrees, _yawDegrees, 0f);
            Vector3 pivot = position + Vector3.up * _shoulderCamera.HeightMeters;
            float sideMeters = _shoulderCamera.SideMeters;
            if (isTeamDeathmatch)
            {
                float chestHeightFromPivotMeters = ArenaWeaponVisual.RightMountMeters.y - _shoulderCamera.HeightMeters;
                float chestDepthMeters = _shoulderCamera.DistanceMeters +
                    chestHeightFromPivotMeters * (orbit * Vector3.forward).y;
                float frustumHalfWidthMeters = Mathf.Max(0f, chestDepthMeters) *
                    Mathf.Tan(_sceneCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) * _sceneCamera.aspect;
                sideMeters = _shoulderCamera.GetArenaSideMeters(frustumHalfWidthMeters);
            }
            Vector3 boom = orbit * Vector3.back * _shoulderCamera.DistanceMeters +
                yaw * Vector3.right * (side * sideMeters);
            if (Physics.SphereCast(pivot, CAMERA_CLEARANCE_METERS, boom.normalized, out RaycastHit hit,
                boom.magnitude, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                boom = boom.normalized * hit.distance;
            }
            _viewCamera.SetPositionAndRotation(pivot + boom, orbit);
            if (isTeamDeathmatch)
            {
                UpdateLocalWeaponView(boom.magnitude, (float)nowSeconds);
                RenderNameplates();
            }
        }

        private void PoseArenaWeapon(Transform marker, ArenaWeaponVisual weapon, bool isLocalPlayer, float nowSeconds)
        {
            float pitchDegrees = isLocalPlayer ? _pitchDegrees : 0f;
            if (!isLocalPlayer && weapon.TryGetFiringDirection(nowSeconds, out Vector3 direction))
            {
                var heading = new Vector3(direction.x, 0f, direction.z);
                if (heading.sqrMagnitude > 0.001f)
                {
                    marker.rotation = Quaternion.LookRotation(heading);
                }
                pitchDegrees = -Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
            }
            weapon.Pose(ArenaWeaponVisual.RightMountMeters, pitchDegrees, 1f, nowSeconds);
        }

        private void UpdateLocalWeaponView(float cameraDistanceMeters, float nowSeconds)
        {
            bool useCloseCameraWeapon = CanShowMatchPlayer(_playerId) &&
                cameraDistanceMeters < CLOSE_CAMERA_DISTANCE_METERS;
            _cameraWeapon.IsVisible = useCloseCameraWeapon;
            if (_localBody != null)
            {
                _localBody.gameObject.SetActive(!useCloseCameraWeapon);
            }
            if (_arenaWeapons.TryGetValue(_playerId, out ArenaWeaponVisual weapon))
            {
                weapon.IsVisible = !useCloseCameraWeapon;
            }
            if (useCloseCameraWeapon)
            {
                _cameraWeapon.Pose(new Vector3(0.24f, -0.22f, 0.51f), 0f, 0.58f, nowSeconds);
            }
        }

        private Transform CreatePlayerMarker(long playerId, Vector3 spawn, bool isLocalPlayer, bool isTeamDeathmatch)
        {
            var marker = new GameObject(isLocalPlayer ? $"Me {playerId}" : $"Player {playerId}").transform;
            marker.SetParent(transform, false);
            marker.position = spawn;
            Material bodyMaterial = isLocalPlayer ? _localMaterial : _remoteMaterial;
            if (isTeamDeathmatch)
            {
                bodyMaterial = _matchPlayers[playerId].Team == TeamId.Red ? _redMaterial : _blueMaterial;
                marker.rotation = Quaternion.Euler(0f, ArenaSpawnYaw(spawn), 0f);
                _arenaWeapons.Add(playerId, CreateWeaponVisual(marker, "Gun"));
            }
            Bounds meshBounds = _playerMesh.bounds;
            var bodyScale = new Vector3(PLAYER_DIAMETER_METERS / meshBounds.size.x,
                PLAYER_HEIGHT_METERS / meshBounds.size.y, PLAYER_DIAMETER_METERS / meshBounds.size.z);
            Vector3 bodyOffset = Vector3.up * (PLAYER_HEIGHT_METERS / 2) - Vector3.Scale(meshBounds.center, bodyScale);
            Transform body = CreateVisual("Body", _playerMesh, marker, bodyOffset, bodyScale, bodyMaterial);
            if (isLocalPlayer)
            {
                _localBody = body;
            }
            _markers.Add(playerId, marker);
            return marker;
        }

        private void RenderNameplates()
        {
            foreach (var marker in _markers)
            {
                if (marker.Key != _playerId && marker.Value.gameObject.activeSelf &&
                    _matchPlayers.TryGetValue(marker.Key, out MatchPlayer player))
                {
                    Color color = player.Team == TeamId.Red ? TeamDeathmatchHud.RedAccent : TeamDeathmatchHud.BlueAccent;
                    _matchHud.ShowNameplate(player, marker.Value.position, _sceneCamera, color);
                }
            }
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

        private ArenaWeaponVisual CreateWeaponVisual(Transform parent, string name)
        {
            return new ArenaWeaponVisual(parent, name, _boxMesh, _weaponMaterial, _weaponAccentMaterial,
                _muzzleFlashMaterial);
        }

        private static Material MakeSolidMaterial(Shader shader, Color color)
        {
            var material = new Material(shader) { color = color };
            material.SetFloat("_Solid", 1f);
            return material;
        }

        private static Transform CreateVisual(string name, Mesh mesh, Transform parent,
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
            return visual.transform;
        }

        private static void RemoveMarker(Transform marker)
        {
            if (marker == null)
            {
                return;
            }
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
            _matchHud.NicknameInput.interactable = canConnect;
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
                _aimHud.SetActive(canControl && !_isScoreboardOpen);
                _connectionPanel.SetActive(!canControl && !_isScoreboardOpen);
                if (canControl && !_isScoreboardOpen && _aimResult != null)
                {
                    RefreshAimResult(position);
                }
            }
            _inputText.text = string.Format(CultureInfo.InvariantCulture, "Input: {0}    Yaw: {1:F1} deg",
                canControl ? "WASD / Space jump / LMB fire / Esc" : "Released", _yawDegrees);
        }

        private void RefreshAimResult(Vector3 position)
        {
            _aimResult.color = Color.white;
            if (_mapId != WorldMapId.TeamDeathmatch)
            {
                _aimResult.text = FormatPosition(position) + "\n" + _latestRayResult;
                return;
            }
            if (!CanPlay())
            {
                _aimResult.text = string.Empty;
                return;
            }
            if (Time.unscaledTime >= _shotStatusUntilSeconds)
            {
                _aimResult.text = "LMB  Fire";
                return;
            }
            _aimResult.text = _shotOutcome switch
            {
                ArenaShotOutcome.Damage => "Hit",
                ArenaShotOutcome.PlayerContact => "No damage",
                ArenaShotOutcome.Terrain => "Cover",
                _ => "Miss"
            };
            if (_shotOutcome == ArenaShotOutcome.Damage)
            {
                _aimResult.color = new Color32(137, 255, 155, 255);
            }
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
                SetScoreboardOpen(false);
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
            _cameraWeapon?.Destroy();
            foreach (Mesh mesh in _mapMeshes)
            {
                Destroy(mesh);
            }
            Destroy(_floorMaterial);
            Destroy(_wallMaterial);
            Destroy(_cautionMaterial);
            Destroy(_arenaMaterial);
            Destroy(_localMaterial);
            Destroy(_remoteMaterial);
            Destroy(_redMaterial);
            Destroy(_blueMaterial);
            Destroy(_weaponMaterial);
            Destroy(_weaponAccentMaterial);
            Destroy(_muzzleFlashMaterial);
            _matchHud?.Dispose();
        }

        /// <summary>Cancels the active connection and clears snapshot markers on Unity's main thread.</summary>
        public void Disconnect()
        {
            if (_mapRoots.ContainsKey(WorldMapId.PhysicsLab))
            {
                SelectMap(WorldMapId.PhysicsLab);
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
            _mapId = WorldMapId.Flat;
            _matchState = null;
            _isScoreboardOpen = false;
            _matchPlayers.Clear();
            _respawnSnapshotTicks.Clear();
            _matchHud?.Reset();
            _latestRayResult = string.Empty;
            _nextShotSeconds = 0;
            _shotStatusUntilSeconds = 0;
            if (_cameraWeapon != null)
            {
                _cameraWeapon.Reset();
                _cameraWeapon.IsVisible = false;
            }
            _sentShots.Clear();
            _feedback?.Clear();
            foreach (Transform marker in _markers.Values)
            {
                RemoveMarker(marker);
            }
            _markers.Clear();
            _arenaWeapons.Clear();
            _localBody = null;
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
