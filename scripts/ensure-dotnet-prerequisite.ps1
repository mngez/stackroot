$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot "installer/dotnet-prereq.ps1")

$prereqDir = Join-Path $repoRoot "installer/prerequisites"
$destination = Join-Path $prereqDir $DotNetDesktopInstallerFileName

New-Item -ItemType Directory -Path $prereqDir -Force | Out-Null

if ((Test-Path -LiteralPath $destination) -and (Get-Item -LiteralPath $destination).Length -gt 0) {
    Write-Host ".NET prerequisite already cached: $destination"
    return $destination
}

Write-Host "Downloading .NET Desktop Runtime prerequisite for offline install..."
Write-Host $DotNetDesktopInstallerUrl
Invoke-WebRequest -Uri $DotNetDesktopInstallerUrl -OutFile $destination -UseBasicParsing

if (-not (Test-Path -LiteralPath $destination) -or (Get-Item -LiteralPath $destination).Length -eq 0) {
    throw "Downloaded .NET prerequisite is missing or empty: $destination"
}

Write-Host "Cached: $destination"
return $destination
