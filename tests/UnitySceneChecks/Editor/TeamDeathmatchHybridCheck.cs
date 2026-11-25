using System;
using System.Globalization;
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
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[InitializeOnLoad]
public static class TeamDeathmatchHybridCheck
{
    private const string RUNNING = "DeathWish.TeamDeathmatchHybridCheck";
    private const string SCENE_PATH = "Assets/DeathWishMovement/Scenes/SyncTestScene.unity";
    private const string RED_NAME = "빨강 Unity";
    private const string BLUE_NAME = "파랑 Unity";
    private const int ROOM_ID = 2;
    private const int DEFAULT_PORT = 17846;
    private const int MOCK_COUNT = MatchState.MAX_PLAYERS - 2;
    private const int WIN_SCORE = 10;
    private const double SHOT_INTERVAL_SECONDS = 0.3;
    private const double CHECK_TIMEOUT_SECONDS = 77;
    private const BindingFlags PRIVATE_MEMBER = BindingFlags.Instance | BindingFlags.NonPublic;

    private static SyncTestClient _red;
    private static SyncTestClient _blue;
    private static Scene _peerScene;
    private static ConnectionTestClient[] _mocks;
    private static CancellationTokenSource _mockDeadline;
    private static Task _joiningMocks;
    private static double _deadlineSeconds;
    private static double _nextShotSeconds;
    private static bool _hasCheckedRoster;
    private static bool _hasCheckedFirstKill;
    private static bool _hasCheckedShotVisuals;
    private static int _port;

    static TeamDeathmatchHybridCheck()
    {
        EditorApplication.update += Check;
    }

    public static void Run()
    {
        EditorSceneManager.OpenScene(SCENE_PATH);
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
            double nowSeconds = EditorApplication.timeSinceStartup;
            if (_red == null)
            {
                _deadlineSeconds = nowSeconds + CHECK_TIMEOUT_SECONDS;
                _port = int.TryParse(Environment.GetEnvironmentVariable("DW_UNITY_HYBRID_PORT"), out int configuredPort)
                    ? configuredPort : DEFAULT_PORT;
                _red = UnityEngine.Object.FindFirstObjectByType<SyncTestClient>();
                Join(_red, RED_NAME);
                return;
            }
            Require(nowSeconds <= _deadlineSeconds, Describe());
            MatchState redMatch = Field<MatchState>(_red, "_matchState");
            if (redMatch == null)
            {
                Require(Field<ConnectionTestClient>(_red, "_client") != null, Describe());
                return;
            }

            if (_blue == null)
            {
                if (!_peerScene.IsValid())
                {
                    Require(redMatch.Phase == MatchPhase.Waiting && redMatch.Players.Count == 1,
                        "The first named Unity player must wait alone.");
                    _peerScene = EditorSceneManager.LoadSceneInPlayMode(SCENE_PATH, new LoadSceneParameters(LoadSceneMode.Additive));
                    return;
                }
                if (_peerScene.isLoaded)
                {
                    GameObject[] roots = _peerScene.GetRootGameObjects();
                    foreach (EventSystem duplicate in roots.SelectMany(root => root.GetComponentsInChildren<EventSystem>()))
                    {
                        duplicate.enabled = false;
                    }
                    _blue = roots.SelectMany(root => root.GetComponentsInChildren<SyncTestClient>()).Single();
                    Join(_blue, BLUE_NAME);
                }
                return;
            }

            MatchState blueMatch = Field<MatchState>(_blue, "_matchState");
            if (blueMatch == null)
            {
                Require(Field<ConnectionTestClient>(_blue, "_client") != null, Describe());
                return;
            }
            if (_joiningMocks == null)
            {
                Require(blueMatch.Phase == MatchPhase.Waiting && blueMatch.Players.Count == 2,
                    "Both Unity clients must wait before mock admission.");
                _mockDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                _mocks = Enumerable.Range(0, MOCK_COUNT).Select(_ => new ConnectionTestClient()).ToArray();
                _joiningMocks = JoinMocksAsync(_mockDeadline.Token);
                return;
            }
            if (!_joiningMocks.IsCompleted)
            {
                return;
            }
            _joiningMocks.GetAwaiter().GetResult();

            if (!_hasCheckedRoster)
            {
                if (redMatch.Phase != MatchPhase.Playing || blueMatch.Phase != MatchPhase.Playing ||
                    redMatch.Players.Any(player => player.PingMs < 0) || blueMatch.Players.Any(player => player.PingMs < 0))
                {
                    return;
                }
                Require(redMatch.Players.Single(player => player.Nickname == RED_NAME).Team == TeamId.Red &&
                    blueMatch.Players.Single(player => player.Nickname == BLUE_NAME).Team == TeamId.Blue,
                    "The actual Unity clients must be on opposing teams.");
                CheckBoard(_red, redMatch);
                CheckBoard(_blue, blueMatch);
                Require(HudText(_red, "Match summary/Score") == HudText(_blue, "Match summary/Score"),
                    "The two Unity score displays differ at match start.");
                _hasCheckedRoster = true;
                Debug.Log("PASS hybrid live TCP: two Unity clients and eight mock sessions, balanced 5v5, matching scoreboard and measured RTT");
            }

            if (!_hasCheckedShotVisuals && Field<ArenaShotOutcome>(_red, "_shotOutcome") == ArenaShotOutcome.Damage &&
                _blue.GetComponentsInChildren<LineRenderer>().Any(line => line.name == "BulletTrail"))
            {
                Require(_red.GetComponentsInChildren<MeshRenderer>().Any(renderer => renderer.name == "Muzzle collar"),
                    "The Unity player weapons should contain a visible muzzle collar.");
                _hasCheckedShotVisuals = true;
                Debug.Log("PASS hybrid live TCP: confirmed damage and a remote shot trail in Unity");
            }

            if (!_hasCheckedFirstKill && redMatch.RedScore == 1 && blueMatch.RedScore == 1)
            {
                string redFeed = HudText(_red, "Kill feed");
                string blueFeed = HudText(_blue, "Kill feed");
                if (redFeed.Contains(RED_NAME) && redFeed.Contains(BLUE_NAME) && redFeed == blueFeed)
                {
                    _hasCheckedFirstKill = true;
                    CheckBoard(_red, redMatch);
                    CheckBoard(_blue, blueMatch);
                    Debug.Log("PASS hybrid live TCP: both Unity clients show the same kill log, score and K/D/A");
                }
            }

            if (redMatch.Phase == MatchPhase.Finished && blueMatch.Phase == MatchPhase.Finished)
            {
                Require(_hasCheckedFirstKill && _hasCheckedShotVisuals &&
                    redMatch.RedScore == WIN_SCORE && blueMatch.RedScore == WIN_SCORE &&
                    redMatch.BlueScore == 0 && blueMatch.BlueScore == 0 && redMatch.Winner == TeamId.Red && blueMatch.Winner == TeamId.Red,
                    "The two Unity clients must finish with the same 10-point Red victory.");
                Require(HudText(_red, "Match summary/Phase").Contains("Red wins") &&
                    HudText(_red, "Match summary/Phase") == HudText(_blue, "Match summary/Phase"),
                    "The two Unity winner labels differ.");
                CheckBoard(_red, redMatch);
                CheckBoard(_blue, blueMatch);
                Debug.Log("PASS hybrid live TCP: real kill and respawn cycles, Red wins 10-0 on both Unity clients");
                Finish(0);
                return;
            }

            long blueId = Field<long>(_blue, "_playerId");
            if (redMatch.Phase == MatchPhase.Playing && nowSeconds >= _nextShotSeconds &&
                redMatch.Players.Single(player => player.PlayerId == blueId).Health > 0)
            {
                QueueShot(_red);
                _nextShotSeconds = nowSeconds + SHOT_INTERVAL_SECONDS;
            }
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            Finish(1);
        }
    }

    private static void Join(SyncTestClient client, string nickname)
    {
        Field<InputField>(client, "_hostInput").text = "127.0.0.1";
        Field<InputField>(client, "_portInput").text = _port.ToString(CultureInfo.InvariantCulture);
        InputField room = Field<InputField>(client, "_roomInput");
        room.text = ROOM_ID.ToString(CultureInfo.InvariantCulture);
        room.transform.parent.GetComponentsInChildren<InputField>().Single(field => field.name == "Nickname").text = nickname;
        client.Connect();
    }

    private static async Task JoinMocksAsync(CancellationToken token)
    {
        for (int index = 0; index < _mocks.Length; index++)
        {
            ConnectionTestClient client = _mocks[index];
            await client.ConnectAsync("127.0.0.1", _port, token).ConfigureAwait(false);
            JoinRoomResponse response = await client.JoinMatchAsync(ROOM_ID, "Mock " + (index + 1), token).ConfigureAwait(false);
            Require(response.Result == RoomJoinResult.Success, $"Mock {index + 1} admission failed: {response.Result}.");
            JoinAck ack = await client.WaitForAdmissionAsync(token).ConfigureAwait(false);
            Require(ack.MapId == WorldMapId.TeamDeathmatch, $"Mock {index + 1} received the wrong map.");
        }
    }

    private static void CheckBoard(SyncTestClient client, MatchState state)
    {
        Require(state.Players.Count(player => player.Team == TeamId.Red) == MatchState.MAX_TEAM_SIZE &&
            state.Players.Count(player => player.Team == TeamId.Blue) == MatchState.MAX_TEAM_SIZE, "Expected 5 players on each team.");
        MethodInfo showBoard = typeof(SyncTestClient).GetMethod("SetScoreboardOpen", PRIVATE_MEMBER);
        showBoard.Invoke(client, new object[] { true });
        Transform board = Hud(client).Find("Scoreboard");
        Require(board.gameObject.activeInHierarchy, "Scoreboard did not open.");
        foreach (MatchPlayer player in state.Players)
        {
            Transform name = board.Find(player.Team + " team").GetComponentsInChildren<Text>()
                .Single(text => text.name == "Nickname" && text.text == player.Nickname).transform;
            string ping = player.PingMs == MatchState.UNKNOWN_PING_MS
                ? "\u2014" : player.PingMs.ToString(CultureInfo.InvariantCulture);
            Require(name.parent.Find("K").GetComponent<Text>().text == player.Kills.ToString(CultureInfo.InvariantCulture) &&
                name.parent.Find("D").GetComponent<Text>().text == player.Deaths.ToString(CultureInfo.InvariantCulture) &&
                name.parent.Find("A").GetComponent<Text>().text == player.Assists.ToString(CultureInfo.InvariantCulture) &&
                name.parent.Find("Ping (ms)").GetComponent<Text>().text == ping,
                $"The score row differs for {player.Nickname}.");
        }
        showBoard.Invoke(client, new object[] { false });
    }

    private static void QueueShot(SyncTestClient client)
    {
        // The two earliest players spawn on the open west lane, both at X = -6 m.
        var request = new CameraRay(System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ);
        object actions = Field<object>(client, "_actions");
        MethodInfo enqueue = actions.GetType().GetMethod("Enqueue");
        Type shotType = Nullable.GetUnderlyingType(enqueue.GetParameters()[0].ParameterType);
        Vector3 origin = new Vector3(-6f, 1f, -12f);
        object shot = Activator.CreateInstance(shotType, request, new Ray(origin, Vector3.forward), origin);
        enqueue.Invoke(actions, new[] { shot });
    }

    private static Transform Hud(SyncTestClient client) => Field<InputField>(client, "_roomInput")
        .GetComponentInParent<Canvas>().transform.Find("Team deathmatch HUD");

    private static string HudText(SyncTestClient client, string path) => Hud(client).Find(path).GetComponent<Text>().text;

    private static T Field<T>(SyncTestClient client, string name) =>
        (T)typeof(SyncTestClient).GetField(name, PRIVATE_MEMBER).GetValue(client);

    private static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new InvalidOperationException(reason);
        }
    }

    private static string Describe() => $"Timed out: Red status={Field<string>(_red, "_status")}, " +
        $"Blue status={(_blue == null ? "not loaded" : Field<string>(_blue, "_status"))}, " +
        $"score={Field<MatchState>(_red, "_matchState")?.RedScore}.";

    private static void Finish(int exitCode)
    {
        SessionState.SetBool(RUNNING, false);
        _mockDeadline?.Cancel();
        if (_mocks != null)
        {
            foreach (ConnectionTestClient mock in _mocks)
            {
                mock.Dispose();
            }
        }
        _mockDeadline?.Dispose();
        _blue?.Disconnect();
        _red?.Disconnect();
        EditorApplication.Exit(exitCode);
    }
}
