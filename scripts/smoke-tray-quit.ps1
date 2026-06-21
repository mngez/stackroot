#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Manual smoke: tray Quit leaves no Stackroot-managed service orphans.

.DESCRIPTION
  Run while Stackroot is closed. After each manual Quit from the tray menu,
  run this script to list common service processes that may have been left behind.

  This does NOT automate Quit — it only helps verify T4 manually.
#>
[CmdletBinding()]
param(
    [string]$StackrootRoot = $(if ($env:STACKROOT_DATA) { $env:STACKROOT_DATA } else { Join-Path $env:APPDATA "Stackroot" })
)

$ErrorActionPreference = "Continue"
$names = @("nginx", "redis-server", "memcached", "mysqld", "php-cgi", "postgres", "mongod", "mailpit")
$found = @()

foreach ($name in $names) {
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    foreach ($proc in $procs) {
        $path = $null
        try { $path = $proc.Path } catch { }
        $underStackroot = $path -and ($path -like "*$StackrootRoot*" -or $path -like "*\Stackroot\*")
        if ($underStackroot -or -not $path) {
            $found += [pscustomobject]@{
                Name = $name
                Pid  = $proc.Id
                Path = if ($path) { $path } else { "(path unavailable)" }
            }
        }
    }
}

Write-Host ""
Write-Host "=== Stackroot tray-quit orphan check ===" -ForegroundColor Cyan
Write-Host "Data root: $StackrootRoot"
Write-Host ""

if ($found.Count -eq 0) {
    Write-Host "[PASS] No obvious Stackroot-managed service processes found." -ForegroundColor Green
    exit 0
}

Write-Host "[WARN] Possible orphan processes ($($found.Count)):" -ForegroundColor Yellow
$found | Format-Table -AutoSize
Write-Host "If Stackroot is fully quit, these should not remain." -ForegroundColor Yellow
exit 1
