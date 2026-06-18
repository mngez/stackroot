param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"

function Resolve-MakensisPath {
    param([string]$Root)

    $candidates = @(
        $env:NSIS_PATH,
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
        "$env:ProgramFiles\NSIS\makensis.exe",
        (Join-Path $Root "resources\tools\nsis\makensis.exe")
    )

    foreach ($path in $candidates) {
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        if (Test-Path $path) { return (Resolve-Path $path).Path }
    }

    $toolsRoot = Join-Path $Root "resources\tools\nsis"
    if (Test-Path $toolsRoot) {
        $nested = Get-ChildItem $toolsRoot -Recurse -Filter makensis.exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($nested) { return $nested.FullName }
    }

    $cmd = Get-Command makensis -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    return $null
}

$existing = Resolve-MakensisPath -Root $RepoRoot
if ($existing) { return $existing }

if (Get-Command winget -ErrorAction SilentlyContinue) {
    Write-Host "NSIS not found. Installing via winget..."
    winget install --id NSIS.NSIS -e --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Null
    $existing = Resolve-MakensisPath -Root $RepoRoot
    if ($existing) { return $existing }
}

throw @"
NSIS (makensis) is required to build the installer.
Install from https://nsis.sourceforge.io/Download
or run: winget install --id NSIS.NSIS -e
Then rerun: ./sr pack
"@
