$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "src/Stackroot.App/Stackroot.App.csproj"
$ReleaseDir = Join-Path $Root "release"
$NsiScript = Join-Path $Root "installer/stackroot.nsi"
$IconPath = Join-Path $Root "assets/icons/icon.ico"

$ProjectXml = [xml](Get-Content $Project -Raw)

function Get-ProjectProperty([string]$Name) {
    $ProjectXml.Project.PropertyGroup |
        ForEach-Object { $_.PSObject.Properties[$Name].Value } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
}

function Convert-ToNsisFileVersion([string]$Value) {
    $match = [regex]::Match($Value, '^(?<major>\d+)(\.(?<minor>\d+))?(\.(?<patch>\d+))?(\.(?<build>\d+))?')
    if (-not $match.Success) {
        return "0.0.0.0"
    }

    $parts = @(
        $match.Groups["major"].Value,
        $match.Groups["minor"].Value,
        $match.Groups["patch"].Value,
        $match.Groups["build"].Value
    )

    $normalized = foreach ($part in $parts) {
        if ([string]::IsNullOrWhiteSpace($part)) { "0" } else { $part }
    }

    return ($normalized | Select-Object -First 4) -join "."
}

$Version = Get-ProjectProperty "Version"
if (-not $Version) { throw "Version missing in Stackroot.App.csproj" }

$SetupName = "Stackroot-Setup-$Version.exe"
$SetupPath = Join-Path $ReleaseDir $SetupName

$StageDir = & (Join-Path $PSScriptRoot "publish-installer.ps1") | Select-Object -Last 1
$FileVersion = Convert-ToNsisFileVersion $Version

$AppPayloadDir = Join-Path $StageDir "app/$Version"
if (-not (Test-Path (Join-Path $AppPayloadDir "Stackroot.dll"))) {
    throw "Published app payload missing: $(Join-Path $AppPayloadDir 'Stackroot.dll')"
}

if (-not (Test-Path (Join-Path $StageDir "Stackroot.exe"))) {
    throw "Launcher missing: $(Join-Path $StageDir 'Stackroot.exe')"
}

$Publisher = Get-ProjectProperty "Company"
if ([string]::IsNullOrWhiteSpace($Publisher)) { $Publisher = "mngez" }

New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null

$DotNetPrereqScript = Join-Path $Root "installer/dotnet-prereq.ps1"
. $DotNetPrereqScript
if ([string]::IsNullOrWhiteSpace($DotNetDesktopInstallerFileName)) {
    throw "DotNetDesktopInstallerFileName missing in $DotNetPrereqScript"
}

& (Join-Path $PSScriptRoot "ensure-dotnet-prerequisite.ps1") | Out-Null

$DotNetBundlePath = Join-Path $Root "installer/prerequisites/$DotNetDesktopInstallerFileName"
if (-not (Test-Path -LiteralPath $DotNetBundlePath) -or (Get-Item -LiteralPath $DotNetBundlePath).Length -eq 0) {
    throw "Bundled .NET prerequisite missing or empty: $DotNetBundlePath"
}

$VcRedistPrereqScript = Join-Path $Root "installer/vc-redist-prereq.ps1"
. $VcRedistPrereqScript
if ([string]::IsNullOrWhiteSpace($VcRedistInstallerFileName)) {
    throw "VcRedistInstallerFileName missing in $VcRedistPrereqScript"
}

& (Join-Path $PSScriptRoot "ensure-vc-redist-prerequisite.ps1") | Out-Null

$VcRedistBundlePath = Join-Path $Root "installer/prerequisites/$VcRedistInstallerFileName"
if (-not (Test-Path -LiteralPath $VcRedistBundlePath) -or (Get-Item -LiteralPath $VcRedistBundlePath).Length -eq 0) {
    throw "Bundled Visual C++ Redistributable missing or empty: $VcRedistBundlePath"
}

$LauncherVersionFile = Join-Path $Root "installer/pinned/launcher.version"
$LauncherProtocolVersion = "2"
if (Test-Path -LiteralPath $LauncherVersionFile) {
    $LauncherProtocolVersion = (Get-Content -LiteralPath $LauncherVersionFile -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($LauncherProtocolVersion)) {
    throw "launcher.version is empty: $LauncherVersionFile"
}

$Makensis = & (Join-Path $PSScriptRoot "ensure-nsis.ps1") -RepoRoot $Root
$NsisDir = Split-Path -Parent $Makensis
$StageDirNsis = ($StageDir -replace '\\', '/')
$InstallerDirNsis = ((Join-Path $Root "installer") -replace '\\', '/')
$ReleaseDirNsis = ($ReleaseDir -replace '\\', '/')
$IconPathNsis = ($IconPath -replace '\\', '/')

$NsisArgs = @(
    "/V2",
    "/DNSISDIR=$NsisDir",
    "/DPRODUCT_VERSION=$Version",
    "/DPRODUCT_FILE_VERSION=$FileVersion",
    "/DPRODUCT_PUBLISHER=$Publisher",
    "/DSTAGE_DIR=$StageDirNsis",
    "/DINSTALLER_DIR=$InstallerDirNsis",
    "/DRELEASE_DIR=$ReleaseDirNsis",
    "/DDOTNET_DESKTOP_INSTALLER=$DotNetDesktopInstallerFileName",
    "/DVC_REDIST_INSTALLER=$VcRedistInstallerFileName",
    "/DLAUNCHER_PROTOCOL_VERSION=$LauncherProtocolVersion",
    "/DICON_PATH=$IconPathNsis",
    $NsiScript
)

& $Makensis @NsisArgs
if ($LASTEXITCODE -ne 0) { throw "makensis failed ($LASTEXITCODE)" }

if (-not (Test-Path $SetupPath)) { throw "Installer not created: $SetupPath" }

Write-Host "Pack: $SetupPath"
