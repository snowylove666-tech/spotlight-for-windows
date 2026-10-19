# Builds artifacts\installer\SpotlightWin-Setup-<version>.exe (needs the .NET 10 SDK and Inno Setup 6).
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version

dotnet publish (Join-Path $root 'src\Spotlight.App') -c Release -r win-x64 -o (Join-Path $root 'artifacts\installer\app')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found: winget install JRSoftware.InnoSetup' }
& $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot 'SpotlightWin.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
