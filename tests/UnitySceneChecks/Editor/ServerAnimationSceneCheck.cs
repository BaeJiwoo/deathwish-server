using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeathWish.Client;
using DeathWish.MockClient.Core;
using DeathWish.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace DeathWish.Client.Editor
{
    [InitializeOnLoad]
    public static class ServerAnimationSceneCheck
    {
        private const string RUNNING = "DeathWish.ServerAnimationSceneCheck.Running";
        private const string PORT_ENV = "DW_ANIMATION_TEST_PORT";
        private const string REQUIRE_BOB_ENV = "DW_ANIMATION_REQUIRE_BOB";
        private const string SCENE_PATH = "Assets/DeathWishMovement/Scenes/SyncTestScene.unity";
        private const string POSITION_GIZMOS_NAME = "Position Gizmos";
        private const long LOCAL_ID = 7;
        private const long REMOTE_ID = 8;
        private const int ROOM_ID = 1;
        private const int TORSO_INDEX = 1;
        private const float POSITION_TOLERANCE_METERS = 0.002f;
        private const float ANGLE_TOLERANCE_DEGREES = 0.1f;
        private const float MOVING_THIGH_ANGLE_DEGREES = 2f;
        private const double NETWORK_TIMEOUT_SECONDS = 15;
        private const double INPUT_INTERVAL_SECONDS = 1.0 / 30;
        private const BindingFlags PRIVATE_INSTANCE = BindingFlags.Instance | BindingFlags.NonPublic;

        private static SyncTestClient _client;
        private static ConnectionTestClient _peer;
        private static CancellationTokenSource _timeout;
        private static Task<JoinAck> _peerAdmission;
        private static Task _lastSend;
        private static uint _nextSequence;
        private static double _nextInputSeconds;
        private static bool _requiresBob;
        private static Transform _bobThigh;
        private static Quaternion _bobStandingThighRotation;

        static ServerAnimationSceneCheck()
        {
            EditorApplication.update += RunCheck;
        }

        public static void Run()
        {
            EditorSceneManager.OpenScene(SCENE_PATH);
            SessionState.SetBool(RUNNING, true);
            EditorApplication.EnterPlaymode();
        }

        private static void RunCheck()
        {
            if (!SessionState.GetBool(RUNNING, false) || !EditorApplication.isPlaying)
            {
                return;
            }

            EditorApplication.QueuePlayerLoopUpdate();
            try
            {
                if (_client == null)
                {
                    _client = UnityEngine.Object.FindFirstObjectByType<SyncTestClient>();
                    Require(_client != null, "SyncTestScene must provide a controller.");
                    VerifyOffline(_client);
                    _client.Disconnect();

                    string configuredPort = Environment.GetEnvironmentVariable(PORT_ENV);
                    _requiresBob = Environment.GetEnvironmentVariable(REQUIRE_BOB_ENV) == "1";
                    if (string.IsNullOrEmpty(configuredPort))
                    {
                        Require(!_requiresBob, REQUIRE_BOB_ENV + " requires " + PORT_ENV + " for the live Bob check.");
                        Finish(0);
                        return;
                    }
                    Require(int.TryParse(configuredPort, out int port) && port is > 0 and <= 65535,
                        PORT_ENV + " must name a valid local TCP port.");
                    StartNetworkCheck(port);
                    return;
                }

                if (VerifyNetworkFrame())
                {
                    Finish(0);
                }
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                Finish(1);
            }
        }

        private static void VerifyOffline(SyncTestClient client)
        {
            SetField(client, "_playerId", LOCAL_ID);
            SetField(client, "_usesServerAnimation", true);
            SetField(client, "_yawDegrees", 35f);
            SetField(client, "_showPositionGizmos", true);
            var localStart = new Vector3(2f, 0f, -1f);
            var localEnd = new Vector3(4f, 0f, 1f);
            var remoteStart = new Vector3(-2f, 0f, 0f);
            var remoteEnd = new Vector3(-4f, 0f, 2f);
            short[] startAngles = MakeAngles(1000);
            short[] endAngles = MakeAngles(5000);

            Apply(client, 1, 1_000_000, 10, localStart, remoteStart, 17_500, startAngles);
            Apply(client, 2, 1_100_000, 10.1, localEnd, remoteEnd, -17_500, endAngles);
            Render(client, 10.1);
            Transform local = Marker(client, LOCAL_ID);
            Require(Vector3.Distance(local.position, localStart) < POSITION_TOLERANCE_METERS,
                "A received future endpoint must wait for the playback delay.");

            Render(client, 10.15);
            Transform localRig = local.Find("ServerRig");
            Transform remote = Marker(client, REMOTE_ID);
            Transform torso = localRig.Find("Root/Torso");
            Vector3 localMidpoint = Vector3.Lerp(localStart, localEnd, 0.5f);
            Vector3 remoteMidpoint = Vector3.Lerp(remoteStart, remoteEnd, 0.5f);
            Require(Vector3.Distance(local.position, localMidpoint) < POSITION_TOLERANCE_METERS &&
                Vector3.Distance(remote.position, remoteMidpoint) < POSITION_TOLERANCE_METERS,
                "The local and remote marker must use the delayed snapshot bracket.");
            Require(Quaternion.Angle(torso.localRotation, Quaternion.Euler(30f, 0f, 0f)) < ANGLE_TOLERANCE_DEGREES &&
                Quaternion.Angle(localRig.rotation, Quaternion.Euler(0f, 180f, 0f)) < ANGLE_TOLERANCE_DEGREES,
                "Root yaw and bone pitch must use shortest-path interpolation with the same snapshot fraction.");
            Require(Quaternion.Angle(local.rotation, Quaternion.Euler(0f, 35f, 0f)) < ANGLE_TOLERANCE_DEGREES,
                "The visual yaw must leave the local motion and aim basis alone.");

            Transform localGizmos = local.Find(POSITION_GIZMOS_NAME);
            Transform remoteGizmos = remote.Find(POSITION_GIZMOS_NAME);
            VerifyPositionGizmos(localGizmos, localEnd, localMidpoint);
            VerifyPositionGizmos(remoteGizmos, remoteEnd, remoteMidpoint);

            SetField(client, "_showPositionGizmos", false);
            Render(client, 10.15);
            Require(!localGizmos.gameObject.activeInHierarchy && !remoteGizmos.gameObject.activeInHierarchy,
                "Disabling the position gizmos must hide the local and remote overlays.");
            SetField(client, "_showPositionGizmos", true);
            Render(client, 10.15);
            VerifyPositionGizmos(localGizmos, localEnd, localMidpoint);
            VerifyPositionGizmos(remoteGizmos, remoteEnd, remoteMidpoint);

            Render(client, 10.9);
            ServerAnimationPoseReferenceCheck.Verify(local, localEnd, -17_500, endAngles);
            ServerAnimationPoseReferenceCheck.Verify(remote, remoteEnd, -17_500, endAngles);
            VerifyPositionGizmos(localGizmos, localEnd, localEnd);
            VerifyPositionGizmos(remoteGizmos, remoteEnd, remoteEnd);
            Debug.Log("PASS offline animation: buffered root/pose interpolation, all rendered bones and boxes, and no extrapolation");

            var fresh = new Vector3(7f, 0f, -3f);
            Apply(client, 3, 2_250_000, 11.25, fresh, null, 0, startAngles);
            Render(client, 11.25);
            Require(!remote.gameObject.activeSelf, "A departing player's marker must be hidden immediately.");
            Require(!remoteGizmos.gameObject.activeInHierarchy,
                "A departing player's server and client position gizmos must disappear with their marker.");
            Debug.Log("PASS offline position gizmos: latest tick, delayed interpolation, toggle, convergence, and departure");
            ServerAnimationPoseReferenceCheck.Verify(local, fresh, 0, startAngles);

            // A stalled receive clock can have a long gap while the server advances only one fixed step.
            Apply(client, 4, 2_350_000, 12.5, localStart, null, 0, startAngles);
            Apply(client, 5, 2_450_000, 12.6, localEnd, null, 0, endAngles);
            Render(client, 12.65);
            Require(Vector3.Distance(local.position, Vector3.Lerp(localStart, localEnd, 0.5f)) < POSITION_TOLERANCE_METERS,
                "A receive gap must reset the clock so delayed interpolation resumes.");

            Apply(client, 6, 2_550_000, 12.7, localEnd, null, 0, endAngles, uint.MaxValue);
            Require(GetField<long>(client, "_playerId") == 0 &&
                GetField<string>(client, "_status").Contains("incompatible"),
                "An unknown rig must disconnect with a visible explanation.");
            SetField(client, "_playerId", 9L);
            var legacy = new WorldSnapshot(ROOM_ID, 1, new[] { Position(9, Vector3.zero) });
            Invoke(client, "ApplySnapshot", legacy, 13.0);
            Render(client, 13.0);
            Transform legacyMarker = Marker(client, 9);
            Require(legacyMarker.Find("Body") != null && legacyMarker.Find("ServerRig") == null,
                "A standard room snapshot must keep the legacy body view.");
            Debug.Log("PASS offline animation: clock and receive gap reset, departure, rig mismatch, and legacy view after disconnect");
        }

        private static void VerifyPositionGizmos(Transform gizmos, Vector3 expectedServer, Vector3 expectedClient)
        {
            Require(gizmos != null && gizmos.gameObject.activeInHierarchy, "The position gizmos must be visible.");
            LineRenderer server = gizmos.Find("Server position (latest tick)")?.GetComponent<LineRenderer>();
            LineRenderer client = gizmos.Find("Client position (interpolated)")?.GetComponent<LineRenderer>();
            LineRenderer offset = gizmos.Find("Position offset")?.GetComponent<LineRenderer>();
            Require(server != null && server.enabled && client != null && client.enabled && offset != null,
                "The overlay must draw both positions and provide an offset line.");
            Require(server.startColor != client.startColor, "The two position outlines must use different colors.");
            Require(Vector3.Distance(server.transform.position, expectedServer) < POSITION_TOLERANCE_METERS &&
                Vector3.Distance(client.transform.position, expectedClient) < POSITION_TOLERANCE_METERS,
                "The server outline must follow the latest tick and the client outline must follow the delayed sample.");
            Require(offset.enabled == (expectedServer != expectedClient),
                "The offset line must be visible only when the server and client positions differ.");
        }

        private static void Apply(SyncTestClient client, long tick, long timeMicroseconds, double receivedAtSeconds,
            Vector3 localPosition, Vector3? remotePosition, short yawCentidegrees, short[] pitchesCentidegrees,
            uint rigId = AnimationRig.RIG_ID)
        {
            SnapshotPlayer[] positions = remotePosition.HasValue
                ? new[] { Position(LOCAL_ID, localPosition), Position(REMOTE_ID, remotePosition.Value) }
                : new[] { Position(LOCAL_ID, localPosition) };
            AnimatedPlayer[] players = remotePosition.HasValue
                ? new[] { new AnimatedPlayer(LOCAL_ID, yawCentidegrees, pitchesCentidegrees),
                    new AnimatedPlayer(REMOTE_ID, yawCentidegrees, pitchesCentidegrees) }
                : new[] { new AnimatedPlayer(LOCAL_ID, yawCentidegrees, pitchesCentidegrees) };
            var snapshot = new AnimatedWorldSnapshot(new WorldSnapshot(ROOM_ID, tick, positions), timeMicroseconds, players, rigId);
            Invoke(client, "ApplyAnimatedSnapshot", snapshot, receivedAtSeconds);
        }

        private static short[] MakeAngles(short torsoPitchCentidegrees)
        {
            var angles = new short[AnimationRig.BONE_COUNT];
            angles[TORSO_INDEX] = torsoPitchCentidegrees;
            angles[3] = -1400;
            angles[7] = 1900;
            return angles;
        }

        private static SnapshotPlayer Position(long id, Vector3 feet)
        {
            return new SnapshotPlayer(id, feet.x, feet.y, feet.z);
        }

        private static void StartNetworkCheck(int port)
        {
            _timeout = new CancellationTokenSource(TimeSpan.FromSeconds(NETWORK_TIMEOUT_SECONDS));
            _peer = new ConnectionTestClient();
            SetField(_client, "_useServerAnimation", true);
            GetField<InputField>(_client, "_hostInput").text = "127.0.0.1";
            GetField<InputField>(_client, "_portInput").text = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            GetField<InputField>(_client, "_roomInput").text = ROOM_ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _client.Connect();
            _peerAdmission = JoinPeerAsync(port, _timeout.Token);
        }

        private static async Task<JoinAck> JoinPeerAsync(int port, CancellationToken cancellationToken)
        {
            await _peer.ConnectAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
            JoinRoomResponse response = await _peer.JoinAnimatedRoomAsync(ROOM_ID, cancellationToken).ConfigureAwait(false);
            Require(response.Result == RoomJoinResult.Success, "The network peer must join the test room.");
            return await _peer.WaitForAdmissionAsync(cancellationToken).ConfigureAwait(false);
        }

        private static bool VerifyNetworkFrame()
        {
            Require(!_timeout.IsCancellationRequested, "Timed out waiting for an animated remote player in Unity.");
            ConnectionTestClient realClient = GetField<ConnectionTestClient>(_client, "_client");
            Require(realClient != null && !realClient.Disconnected.IsCompleted,
                "The real Unity client must remain connected.");
            if (!_peerAdmission.IsCompleted || realClient.LatestAnimatedSnapshot == null)
            {
                return false;
            }

            JoinAck peer = _peerAdmission.GetAwaiter().GetResult();
            Transform remoteMarker = _client.transform.Find($"Player {peer.PlayerId}");
            Transform remoteRig = remoteMarker == null ? null : remoteMarker.Find("ServerRig");
            Transform leftThigh = remoteRig == null ? null : remoteRig.Find("Root/LeftThigh");
            if (leftThigh == null || !remoteMarker.gameObject.activeInHierarchy)
            {
                return false;
            }

            if (_requiresBob && _bobThigh == null)
            {
                _bobThigh = FindBobThigh(remoteRig);
                _bobStandingThighRotation = _bobThigh.localRotation;
            }

            double nowSeconds = EditorApplication.timeSinceStartup;
            if (nowSeconds >= _nextInputSeconds && (_lastSend == null || _lastSend.IsCompleted))
            {
                _lastSend?.GetAwaiter().GetResult();
                _lastSend = _peer.SendMoveAsync(new MoveInput(_nextSequence++, 1f, 0f), _timeout.Token);
                _nextInputSeconds = nowSeconds + INPUT_INTERVAL_SECONDS;
            }

            AnimatedWorldSnapshot snapshot = realClient.LatestAnimatedSnapshot;
            AnimatedPlayer pose = snapshot.Players.FirstOrDefault(player => player.PlayerId == peer.PlayerId);
            bool serverSendsWalking = pose.BonePitchCentidegrees != null &&
                pose.BonePitchCentidegrees.Any(angle => Math.Abs(angle) > 250);
            float thighRotationDegrees = Quaternion.Angle(leftThigh.localRotation, Quaternion.identity);
            if (!serverSendsWalking || remoteMarker.position.x < peer.X + 0.2f ||
                thighRotationDegrees < MOVING_THIGH_ANGLE_DEGREES)
            {
                return false;
            }

            if (_requiresBob)
            {
                float bobRotationDegrees = Quaternion.Angle(_bobStandingThighRotation, _bobThigh.localRotation);
                Require(Mathf.Abs(bobRotationDegrees - thighRotationDegrees) < ANGLE_TOLERANCE_DEGREES,
                    $"Bob's skinned thigh moved {bobRotationDegrees:F2}°, expected {thighRotationDegrees:F2}° from the server pose.");
                Debug.Log("PASS live Bob animation: the visible skinned thigh follows the replicated pose");
            }

            Debug.Log("PASS live TCP animation: remote pose from the server drives the moving Unity rig");
            return true;
        }

        private static Transform FindBobThigh(Transform rig)
        {
            Transform bob = rig.Find("Root/Bob");
            Require(bob != null && bob.gameObject.activeInHierarchy, "The installed Bob model must be visible under ServerRig/Root.");
            foreach (SkinnedMeshRenderer renderer in bob.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!renderer.enabled || renderer.forceRenderingOff || renderer.sharedMesh == null ||
                    renderer.sharedMesh.vertexCount == 0)
                {
                    continue;
                }

                Transform thigh = Array.Find(renderer.bones,
                    bone => bone != null && bone.name.EndsWith("LeftUpLeg", StringComparison.Ordinal));
                if (thigh != null)
                {
                    return thigh;
                }
            }

            throw new InvalidOperationException("Bob must have a visible skinned mesh bound to a left thigh bone.");
        }

        private static void Finish(int result)
        {
            SessionState.SetBool(RUNNING, false);
            _timeout?.Cancel();
            _peer?.Dispose();
            _timeout?.Dispose();
            _client?.Disconnect();
            EditorApplication.Exit(result);
        }

        private static Transform Marker(SyncTestClient client, long id)
        {
            var markers = GetField<Dictionary<long, Transform>>(client, "_markers");
            Require(markers.TryGetValue(id, out Transform marker), "Missing player marker " + id);
            return marker;
        }

        private static void Render(SyncTestClient client, double nowSeconds)
        {
            Invoke(client, "RenderWorld", nowSeconds);
        }

        private static void Invoke(SyncTestClient client, string name, params object[] arguments)
        {
            typeof(SyncTestClient).GetMethod(name, PRIVATE_INSTANCE).Invoke(client, arguments);
        }

        private static T GetField<T>(SyncTestClient client, string name)
        {
            return (T)typeof(SyncTestClient).GetField(name, PRIVATE_INSTANCE).GetValue(client);
        }

        private static void SetField(SyncTestClient client, string name, object value)
        {
            typeof(SyncTestClient).GetField(name, PRIVATE_INSTANCE).SetValue(client, value);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
