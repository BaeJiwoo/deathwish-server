[CmdletBinding()]
param([string]$UnityEditorRoot = 'C:/Program Files/Unity/Hub/Editor/6000.3.11f1/Editor')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$fixture = Join-Path $PSScriptRoot ('DeathWish.UnityCompileChecks/bin/InstallerRegression-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path "$fixture/ProjectSettings", "$fixture/Packages", "$fixture/Assets/Scripts" | Out-Null
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText("$fixture/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.3.11f1`n", $utf8WithoutBom)
[IO.File]::WriteAllText("$fixture/ProjectSettings/ProjectSettings.asset", "activeInputHandler: 0`n", $utf8WithoutBom)
$manifestPath = "$fixture/Packages/manifest.json"
$manifestText = '{"dependencies":{"com.unity.modules.imgui":"1.0.0"},"scopedRegistries":[]}'
[IO.File]::WriteAllText($manifestPath, $manifestText, (New-Object System.Text.UTF8Encoding($true)))
[IO.File]::WriteAllText("$fixture/Assets/Scripts/ConnectionTest.cs", '// preserved legacy source', $utf8WithoutBom)
New-Item -ItemType Directory -Force -Path "$fixture/Assets/DeathWishMovement" | Out-Null
[IO.File]::WriteAllText("$fixture/Assets/DeathWishMovement/MovementTest.cs", '// preserved old movement source', $utf8WithoutBom)
[IO.File]::WriteAllText("$fixture/Assets/DeathWishMovement/MovementTest.cs.meta", "fileFormatVersion: 2`nguid: f034c0e6460181141894370aa75a5db3`n", $utf8WithoutBom)

function Assert-Manifest {
    $bytes = [IO.File]::ReadAllBytes($manifestPath)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw 'UTF-8 BOM remains in manifest.'
    }
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ($null -eq $manifest.PSObject.Properties['scopedRegistries']) { throw 'Unrelated manifest settings lost.' }
    if ($manifest.dependencies.'com.unity.modules.imgui' -ne '1.0.0') { throw 'Existing dependency changed.' }
    if ($null -ne $manifest.dependencies.PSObject.Properties['com.unity.modules.inputlegacy']) {
        throw 'Invalid legacy input package reference remains.'
    }
}

& (Join-Path $repo 'integrations/Unity/Repair-Manifest.ps1') -UnityProject $fixture
Assert-Manifest
if ([IO.File]::ReadAllText($manifestPath) -cne $manifestText) { throw 'BOM repair changed the JSON body.' }

# Reproduce both opening blockers, and check repair without a build or reinstall.
$brokenManifestText = '{"dependencies":{"com.unity.modules.imgui":"1.0.0","com.unity.modules.inputlegacy":"1.0.0"},"scopedRegistries":[]}'
[IO.File]::WriteAllText($manifestPath, $brokenManifestText, (New-Object System.Text.UTF8Encoding($true)))
& (Join-Path $repo 'integrations/Unity/Repair-Manifest.ps1') -UnityProject $fixture
Assert-Manifest
$repairedManifestText = [IO.File]::ReadAllText($manifestPath)
& (Join-Path $repo 'integrations/Unity/Repair-Manifest.ps1') -UnityProject $fixture
if ([IO.File]::ReadAllText($manifestPath) -cne $repairedManifestText) { throw 'Repeated repair changed valid JSON.' }

# Other JSON corruption must fail without changing the file.
$malformedManifestText = '{"dependencies":'
[IO.File]::WriteAllText($manifestPath, $malformedManifestText, $utf8WithoutBom)
$hasRejectedMalformedJson = $false
try {
    & (Join-Path $repo 'integrations/Unity/Repair-Manifest.ps1') -UnityProject $fixture
} catch {
    $hasRejectedMalformedJson = $true
}
if (!$hasRejectedMalformedJson) { throw 'Malformed JSON was accepted.' }
if ([IO.File]::ReadAllText($manifestPath) -cne $malformedManifestText) { throw 'Malformed JSON was overwritten.' }

# Reproduce an upgrade from the broken installer, then verify a repeated install stays safe.
[IO.File]::WriteAllText($manifestPath, $brokenManifestText, (New-Object System.Text.UTF8Encoding($true)))
for ($attempt = 0; $attempt -lt 2; $attempt++) {
    & (Join-Path $repo 'integrations/Unity/Install.ps1') -UnityProject $fixture -UnityEditorRoot $UnityEditorRoot
    Assert-Manifest
    if (Test-Path -LiteralPath "$fixture/Assets/Scripts/ConnectionTest.cs") { throw 'Old auto-start script remains active.' }
    if ([IO.File]::ReadAllText("$fixture/Assets/Scripts/ConnectionTest.cs.disabled") -cne '// preserved legacy source') {
        throw 'Legacy source backup changed.'
    }
    if (Test-Path -LiteralPath "$fixture/Assets/DeathWishMovement/MovementTest.cs") { throw 'Old movement auto-start script remains active.' }
    if ([IO.File]::ReadAllText("$fixture/Assets/DeathWishMovement/MovementTest.cs.disabled") -cne '// preserved old movement source') {
        throw 'Old movement source backup changed.'
    }
    if (!(Test-Path -LiteralPath "$fixture/Assets/DeathWishMovement/MovementTest.cs.disabled.meta")) { throw 'Old movement metadata not preserved.' }
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ($manifest.dependencies.'com.unity.ugui' -ne '2.0.0') { throw 'Supported Canvas package missing.' }
    if ($manifest.dependencies.'com.unity.modules.physics' -ne '1.0.0') { throw 'Required camera physics package missing.' }
    foreach ($relative in @('SyncTestClient.cs', 'SyncTestClient.cs.meta', 'Scenes/SyncTestScene.unity.meta',
        'RaycastFeedback.cs', 'RaycastFeedback.cs.meta', 'ShoulderCameraSettings.cs', 'ShoulderCameraSettings.cs.meta',
        'TeamDeathmatchHud.cs', 'TeamDeathmatchHud.cs.meta', 'ArenaWeaponVisual.cs', 'ArenaWeaponVisual.cs.meta',
        'Resources/ShotFeedback.shader', 'Resources/ShotFeedback.shader.meta', 'Resources/TeamDeathmatch.json.meta',
        'Resources/SyncTestWorld.shader', 'Resources/SyncTestWorld.shader.meta')) {
        $sourceHash = (Get-FileHash (Join-Path $repo "integrations/Unity/$relative")).Hash
        $installedHash = (Get-FileHash "$fixture/Assets/DeathWishMovement/$relative").Hash
        if ($sourceHash -ne $installedHash) { throw "Installed content mismatch: $relative" }
    }
    foreach ($map in @('PhysicsLab', 'TeamDeathmatch')) {
        $sourceHash = (Get-FileHash (Join-Path $repo "maps/$map/$map.json")).Hash
        $installedHash = (Get-FileHash "$fixture/Assets/DeathWishMovement/Resources/$map.json").Hash
        if ($sourceHash -ne $installedHash) { throw "Installed map content mismatch: $map" }
    }
    $installedScenePath = "$fixture/Assets/DeathWishMovement/Scenes/SyncTestScene.unity"
    if ($attempt -eq 0) {
        $sceneSource = [IO.File]::ReadAllText((Join-Path $repo 'integrations/Unity/Scenes/SyncTestScene.unity'))
        if ([IO.File]::ReadAllText($installedScenePath) -cne $sceneSource) { throw 'Scene installation changed the template.' }
        [IO.File]::AppendAllText($installedScenePath, "`n# User layout customization retained.`n", $utf8WithoutBom)
        $customizedScene = [IO.File]::ReadAllText($installedScenePath)
        $customizedManifest = ($manifest | ConvertTo-Json -Depth 100 -Compress) + "`n"
        [IO.File]::WriteAllText($manifestPath, $customizedManifest, $utf8WithoutBom)
    } else {
        if ([IO.File]::ReadAllText($installedScenePath) -cne $customizedScene) {
            throw 'Repeated install overwrote the customized scene.'
        }
        if ([IO.File]::ReadAllText($manifestPath) -cne $customizedManifest) {
            throw 'Repeated install rewrote a compatible manifest.'
        }
    }
}
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
foreach ($dependency in $manifest.dependencies.PSObject.Properties) {
    $packagePath = Join-Path $UnityEditorRoot "Data/Resources/PackageManager/BuiltInPackages/$($dependency.Name)/package.json"
    if (!(Test-Path -LiteralPath $packagePath)) { throw "Unity does not provide fixture dependency: $($dependency.Name)" }
    $package = [IO.File]::ReadAllText($packagePath) | ConvertFrom-Json
    if ($package.name -ne $dependency.Name -or $package.version -ne $dependency.Value) {
        throw "Fixture dependency does not match installed Unity: $($dependency.Name)"
    }
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'DeathWish.UnityCompileChecks/bin/latest-installer-fixture.txt'), $fixture, $utf8WithoutBom)
Write-Host "PASS PowerShell $($PSVersionTable.PSVersion): manifest repair, Canvas scene installation/preservation, legacy script migration and installed Unity package compatibility."
