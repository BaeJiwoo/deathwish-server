[CmdletBinding()]
param(
    [string]$UnityProject = (Join-Path $PSScriptRoot '../../../deathwish-client'),
    [string]$UnityEditorRoot = 'C:/Program Files/Unity/Hub/Editor/6000.3.11f1/Editor'
)
$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $UnityProject).Path
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path

# Repair the project-opening blocker even if the later client build cannot complete.
& (Join-Path $PSScriptRoot 'Repair-Manifest.ps1') -UnityProject $project
$settings = Get-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectSettings.asset') -Raw
if ($settings -match 'activeInputHandler: 1\s') {
    throw 'Set Active Input Handling to Input Manager (Old) or Both in Unity Player Settings, then rerun.'
}
$packagePath = Join-Path $UnityEditorRoot 'Data/Resources/PackageManager/BuiltInPackages/com.unity.ugui/package.json'
$package = [IO.File]::ReadAllText($packagePath) | ConvertFrom-Json
if ($package.name -ne 'com.unity.ugui' -or $package.version -ne '2.0.0') {
    throw 'This scene requires the Unity 6 built-in com.unity.ugui 2.0.0 package.'
}
$manifestPath = Join-Path $project 'Packages/manifest.json'
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$existingUiVersion = $manifest.dependencies.'com.unity.ugui'
$existingPhysicsVersion = $manifest.dependencies.'com.unity.modules.physics'
if ($null -ne $existingUiVersion -and $existingUiVersion -ne $package.version) {
    throw 'The project has a different uGUI version. Resolve the version before installing this scene.'
}
$destination = Join-Path $project 'Assets/DeathWishMovement'
$scenePath = Join-Path $destination 'Scenes/SyncTestScene.unity'
$scriptGuid = '6ae419937bd34d429b5a125a5e72f810'
$scriptMeta = Join-Path $destination 'SyncTestClient.cs.meta'
if ((Test-Path -LiteralPath $scriptMeta) -and [IO.File]::ReadAllText($scriptMeta) -notmatch "guid: $scriptGuid") {
    throw 'SyncTestClient.cs.meta belongs to a different script. Resolve the asset conflict first.'
}
if ((Test-Path -LiteralPath $scenePath) -and ![IO.File]::ReadAllText($scenePath).Contains($scriptGuid)) {
    throw 'SyncTestScene.unity already exists without this client component. Resolve the scene conflict first.'
}
$legacyPaths = @(
    (Join-Path $project 'Assets/Scripts/ConnectionTest.cs'),
    (Join-Path $destination 'MovementTest.cs')
)
foreach ($legacy in $legacyPaths) {
    if ((Test-Path -LiteralPath $legacy) -and
        ((Test-Path -LiteralPath ($legacy + '.disabled')) -or (Test-Path -LiteralPath ($legacy + '.disabled.meta')))) {
        throw "Legacy backup already exists for $legacy. Resolve it before rerunning."
    }
}
& dotnet build (Join-Path $repo 'mockclient/DeathWish.MockClient.Core') -c Release -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw 'Client build failed. The manifest repair has completed; integration files were not changed.' }
New-Item -ItemType Directory -Force -Path (Join-Path $destination 'Scenes'), (Join-Path $destination 'Resources') | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'maps/PhysicsLab/PhysicsLab.json') -Destination (Join-Path $destination 'Resources') -Force
Copy-Item -LiteralPath (Join-Path $repo 'maps/TeamDeathmatch/TeamDeathmatch.json') -Destination (Join-Path $destination 'Resources') -Force
foreach ($file in @('SyncTestWorld.shader', 'SyncTestWorld.shader.meta', 'ShotFeedback.shader', 'ShotFeedback.shader.meta', 'TeamDeathmatch.json.meta')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Resources/$file") -Destination (Join-Path $destination 'Resources') -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SyncTestClient.cs') -Destination $destination -Force
foreach ($file in @('RaycastFeedback.cs', 'RaycastFeedback.cs.meta', 'ShoulderCameraSettings.cs', 'ShoulderCameraSettings.cs.meta',
    'TeamDeathmatchHud.cs', 'TeamDeathmatchHud.cs.meta', 'ArenaWeaponVisual.cs', 'ArenaWeaponVisual.cs.meta')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $destination -Force
}
if (!(Test-Path -LiteralPath $scriptMeta)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SyncTestClient.cs.meta') -Destination $scriptMeta
}
# Upgrade either previously shipped scene, including a checkout with Windows newlines.
# A customized scene keeps its layout and Inspector references.
$installScene = !(Test-Path -LiteralPath $scenePath)
if (!$installScene) {
    $sceneText = [IO.File]::ReadAllText($scenePath).Replace("`r`n", "`n")
    if (!$sceneText.Contains('_shoulderCamera:')) {
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sceneText))).Replace('-', '')
        } finally {
            $sha.Dispose()
        }
        $installScene = $hash -in @('70E9DCC3D332E57016F51391E7D445709A642AE853EDA3D3A548F97AE56154D4', 'D5BB4B7D7731F549142E4247862C07426229C181496409C4208F6C8DFB872F72', 'BA1B78065CC506490DD1A7A360DA421E9D928DA399DCE909AFF21DEBBDE45C44', '13BCED15CE471E1FD294DA1810F26D31764C8DC21846C689EE2D8D4C188D44DA')
        if (!$installScene) {
            Write-Warning 'Custom scene preserved. Merge the camera position inputs and HUD from the supplied scene and wire their SyncTestClient references.'
        }
    }
}
if ($installScene) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Scenes/SyncTestScene.unity') -Destination $scenePath
}
if (!(Test-Path -LiteralPath ($scenePath + '.meta'))) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Scenes/SyncTestScene.unity.meta') -Destination ($scenePath + '.meta')
}
Copy-Item -LiteralPath (Join-Path $repo 'mockclient/DeathWish.MockClient.Core/bin/Release/netstandard2.1/DeathWish.MockClient.Core.dll') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $repo 'src/DeathWish.Protocol/bin/Release/netstandard2.1/DeathWish.Protocol.dll') -Destination $destination -Force
foreach ($legacy in $legacyPaths) {
    $backup = $legacy + '.disabled'
    # Verify absolute file paths before moving the old auto-start scripts and their metadata.
    if (!$legacy.StartsWith($project + [IO.Path]::DirectorySeparatorChar) -or !$backup.StartsWith($project + [IO.Path]::DirectorySeparatorChar)) {
        throw 'Legacy script path escaped the project.'
    }
    if (Test-Path -LiteralPath $legacy) {
        Move-Item -LiteralPath $legacy -Destination $backup
        if (Test-Path -LiteralPath ($legacy + '.meta')) {
            Move-Item -LiteralPath ($legacy + '.meta') -Destination ($backup + '.meta')
        }
    }
}
if ($null -eq $existingUiVersion -or $existingPhysicsVersion -ne '1.0.0') {
    $manifest.dependencies | Add-Member -NotePropertyName $package.name -NotePropertyValue $package.version -Force
    $manifest.dependencies | Add-Member -NotePropertyName 'com.unity.modules.physics' -NotePropertyValue '1.0.0' -Force
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 100), (New-Object System.Text.UTF8Encoding($false)))
}
Write-Host 'Installed. Open Assets/DeathWishMovement/Scenes/SyncTestScene.unity. Canvas is already in the scene.'
Write-Host 'Enter Play mode, Connect and join, then Capture input. Use WASD and mouse look in the third-person Game view.'
