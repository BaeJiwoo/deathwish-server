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
        private static bool _verifiedIdleAim;
        private static bool _verifiedWalking;
        private static int _remoteShotsSeen;
        private static bool _sentRemoteShot;
        private static bool _sawRemoteKick;
        private static bool _receivedShotResult;
        private static bool _sentLocalShot;
        private static readonly AimAngles PeerIdleAim = new AimAngles(-8_000, -4_000);
        private static readonly AimAngles PeerMovingAim = new AimAngles(-8_000, 3_000);
        private static readonly AimAngles LocalIdleAim = new AimAngles(7_000, 4_000);
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
                    if (Resources.Load<GameObject>("bob_rigged") != null)
                    {
                        ShooterAimSceneCheck.Verify(_client);
                        _client.Disconnect();
                    }

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
            SetField(client, "_pitchDegrees", 0f);
            SetField(client, "_showPositionGizmos", true);
            var localStart = new Vector3(2f, 0f, -1f);
            var localEnd = new Vector3(4f, 0f, 1f);
            var remoteStart = new Vector3(-2f, 0f, 0f);
            var remoteEnd = new Vector3(-4f, 0f, 2f);
            const uint START_PHASE = 0xfffff000;
            const uint END_PHASE = 0x1000;

            Apply(client, 1, 1_000_000, 10, localStart, remoteStart, 17_500, true, START_PHASE);
            Apply(client, 2, 1_100_000, 10.1, localEnd, remoteEnd, -17_500, true, END_PHASE);
            Render(client, 10.1);
            Transform local = Marker(client, LOCAL_ID);
            Require(Vector3.Distance(local.position, localStart) < POSITION_TOLERANCE_METERS,
                "A received future endpoint must wait for the playback delay.");

            Render(client, 10.15);
            Transform localRig = local.Find("ServerRig");
            Transform remote = Marker(client, REMOTE_ID);
            Vector3 localMidpoint = Vector3.Lerp(localStart, localEnd, 0.5f);
            Vector3 remoteMidpoint = Vector3.Lerp(remoteStart, remoteEnd, 0.5f);
            Require(Vector3.Distance(local.position, localMidpoint) < POSITION_TOLERANCE_METERS &&
                Vector3.Distance(remote.position, remoteMidpoint) < POSITION_TOLERANCE_METERS,
                "The local and remote marker must use the delayed snapshot bracket.");
            Require(Quaternion.Angle(remote.Find("ServerRig").rotation, Quaternion.Euler(0f, 180f, 0f)) < ANGLE_TOLERANCE_DEGREES,
                "Remote aim yaw must take the shortest path across the 180-degree boundary.");
            Require(Quaternion.Angle(localRig.rotation, Quaternion.Euler(0f, 35f, 0f)) < ANGLE_TOLERANCE_DEGREES,
                "Local yaw must follow the camera in the current frame.");

            if (localRig.GetComponentInChildren<Animator>() != null)
            {
                foreach (Transform marker in new[] { local, remote })
                {
                    Transform grip = marker.GetComponentsInChildren<Transform>().Single(t => t.name == "Grip");
                    ShooterAimSceneCheck.VerifyWeapon(grip);
                }
                ShooterAimSceneCheck.VerifyClipAt(localRig, "ShooterWalk", 0f);
                AssertBarrel(remote, Quaternion.Euler(5f, 180f, 0f) * Vector3.forward);
                SetField(client, "_pitchDegrees", -53.5f);
                SetField(client, "_yawDegrees", 261.25f);
                Render(client, 10.15);
                AssertBarrel(local, GetField<Transform>(client, "_viewCamera").forward);
                Require(Vector3.Distance(local.position, localMidpoint) < POSITION_TOLERANCE_METERS,
                    "Rotating the local camera between snapshots must keep the displayed feet in place.");
                SetField(client, "_yawDegrees", 35f);
                SetField(client, "_pitchDegrees", 0f);
                Render(client, 10.15);
            }

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

            if (Resources.Load<GameObject>("bob_rigged") != null)
            {
                VerifyDelayedShot(client, local, 10.9);
                VerifyLateRemoteShots(client, remote);
            }
            else
            {
                Render(client, 10.9);
            }
            VerifyHeldPose(local, localEnd, 3500, true, END_PHASE);
            VerifyHeldPose(remote, remoteEnd, -17_500, true, END_PHASE, 3_000);
            VerifyPositionGizmos(localGizmos, localEnd, localEnd);
            VerifyPositionGizmos(remoteGizmos, remoteEnd, remoteEnd);
            Debug.Log("PASS offline gait: phase wrap, root interpolation, and held playback after the last snapshot");

            var fresh = new Vector3(7f, 0f, -3f);
            Apply(client, 3, 2_250_000, 11.25, fresh, null, 0, false, END_PHASE);
            Render(client, 11.25);
            Require(!remote.gameObject.activeSelf, "A departing player's marker must be hidden immediately.");
            Require(!remoteGizmos.gameObject.activeInHierarchy,
                "A departing player's server and client position gizmos must disappear with their marker.");
            Debug.Log("PASS offline position gizmos: latest tick, delayed interpolation, toggle, convergence, and departure");
            VerifyHeldPose(local, fresh, 3500, false, END_PHASE);

            // A stalled receive clock can have a long gap while the server advances only one fixed step.
            Apply(client, 4, 2_350_000, 12.5, localStart, null, 0, false, END_PHASE);
            Apply(client, 5, 2_450_000, 12.6, localEnd, null, 0, false, END_PHASE);
            Render(client, 12.65);
            Require(Vector3.Distance(local.position, Vector3.Lerp(localStart, localEnd, 0.5f)) < POSITION_TOLERANCE_METERS,
                "A receive gap must reset the clock so delayed interpolation resumes.");

            client.Disconnect();
            SetField(client, "_playerId", 9L);
            var legacy = new WorldSnapshot(ROOM_ID, 1, new[] { Position(9, Vector3.zero) });
            Invoke(client, "ApplySnapshot", legacy, 13.0);
            Render(client, 13.0);
            Transform legacyMarker = Marker(client, 9);
            Require(legacyMarker.Find("Body") != null && legacyMarker.Find("ServerRig") == null,
                "A standard room snapshot must keep the legacy body view.");
            Debug.Log("PASS offline animation: clock and receive gap reset, departure and legacy view after disconnect");
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
            Vector3 localPosition, Vector3? remotePosition, short yawCentidegrees, bool walking, uint phase)
        {
            SnapshotPlayer[] positions = remotePosition.HasValue
                ? new[] { Position(LOCAL_ID, localPosition), Position(REMOTE_ID, remotePosition.Value) }
                : new[] { Position(LOCAL_ID, localPosition) };
            var aim = new AimAngles(yawCentidegrees, yawCentidegrees >= 0 ? (short)-2_000 : (short)3_000);
            var local = new GaitPlayer(LOCAL_ID, 0, walking, phase, aim);
            var players = remotePosition.HasValue
                ? new[] { local, new GaitPlayer(REMOTE_ID, 0, walking, phase, aim) }
                : new[] { local };
            var snapshot = new GaitWorldSnapshot(new WorldSnapshot(ROOM_ID, tick, positions), timeMicroseconds, players);
            Invoke(client, "ApplyGaitSnapshot", snapshot, receivedAtSeconds);
        }

        private static void VerifyHeldPose(Transform marker, Vector3 feet, short yaw, bool walking, uint phase,
            short pitch = 0)
        {
            Transform rig = marker.Find("ServerRig");
            Require(Vector3.Distance(marker.position, feet) < POSITION_TOLERANCE_METERS,
                "A held gait frame must keep the authoritative feet position.");
            float cycle = (phase & 0xffff) / 65536f;
            if (rig.GetComponentInChildren<Animator>() != null)
            {
                ShooterAimSceneCheck.VerifyClipAt(rig, walking ? "ShooterWalk" : "ShooterStand", cycle);
                return;
            }
            var pitches = new short[AnimationRig.BONE_COUNT];
            AnimationRig.SampleWalk(cycle, walking, pitches);
            for (int index = 0; index < AnimationRig.Bones.Count; index++)
            {
                if (AnimationRig.Bones[index].Name == "Torso")
                {
                    pitches[index] += pitch;
                }
            }
            ServerAnimationPoseReferenceCheck.Verify(marker, feet, yaw, pitches);
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
            JoinRoomResponse response = await _peer.JoinShootingRoomAsync(ROOM_ID, cancellationToken).ConfigureAwait(false);
            Require(response.Result == RoomJoinResult.Success, "The network peer must join the test room.");
            return await _peer.WaitForAdmissionAsync(cancellationToken).ConfigureAwait(false);
        }

        private static bool VerifyNetworkFrame()
        {
            Require(!_timeout.IsCancellationRequested, "Timed out waiting for an animated remote player in Unity.");
            ConnectionTestClient realClient = GetField<ConnectionTestClient>(_client, "_client");
            Require(realClient != null && !realClient.Disconnected.IsCompleted,
                "The real Unity client must remain connected.");
            if (!_peerAdmission.IsCompleted || realClient.LatestGaitSnapshot == null)
            {
                return false;
            }

            JoinAck peer = _peerAdmission.GetAwaiter().GetResult();
            Transform remoteMarker = _client.transform.Find($"Player {peer.PlayerId}");
            Transform remoteRig = remoteMarker == null ? null : remoteMarker.Find("ServerRig");
            if (remoteRig == null || !remoteMarker.gameObject.activeInHierarchy)
            {
                return false;
            }

            if (_requiresBob && _bobThigh == null)
            {
                _bobThigh = FindBobThigh(remoteRig);
                _bobStandingThighRotation = _bobThigh.localRotation;
            }

            SetField(_client, "_yawDegrees", LocalIdleAim.YawCentidegrees / 100f);
            SetField(_client, "_pitchDegrees", LocalIdleAim.PitchCentidegrees / 100f);
            double nowSeconds = EditorApplication.timeSinceStartup;
            if (_verifiedWalking)
            {
                return VerifyReplicatedFire(realClient, remoteMarker, peer.PlayerId);
            }
            if (nowSeconds >= _nextInputSeconds && (_lastSend == null || _lastSend.IsCompleted))
            {
                _lastSend?.GetAwaiter().GetResult();
                float strafe = _verifiedIdleAim ? 1f : 0f;
                AimAngles aim = _verifiedIdleAim ? PeerMovingAim : PeerIdleAim;
                _lastSend = _peer.SendAimedMoveAsync(new MoveInput(_nextSequence++, strafe, 0f), aim, _timeout.Token);
                _nextInputSeconds = nowSeconds + INPUT_INTERVAL_SECONDS;
            }

            GaitPlayer gait = realClient.LatestGaitSnapshot.Players.FirstOrDefault(player => player.PlayerId == peer.PlayerId);
            if (!_verifiedIdleAim)
            {
                var peerView = _peer.LatestGaitSnapshot;
                long localId = GetField<long>(_client, "_playerId");
                GaitPlayer local = peerView?.Players.FirstOrDefault(player => player.PlayerId == localId) ?? default;
                if (!SameAim(local.Aim, LocalIdleAim) || !SameAim(gait.Aim, PeerIdleAim) ||
                    (_requiresBob && !BarrelMatches(remoteMarker, Direction(PeerIdleAim))))
                {
                    return false;
                }
                Require(!gait.IsWalking && !local.IsWalking && gait.CyclePhase == 0 && local.CyclePhase == 0,
                    "Aiming without movement input must leave both gait cycles idle.");
                Require(Vector2.Distance(new Vector2(peer.X, peer.Z), new Vector2(remoteMarker.position.x, remoteMarker.position.z)) <
                    POSITION_TOLERANCE_METERS, "Remote camera rotation must keep the player in place.");
                if (_requiresBob)
                {
                    AssertBarrel(Marker(_client, localId), GetField<Transform>(_client, "_viewCamera").forward);
                }
                _verifiedIdleAim = true;
                Debug.Log("PASS live TCP aim: two stationary players see the other player's yaw and pitch without firing.");
                return false;
            }

            if (!gait.IsWalking || !SameAim(gait.Aim, PeerMovingAim) || remoteMarker.position.x < peer.X + 0.2f)
            {
                return false;
            }
            if (_requiresBob && (!BarrelMatches(remoteMarker, Direction(PeerMovingAim)) ||
                Quaternion.Angle(_bobStandingThighRotation, _bobThigh.localRotation) < MOVING_THIGH_ANGLE_DEGREES))
            {
                return false;
            }
            Require(realClient.LatestAnimatedSnapshot == null, "The new Unity connection must receive gait and aim together.");
            Debug.Log("PASS live TCP gait and aim: a strafing Bob walks in +X while aiming toward -X and down.");
            _verifiedWalking = true;
            return !_requiresBob;
        }

        private static bool VerifyReplicatedFire(ConnectionTestClient realClient, Transform remote, long peerId)
        {
            if (_lastSend != null && !_lastSend.IsCompleted)
            {
                return false;
            }
            _lastSend?.GetAwaiter().GetResult();
            long localId = GetField<long>(_client, "_playerId");
            if (_remoteShotsSeen == 2)
            {
                if (!_sentLocalShot)
                {
                    Require(!_peer.TryReadShotFired(out _), "Joining, looking and walking must never broadcast a shot.");
                    Invoke(_client, "QueueLocalShot", Marker(_client, localId), Time.unscaledTimeAsDouble);
                    _sentLocalShot = true;
                }
                if (!_peer.TryReadShotFired(out ShotFired accepted))
                {
                    return false;
                }
                Require(accepted.PlayerId == localId, "The observer must receive the local click exactly from its shooter.");
                Debug.Log("PASS live TCP fire: two repeated peer shots kicked and recovered. A local click was visible to the observer.");
                return true;
            }

            GaitPlayer gait = realClient.LatestGaitSnapshot.Players.First(player => player.PlayerId == peerId);
            if (gait.IsWalking)
            {
                return false;
            }
            Vector3 aim = Direction(PeerMovingAim);
            float recoil = Vector3.Angle(FindMuzzle(remote).forward, aim);
            if (!_sentRemoteShot)
            {
                if (recoil > ANGLE_TOLERANCE_DEGREES)
                {
                    return false;
                }
                var ray = new CameraRay(System.Numerics.Vector3.Zero,
                    new System.Numerics.Vector3(aim.x, aim.y, aim.z));
                _lastSend = _peer.SendRayAsync(ray, _timeout.Token);
                _sentRemoteShot = true;
                return false;
            }
            _receivedShotResult |= _peer.TryReadRaycastResult(out _);
            _sawRemoteKick |= recoil > 2f;
            if (_receivedShotResult && _sawRemoteKick && recoil < ANGLE_TOLERANCE_DEGREES)
            {
                _remoteShotsSeen++;
                _sentRemoteShot = false;
                _sawRemoteKick = false;
                _receivedShotResult = false;
            }
            return false;
        }

        private static bool SameAim(AimAngles actual, AimAngles expected)
        {
            return actual.YawCentidegrees == expected.YawCentidegrees &&
                actual.PitchCentidegrees == expected.PitchCentidegrees;
        }

        private static Vector3 Direction(AimAngles aim)
        {
            return Quaternion.Euler(aim.PitchCentidegrees / 100f, aim.YawCentidegrees / 100f, 0f) * Vector3.forward;
        }

        private static bool BarrelMatches(Transform marker, Vector3 direction)
        {
            return Vector3.Angle(FindMuzzle(marker).forward, direction) <= ANGLE_TOLERANCE_DEGREES;
        }

        private static Transform FindMuzzle(Transform marker)
        {
            return marker.GetComponentsInChildren<Transform>().Single(t => t.name == "Muzzle");
        }

        private static void AssertBarrel(Transform marker, Vector3 direction)
        {
            Require(BarrelMatches(marker, direction), "The visible weapon must be parallel to the camera aim direction.");
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

        private static void VerifyDelayedShot(SyncTestClient client, Transform marker, double nextRenderSeconds)
        {
            Transform muzzle = FindMuzzle(marker);
            Vector3 clickPosition = muzzle.position;
            Vector3 view = GetField<Transform>(client, "_viewCamera").forward;
            AssertBarrel(marker, view);
            const double CLICK_SECONDS = 10.15;
            Invoke(client, "QueueLocalShot", marker, CLICK_SECONDS);
            var queue = (System.Collections.IEnumerable)GetField<object>(client, "_actions");
            object shot = queue.Cast<object>().Single();
            var request = (CameraRay)shot.GetType().GetField("Request").GetValue(shot);
            var rayDirection = new Vector3(request.Direction.X, request.Direction.Y, request.Direction.Z);
            Require(Vector3.Angle(rayDirection, view) < ANGLE_TOLERANCE_DEGREES,
                "Recoil must leave the click-time camera ray intact.");
            Require(Vector3.Angle(muzzle.forward, view) > 1f, "The click must move the local rifle in the same frame.");
            ShooterAimSceneCheck.VerifyClipAt(marker.Find("ServerRig"), "ShooterWalk", 0f);
            Transform[] bones = marker.GetComponentsInChildren<Transform>();
            Transform upperArm = bones.Single(t => t.name.EndsWith("RightArm", StringComparison.Ordinal));
            Quaternion kickRotation = upperArm.rotation;
            Render(client, CLICK_SECONDS + 0.04);
            float firstKick = Vector3.Angle(muzzle.forward, view);
            Require(firstKick > 2f && Quaternion.Angle(kickRotation, upperArm.rotation) > 0.2f,
                "The shoulder and the held rifle must animate together after the click.");
            foreach (string side in new[] { "Right", "Left" })
            {
                Transform wrist = bones.Single(t => t.name.EndsWith(side + "Hand", StringComparison.Ordinal));
                Transform knuckle = bones.Single(t => t.name.EndsWith(side + "HandIndex1", StringComparison.Ordinal));
                Transform socket = bones.Single(t => t.name == (side == "Right" ? "Grip" : "SupportHand"));
                Require(Vector3.Distance(Vector3.Lerp(wrist.position, knuckle.position, 0.72f), socket.position) < 0.01f,
                    $"{side} palm must stay on its rifle socket during recoil.");
            }
            Render(client, CLICK_SECONDS + 0.18);
            Invoke(client, "QueueLocalShot", marker, CLICK_SECONDS + 0.18);
            Render(client, CLICK_SECONDS + 0.22);
            Require(Vector3.Angle(muzzle.forward, view) > firstKick * 0.8f && queue.Cast<object>().Count() == 2,
                "A second click during recovery must restart the kick and enqueue a second ray.");
            Render(client, nextRenderSeconds);
            AssertBarrel(marker, view);
            Require(Vector3.Distance(muzzle.position, clickPosition) > POSITION_TOLERANCE_METERS,
                "The player must have moved before the raycast response.");
            object feedback = GetField<object>(client, "_feedback");
            feedback.GetType().GetMethod("Show").Invoke(feedback,
                new object[] { shot, "Raycast miss", Time.unscaledTime });
            LineRenderer trail = client.transform.Find("BulletTrail").GetComponent<LineRenderer>();
            Require(Vector3.Distance(trail.GetPosition(0), clickPosition) < POSITION_TOLERANCE_METERS,
                "The shot trail must start at the click-time muzzle, even after the shooter moves.");
            Debug.Log($"PASS local fire: immediate recoil, a second click during recovery, return to aim, and captured muzzle. Sample kick {firstKick:F2}°.");
        }

        private static void VerifyLateRemoteShots(SyncTestClient client, Transform remote)
        {
            const double ARRIVAL_SECONDS = 10.9;
            Transform muzzle = FindMuzzle(remote);
            Vector3 rest = muzzle.forward;
            // The captured clock is server + 9s, plus a 100ms render delay. Both events
            // therefore arrive after their original recoil would already have finished.
            Invoke(client, "QueueRemoteShot", new ShotFired(REMOTE_ID, 1_400_000), ARRIVAL_SECONDS);
            Invoke(client, "QueueRemoteShot", new ShotFired(REMOTE_ID, 1_450_000), ARRIVAL_SECONDS);
            Render(client, ARRIVAL_SECONDS);
            Require(Vector3.Angle(rest, muzzle.forward) > 1f, "An overdue live shot must still be shown on arrival.");
            Render(client, ARRIVAL_SECONDS + 0.035);
            float firstPeak = Vector3.Angle(rest, muzzle.forward);
            Render(client, ARRIVAL_SECONDS + 0.081);
            float secondStart = Vector3.Angle(rest, muzzle.forward);
            Render(client, ARRIVAL_SECONDS + 0.12);
            float secondPeak = Vector3.Angle(rest, muzzle.forward);
            Require(firstPeak > 5f && secondStart < firstPeak - 2f && secondPeak > secondStart + 2f,
                $"A batch must play both shots, measured {firstPeak:F2}°, {secondStart:F2}°, {secondPeak:F2}°.");
            Render(client, ARRIVAL_SECONDS + 0.40);
            AssertBarrel(remote, rest);
            Debug.Log("PASS remote fire: both delayed events kicked on a stalled snapshot timeline, then recovered.");
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
