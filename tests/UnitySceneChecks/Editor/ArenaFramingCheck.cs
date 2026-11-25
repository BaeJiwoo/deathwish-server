using System;
using System.Reflection;
using DeathWish.Client;
using DeathWish.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class ArenaFramingCheck
{
    private const string RUNNING = "DeathWish.ArenaFramingCheck";
    private const string SCENE_PATH = "Assets/DeathWishMovement/Scenes/SyncTestScene.unity";
    private const int ROOM_ID = 2;
    private const long PLAYER_ID = 1;
    private const long SERVER_TICK = 1;
    private const float CHEST_HEIGHT_METERS = 1.17f;
    private const float PITCH_DEGREES = 8f;
    private const float YAW_DEGREES = 0f;
    private const float ASPECT_16_BY_9 = 16f / 9f;
    private const BindingFlags PRIVATE_MEMBER = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly string[] CameraSettingNames = { "_sideMeters", "_heightMeters", "_distanceMeters" };
    private static readonly Vector3 Spawn = new Vector3(-6f, 0f, -10f);

    static ArenaFramingCheck()
    {
        EditorApplication.update += Check;
    }

    public static void Run()
    {
        EditorSceneManager.OpenScene(SCENE_PATH);
        var client = UnityEngine.Object.FindFirstObjectByType<SyncTestClient>();
        var serialized = new SerializedObject(client);
        foreach (string name in CameraSettingNames)
        {
            SessionState.SetFloat(RUNNING + name, serialized.FindProperty("_shoulderCamera." + name).floatValue);
        }
        SessionState.SetBool(RUNNING, true);
        EditorApplication.EnterPlaymode();
    }

    private static void Check()
    {
        if (!SessionState.GetBool(RUNNING, false) || !EditorApplication.isPlaying)
        {
            return;
        }

        SessionState.SetBool(RUNNING, false);
        int exitCode = 0;
        var client = UnityEngine.Object.FindFirstObjectByType<SyncTestClient>();
        try
        {
            Require(client != null && Camera.main != null, "Missing scene client or main camera.");
            VerifyFraming(client, Camera.main);
            client.Disconnect();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            exitCode = 1;
        }
        finally
        {
            EditorApplication.Exit(exitCode);
        }
    }

    private static void VerifyFraming(SyncTestClient client, Camera camera)
    {
        object settings = typeof(SyncTestClient).GetField("_shoulderCamera", PRIVATE_MEMBER).GetValue(client);
        foreach (string name in CameraSettingNames)
        {
            // Exercise the scene's serialized defaults without changing any saved player preferences.
            SetField(settings, name, SessionState.GetFloat(RUNNING + name, float.NaN));
        }

        Call(client, "SelectMap", WorldMapId.TeamDeathmatch);
        SetField(client, "_mapId", WorldMapId.TeamDeathmatch);
        SetField(client, "_playerId", PLAYER_ID);
        SetField(client, "_pitchDegrees", PITCH_DEGREES);
        SetField(client, "_yawDegrees", YAW_DEGREES);
        Call(client, "ApplyMatchState", new MatchState(ROOM_ID, SERVER_TICK, MatchPhase.Playing, 0, 0, null, new[]
        {
            new MatchPlayer(PLAYER_ID, "Framing", TeamId.Red, MatchState.MAX_HEALTH, 0, 0, 0, MatchState.UNKNOWN_PING_MS)
        }));
        double now = Time.unscaledTimeAsDouble;
        Call(client, "ApplySnapshot", new WorldSnapshot(ROOM_ID, SERVER_TICK, new[]
        {
            new SnapshotPlayer(PLAYER_ID, Spawn.x, Spawn.y, Spawn.z)
        }), now);

        client.transform.Find("TeamDeathmatch").gameObject.SetActive(false);
        Physics.SyncTransforms();
        var aimRotation = Quaternion.Euler(PITCH_DEGREES, YAW_DEGREES, 0f);
        foreach (float aspect in new[] { 4f / 3f, ASPECT_16_BY_9, 21f / 9f })
        {
            camera.aspect = aspect;
            foreach (bool isLeftShoulder in new[] { false, true })
            {
                SetField(client, "_isLeftShoulder", isLeftShoulder);
                Call(client, "RenderWorld", now);
                VerifyOpenShoulder(client, camera, aimRotation, isLeftShoulder);
            }
        }
        Debug.Log("PASS arena shoulder framing and centered aim at 4:3, 16:9 and 21:9 with the gun on the player's right");

        camera.aspect = ASPECT_16_BY_9;
        VerifyCloseWeapon(client, camera, aimRotation, now);
        Debug.Log("PASS arena camera obstruction preserves aim and puts the close gun on the right for both shoulders");
    }

    private static void VerifyOpenShoulder(SyncTestClient client, Camera camera, Quaternion aimRotation, bool isLeftShoulder)
    {
        var player = client.transform.Find($"Me {PLAYER_ID}");
        Require(player != null, "Local player marker is missing.");
        var chest = camera.WorldToViewportPoint(player.position + Vector3.up * CHEST_HEIGHT_METERS);
        float inset = isLeftShoulder ? 1f - chest.x : chest.x;
        Require(inset is >= 0.14f and <= 0.18f && chest.y > 0f && chest.y < 1f && chest.z > 0f,
            $"Chest projection {chest} is outside the expected shoulder band at aspect {camera.aspect:F3}.");
        VerifyAim(camera, aimRotation);

        Transform gun = player.Find("Gun");
        Require(gun != null && gun.gameObject.activeInHierarchy && player.InverseTransformPoint(gun.position).x > 0f,
            "The visible world gun must stay on the player's right when the shoulder changes.");
        VerifyGunMesh(gun);
    }

    private static void VerifyCloseWeapon(SyncTestClient client, Camera camera, Quaternion aimRotation, double now)
    {
        Call(client, "RenderWorld", now);
        float openDistanceMeters = Vector3.Distance(Spawn, camera.transform.position);
        var wall = new GameObject("Framing test wall");
        wall.transform.position = Spawn + new Vector3(0f, 1.5f, -0.9f);
        wall.AddComponent<BoxCollider>().size = new Vector3(6f, 3f, 0.2f);
        Physics.SyncTransforms();
        try
        {
            foreach (bool isLeftShoulder in new[] { false, true })
            {
                SetField(client, "_isLeftShoulder", isLeftShoulder);
                Call(client, "RenderWorld", now);
                Require(Vector3.Distance(Spawn, camera.transform.position) < openDistanceMeters - 0.5f,
                    "The obstacle must shorten the arena camera boom.");
                VerifyAim(camera, aimRotation);

                Transform gun = camera.transform.Find("Close camera weapon");
                Transform worldGun = client.transform.Find($"Me {PLAYER_ID}/Gun");
                Require(gun != null && gun.gameObject.activeInHierarchy && gun.localPosition.x > 0f &&
                    worldGun != null && !worldGun.gameObject.activeInHierarchy,
                    "The right-side close camera gun must replace the world gun for both shoulders.");
                VerifyGunMesh(gun);
            }
        }
        finally
        {
            UnityEngine.Object.Destroy(wall);
        }
    }

    private static void VerifyAim(Camera camera, Quaternion expectedRotation)
    {
        Require(Quaternion.Angle(camera.transform.rotation, expectedRotation) < 0.05f,
            "Camera framing changed the look rotation.");
        Ray center = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        Require(Vector3.Dot(center.direction, expectedRotation * Vector3.forward) > 0.9999f,
            "The centered look ray must match the unchanged firing direction.");
    }

    private static void VerifyGunMesh(Transform gun)
    {
        Require(gun.GetComponentInChildren<MeshRenderer>() != null && gun.GetComponentsInChildren<Collider>(true).Length == 0,
            "The gun needs visible geometry without adding a collider.");
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, PRIVATE_MEMBER).SetValue(target, value);
    }

    private static void Call(SyncTestClient client, string name, params object[] arguments)
    {
        typeof(SyncTestClient).GetMethod(name, PRIVATE_MEMBER).Invoke(client, arguments);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
