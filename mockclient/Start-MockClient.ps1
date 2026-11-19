[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int] $Port = 7777
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'DeathWish.MockClient.Console/DeathWish.MockClient.Console.csproj'
& dotnet build $project -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw 'MockClient build failed.' }
$dll = Join-Path $PSScriptRoot 'DeathWish.MockClient.Console/bin/Debug/net10.0/DeathWish.MockClient.Console.dll'
# Explicitly launch an interactive classic console so HWND focus matches the input surface.
Start-Process -FilePath "$env:WINDIR/System32/conhost.exe" -ArgumentList @('dotnet.exe', ('"' + $dll + '"'), '127.0.0.1', [string]$Port)
