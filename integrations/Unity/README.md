# SyncTestScene and WASD test

The server owns all positions. Clients send directions at about 30 Hz; the server moves at up to 5 m/s and sends full snapshots at 10 Hz (capped by the world tick rate). Physics, collision, jumping, client prediction and reconciliation are intentionally absent.

## Console

Run from the DeathWish repository in separate terminals:

```powershell
dotnet run --project src/DeathWish.Server
dotnet run --project mockclient/DeathWish.MockClient.Console
```

Enter `/join 1`, then `/wasd`. Hold W/A/S/D to move, use Left/Right arrows to turn the movement basis, and press Esc to return to commands. Server tick, local position and player count appear in the console. Releasing keys sends zero input; losing foreground focus sends zero input. `/wasd` requires a visible standalone Windows Console Host window; VS Code and Windows Terminal tabs are deliberately rejected because their top-level window does not identify the focused terminal. Use `powershell -ExecutionPolicy Bypass -File .\mockclient\Start-MockClient.ps1` to launch a supported interactive window; `/move x z` remains available on other systems. The console provides coordinates rather than a 3D view or mouse capture.

## Unity installation

Run this command locally from the DeathWish repository with Unity closed. It repairs the package manifest first and updates the integration. The previous movement screen in the sibling project is replaced by the supplied scene when you install this revision:

```powershell
powershell -ExecutionPolicy Bypass -File .\integrations\Unity\Install.ps1 -UnityProject ..\deathwish-client
```

The installer builds and copies the .NET Standard 2.1 protocol/Core assemblies, `SyncTestClient.cs`, the world shader resource and their metadata into `Assets/DeathWishMovement`. It installs `Scenes/SyncTestScene.unity` on first setup and preserves existing scene customizations on reinstall. Canvas uses the Unity 6 built-in `com.unity.ugui` 2.0.0 package. Old `ConnectionTest.cs` and `MovementTest.cs` auto-start scripts are backed up as `.cs.disabled` with their metadata.

Keep Active Input Handling set to **Input Manager (Old)** or **Both**. The client uses the existing Mouse X/Y axes, and Canvas navigation uses Horizontal, Vertical, Submit and Cancel. The installer removes the old invalid `com.unity.modules.inputlegacy` reference. Do not import duplicate protocol/Core source or assemblies elsewhere in Assets.

## Scene and controls

Open **Assets/DeathWishMovement/Scenes/SyncTestScene.unity** before entering Play mode. Opening an arbitrary empty scene no longer creates a test screen. The supplied scene already contains a Camera, Canvas, EventSystem and SyncTestController. Canvas text, input fields, buttons, serialized controller references and button events can be edited in the Inspector before Play; there is no runtime or editor scene/UI factory.

1. In Game view, set host/port/room and click **Connect and join**.
2. Read **My server position (m)**, Player ID, Tick and Players on the Canvas. Before admission, position is `--`.
3. The Game view shows the local green capsule and remote cyan capsules over a grid. The camera follows the local capsule from behind.
4. Click **Capture input** in Game view. Mouse X/Y orbits the camera, and WASD moves relative to its horizontal heading. Esc or focus loss releases input and stops movement.
5. Enable **Gizmos** in Scene view to see ID and coordinate labels for the same snapshot. Positions are displayed directly from the latest server snapshot, without interpolation or prediction.

Add the scene to your build profile if building a standalone client. Scene-view Gizmos are editor-only.

For a two-player check, join room 1 from Unity and Console, move Console away from the common spawn, and look for the cyan capsule in Game view. Pan with the mouse if needed. Disconnect Console and verify that the capsule disappears and Players drops. See [the server test guide](../../docs/Testing.md) for the full sequence.

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

The scene was imported and run in Unity 6000.3.11f1 in a disposable project. Scene validation passed for the Canvas and its references, fonts and button events. In Play mode, synthetic snapshots verified player colors and positions, camera framing, follow and orbit, telemetry, remote departure and disconnect cleanup. The Game view showed both capsules over the grid. Live network movement and standalone builds remain unverified, and the original sibling project still needs the updated installer.

To repeat the scene checks in a disposable installed project, copy `tests/UnitySceneChecks/Editor/SyncSceneValidation.cs` into that project's `Assets/Editor` directory. Run **DeathWish > Validate Sync Test Scene** before Play, then **DeathWish > Validate Snapshot Display (Play Mode)** while playing. The second command injects sample state without a server; use it without a live connection, then stop Play before normal network testing. This helper is not included by the production installer.


Both clients share MovementInput.FromLocalAxes: validate local axes, normalize diagonal length, rotate in double precision and clamp the final X/Z components before encoding. The server still rejects nonfinite or out-of-range wire input. Tests cover the previously failing angles plus all digital axis combinations across a full 360-degree sweep.
