param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$launcherProject = Join-Path $repoRoot "src/Stackroot.Launcher/Stackroot.Launcher.csproj"
$pinnedDir = Join-Path $repoRoot "installer/pinned"
$pinnedExe = Join-Path $pinnedDir "Stackroot.exe"
$pinnedVersionFile = Join-Path $pinnedDir "launcher.version"
$buildDir = Join-Path $pinnedDir "_build"

if (Test-Path $buildDir) {
    Remove-Item $buildDir -Recurse -Force
}

New-Item -ItemType Directory -Path $buildDir -Force | Out-Null

Write-Host "Building pinned Stackroot launcher (single-file, framework-dependent)..."
dotnet publish $launcherProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $buildDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish Stackroot.Launcher failed ($LASTEXITCODE)"
}

$builtExe = Join-Path $buildDir "Stackroot.Launcher.exe"
if (-not (Test-Path $builtExe)) {
    throw "Pinned launcher build missing: $builtExe"
}

New-Item -ItemType Directory -Path $pinnedDir -Force | Out-Null
Copy-Item $builtExe $pinnedExe -Force
Remove-Item $buildDir -Recurse -Force

Get-ChildItem -Path $pinnedDir -File | Where-Object {
    $_.Name -notin @("Stackroot.exe", "launcher.version")
} | Remove-Item -Force

Get-ChildItem -Path $pinnedDir -Directory | Where-Object {
    $_.Name -match '^_(build|probe)'
} | Remove-Item -Recurse -Force

$launcherProtocolVersion = "8"
Set-Content -Path $pinnedVersionFile -Value $launcherProtocolVersion -NoNewline

$hash = (Get-FileHash -Path $pinnedExe -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Pinned launcher: $pinnedExe"
Write-Host "Exe size: $((Get-Item $pinnedExe).Length) bytes"
Write-Host "SHA256: $hash"
Write-Host "Protocol version: $launcherProtocolVersion"
Write-Host "Single Stackroot.exe at install root; rebuild only when src/Stackroot.Launcher changes."
