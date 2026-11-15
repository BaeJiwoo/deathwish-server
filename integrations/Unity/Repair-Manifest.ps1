[CmdletBinding()]
param([string]$UnityProject = (Join-Path $PSScriptRoot '../../../deathwish-client'))
$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $UnityProject).Path
$manifestPath = Join-Path $project 'Packages/manifest.json'
if (!(Test-Path -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt'))) {
    throw 'Target must be a Unity project.'
}
# ReadAllText detects/removes a UTF-8 BOM. Reject other JSON corruption before writing.
$manifestText = [IO.File]::ReadAllText($manifestPath)
$manifest = $manifestText | ConvertFrom-Json
if ($null -ne $manifest.dependencies -and
    $null -ne $manifest.dependencies.PSObject.Properties['com.unity.modules.inputlegacy']) {
    # InputLegacyModule is an engine assembly, not a resolvable UPM package.
    $manifest.dependencies.PSObject.Properties.Remove('com.unity.modules.inputlegacy')
    $manifestText = $manifest | ConvertTo-Json -Depth 100
    Write-Host 'Removed invalid com.unity.modules.inputlegacy package reference.'
}
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($manifestPath, $manifestText, $utf8WithoutBom)
Write-Host 'Unity package manifest validated and saved as UTF-8 without BOM.'
