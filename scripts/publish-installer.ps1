param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "src/Stackroot.App/Stackroot.App.csproj"
$pinnedExe = Join-Path $repoRoot "installer/pinned/Stackroot.exe"

if (-not (Test-Path $appProject)) {
    throw "Could not find project file at $appProject"
}

if (-not (Test-Path $pinnedExe)) {
    Write-Host "Pinned launcher missing - building once via build-pinned-launcher.ps1..."
    & (Join-Path $PSScriptRoot "build-pinned-launcher.ps1") -Configuration $Configuration -Runtime $Runtime
}

$projectXml = [xml](Get-Content $appProject -Raw)
$targetFramework = $projectXml.Project.PropertyGroup |
    ForEach-Object { $_.TargetFramework } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "TargetFramework missing in $appProject"
}

$version = $projectXml.Project.PropertyGroup |
    ForEach-Object { $_.Version } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version missing in $appProject"
}

$stageRoot = Join-Path $repoRoot "release/staging/$version"
$appDir = Join-Path $stageRoot "app/$version"

if (Test-Path $stageRoot) {
    Remove-Item $stageRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $appDir -Force | Out-Null

$appPublishArgs = @(
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "false"
)

Write-Host "Publishing Stackroot.App payload: $Configuration $Runtime"
dotnet publish $appProject @appPublishArgs -o $appDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish Stackroot.App failed ($LASTEXITCODE)" }

$dnsHelperDir = Join-Path $stageRoot "dns-helper"
New-Item -ItemType Directory -Path $dnsHelperDir -Force | Out-Null

$dnsHelperProject = Join-Path $repoRoot "src/Stackroot.DnsHelper/Stackroot.DnsHelper.csproj"
Write-Host "Publishing Stackroot.DnsHelper (framework-dependent, single-file): $Configuration $Runtime"
$dnsHelperPublishArgs = @(
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "false",
    "-p:PublishSingleFile=true",
    "-p:DebugType=none"
)
dotnet publish $dnsHelperProject @dnsHelperPublishArgs -o $dnsHelperDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish Stackroot.DnsHelper failed ($LASTEXITCODE)" }

Get-ChildItem $dnsHelperDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

$resourcesSource = Join-Path $repoRoot "resources/packages"
$resourcesDestination = Join-Path $appDir "resources/packages"
New-Item -ItemType Directory -Path $resourcesDestination -Force | Out-Null

& (Join-Path $PSScriptRoot "ensure-pie.ps1") -RepoRoot $repoRoot | Out-Null

foreach ($name in @("catalog.json", "php-extensions.json", "pie.phar")) {
    $source = Join-Path $resourcesSource $name
    if (-not (Test-Path $source)) {
        throw "Missing bootstrap resource: $source"
    }

    Copy-Item $source (Join-Path $resourcesDestination $name) -Force
}

$sevenZipSource = Join-Path $repoRoot "resources/tools/7zip/7za.exe"
if (Test-Path $sevenZipSource) {
    $sevenZipDestination = Join-Path $appDir "resources/tools/7zip"
    New-Item -ItemType Directory -Path $sevenZipDestination -Force | Out-Null
    Copy-Item $sevenZipSource (Join-Path $sevenZipDestination "7za.exe") -Force
}

$iconsSource = Join-Path $repoRoot "assets/icons"
$iconsDestination = Join-Path $appDir "assets/icons"
New-Item -ItemType Directory -Path $iconsDestination -Force | Out-Null
Copy-Item (Join-Path $iconsSource "*") $iconsDestination -Recurse -Force

$trayPath = Join-Path $appDir "Assets/tray.png"
if (-not (Test-Path $trayPath)) {
    Write-Warning "Expected tray asset missing in publish output: $trayPath"
}

Write-Host "Using pinned launcher (unchanged across app releases)..."
Copy-Item $pinnedExe (Join-Path $stageRoot "Stackroot.exe") -Force

$pinnedVersionFile = Join-Path $repoRoot "installer/pinned/launcher.version"
if (Test-Path $pinnedVersionFile) {
    Copy-Item $pinnedVersionFile (Join-Path $stageRoot "launcher.version") -Force
}

Set-Content -Path (Join-Path $stageRoot "current.txt") -Value $version -NoNewline

if (-not (Test-Path (Join-Path $appDir "Stackroot.dll"))) {
    throw "Published app payload missing: $(Join-Path $appDir 'Stackroot.dll')"
}

if (-not (Test-Path (Join-Path $appDir "Stackroot.exe"))) {
    throw "Published app payload missing: $(Join-Path $appDir 'Stackroot.exe')"
}

if (-not (Test-Path (Join-Path $stageRoot "Stackroot.exe"))) {
    throw "Launcher staging missing: $(Join-Path $stageRoot 'Stackroot.exe')"
}

Write-Host "Publish completed."
Write-Host "Stage: $stageRoot"
Write-Host "Payload: $appDir"
Write-Host "Launcher: installer\pinned\Stackroot.exe"
return $stageRoot
