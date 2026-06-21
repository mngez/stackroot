param(
    [string]$ReleaseNotes
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "src/Stackroot.App/Stackroot.App.csproj"
$ReleaseDir = Join-Path $Root "release"

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

$Version = Get-ProjectProperty "Version"
if (-not $Version) { throw "Version missing in Stackroot.App.csproj" }

$SetupName = "Stackroot-Setup-$Version.exe"
$SetupPath = Join-Path $ReleaseDir $SetupName
if (-not (Test-Path $SetupPath)) {
    throw "Installer not found: $SetupPath (run ./scripts/pack-release.ps1 first)"
}

if (-not $env:GH_TOKEN) {
    . (Join-Path $PSScriptRoot "load-dotenv.ps1") -RepoRoot $Root
}

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
    Write-Host "Published: https://github.com/$Repo/releases/tag/$Tag"
    return
}

$Token = $env:GH_TOKEN
if (-not $Token) { throw "Install gh or set GH_TOKEN to publish a GitHub release." }

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
Write-Host "Published: https://github.com/$Repo/releases/tag/$Tag"
