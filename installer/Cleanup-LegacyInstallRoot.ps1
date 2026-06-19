# Removes 0.1/0.2 self-contained payload left in the install root after upgrading
# to the thin-launcher layout (root launcher + app\{version}\ payload).
# Leaving hostfxr.dll / coreclr.dll beside the new launcher breaks startup.

param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'

$installRoot = [System.IO.Path]::GetFullPath($InstallDir)
if (-not (Test-Path -LiteralPath $installRoot)) {
    exit 0
}

$legacyMarkers = @('hostfxr.dll', 'coreclr.dll', 'Stackroot.dll')
$hasLegacy = $false
foreach ($name in $legacyMarkers) {
    if (Test-Path -LiteralPath (Join-Path $installRoot $name)) {
        $hasLegacy = $true
        break
    }
}

if (-not $hasLegacy) {
    exit 0
}

$keepFiles = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@('Stackroot.exe', 'current.txt', 'launcher.version', 'Uninstall.exe'),
    [StringComparer]::OrdinalIgnoreCase)

$keepDirs = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@('app'),
    [StringComparer]::OrdinalIgnoreCase)

Write-Host "Legacy install root detected; cleaning $installRoot"

foreach ($file in Get-ChildItem -LiteralPath $installRoot -File -Force) {
    if ($keepFiles.Contains($file.Name)) {
        continue
    }

    Remove-Item -LiteralPath $file.FullName -Force
}

foreach ($dir in Get-ChildItem -LiteralPath $installRoot -Directory -Force) {
    if ($keepDirs.Contains($dir.Name)) {
        continue
    }

    Remove-Item -LiteralPath $dir.FullName -Recurse -Force
}

exit 0
