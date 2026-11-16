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
foreach ($file in @('SyncTestWorld.shader', 'SyncTestWorld.shader.meta')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Resources/$file") -Destination (Join-Path $destination 'Resources') -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SyncTestClient.cs') -Destination $destination -Force
if (!(Test-Path -LiteralPath $scriptMeta)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SyncTestClient.cs.meta') -Destination $scriptMeta
}
# Preserve scene layout and Inspector wiring customized after the first installation.
if (!(Test-Path -LiteralPath $scenePath)) {
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
$manifest.dependencies | Add-Member -NotePropertyName $package.name -NotePropertyValue $package.version -Force
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 100), (New-Object System.Text.UTF8Encoding($false)))
Write-Host 'Installed. Open Assets/DeathWishMovement/Scenes/SyncTestScene.unity. Canvas is already in the scene.'
Write-Host 'Enter Play mode, Connect and join, then Capture input. Use WASD and mouse look in the third-person Game view.'
