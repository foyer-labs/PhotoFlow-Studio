# SPDX-License-Identifier: Apache-2.0
#
# Builds the Windows package of PhotoFlow Studio:
#   1. the development engine with PyInstaller (engine/dist/engine), unless -SkipEngine;
#   2. the self-contained application (dotnet publish), which copies the engine next to itself;
#   3. the Velopack installer, renamed PhotoFlowStudio-<version>-Setup.exe.
#
# Requirements: .NET SDK (global.json), engine/.venv with the [build] extras, vpk 1.2.161 (dotnet tool).
#
# Usage:
#   pwsh scripts/build-studio.ps1                 # version from Directory.Build.props, output in artifacts/
#   pwsh scripts/build-studio.ps1 -SkipEngine     # reuse engine/dist/engine

param(
    [string]$Version,
    [string]$Output = "artifacts",
    [switch]$SkipEngine,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

$publish = Join-Path $Output "publish"
$releases = Join-Path $Output "releases"

if (-not $SkipEngine) {
    Write-Host "== Engine (PyInstaller)"
    Push-Location engine
    try {
        & .\.venv\Scripts\pyinstaller.exe packaging\engine.spec --noconfirm
        if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed" }
    }
    finally { Pop-Location }
}

if (-not (Test-Path engine/dist/engine/photoflow-engine.exe)) { throw "engine/dist/engine/photoflow-engine.exe is missing" }

Write-Host "== Application $Version (dotnet publish)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish src/PhotoFlow.App/PhotoFlow.App.csproj -c Release -r win-x64 --self-contained -o $publish -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path (Join-Path $publish "engine/photoflow-engine.exe"))) { throw "The engine was not copied next to the application" }

if ($SkipInstaller) { return }

Write-Host "== Installer (Velopack)"
vpk pack --packId FoyerLabs.PhotoFlowStudio --packVersion $Version --packDir $publish --mainExe PhotoFlow.App.exe `
    --packTitle "PhotoFlow Studio" --packAuthors "Foyer Labs" --icon src/PhotoFlow.App/Assets/photoflow.ico --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

$setup = Get-ChildItem $releases -Filter "*-Setup.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$named = Join-Path $releases "PhotoFlowStudio-$Version-Setup.exe"
Copy-Item $setup.FullName $named -Force
Write-Host "== Done: $named"
