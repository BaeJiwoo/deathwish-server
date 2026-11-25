using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using DeathWish.Client;
using DeathWish.MockClient.Core;
using DeathWish.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[InitializeOnLoad]
public static class PhysicsLabRoomCheck
{
    private const string RUNNING = "DeathWish.PhysicsLabRoomCheck";
    private const string IS_PREVIEW_ONLY = "DeathWish.PhysicsLabRoomCheck.PreviewOnly";
    private const string IS_COMBAT_PREVIEW = "DeathWish.PhysicsLabRoomCheck.CombatPreview";
    private const string LIVE_ATTACKER_NAME = "하늘";
    private const string LIVE_VICTIM_NAME = "바다";
    private const int LIVE_DAMAGE_PER_SHOT = 25;
    private const double LIVE_SHOT_INTERVAL_SECONDS = 0.3;
    private const float ARENA_ROUTE_X_METERS = 4.5f;
    private const float ARENA_ARCH_FRONT_Z_METERS = -7.7f;
    private const float ARENA_COLLISION_TOLERANCE_METERS = 0.02f;
    private const float ARENA_WIDTH_METERS = 32f;
    private const float ARENA_LENGTH_METERS = 38f;
    private const float PLAYER_HEIGHT_METERS = 1.8f;
    private const float PLAYER_DIAMETER_METERS = 0.65f;
    private static double deadline;
    private static double nextShot;
    private static int phase;
    private static int shotsSent;
    private static long departedPlayerId;
    private static Task pendingShot;
    private static SyncTestClient first;
    private static SyncTestClient peer;
    private static UnityEngine.SceneManagement.Scene peerScene;

    static PhysicsLabRoomCheck()
    {
        EditorApplication.update += Check;
    }

    public static void Run()
    {
        StartCheck(false);
    }

    public static void RunMapPreview()
    {
        StartCheck(true);
    }

    public static void RunCombatPreview()
    {
        StartCheck(true, true);
    }

    private static void StartCheck(bool isPreviewOnly, bool isCombatPreview = false)
    {
        EditorSceneManager.OpenScene("Assets/DeathWishMovement/Scenes/SyncTestScene.unity");
        SessionState.SetBool(IS_PREVIEW_ONLY, isPreviewOnly);
        SessionState.SetBool(IS_COMBAT_PREVIEW, isCombatPreview);
        SessionState.SetBool(RUNNING, true);
        EditorApplication.EnterPlaymode();
    }

    private static void Check()
    {
        if (!SessionState.GetBool(RUNNING, false) || !EditorApplication.isPlaying)
        {
            return;
        }
        try
        {
            EditorApplication.isPaused = false;
            EditorApplication.QueuePlayerLoopUpdate();
            var client = first ?? (first = UnityEngine.Object.FindFirstObjectByType<SyncTestClient>());
            if (phase == 0)
            {
                Field<InputField>(client, "_hostInput").text = "127.0.0.1";
                Field<InputField>(client, "_portInput").text = Environment.GetEnvironmentVariable("DW_UNITY_TEST_PORT") ?? "17779";
                if (Field<InputField>(client, "_roomInput").text != "2")
                {
                    throw new InvalidOperationException("Unexpected default room.");
                }
                Field<InputField>(client, "_roomInput").text = Environment.GetEnvironmentVariable("DW_UNITY_LAB_ROOM") ?? "2";
                var preview = client.transform.Find("PhysicsLab").gameObject;
                if (!preview.activeSelf || client.transform.Find("Flat map").gameObject.activeSelf)
                {
                    throw new InvalidOperationException("Missing preview.");
                }
                client.SendMessage("LateUpdate");
                Color32[] visible = RenderCamera();
                preview.SetActive(false);
                Color32[] background = RenderCamera();
                preview.SetActive(true);
                int pixels = visible.Zip(background, (a, b) => !a.Equals(b)).Count(changed => changed);
                if (pixels < visible.Length / 4)
                {
                    throw new InvalidOperationException("Missing camera geometry: " + pixels);
                }
                Debug.Log("PASS offline preview pixels " + pixels + "/" + visible.Length);
                if (SessionState.GetBool(IS_PREVIEW_ONLY, false))
                {
                    CheckMapPreview(client, preview);
                    if (SessionState.GetBool(IS_COMBAT_PREVIEW, false))
                    {
                        CheckCombatPreview(client);
                    }
                    SessionState.SetBool(RUNNING, false);
                    EditorApplication.Exit(0);
                    return;
                }
                client.Connect();
                deadline = EditorApplication.timeSinceStartup + 20;
                phase++;
                return;
            }
            if (EditorApplication.timeSinceStartup > deadline)
            {
                throw new TimeoutException($"scene={peerScene.isLoaded}, main={Describe(client)}, peer={Describe(peer)}");
            }
            if (Field<long>(client, "_playerId") == 0)
            {
                return;
            }
            GameObject flat = client.transform.Find("Flat map").gameObject;
            GameObject lab = client.transform.Find("PhysicsLab").gameObject;
            if (phase == 1)
            {
                MeshCollider[] meshes = lab.GetComponentsInChildren<MeshCollider>();
                Transform paint = lab.transform.Find("CautionLines");
                if (flat.activeSelf || !lab.activeSelf || meshes.Length != 7 ||
                    paint == null || paint.GetComponent<MeshCollider>() != null ||
                    paint.GetComponent<MeshRenderer>().sharedMaterial.GetFloat("_Caution") != 1f ||
                    meshes.Sum(m => m.sharedMesh.vertexCount) != 616 ||
                    meshes.Sum(m => m.sharedMesh.triangles.Length / 3) != 1202)
                {
                    throw new InvalidOperationException("Incorrect PhysicsLab geometry.");
                }
                if (peer == null)
                {
                    // Open a second copy of the saved scene so each client owns its UI and world objects.
                    if (!peerScene.IsValid())
                    {
                        peerScene = EditorSceneManager.LoadSceneInPlayMode(
                        "Assets/DeathWishMovement/Scenes/SyncTestScene.unity",
                        new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Additive));
                        return;
                    }
                    if (!peerScene.isLoaded)
                    {
                        return;
                    }
                    peer = PreparePeerScene();
                    Field<InputField>(peer, "_portInput").text = Field<InputField>(client, "_portInput").text;
                    Field<InputField>(peer, "_roomInput").text = Field<InputField>(client, "_roomInput").text;
                    peer.Connect();
                    return;
                }
                if (Field<long>(peer, "_playerId") == 0 ||
                    !HasRemote(client, peer) || !HasRemote(peer, client))
                {
                    return;
                }
                CheckRemotePixels(client, peer);
                CheckRemotePixels(peer, client);
                peer.Disconnect();
                SceneManager.UnloadSceneAsync(peerScene);
                peer = null;
                peerScene = default;
                client.Disconnect();
                Field<InputField>(client, "_roomInput").text = "1";
                client.Connect();
                deadline = EditorApplication.timeSinceStartup + 20;
                phase++;
                return;
            }
            if (phase == 2)
            {
                if (!flat.activeSelf || lab.activeSelf)
                {
                    throw new InvalidOperationException("Flat room did not restore its floor.");
                }
                Debug.Log("PASS PhysicsLab and flat room live Unity admission, 7 meshes, 616 vertices, 1202 triangles");
                string arenaRoom = Environment.GetEnvironmentVariable("DW_UNITY_TDM_ROOM");
                if (!string.IsNullOrWhiteSpace(arenaRoom))
                {
                    client.Disconnect();
                    Field<InputField>(client, "_roomInput").text = arenaRoom;
                    NicknameInput(client).text = IsLiveCombat() ? LIVE_ATTACKER_NAME : "Arena check";
                    client.Connect();
                    deadline = EditorApplication.timeSinceStartup + 20;
                    phase++;
                    return;
                }
            }
            else if (phase == 3)
            {
                var state = Core(client).LatestMatchState;
                if (state == null)
                {
                    return;
                }
                if (!state.Players.Any(player => player.PlayerId == Field<long>(client, "_playerId") &&
                    player.Nickname == NicknameInput(client).text))
                {
                    throw new InvalidOperationException("TeamDeathmatch did not apply the admitted nickname.");
                }
                CheckArena(client, flat, lab);
                Debug.Log("PASS TeamDeathmatch live Unity admission");
                if (IsLiveCombat())
                {
                    if (state.Phase != MatchPhase.Waiting || state.Players.Count != 1)
                    {
                        throw new InvalidOperationException("First arena client should wait for its opponent.");
                    }
                    phase++;
                    return;
                }
            }
            else if (!CheckLiveCombat(client))
            {
                return;
            }
            SessionState.SetBool(RUNNING, false);
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            SessionState.SetBool(RUNNING, false);
            EditorApplication.Exit(1);
        }
    }

    private static bool IsLiveCombat() => Environment.GetEnvironmentVariable("DW_UNITY_COMBAT_TEST") == "1";

    private static bool CheckLiveCombat(SyncTestClient client)
    {
        ConnectionTestClient attacker = Core(client);
        long attackerId = Field<long>(client, "_playerId");
        if (phase == 4)
        {
            if (peer == null)
            {
                OpenCombatPeer(client);
                return false;
            }

            MatchState attackState = attacker.LatestMatchState;
            MatchState victimState = Core(peer)?.LatestMatchState;
            if (attackState?.Phase != MatchPhase.Playing || victimState?.Phase != MatchPhase.Playing ||
                !attackState.Players.All(player => player.PingMs >= 0) ||
                !victimState.Players.All(player => player.PingMs >= 0))
            {
                return false;
            }
            long victimId = Field<long>(peer, "_playerId");
            if (attackState.Players.Count != 2 || victimState.Players.Count != 2 ||
                !MatchesTeam(attackState, attackerId, LIVE_ATTACKER_NAME, TeamId.Red) ||
                !MatchesTeam(attackState, victimId, LIVE_VICTIM_NAME, TeamId.Blue) ||
                !MatchesTeam(victimState, attackerId, LIVE_ATTACKER_NAME, TeamId.Red) ||
                !MatchesTeam(victimState, victimId, LIVE_VICTIM_NAME, TeamId.Blue))
            {
                throw new InvalidOperationException("Clients disagree on the named 1v1 match roster.");
            }

            Debug.Log("PASS two Unity players entered a named 1v1 match with measured RTTs on each screen");
            phase++;
        }
        if (phase == 7)
        {
            bool hasRemovedPeer = attacker.LatestMatchState?.Players.Count == 1 &&
                client.transform.Find("Player " + departedPlayerId) == null;
            if (hasRemovedPeer)
            {
                Debug.Log("PASS live Unity departure removed the disconnected player from roster and scene");
            }
            return hasRemovedPeer;
        }

        long opponentId = Field<long>(peer, "_playerId");
        ConnectionTestClient opponent = Core(peer);
        MatchPlayer victim = opponent.LatestMatchState.Players.Single(player => player.PlayerId == opponentId);
        if (phase == 5)
        {
            if (pendingShot != null)
            {
                if (!pendingShot.IsCompleted)
                {
                    return false;
                }
                pendingShot.GetAwaiter().GetResult();
                pendingShot = null;
            }

            int expectedHealth = MatchState.MAX_HEALTH - shotsSent * LIVE_DAMAGE_PER_SHOT;
            if (victim.Health != expectedHealth)
            {
                return false;
            }
            if (expectedHealth > 0)
            {
                if (EditorApplication.timeSinceStartup < nextShot || Field<bool>(client, "_isSending") ||
                    attacker.LatestSnapshot == null || opponent.LatestSnapshot == null)
                {
                    return false;
                }

                System.Numerics.Vector3 from = Feet(attacker.LatestSnapshot, attackerId);
                System.Numerics.Vector3 to = Feet(opponent.LatestSnapshot, opponentId);
                var ray = new CameraRay(new System.Numerics.Vector3(0, 1.1f, 0), System.Numerics.Vector3.Normalize(to - from));
                pendingShot = attacker.SendRayAsync(ray, CancellationToken.None);
                shotsSent++;
                nextShot = EditorApplication.timeSinceStartup + LIVE_SHOT_INTERVAL_SECONDS;
                return false;
            }

            string killLine = $"[{LIVE_ATTACKER_NAME}] eliminated [{LIVE_VICTIM_NAME}]";
            if (MatchHud(client).Find("Kill feed").GetComponent<Text>().text != killLine ||
                MatchHud(peer).Find("Kill feed").GetComponent<Text>().text != killLine ||
                MatchHud(peer).Find("Health bar/Health").GetComponent<Text>().text != "HP 0  ·  Respawning")
            {
                return false;
            }
            if (victim.Deaths != 1 || opponent.LatestMatchState.RedScore != 1 ||
                attacker.LatestMatchState.Players.Single(player => player.PlayerId == attackerId).Kills != 1)
            {
                throw new InvalidOperationException("Fatal hit did not synchronize kill, death and team score.");
            }
            Debug.Log("PASS four live Unity shots synchronized fatal damage, HUD health, K/D, team score and both kill feeds");
            phase++;
            return false;
        }

        if (victim.Health != MatchState.MAX_HEALTH || opponent.LatestSnapshot == null ||
            MatchHud(peer).Find("Health bar/Health").GetComponent<Text>().text != "HP 100 / 100")
        {
            return false;
        }
        if (victim.Deaths != 1 || victim.Nickname != LIVE_VICTIM_NAME ||
            Feet(opponent.LatestSnapshot, opponentId).Z < 11.9f)
        {
            throw new InvalidOperationException("Live respawn lost the victim identity or team spawn.");
        }
        Debug.Log("PASS live Unity victim respawned on its team spawn with the original nickname and restored health");
        departedPlayerId = opponentId;
        peer.Disconnect();
        SceneManager.UnloadSceneAsync(peerScene);
        phase++;
        return false;
    }

    private static void OpenCombatPeer(SyncTestClient client)
    {
        if (!peerScene.IsValid())
        {
            peerScene = EditorSceneManager.LoadSceneInPlayMode(
                "Assets/DeathWishMovement/Scenes/SyncTestScene.unity",
                new LoadSceneParameters(LoadSceneMode.Additive));
            return;
        }
        if (!peerScene.isLoaded)
        {
            return;
        }

        peer = PreparePeerScene();
        Field<InputField>(peer, "_hostInput").text = Field<InputField>(client, "_hostInput").text;
        Field<InputField>(peer, "_portInput").text = Field<InputField>(client, "_portInput").text;
        Field<InputField>(peer, "_roomInput").text = Field<InputField>(client, "_roomInput").text;
        NicknameInput(peer).text = LIVE_VICTIM_NAME;
        peer.Connect();
        deadline = EditorApplication.timeSinceStartup + 20;
    }

    private static SyncTestClient PreparePeerScene()
    {
        GameObject[] roots = peerScene.GetRootGameObjects();
        foreach (EventSystem eventSystem in roots.SelectMany(root => root.GetComponentsInChildren<EventSystem>()))
        {
            eventSystem.gameObject.SetActive(false);
        }
        var client = roots.SelectMany(root => root.GetComponentsInChildren<SyncTestClient>()).Single();
        var camera = roots.SelectMany(root => root.GetComponentsInChildren<Camera>()).Single();
        SetField(client, "_sceneCamera", camera);
        SetField(client, "_viewCamera", camera.transform);
        return client;
    }

    private static bool MatchesTeam(MatchState state, long playerId, string name, TeamId team) =>
        state.Players.Any(player => player.PlayerId == playerId && player.Nickname == name && player.Team == team);

    private static System.Numerics.Vector3 Feet(WorldSnapshot snapshot, long playerId)
    {
        SnapshotPlayer player = snapshot.Players.Single(entry => entry.PlayerId == playerId);
        return new System.Numerics.Vector3(player.X, player.Y, player.Z);
    }

    private static ConnectionTestClient Core(SyncTestClient client) => Field<ConnectionTestClient>(client, "_client");

    private static Transform MatchHud(SyncTestClient client) =>
        NicknameInput(client).GetComponentInParent<Canvas>().transform.Find("Team deathmatch HUD");

    private static void CheckCombatPreview(SyncTestClient client)
    {
        const long SELF_ID = 51;
        const long PEER_ID = 52;
        const int ROOM_ID = 2;
        const string SELF_NAME = "하늘";
        const string PEER_NAME = "바다";
        InputField nickname = NicknameInput(client);
        nickname.text = SELF_NAME;
        if (nickname.text != SELF_NAME || nickname.textComponent.supportRichText ||
            !nickname.textComponent.font.HasCharacter(SELF_NAME[0]))
        {
            throw new InvalidOperationException("Nickname input does not preserve or render Korean.");
        }

        var selfSpawn = new SnapshotPlayer(SELF_ID, -6, 0, -11);
        var peerSpawn = new SnapshotPlayer(PEER_ID, -6, 0, -7);
        TeamId? winner = null;
        MatchState State(long tick, int health, int peerHealth, int deaths) =>
            new MatchState(ROOM_ID, tick, MatchPhase.Playing, 0, 0, winner, new[]
            {
                new MatchPlayer(SELF_ID, SELF_NAME, TeamId.Red, health, deaths, 0, 0, -1),
                new MatchPlayer(PEER_ID, PEER_NAME, TeamId.Blue, peerHealth, 0, deaths, 0, -1)
            });

        Invoke(client, "SelectMap", WorldMapId.TeamDeathmatch);
        SetField(client, "_mapId", WorldMapId.TeamDeathmatch);
        SetField(client, "_playerId", SELF_ID);
        Invoke(client, "ApplyMatchState", State(5, 100, 100, 0));
        Invoke(client, "ApplySnapshot", new WorldSnapshot(ROOM_ID, 5, new[] { selfSpawn, peerSpawn }),
            Time.unscaledTimeAsDouble - 1);
        client.SendMessage("LateUpdate");

        var self = client.transform.Find("Me " + SELF_ID);
        var peer = client.transform.Find("Player " + PEER_ID);
        if (self == null || peer == null || self.Find("Gun/Receiver")?.GetComponent<MeshFilter>() == null ||
            peer.Find("Gun/Receiver")?.GetComponent<MeshFilter>() == null ||
            self.Find("Body").GetComponent<MeshRenderer>().sharedMaterial.color ==
            peer.Find("Body").GetComponent<MeshRenderer>().sharedMaterial.color)
        {
            throw new InvalidOperationException("Team colors or the square gun are missing.");
        }
        CheckCylinderBody(self);
        CheckCylinderBody(peer);
        CheckLocalWeaponPixels(Field<Transform>(client, "_viewCamera").GetComponent<Camera>(), self.Find("Gun"));

        Canvas canvas = nickname.GetComponentInParent<Canvas>();
        Transform hud = canvas.transform.Find("Team deathmatch HUD");
        Text nameplate = hud.Find("Nameplate " + PEER_ID)?.GetComponent<Text>();
        if (nameplate == null || !nameplate.gameObject.activeSelf || nameplate.text != PEER_NAME || nameplate.supportRichText)
        {
            throw new InvalidOperationException("The remote nickname is missing.");
        }

        Invoke(client, "ApplyMatchState", State(6, 75, 0, 1));
        client.SendMessage("LateUpdate");
        RectTransform fill = (RectTransform)hud.Find("Health bar/Health track/Health fill");
        if (peer.gameObject.activeSelf || nameplate.gameObject.activeSelf || !Mathf.Approximately(fill.anchorMax.x, 0.75f))
        {
            throw new InvalidOperationException("Damage did not update the health bar or hide the dead player.");
        }
        object hudLogic = Field<object>(client, "_matchHud");
        Invoke(hudLogic, "AddKill", new KillEvent(ROOM_ID, 6, SELF_ID, SELF_NAME, PEER_ID, PEER_NAME), 2f);
        Text feed = hud.Find("Kill feed").GetComponent<Text>();
        if (!feed.text.Contains(SELF_NAME) || !feed.text.Contains(PEER_NAME) || feed.supportRichText)
        {
            throw new InvalidOperationException("The confirmed kill is missing player nicknames.");
        }

        Invoke(client, "ApplyMatchState", State(7, 75, 100, 1));
        client.SendMessage("LateUpdate");
        if (peer.gameObject.activeSelf)
        {
            throw new InvalidOperationException("A respawn was rendered with stale coordinates.");
        }
        var newSpawn = new SnapshotPlayer(PEER_ID, 6, 0, 12);
        Invoke(client, "ApplySnapshot", new WorldSnapshot(ROOM_ID, 7, new[] { selfSpawn, newSpawn }), Time.unscaledTimeAsDouble);
        client.SendMessage("LateUpdate");
        if (!peer.gameObject.activeSelf || Vector3.Distance(peer.position, new Vector3(6, 0, 12)) > 0.01f)
        {
            throw new InvalidOperationException("The player did not snap to the authoritative respawn.");
        }
        Invoke(client, "ApplyMatchState", State(8, 0, 100, 1));
        if ((bool)Invoke(client, "CanPlay") || fill.anchorMax.x != 0)
        {
            throw new InvalidOperationException("Dead local player retained gameplay input or health.");
        }
        Invoke(hudLogic, "ExpireKills", 20f);
        if (feed.text.Length != 0)
        {
            throw new InvalidOperationException("A kill feed line did not expire.");
        }
        CheckMatchScoreboard(client, hud, ROOM_ID, selfSpawn, peerSpawn);
        client.Disconnect();
        if (hud.gameObject.activeSelf || hud.Find("Scoreboard").gameObject.activeSelf)
        {
            throw new InvalidOperationException("Disconnect did not clear the combat HUD and scoreboard.");
        }
        Debug.Log("PASS combat preview: Korean nicknames, team cylinders, box guns, health, kill feed, death and respawn");
    }

    private static void CheckMatchScoreboard(SyncTestClient client, Transform hud, int roomId,
        SnapshotPlayer self, SnapshotPlayer peer)
    {
        var red = new MatchPlayer(self.PlayerId, "하늘", TeamId.Red, 100, 7, 2, 3, 26);
        var blue = new MatchPlayer(peer.PlayerId, "바다", TeamId.Blue, 100, 4, 3, 1, MatchState.UNKNOWN_PING_MS);
        var waiting = new MatchState(roomId, 9, MatchPhase.Waiting, 0, 0, null, new[] { red, blue });
        Invoke(client, "ApplyMatchState", waiting);
        Invoke(client, "ApplySnapshot", new WorldSnapshot(roomId, 9, new[] { self, peer }), Time.unscaledTimeAsDouble);

        var phase = hud.Find("Match summary/Phase").GetComponent<Text>();
        var score = hud.Find("Match summary/Score").GetComponent<Text>();
        Transform board = hud.Find("Scoreboard");
        if (phase.text != "Waiting · Red 1 / Blue 1" || board.gameObject.activeSelf || (bool)Invoke(client, "CanPlay"))
        {
            throw new InvalidOperationException("Waiting phase did not show roster counts or prevent play.");
        }
        Invoke(client, "SetScoreboardOpen", true);
        Invoke(client, "RefreshTelemetry");
        if (!board.gameObject.activeSelf || Field<GameObject>(client, "_connectionPanel").activeSelf)
        {
            throw new InvalidOperationException("The held scoreboard is hidden or overlaps the connection controls.");
        }

        MatchPlayer[] players = new[] { red, blue }.Concat(Enumerable.Range(0, 8).Select(index =>
            new MatchPlayer(index + 100, $"Peer {index}", index % 2 == 0 ? TeamId.Red : TeamId.Blue,
                100, 0, 0, 0, 8))).ToArray();
        Invoke(client, "ApplyMatchState", new MatchState(roomId, 10, MatchPhase.Playing, 9, 4, null, players));
        if (score.text != "Red 9  ·  4 Blue" || !(bool)Invoke(client, "CanPlay") ||
            board.Find("Red team").Cast<Transform>().Count(row => row.name.StartsWith("Player ", StringComparison.Ordinal)
                && row.gameObject.activeSelf) != 5 ||
            board.Find("Blue team").Cast<Transform>().Count(row => row.name.StartsWith("Player ", StringComparison.Ordinal)
                && row.gameObject.activeSelf) != 5)
        {
            throw new InvalidOperationException("Playing phase did not show both five-player teams and the score.");
        }
        var redRow = board.Find("Red team/Player 1");
        var blueRow = board.Find("Blue team/Player 1");
        if (redRow.Find("Nickname").GetComponent<Text>().text != red.Nickname ||
            redRow.Find("K").GetComponent<Text>().text != "7" ||
            redRow.Find("D").GetComponent<Text>().text != "2" ||
            redRow.Find("A").GetComponent<Text>().text != "3" ||
            redRow.Find("Ping (ms)").GetComponent<Text>().text != "26" ||
            blueRow.Find("Nickname").GetComponent<Text>().text != blue.Nickname ||
            blueRow.Find("Ping (ms)").GetComponent<Text>().text != "\u2014")
        {
            throw new InvalidOperationException("Scoreboard is missing a player name, K/D/A or RTT.");
        }

        Invoke(client, "SetScoreboardOpen", false);
        Invoke(client, "RefreshTelemetry");
        if (board.gameObject.activeSelf || !Field<GameObject>(client, "_connectionPanel").activeSelf)
        {
            throw new InvalidOperationException("Releasing the scoreboard did not restore connection controls.");
        }
        Invoke(client, "ApplyMatchState", new MatchState(roomId, 11, MatchPhase.Finished, 10, 4, TeamId.Red, players));
        Invoke(client, "SetScoreboardOpen", true);
        if (score.text != "Red 10  ·  4 Blue" || phase.text != "Red wins · Hold Tab" || (bool)Invoke(client, "CanPlay") ||
            !board.gameObject.activeSelf)
        {
            throw new InvalidOperationException("The finish state did not announce the winner or stop play.");
        }
        Invoke(client, "OnApplicationFocus", false);
        if (board.gameObject.activeSelf)
        {
            throw new InvalidOperationException("Focus loss left the scoreboard open.");
        }
        Debug.Log("PASS match preview: waiting, live scores, ten-player Tab roster, K/D/A, ping, winner and focus");
    }

    private static InputField NicknameInput(SyncTestClient client) =>
        Field<InputField>(client, "_roomInput").transform.parent.GetComponentsInChildren<InputField>()
            .Single(field => field.name == "Nickname");

    private static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);

    private static void SetField(SyncTestClient client, string name, object value) =>
        typeof(SyncTestClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, value);

    private static void CheckMapPreview(SyncTestClient client, GameObject lab)
    {
        GameObject flat = client.transform.Find("Flat map").gameObject;
        MethodInfo select = typeof(SyncTestClient).GetMethod("SelectMap", BindingFlags.Instance | BindingFlags.NonPublic);
        select.Invoke(client, new object[] { WorldMapId.TeamDeathmatch });
        CheckArena(client, flat, lab);

        GameObject arena = client.transform.Find("TeamDeathmatch").gameObject;
        select.Invoke(client, new object[] { WorldMapId.Flat });
        if (!flat.activeSelf || lab.activeSelf || arena.activeSelf)
        {
            throw new InvalidOperationException("Flat preview selected the wrong map.");
        }
        client.Disconnect();
        if (!lab.activeSelf || flat.activeSelf || arena.activeSelf)
        {
            throw new InvalidOperationException("Disconnect did not restore the PhysicsLab preview.");
        }
        Debug.Log("PASS offline Unity map switching and disconnect preview");
    }

    private static void CheckArena(SyncTestClient client, GameObject flat, GameObject lab)
    {
        Transform arena = client.transform.Find("TeamDeathmatch");
        if (arena == null || !arena.gameObject.activeSelf || flat.activeSelf || lab.activeSelf)
        {
            throw new InvalidOperationException("TeamDeathmatch selected the wrong map.");
        }
        int meshCount = CheckArenaMeshes(arena);
        CheckExpandedArenaFloor(arena);
        CheckArenaFeatureCollisions(arena);
        CheckJumpCourseCollisions(arena);
        client.SendMessage("LateUpdate");
        var camera = Field<Transform>(client, "_viewCamera").GetComponent<Camera>();
        Color32[] visible = RenderCamera(camera);
        arena.gameObject.SetActive(false);
        Color32[] background = RenderCamera(camera);
        arena.gameObject.SetActive(true);
        int pixels = visible.Zip(background, (a, b) => !a.Equals(b)).Count(changed => changed);
        if (pixels < 100)
        {
            throw new InvalidOperationException("TeamDeathmatch geometry missing from camera: " + pixels);
        }
        Debug.Log($"PASS TeamDeathmatch rendering, {meshCount} shared JSON meshes and {pixels} visible camera pixels");
    }

    private static int CheckArenaMeshes(Transform arena)
    {
        TextAsset asset = Resources.Load<TextAsset>("TeamDeathmatch");
        if (asset == null)
        {
            throw new InvalidOperationException("TeamDeathmatch JSON is missing.");
        }

        using var stream = new MemoryStream(asset.bytes);
        var data = (ArenaAsset)new DataContractJsonSerializer(typeof(ArenaAsset)).ReadObject(stream);
        if (data.Parts.Length == 0 ||
            arena.GetComponentsInChildren<MeshCollider>().Length != data.Parts.Length ||
            arena.GetComponentsInChildren<MeshFilter>().Length != data.Parts.Length)
        {
            throw new InvalidOperationException("TeamDeathmatch scene mesh counts disagree with the installed JSON.");
        }

        foreach (ArenaPart part in data.Parts)
        {
            Transform visual = arena.Find(part.Name);
            Mesh mesh = visual?.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null || mesh != visual.GetComponent<MeshCollider>()?.sharedMesh ||
                visual.GetComponent<MeshRenderer>() == null)
            {
                throw new InvalidOperationException($"{part.Name} is missing shared visual and collision geometry.");
            }

            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            if (vertices.Length != part.Vertices.Length || triangles.Length != part.Triangles.Length * 3)
            {
                throw new InvalidOperationException($"{part.Name} mesh topology disagrees with the installed JSON.");
            }
            for (int index = 0; index < vertices.Length; index++)
            {
                float[] point = part.Vertices[index];
                if (vertices[index] != new Vector3(point[0], point[1], point[2]))
                {
                    throw new InvalidOperationException($"{part.Name} vertex {index} differs from the installed JSON.");
                }
            }
            for (int index = 0; index < part.Triangles.Length; index++)
            {
                int[] face = part.Triangles[index];
                int offset = index * 3;
                // The exported front face uses the opposite index order from the Unity mesh.
                if (triangles[offset] != face[0] || triangles[offset + 1] != face[2] || triangles[offset + 2] != face[1])
                {
                    throw new InvalidOperationException($"{part.Name} triangle {index} has unexpected indices or winding.");
                }
            }
        }
        return data.Parts.Length;
    }

    private static void CheckArenaFeatureCollisions(Transform arena)
    {
        MeshCollider arch = arena.Find("EastArch")?.GetComponent<MeshCollider>();
        MeshCollider bridge = arena.Find("EastNarrowBridge")?.GetComponent<MeshCollider>();
        MeshCollider floor = arena.Find("Floor")?.GetComponent<MeshCollider>();
        if (arch == null || bridge == null || floor == null)
        {
            throw new InvalidOperationException("TeamDeathmatch is missing the curved arch, narrow bridge or landing floor.");
        }

        Physics.SyncTransforms();
        CheckArchRay(arch, ARENA_ROUTE_X_METERS, 1.8f, shouldHit: false);
        CheckArchRay(arch, ARENA_ROUTE_X_METERS, 2.9f, shouldHit: true);
        CheckArchRay(arch, 5.55f, 1f, shouldHit: false);
        CheckArchRay(arch, 5.55f, 2.45f, shouldHit: true);
        CheckArchRay(arch, 5.98f, 1f, shouldHit: true);

        CheckArenaSurface(bridge, new Vector3(ARENA_ROUTE_X_METERS, 4f, -4f), Vector3.down, expectedHeightMeters: 1.1f);
        CheckArenaSurface(bridge, new Vector3(ARENA_ROUTE_X_METERS, 4f, 0f), Vector3.down, expectedHeightMeters: 2.2f);
        CheckArenaSurface(bridge, new Vector3(ARENA_ROUTE_X_METERS, 4f, 6f), Vector3.down, expectedHeightMeters: 1.1f);
        CheckArenaSurface(bridge, new Vector3(ARENA_ROUTE_X_METERS, 0.5f, 0f), Vector3.up, expectedHeightMeters: 1.95f);
        CheckArenaSurface(floor, new Vector3(3.5f, 4f, 0f), Vector3.down, expectedHeightMeters: 0f);
        CheckArenaSurface(floor, new Vector3(5.5f, 4f, 0f), Vector3.down, expectedHeightMeters: 0f);
        Debug.Log("PASS TeamDeathmatch Unity collision: arch aperture, curved crown and jamb; ramps, deck, underside and fall flanks");
    }

    private static void CheckExpandedArenaFloor(Transform arena)
    {
        MeshCollider floor = RequireArenaCollider(arena, "Floor");
        Physics.SyncTransforms();
        if (Mathf.Abs(floor.bounds.size.x - ARENA_WIDTH_METERS) > ARENA_COLLISION_TOLERANCE_METERS ||
            Mathf.Abs(floor.bounds.size.z - ARENA_LENGTH_METERS) > ARENA_COLLISION_TOLERANCE_METERS)
        {
            throw new InvalidOperationException($"The arena floor has the wrong dimensions: {floor.bounds.size}.");
        }

        foreach (float xMeters in new[] { -14f, 14f })
        {
            foreach (float zMeters in new[] { -17f, 17f })
            {
                CheckArenaSurface(floor, new Vector3(xMeters, 4f, zMeters), Vector3.down, 0f);
            }
        }
        Debug.Log("PASS 32 x 38 m Unity arena floor and expanded corners");
    }

    private static void CheckJumpCourseCollisions(Transform arena)
    {
        const float COURSE_X_METERS = -10.4f;
        const float RAY_START_HEIGHT_METERS = 4f;
        const float EDGE_INSET_METERS = 0.1f;
        var blocks = new (string Name, float StartZMeters, float EndZMeters, float HeightMeters)[]
        {
            ("JumpBlock01", -12f, -10f, 0.75f),
            ("JumpBlock02", -7.9f, -5.9f, 1f),
            ("JumpBlock03", -2.9f, -0.9f, 1.35f),
            ("JumpBlock04", 3.1f, 5.1f, 1.15f),
            ("JumpBlock05", 9.75f, 11.75f, 1.4f)
        };
        MeshCollider floor = RequireArenaCollider(arena, "Floor");
        MeshCollider entry = RequireArenaCollider(arena, "JumpEntryRamp");
        MeshCollider exit = RequireArenaCollider(arena, "JumpExitRamp");

        CheckArenaSurface(entry, new Vector3(COURSE_X_METERS, RAY_START_HEIGHT_METERS, -13.075f), Vector3.down, 0.375f);
        CheckArenaSurface(exit, new Vector3(COURSE_X_METERS, RAY_START_HEIGHT_METERS, 12.975f), Vector3.down, 0.7f);
        CheckArenaSurface(floor, new Vector3(COURSE_X_METERS, RAY_START_HEIGHT_METERS, -14.4f), Vector3.down, 0f);
        CheckArenaSurface(floor, new Vector3(COURSE_X_METERS, RAY_START_HEIGHT_METERS, 14.45f), Vector3.down, 0f);

        for (int index = 0; index < blocks.Length; index++)
        {
            var block = blocks[index];
            MeshCollider collider = RequireArenaCollider(arena, block.Name);
            CheckArenaSurface(collider, new Vector3(COURSE_X_METERS, RAY_START_HEIGHT_METERS, block.StartZMeters + EDGE_INSET_METERS),
                Vector3.down, block.HeightMeters);
            CheckArenaSurface(collider, new Vector3(COURSE_X_METERS, RAY_START_HEIGHT_METERS, block.EndZMeters - EDGE_INSET_METERS),
                Vector3.down, block.HeightMeters);

            if (index + 1 < blocks.Length)
            {
                float gapEndZMeters = blocks[index + 1].StartZMeters;
                CheckArenaSurface(floor, new Vector3(COURSE_X_METERS, RAY_START_HEIGHT_METERS, block.EndZMeters + EDGE_INSET_METERS),
                    Vector3.down, 0f);
                CheckArenaSurface(floor, new Vector3(COURSE_X_METERS, RAY_START_HEIGHT_METERS, gapEndZMeters - EDGE_INSET_METERS),
                    Vector3.down, 0f);
            }
        }
        Debug.Log("PASS TeamDeathmatch Unity jump course: five solid blocks, four open gaps to the floor and two ramps");
    }

    private static MeshCollider RequireArenaCollider(Transform arena, string name)
    {
        return arena.Find(name)?.GetComponent<MeshCollider>()
            ?? throw new InvalidOperationException($"TeamDeathmatch is missing the {name} collision mesh.");
    }

    private static void CheckArchRay(MeshCollider arch, float xMeters, float yMeters, bool shouldHit)
    {
        const float APPROACH_Z_METERS = -8.4f;
        const float RAY_LENGTH_METERS = 2f;
        var ray = new Ray(new Vector3(xMeters, yMeters, APPROACH_Z_METERS), Vector3.forward);
        bool hitArch = arch.Raycast(ray, out RaycastHit hit, RAY_LENGTH_METERS);
        if (hitArch != shouldHit ||
            (hitArch && Mathf.Abs(hit.point.z - ARENA_ARCH_FRONT_Z_METERS) > ARENA_COLLISION_TOLERANCE_METERS))
        {
            throw new InvalidOperationException($"Unexpected arch collision at x={xMeters} m, y={yMeters} m.");
        }
    }

    private static void CheckArenaSurface(MeshCollider expected, Vector3 origin, Vector3 direction, float expectedHeightMeters)
    {
        const float RAY_LENGTH_METERS = 5f;
        if (!Physics.Raycast(origin, direction, out RaycastHit hit, RAY_LENGTH_METERS) ||
            hit.collider != expected || Mathf.Abs(hit.point.y - expectedHeightMeters) > ARENA_COLLISION_TOLERANCE_METERS)
        {
            throw new InvalidOperationException($"Expected {expected.name} at y={expectedHeightMeters} m from {origin}, got {hit.collider?.name} at {hit.point}.");
        }
    }

    private static string Describe(SyncTestClient client)
    {
        if (client == null) return "unloaded";
        return Field<string>(client, "_status") + " " + Field<Text>(client, "_sessionText").text
            + " enabled=" + client.isActiveAndEnabled + " frame=" + Time.frameCount + " latest=" + Field<DeathWish.MockClient.Core.ConnectionTestClient>(client, "_client")?.LatestSnapshot?.Players.Count + " children=" + string.Join(",", client.GetComponentsInChildren<MeshRenderer>().Select(r => r.transform.parent.name));
    }

    private static bool HasRemote(SyncTestClient viewer, SyncTestClient other)
    {
        return viewer.transform.Find("Player " + Field<long>(other, "_playerId")) != null;
    }

    private static void CheckCylinderBody(Transform player)
    {
        Transform body = player.Find("Body");
        MeshFilter filter = body?.GetComponent<MeshFilter>();
        Mesh cylinder = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
        if (cylinder == null || filter?.sharedMesh != cylinder)
        {
            throw new InvalidOperationException("Player rendering does not use the Unity cylinder mesh.");
        }
        Vector3 expectedSize = new Vector3(PLAYER_DIAMETER_METERS, PLAYER_HEIGHT_METERS, PLAYER_DIAMETER_METERS);
        Vector3 actualSize = Vector3.Scale(cylinder.bounds.size, body.localScale);
        Vector3 center = body.localPosition + Vector3.Scale(cylinder.bounds.center, body.localScale);
        if (Vector3.Distance(actualSize, expectedSize) > 0.01f ||
            Vector3.Distance(center, Vector3.up * (PLAYER_HEIGHT_METERS / 2)) > 0.01f)
        {
            throw new InvalidOperationException($"Player cylinder has size {actualSize} and local center {center}.");
        }
    }

    private static void CheckLocalWeaponPixels(Camera camera, Transform worldGun)
    {
        Transform gun = worldGun.gameObject.activeInHierarchy
            ? worldGun
            : camera.transform.Find("Close camera weapon");
        Color32[] shown = RenderCamera(camera);
        gun.gameObject.SetActive(false);
        Color32[] hidden;
        try
        {
            hidden = RenderCamera(camera);
        }
        finally
        {
            gun.gameObject.SetActive(true);
        }
        int pixels = shown.Zip(hidden, (a, b) => !a.Equals(b)).Count(changed => changed);
        if (pixels < 30)
        {
            throw new InvalidOperationException("Local gun is not clearly visible in the arena camera: " + pixels);
        }
        Debug.Log("PASS local arena weapon visible pixels " + pixels);
    }

    private static void CheckRemotePixels(SyncTestClient viewer, SyncTestClient other)
    {
        var otherRenderers = other.GetComponentsInChildren<MeshRenderer>();
        foreach (var renderer in otherRenderers)
        {
            renderer.enabled = false;
        }
        viewer.SendMessage("LateUpdate");
        var camera = Field<Transform>(viewer, "_viewCamera").GetComponent<Camera>();
        var remote = viewer.transform.Find("Player " + Field<long>(other, "_playerId")).GetComponentInChildren<MeshRenderer>();
        Color32[] shown = RenderCamera(camera);
        remote.enabled = false;
        Color32[] hidden = RenderCamera(camera);
        remote.enabled = true;
        foreach (var renderer in otherRenderers)
        {
            renderer.enabled = true;
        }
        int pixels = shown.Zip(hidden, (a, b) => !a.Equals(b)).Count(changed => changed);
        if (pixels < 10)
        {
            throw new InvalidOperationException("Remote player missing from camera: " + pixels);
        }
        Debug.Log("PASS remote player visible pixels " + pixels);
    }

    private static Color32[] RenderCamera(Camera camera = null)
    {
        camera = camera ?? Camera.main;
        var previous = RenderTexture.active;
        var original = camera.targetTexture;
        var target = RenderTexture.GetTemporary(320, 180, 24);
        var image = new Texture2D(target.width, target.height);
        try
        {
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            return image.GetPixels32();
        }
        finally
        {
            camera.targetTexture = original;
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            UnityEngine.Object.Destroy(image);
        }
    }

    private static T Field<T>(SyncTestClient client, string name)
    {
        return (T)typeof(SyncTestClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
    }

    [DataContract]
    private sealed class ArenaAsset
    {
        [DataMember(Name = "objects", IsRequired = true)] public ArenaPart[] Parts { get; set; }
    }

    [DataContract]
    private sealed class ArenaPart
    {
        [DataMember(Name = "name", IsRequired = true)] public string Name { get; set; }
        [DataMember(Name = "vertices", IsRequired = true)] public float[][] Vertices { get; set; }
        [DataMember(Name = "triangles", IsRequired = true)] public int[][] Triangles { get; set; }
    }
}
