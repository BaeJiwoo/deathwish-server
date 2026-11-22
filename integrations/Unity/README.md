# Unity integration and SyncTestScene

The server owns movement, gravity, collisions, jumps and raycasts. The Unity client renders server snapshots, captures input and displays the result of each camera raycast. For the complete local run with the server, MockClient and Unity, follow the [server README](../../README.md).

## Console

From the server repository root, run the server in one PowerShell window and the MockClient launcher in another.

```powershell
$env:SERVER_ROOM_COUNT = '2'
dotnet run --project src/DeathWish.Server -- 127.0.0.1 17779
```

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\mockclient\Start-MockClient.ps1 -Port 17779
```

In the new Console Host window, enter `/join 2`, then `/wasd`. Hold WASD to move, use the Left/Right arrows to turn, and press Esc to return to commands. For command mode in a VS Code terminal or on Linux, run `dotnet run --project mockclient/DeathWish.MockClient.Console -- 127.0.0.1 17779`, then use `/join 2` and `/move 1 0`.

## Unity installation

The matching Unity client revision already contains the scene, controller and DLLs. To update an older Unity project, close the editor and run this command from the server repository root. The example assumes the client is a sibling directory.

```powershell
powershell -ExecutionPolicy Bypass -File .\integrations\Unity\Install.ps1 -UnityProject ..\deathwish-client
```

The installer builds and copies the .NET Standard 2.1 protocol/Core assemblies, `SyncTestClient.cs`, the world shader resource and their metadata into `Assets/DeathWishMovement`. It installs `Scenes/SyncTestScene.unity` on first setup and preserves existing scene customizations on reinstall. Canvas uses the Unity 6 built-in `com.unity.ugui` 2.0.0 package. Old `ConnectionTest.cs` and `MovementTest.cs` auto-start scripts are backed up as `.cs.disabled` with their metadata.

Keep Active Input Handling set to **Input Manager (Old)** or **Both**. The client uses the existing Mouse X/Y axes, and Canvas navigation uses Horizontal, Vertical, Submit and Cancel. The installer removes the old invalid `com.unity.modules.inputlegacy` reference. Do not import duplicate protocol/Core source or assemblies elsewhere in Assets.

The scene tracked in the client repository starts with port `17779`. On first installation into another project, the server's scene template starts with `7777`. Enter the port printed by the running server before connecting.

## Scene and controls

Open **Assets/DeathWishMovement/Scenes/SyncTestScene.unity** before entering Play mode. Opening an arbitrary empty scene no longer creates a test screen. The supplied scene already contains a Camera, Canvas, EventSystem and SyncTestController. Canvas text, input fields, buttons, serialized controller references and button events can be edited in the Inspector before Play; there is no runtime or editor scene/UI factory.

1. For the server example above, enter host `127.0.0.1`, port `17779` and room `2` in Game view. Click **Connect and join**.
2. Read **My server position (m)**, Player ID, Tick and Players on the Canvas. Before admission, position is `--`.
3. The Game view shows the local capsule in blue and remote players in yellow. With two rooms configured, room 2 loads the PhysicsLab terrain and room 1 loads the flat map.
4. Click **Capture input**. Move with WASD, orbit the camera with the mouse and jump with Space while grounded. Left-click to send a camera ray. Read the server's hit or miss in the status panel and Unity Console. Esc or focus loss releases input.
5. Enable **Gizmos** in Scene view to see ID and coordinate labels for the same snapshot. The Game view blends local and remote positions toward the latest snapshot over 100 ms. The camera follows the blended local marker. The coordinate label and Scene gizmos show the latest authoritative sample.

Add the scene to your build profile if building a standalone client. Scene-view Gizmos are editor-only.

For a two-player check, join room 2 from both clients and move the console player. Watch the yellow capsule in Unity, then disconnect the console and verify that the capsule disappears and Players drops. See [the server test guide](../../docs/Testing.md) for the full sequence.

## Project will not open: manifest repair

The earlier installer introduced two opening blockers: a UTF-8 BOM via `Set-Content -Encoding UTF8` under Windows PowerShell 5.1, and an invalid `com.unity.modules.inputlegacy` dependency. Unity 6000.3.11f1 reports `Non-whitespace before {[ ... Char: 65279` for the BOM; after that is repaired, it reports `Package [com.unity.modules.inputlegacy@1.0.0] cannot be found`. The repair removes that invalid reference and writes UTF-8 explicitly without a BOM on both PowerShell 5.1 and 7, preserving unrelated settings. Installation repairs both blockers before building the client.

To repair only the JSON without building/updating DLLs:

```powershell
powershell -ExecutionPolicy Bypass -File .\integrations\Unity\Repair-Manifest.ps1 -UnityProject ..\deathwish-client
```

The repair preserves the original JSON text when the invalid dependency is absent. Other JSON syntax errors are reported without rewriting the file. No Library deletion or licensing/graphics change is needed for these two errors.

## Verification

```powershell
dotnet run --project tests/DeathWish.ConnectionTests
dotnet build tests/DeathWish.UnityCompileChecks
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Verify-UnityInstall.ps1
```

The Unity compile check references installed Unity 6000.3.11f1 engine/editor assemblies and the uGUI assembly supplied in its 3D template cache. Override `-p:UnityEditorRoot="C:/.../Editor"` and `-p:UnityUiAssembly="C:/.../UnityEngine.UI.dll"` for another installation. It compiles the editor Gizmo code too, but does not establish Play-mode success. The installer regression verifies scene preservation, old-script migration, BOM-safe manifests and actual installed package metadata. ConnectionTests exits 2 when the environment blocks TCP, so an in-memory pass does not establish network success.

Earlier scene checks in a disposable Unity 6000.3.11f1 project covered Canvas references, button events and synthetic snapshot display. The jump and raycast revision passed the Unity compile check and in-memory server tests. Live input over TCP and standalone builds still require separate verification.

To repeat the scene checks in a disposable installed project, copy `tests/UnitySceneChecks/Editor/SyncSceneValidation.cs` into that project's `Assets/Editor` directory. Run **DeathWish > Validate Sync Test Scene** before Play, then **DeathWish > Validate Snapshot Display (Play Mode)** while playing. The second command injects sample state without a server; use it without a live connection, then stop Play before normal network testing. This helper is not included by the production installer.


Both clients share MovementInput.FromLocalAxes: validate local axes, normalize diagonal length, rotate in double precision and clamp the final X/Z components before encoding. The server still rejects nonfinite or out-of-range wire input. Tests cover the previously failing angles plus all digital axis combinations across a full 360-degree sweep.
