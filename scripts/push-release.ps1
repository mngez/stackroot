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

function Invoke-Git {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$GitArgs
    )

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & git @GitArgs
        if ($LASTEXITCODE -ne 0) {
            throw "git $($GitArgs -join ' ') failed ($LASTEXITCODE)"
        }
    } finally {
        $ErrorActionPreference = $prev
    }
}

function Invoke-GitQuiet {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$GitArgs
    )

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & git @GitArgs 2>$null
        return $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }
}

function Get-GitOutput {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$GitArgs
    )

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git @GitArgs 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "git $($GitArgs -join ' ') failed ($LASTEXITCODE)"
        }
        return $output
    } finally {
        $ErrorActionPreference = $prev
    }
}

Push-Location $Root
try {
    $branch = (Get-GitOutput rev-parse --abbrev-ref HEAD).Trim()
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
            Invoke-Git add -- "$Project"
            Invoke-Git commit -m "Set release version to $Version."
        }
    } elseif ($csprojVersion -ne $Version) {
        throw "Stackroot.App.csproj is '$csprojVersion' but you asked to push '$Version'. Bump the csproj first or use: ./sr push $Version +"
    }

    $dirty = Get-GitOutput status --porcelain
    if ($dirty) {
        throw "Working tree is not clean. Commit or stash changes before pushing a release."
    }

    if ((Invoke-GitQuiet fetch origin main) -ne 0) {
        if ((Invoke-GitQuiet fetch origin) -ne 0) {
            throw "git fetch failed"
        }
    }

    $ahead = [int](Get-GitOutput rev-list --count "origin/main..HEAD")
    if ($ahead -gt 0) {
        Write-Host "Pushing $ahead commit(s) to origin/main..."
        Invoke-Git push origin main
    }

    $localTag = Get-GitOutput tag -l $Tag
    if ($localTag) {
        Write-Host "Removing local tag $Tag..."
        Invoke-GitQuiet tag -d $Tag | Out-Null
    }

    $remoteTag = Get-GitOutput ls-remote --tags origin "refs/tags/$Tag"
    if ($remoteTag) {
        Write-Host "Replacing remote tag $Tag (GitHub Actions will rebuild the installer)..."
        Invoke-Git push origin ":refs/tags/$Tag"
    }

    Invoke-Git tag $Tag
    Invoke-Git push origin $Tag

    $repo = "mngez/stackroot"
    $remote = Get-GitOutput remote get-url origin
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
