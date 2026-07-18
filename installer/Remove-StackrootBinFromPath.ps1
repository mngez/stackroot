# Removes Stackroot bin directories from the current user's PATH during uninstall.
# Does not delete AppData / LocalAppData data trees.

$ErrorActionPreference = 'Stop'

$entriesToRemove = @(
    (Join-Path $env:LOCALAPPDATA 'Stackroot\runtime\bin'),
    (Join-Path $env:APPDATA 'Stackroot\runtime\bin'),
    (Join-Path $env:APPDATA 'Stackroot\bin')
) | ForEach-Object { $_.TrimEnd('\') }

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ([string]::IsNullOrWhiteSpace($userPath)) {
    exit 0
}

$kept = New-Object System.Collections.Generic.List[string]
foreach ($entry in $userPath.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)) {
    $normalized = $entry.Trim().TrimEnd('\')
    $remove = $false
    foreach ($candidate in $entriesToRemove) {
        if ([string]::Equals($normalized, $candidate, [System.StringComparison]::OrdinalIgnoreCase)) {
            $remove = $true
            break
        }
    }

    if (-not $remove -and -not [string]::IsNullOrWhiteSpace($normalized)) {
        [void]$kept.Add($entry.Trim())
    }
}

$updated = [string]::Join(';', $kept)
if ($updated -ne $userPath) {
    [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
    Write-Host "Removed Stackroot bin entries from user PATH."
}
else {
    Write-Host "No Stackroot bin entries found in user PATH."
}
