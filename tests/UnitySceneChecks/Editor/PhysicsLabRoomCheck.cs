using System;
using System.Linq;
using System.Reflection;
using DeathWish.Client;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

[InitializeOnLoad]
public static class PhysicsLabRoomCheck
{
    private const string RUNNING = "DeathWish.PhysicsLabRoomCheck";
    private static double deadline;
    private static int phase;
    private static SyncTestClient first;
    private static SyncTestClient peer;
    private static UnityEngine.SceneManagement.Scene peerScene;

    static PhysicsLabRoomCheck()
    {
        EditorApplication.update += Check;
    }

    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/DeathWishMovement/Scenes/SyncTestScene.unity");
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
                MeshFilter[] meshes = lab.GetComponentsInChildren<MeshFilter>();
                if (flat.activeSelf || !lab.activeSelf || meshes.Length != 7 ||
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
                    peer = peerScene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<SyncTestClient>()).Single();
                    Field<InputField>(peer, "_portInput").text = Field<InputField>(client, "_portInput").text;
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
                client.Disconnect();
                Field<InputField>(client, "_roomInput").text = "1";
                client.Connect();
                phase++;
                return;
            }
            if (!flat.activeSelf || lab.activeSelf)
            {
                throw new InvalidOperationException("Flat room did not restore its floor.");
            }
            Debug.Log("PASS PhysicsLab and flat room live Unity admission, 7 meshes, 616 vertices, 1202 triangles");
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
}