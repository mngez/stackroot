param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"

$Dest = Join-Path $RepoRoot "resources\packages\cacert.pem"
$MinBytes = 10000
$Url = "https://curl.se/ca/cacert.pem"

function Test-CacertReady([string]$Path) {
    return (Test-Path $Path) -and ((Get-Item $Path).Length -gt $MinBytes)
}

if (Test-CacertReady $Dest) {
    return $Dest
}

Write-Host "Downloading Mozilla CA bundle..."
New-Item -ItemType Directory -Path (Split-Path $Dest) -Force | Out-Null
Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing

if (-not (Test-CacertReady $Dest)) {
    throw "cacert.pem download failed or file is too small: $Dest"
}

Write-Host "Saved cacert.pem ($([math]::Round((Get-Item $Dest).Length / 1KB, 1)) KB)"
return $Dest
