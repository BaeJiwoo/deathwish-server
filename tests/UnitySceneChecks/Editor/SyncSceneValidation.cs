using System;
using System.Collections.Generic;
using System.Reflection;
using DeathWish.Client;
using DeathWish.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DeathWish.UnitySceneChecks
{
    public static class SyncSceneValidation
    {
        [MenuItem("DeathWish/Validate Sync Test Scene")]
        public static void Run()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            EditorSceneManager.OpenScene("Assets/DeathWishMovement/Scenes/SyncTestScene.unity");
            var clients = UnityEngine.Object.FindObjectsByType<SyncTestClient>(FindObjectsSortMode.None);
            Require(clients.Length == 1, "one preplaced client component");
            Require(UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None).Length == 1, "one preplaced Canvas");
            Require(UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length == 1, "preplaced EventSystem");
            Require(Camera.main != null, "tagged main camera");
            Require(Resources.Load<Shader>("SyncTestWorld")?.isSupported == true, "supported world shader");

            var serialized = new SerializedObject(clients[0]);
            foreach (string name in new[] { "_hostInput", "_portInput", "_roomInput", "_connectButton", "_captureButton",
                "_disconnectButton", "_statusText", "_sessionText", "_positionText", "_inputText" })
            {
                Require(serialized.FindProperty(name)?.objectReferenceValue != null, "serialized Canvas reference " + name);
            }

            foreach (Button button in UnityEngine.Object.FindObjectsByType<Button>(FindObjectsSortMode.None))
            {
                Require(button.onClick.GetPersistentEventCount() == 1, "persistent button event " + button.name);
                Require(button.onClick.GetPersistentTarget(0) == clients[0], "button target " + button.name);
                string method = button.onClick.GetPersistentMethodName(0);
                Require(typeof(SyncTestClient).GetMethod(method, BindingFlags.Public | BindingFlags.Instance) != null,
                    "button method " + method);
            }

            foreach (Text label in UnityEngine.Object.FindObjectsByType<Text>(FindObjectsSortMode.None))
            {
                Require(label.font != null && label.font == font, "Unity 6 font " + label.name);
            }
            foreach (InputField input in UnityEngine.Object.FindObjectsByType<InputField>(FindObjectsSortMode.None))
            {
                Require(input.textComponent != null, "input text " + input.name);
            }
            Debug.Log("PASS SyncTestScene: imported Canvas, fonts, input fields, button wiring, camera and shader.");
        }

        [MenuItem("DeathWish/Validate Snapshot Display (Play Mode)")]
        public static void VerifySnapshotDisplay()
        {
            Require(EditorApplication.isPlaying, "enter Play mode before snapshot display validation");
            SyncTestClient client = UnityEngine.Object.FindFirstObjectByType<SyncTestClient>();
            Require(client != null, "scene client in Play mode");
            var serialized = new SerializedObject(client);
            var positionLabel = (Text)serialized.FindProperty("_positionText").objectReferenceValue;
            var sessionLabel = (Text)serialized.FindProperty("_sessionText").objectReferenceValue;
            Type type = typeof(SyncTestClient);
            const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.NonPublic;
            var positions = (Dictionary<long, Vector3>)type.GetField("_positions", FLAGS).GetValue(client);
            var markers = (Dictionary<long, Transform>)type.GetField("_markers", FLAGS).GetValue(client);
            Camera camera = Camera.main;
            Bounds floor = client.transform.Find("Grid floor").GetComponent<MeshRenderer>().bounds;
            Require(Vector3.Distance(floor.min, new Vector3(-50, 0, -50)) < 0.001f &&
                Vector3.Distance(floor.max, new Vector3(50, 0, 50)) < 0.001f, "server floor bounds");
            var sample = new WorldSnapshot(1, 11, new[]
            {
                new SnapshotPlayer(1, 1.25f, 0, -2.5f),
                new SnapshotPlayer(2, 6, -1, 2)
            });
            double now = Time.unscaledTimeAsDouble;
            Action render = () => type.GetMethod("RenderWorld", FLAGS).Invoke(client, new object[] { now });
            Action<WorldSnapshot> apply = snapshot =>
            {
                type.GetField("_playerId", FLAGS).SetValue(client, 1L);
                type.GetMethod("ApplySnapshot", FLAGS).Invoke(client, new object[] { snapshot, now });
                type.GetMethod("RefreshView", FLAGS).Invoke(client, null);
                render();
            };

            apply(sample);
            Require(positions.Count == 2 && positions[1] == new Vector3(1.25f, 0, -2.5f), "exact local snapshot position");
            Require(positionLabel.text.Contains("X: 1.250") && positionLabel.text.Contains("Z: -2.500"), "local coordinate UI");
            Require(sessionLabel.text.Contains("Players: 2") && sessionLabel.text.Contains("Tick: 11"), "snapshot telemetry");
            Require(markers.Count == 2 && markers[1].position == positions[1] && markers[2].position == positions[2],
                "local and remote meshes use exact server positions");
            Require(markers[1].GetComponentInChildren<MeshRenderer>().sharedMaterial.color !=
                markers[2].GetComponentInChildren<MeshRenderer>().sharedMaterial.color, "distinct marker colors");
            Vector3 viewportPosition = camera.WorldToViewportPoint(positions[1] + Vector3.up * 0.9f);
            Require(viewportPosition.x > 0.57f && viewportPosition.x < 1f && viewportPosition.y > 0f &&
                viewportPosition.y < 1f && viewportPosition.z > 0f, "local player framed to the right of the panel");
            Vector3 previousCameraPosition = camera.transform.position;
            Transform remoteMarker = markers[2];
            apply(new WorldSnapshot(1, 12, new[] { new SnapshotPlayer(1, 4.5f, -2f, 6.25f), new SnapshotPlayer(2, 8, -3, 2) }));
            now += 0.05;
            render();
            Vector3 midpoint = markers[1].position;
            Require(midpoint == new Vector3(2.875f, -1, 1.875f) && markers[2].position == new Vector3(7, -2, 2),
                "local and remote markers interpolate halfway");
            Require(Vector3.Distance(camera.transform.position - previousCameraPosition, midpoint - new Vector3(1.25f, 0, -2.5f)) < 0.001f,
                "camera follows the blended local marker");
            apply(new WorldSnapshot(1, 13, new[] { new SnapshotPlayer(1, 4.5f, -2f, 6.25f) }));
            Require(markers[1].position == midpoint, "early update preserves display continuity");
            now += 1;
            render();
            Require(markers[1].position == positions[1], "late packets hold the last authoritative target");
            Require(positions.Count == 1 && !positions.ContainsKey(2), "departure removes remote marker");
            Require(markers.Count == 1 && !remoteMarker.gameObject.activeSelf, "departed mesh is hidden");
            Require(Vector3.Distance(camera.transform.position - previousCameraPosition,
                new Vector3(3.25f, -2f, 8.75f)) < 0.001f, "camera follows local server movement");
            type.GetField("_yawDegrees", FLAGS).SetValue(client, 90f);
            render();
            Require(Quaternion.Angle(camera.transform.rotation, Quaternion.Euler(22f, 90f, 0f)) < 0.1f,
                "camera orbits with mouse yaw");
            Require(positionLabel.text.Contains("X: 4.500") && positionLabel.text.Contains("Y: -2.000") &&
                positionLabel.text.Contains("Z: 6.250"), "updated local coordinate UI");
            client.Disconnect();
            Require(positions.Count == 0 && markers.Count == 0 && positionLabel.text.Contains("X: --"),
                "disconnect clears markers and local position");
            apply(sample);
            SceneView.RepaintAll();
            Debug.Log("PASS synthetic snapshot display: local and remote meshes, camera tracking and orbit, telemetry, departure and disconnect reset.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("SyncTestScene validation failed: " + message);
            }
        }
    }
}
