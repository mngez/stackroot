param(
    [switch]$Publish,
    [string]$ReleaseNotes
)

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

function Resolve-GitHubRepo {
    $remote = $null
    if (Get-Command git -ErrorAction SilentlyContinue) {
        try {
            $remote = git -C $Root remote get-url origin 2>$null
        } catch {
            $remote = $null
        }
    }

    if ([string]::IsNullOrWhiteSpace($remote)) {
        return "mngez/stackroot"
    }

    $normalized = $remote.Trim()
    if ($normalized -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+)(\.git)?$') {
        return "$($Matches.owner)/$($Matches.repo)"
    }

    return "mngez/stackroot"
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

$PublishDir = & (Join-Path $PSScriptRoot "publish-installer.ps1") | Select-Object -Last 1

$Version = Get-ProjectProperty "Version"
if (-not $Version) { throw "Version missing in Stackroot.App.csproj" }
$FileVersion = Convert-ToNsisFileVersion $Version

if (-not (Test-Path (Join-Path $PublishDir "Stackroot.exe"))) {
    throw "Published app missing: $(Join-Path $PublishDir 'Stackroot.exe')"
}

$Publisher = Get-ProjectProperty "Company"
if ([string]::IsNullOrWhiteSpace($Publisher)) { $Publisher = "mngez" }

New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null

$Makensis = & (Join-Path $PSScriptRoot "ensure-nsis.ps1") -RepoRoot $Root
$NsisDir = Split-Path -Parent $Makensis
$PublishDirNsis = ($PublishDir -replace '\\', '/')
$ReleaseDirNsis = ($ReleaseDir -replace '\\', '/')
$IconPathNsis = ($IconPath -replace '\\', '/')

$NsisArgs = @(
    "/V2",
    "/DNSISDIR=$NsisDir",
    "/DPRODUCT_VERSION=$Version",
    "/DPRODUCT_FILE_VERSION=$FileVersion",
    "/DPRODUCT_PUBLISHER=$Publisher",
    "/DPUBLISH_DIR=$PublishDirNsis",
    "/DRELEASE_DIR=$ReleaseDirNsis",
    "/DICON_PATH=$IconPathNsis",
    $NsiScript
)

& $Makensis @NsisArgs
if ($LASTEXITCODE -ne 0) { throw "makensis failed ($LASTEXITCODE)" }

$SetupName = "Stackroot-Setup-$Version.exe"
$SetupPath = Join-Path $ReleaseDir $SetupName
if (-not (Test-Path $SetupPath)) { throw "Installer not created: $SetupPath" }

Write-Host "Pack: $SetupPath"

if (-not $Publish) { return }

. (Join-Path $PSScriptRoot "load-dotenv.ps1") -RepoRoot $Root

$Tag = "v$Version"
$Repo = Resolve-GitHubRepo
$Title = "Stackroot $Version"
$NotesPath = Join-Path $Root "release-notes/$Version.md"
$Notes = if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $ReleaseNotes
} elseif (Test-Path $NotesPath) {
    Get-Content $NotesPath -Raw
} else {
    "Windows installer (NSIS)."
}

if (Get-Command gh -ErrorAction SilentlyContinue) {
    if ($env:GH_TOKEN) {
        $ghAuthenticated = $true
    } else {
        gh auth status --hostname github.com *> $null
        $ghAuthenticated = $LASTEXITCODE -eq 0
    }
} else {
    $ghAuthenticated = $false
}

if ($ghAuthenticated) {
    $prevErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    gh release view $Tag --repo $Repo 2>$null | Out-Null
    $releaseExists = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $prevErrorAction

    if ($releaseExists) {
        gh release upload $Tag $SetupPath --repo $Repo --clobber
    } else {
        gh release create $Tag $SetupPath --repo $Repo --title $Title --notes $Notes
    }

    if ($LASTEXITCODE -ne 0) { throw "gh release failed ($LASTEXITCODE)" }
    Write-Host "Ship: https://github.com/$Repo/releases/tag/$Tag"
    return
}

$Token = $env:GH_TOKEN
if (-not $Token) { throw "Install gh or set GH_TOKEN for ship." }

$Headers = @{
    Authorization = "Bearer $Token"
    Accept        = "application/vnd.github+json"
    "User-Agent"  = "stackroot-release"
}

$ReleaseBody = @{
    tag_name   = $Tag
    name       = $Title
    body       = $Notes
    draft      = $false
    prerelease = $false
} | ConvertTo-Json

$Release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/releases" -Headers $Headers -Body $ReleaseBody
$UploadHeaders = @{
    Authorization = "Bearer $Token"
    Accept        = "application/vnd.github+json"
    "Content-Type" = "application/octet-stream"
    "User-Agent"  = "stackroot-release"
}
$UploadUri = "https://uploads.github.com/repos/$Repo/releases/$($Release.id)/assets?name=$SetupName"
Invoke-RestMethod -Method Post -Uri $UploadUri -Headers $UploadHeaders -InFile $SetupPath | Out-Null
Write-Host "Ship: https://github.com/$Repo/releases/tag/$Tag"
