param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"

$PieVersion = "1.4.5"
$Dest = Join-Path $RepoRoot "resources\packages\pie.phar"
$MinBytes = 100000

function Test-PiePharReady([string]$Path) {
    return (Test-Path $Path) -and ((Get-Item $Path).Length -gt $MinBytes)
}

if (Test-PiePharReady $Dest) {
    return $Dest
}

$url = "https://github.com/php/pie/releases/download/$PieVersion/pie.phar"
Write-Host "Downloading PIE $PieVersion..."
New-Item -ItemType Directory -Path (Split-Path $Dest) -Force | Out-Null
Invoke-WebRequest -Uri $url -OutFile $Dest -UseBasicParsing

if (-not (Test-PiePharReady $Dest)) {
    throw "pie.phar download failed or file is too small: $Dest"
}

Write-Host "Saved pie.phar ($([math]::Round((Get-Item $Dest).Length / 1MB, 2)) MB)"
return $Dest
