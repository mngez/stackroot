param(
    [Parameter(Mandatory)]
    [string]$Version,
    [switch]$BumpVersion
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "src/Stackroot.App/Stackroot.App.csproj"
$Tag = "v$($Version.Trim().TrimStart('v', 'V').TrimEnd('+'))"

if ($Tag -notmatch '^v\d+\.\d+\.\d+([-.].+)?$') {
    throw "Invalid version '$Version'. Use semver like 0.2.6."
}

$Version = $Tag.Substring(1)
$NotesPath = Join-Path $Root "release-notes/$Version.md"
if (-not (Test-Path -LiteralPath $NotesPath)) {
    throw "Missing release notes: release-notes/$Version.md"
}

function Get-ProjectVersion([string]$Path) {
    $projectXml = [xml](Get-Content $Path -Raw)
    return $projectXml.Project.PropertyGroup |
        ForEach-Object { $_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
}

function Set-ProjectVersion([string]$Path, [string]$TargetVersion) {
    $projectXml = [xml](Get-Content $Path -Raw)
    $updated = $false

    foreach ($group in $projectXml.Project.PropertyGroup) {
        if ($null -ne $group.Version) {
            $group.Version = $TargetVersion
            $updated = $true
            break
        }
    }

    if (-not $updated) {
        $firstGroup = $projectXml.Project.PropertyGroup | Select-Object -First 1
        if ($null -eq $firstGroup) {
            throw "No PropertyGroup found in Stackroot.App.csproj"
        }

        $versionNode = $projectXml.CreateElement("Version")
        $versionNode.InnerText = $TargetVersion
        $firstGroup.AppendChild($versionNode) | Out-Null
    }

    $projectXml.Save($Path)
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "git is required."
}

Push-Location $Root
try {
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne "main") {
        throw "Push releases from main (current branch: $branch)."
    }

    $csprojVersion = Get-ProjectVersion $Project
    if ([string]::IsNullOrWhiteSpace($csprojVersion)) {
        throw "Version missing in Stackroot.App.csproj"
    }

    if ($BumpVersion) {
        if ($csprojVersion -ne $Version) {
            Write-Host "Updating Stackroot.App.csproj: $csprojVersion -> $Version"
            Set-ProjectVersion $Project $Version
            git add -- "$Project"
            git commit -m "Set release version to $Version."
            if ($LASTEXITCODE -ne 0) { throw "git commit failed ($LASTEXITCODE)" }
        }
    } elseif ($csprojVersion -ne $Version) {
        throw "Stackroot.App.csproj is '$csprojVersion' but you asked to push '$Version'. Bump the csproj first or use: ./sr push $Version +"
    }

    $dirty = git status --porcelain
    if ($dirty) {
        throw "Working tree is not clean. Commit or stash changes before pushing a release."
    }

    git fetch origin main 2>$null
    if ($LASTEXITCODE -ne 0) {
        git fetch origin
        if ($LASTEXITCODE -ne 0) { throw "git fetch failed ($LASTEXITCODE)" }
    }

    $ahead = [int](git rev-list --count "origin/main..HEAD" 2>$null)
    if ($ahead -gt 0) {
        Write-Host "Pushing $ahead commit(s) to origin/main..."
        git push origin main
        if ($LASTEXITCODE -ne 0) { throw "git push origin main failed ($LASTEXITCODE)" }
    }

    $localTag = git tag -l $Tag
    if ($localTag) {
        Write-Host "Removing local tag $Tag..."
        git tag -d $Tag | Out-Null
    }

    $remoteTag = git ls-remote --tags origin "refs/tags/$Tag"
    if ($remoteTag) {
        Write-Host "Replacing remote tag $Tag (GitHub Actions will rebuild the installer)..."
        git push origin ":refs/tags/$Tag"
        if ($LASTEXITCODE -ne 0) { throw "Failed to delete remote tag $Tag ($LASTEXITCODE)" }
    }

    git tag $Tag
    if ($LASTEXITCODE -ne 0) { throw "git tag failed ($LASTEXITCODE)" }

    git push origin $Tag
    if ($LASTEXITCODE -ne 0) { throw "git push origin $Tag failed ($LASTEXITCODE)" }

    $repo = "mngez/stackroot"
    $remote = git remote get-url origin 2>$null
    if ($remote -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+)(\.git)?$') {
        $repo = "$($Matches.owner)/$($Matches.repo)"
    }

    Write-Host ""
    Write-Host "Release $Tag queued."
    Write-Host "  Actions: https://github.com/$repo/actions/workflows/release.yml"
    Write-Host "  Release: https://github.com/$repo/releases/tag/$Tag"
}
finally {
    Pop-Location
}
