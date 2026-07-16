$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot "installer/vc-redist-prereq.ps1")
. (Join-Path $PSScriptRoot "download-file.ps1")

$prereqDir = Join-Path $repoRoot "installer/prerequisites"
$destination = Join-Path $prereqDir $VcRedistInstallerFileName

New-Item -ItemType Directory -Path $prereqDir -Force | Out-Null

if ((Test-Path -LiteralPath $destination) -and (Get-Item -LiteralPath $destination).Length -gt 0) {
    Write-Host "Visual C++ Redistributable prerequisite already cached: $destination"
    return $destination
}

Write-Host "Downloading Visual C++ Redistributable prerequisite for offline install (required for PHP)..."
Write-Host $VcRedistInstallerUrl
Download-FileWithRetry -Uri $VcRedistInstallerUrl -OutFile $destination

if (-not (Test-Path -LiteralPath $destination) -or (Get-Item -LiteralPath $destination).Length -eq 0) {
    throw "Downloaded Visual C++ prerequisite is missing or empty: $destination"
}

Write-Host "Cached: $destination"
return $destination
