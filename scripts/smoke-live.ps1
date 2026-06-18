#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Live smoke checks against a real Stackroot data directory (%APPDATA%\Stackroot).

.DESCRIPTION
  Validates user-facing journeys from the UI/CLI perspective - not isolated Core unit tests.
  See docs/PARITY-PLAN.md for acceptance criteria and progress tracking.

.PARAMETER StackrootRoot
  Override data root (default: $env:APPDATA\Stackroot).

.PARAMETER RepoRoot
  Repository root for bundled 7za lookup (default: parent of scripts/).
#>
[CmdletBinding()]
param(
    [string]$StackrootRoot = $(if ($env:STACKROOT_DATA) { $env:STACKROOT_DATA } else { Join-Path $env:APPDATA "Stackroot" }),
    [string]$RepoRoot = ""
)

if (-not $RepoRoot) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepoRoot = Split-Path -Parent $scriptDir
}

$ErrorActionPreference = "Continue"
$script:Checks = [System.Collections.Generic.List[object]]::new()
$script:FailCount = 0
$script:WarnCount = 0

function Add-Check {
    param(
        [string]$Id,
        [string]$Name,
        [ValidateSet("PASS", "FAIL", "WARN", "SKIP")]
        [string]$Status,
        [string]$Detail = ""
    )
    $icon = switch ($Status) {
        "PASS" { "[PASS]" }
        "FAIL" { "[FAIL]"; $script:FailCount++ }
        "WARN" { "[WARN]"; $script:WarnCount++ }
        "SKIP" { "[SKIP]" }
    }
    $script:Checks.Add([pscustomobject]@{ Id = $Id; Name = $Name; Status = $Status; Detail = $Detail })
    $line = "$icon $Id - $Name"
    if ($Detail) { $line += " | $Detail" }
    switch ($Status) {
        "FAIL" { Write-Host $line -ForegroundColor Red }
        "WARN" { Write-Host $line -ForegroundColor Yellow }
        "PASS" { Write-Host $line -ForegroundColor Green }
        default { Write-Host $line }
    }
}

function Invoke-Shim {
    param(
        [string]$Path,
        [string[]]$ShimArgs = @("--version")
    )
    if (-not (Test-Path -LiteralPath $Path)) {
        return @{ Ok = $false; Output = "shim missing: $Path" }
    }
    try {
        $text = (& $Path @ShimArgs 2>&1 | Out-String).Trim()
        $ok = $false
        if ($text -match "PHP \d|Composer version|Laravel Installer|vite/|pnpm|v\d+\.\d+|\d+\.\d+\.\d+") {
            $ok = $true
        }
        return @{ Ok = $ok; Output = if ($text) { $text } else { "(no output)" } }
    }
    catch {
        return @{ Ok = $false; Output = $_.Exception.Message }
    }
}

function Test-TcpPort {
    param([string]$HostName = "127.0.0.1", [int]$Port, [int]$TimeoutMs = 800)
    if ($Port -le 0) { return $false }
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $task = $client.ConnectAsync($HostName, $Port)
        if (-not $task.Wait($TimeoutMs)) { return $false }
        $client.Close()
        return $true
    }
    catch { return $false }
}

function Test-NpmPrefixHealthy {
    param([string]$InstallPath, [string]$CmdName)
    if (-not (Test-Path -LiteralPath $InstallPath)) { return $false }
    $bin = Join-Path $InstallPath "node_modules\.bin\$CmdName"
    return Test-Path -LiteralPath $bin
}

function Resolve-SevenZipPath {
    param([string]$Root)
    $envPath = $env:STACKROOT_7Z
    if ($envPath -and (Test-Path -LiteralPath $envPath)) { return $envPath }
    $resourcesBundled = Join-Path $Root "resources\tools\7zip\7za.exe"
    if (Test-Path -LiteralPath $resourcesBundled) { return $resourcesBundled }
    return $null
}

Write-Host ""
Write-Host "Stackroot live smoke" -ForegroundColor Cyan
Write-Host "Data root: $StackrootRoot"
Write-Host "Repo root: $RepoRoot"
Write-Host ("-" * 60)

# --- Prerequisites ---
if (-not (Test-Path -LiteralPath $StackrootRoot)) {
    Add-Check -Id "00" -Name "Data root exists" -Status "FAIL" -Detail $StackrootRoot
    Write-Host ""
    Write-Host "Smoke aborted: no Stackroot data directory." -ForegroundColor Red
    exit 1
}
Add-Check -Id "00" -Name "Data root exists" -Status "PASS" -Detail $StackrootRoot

$settingsPath = Join-Path $StackrootRoot "settings.json"
$installedPath = Join-Path $StackrootRoot "installed.json"
$binDir = Join-Path $StackrootRoot "runtime\bin"

$settings = $null
$installed = $null

if (Test-Path -LiteralPath $settingsPath) {
    try {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        Add-Check -Id "00a" -Name "settings.json readable" -Status "PASS"
    }
    catch {
        Add-Check -Id "00a" -Name "settings.json readable" -Status "FAIL" -Detail $_.Exception.Message
    }
}
else {
    Add-Check -Id "00a" -Name "settings.json readable" -Status "FAIL" -Detail "missing"
}

if (Test-Path -LiteralPath $installedPath) {
    try {
        $installed = Get-Content -LiteralPath $installedPath -Raw | ConvertFrom-Json
        Add-Check -Id "00b" -Name "installed.json readable" -Status "PASS"
    }
    catch {
        Add-Check -Id "00b" -Name "installed.json readable" -Status "FAIL" -Detail $_.Exception.Message
    }
}
else {
    Add-Check -Id "00b" -Name "installed.json readable" -Status "FAIL" -Detail "missing"
}

# --- [1] 7-Zip ---
$sevenZip = Resolve-SevenZipPath -Root $RepoRoot
if ($sevenZip) {
    Add-Check -Id "01" -Name "7-Zip (7za.exe) available" -Status "PASS" -Detail $sevenZip
}
else {
    Add-Check -Id "01" -Name "7-Zip (7za.exe) available" -Status "FAIL" -Detail "Set STACKROOT_7Z or bundle resources/tools/7zip/7za.exe"
}

# --- [2] Node / nvm ---
$nvmCmd = Join-Path $binDir "nvm.cmd"
$nvmExe = Join-Path $StackrootRoot "runtime\nvm\nvm.exe"
if ((Test-Path -LiteralPath $nvmCmd) -or (Test-Path -LiteralPath $nvmExe)) {
    Add-Check -Id "02" -Name "nvm installed" -Status "PASS"
}
else {
    Add-Check -Id "02" -Name "nvm installed" -Status "FAIL"
}

$nodeShim = Join-Path $binDir "node.cmd"
$nodeResult = Invoke-Shim -Path $nodeShim -ShimArgs @("-v")
if ($nodeResult.Ok) {
    $active = if ($settings) { $settings.node.activeVersion } else { "?" }
    Add-Check -Id "03" -Name "Node active (node.cmd -v)" -Status "PASS" -Detail "$($nodeResult.Output.Trim()) (settings: $active)"
}
else {
    Add-Check -Id "03" -Name "Node active (node.cmd -v)" -Status "FAIL" -Detail $nodeResult.Output
}

foreach ($pair in @(
        @{ Id = "03a"; Name = "npm shim"; File = "npm.cmd" }
        @{ Id = "03b"; Name = "npx shim"; File = "npx.cmd" }
    )) {
    $shim = Join-Path $binDir $pair.File
    if (Test-Path -LiteralPath $shim) {
        Add-Check -Id $pair.Id -Name $pair.Name -Status "PASS"
    }
    else {
        Add-Check -Id $pair.Id -Name $pair.Name -Status "FAIL" -Detail "missing $shim"
    }
}

# --- [3] CLI tools ---
foreach ($tool in @(
        @{ Id = "04"; Name = "Composer"; File = "composer.cmd"; Args = @("--version") }
        @{ Id = "05"; Name = "Laravel"; File = "laravel.cmd"; Args = @("--version") }
        @{ Id = "06"; Name = "pnpm"; File = "pnpm.cmd"; Args = @("-v") }
        @{ Id = "07"; Name = "Vite"; File = "vite.cmd"; Args = @("--version") }
    )) {
    $shim = Join-Path $binDir $tool.File
    $result = Invoke-Shim -Path $shim -ShimArgs $tool.Args
    if ($result.Ok) {
        $firstLine = ($result.Output -split "`n")[0].Trim()
        if ($tool.Id -eq "07" -and $result.Output -match "requires Node\.js version") {
            Add-Check -Id $tool.Id -Name $tool.Name -Status "WARN" -Detail "shim works but Node version too old for Vite 7"
        }
        else {
            Add-Check -Id $tool.Id -Name $tool.Name -Status "PASS" -Detail $firstLine
        }
    }
    else {
        Add-Check -Id $tool.Id -Name $tool.Name -Status "FAIL" -Detail $result.Output
    }
}

# pnpm/vite install folder health (registry vs reality)
if ($installed -and $installed.packages) {
    foreach ($pkg in $installed.packages) {
        if ($pkg.type -eq "pnpm") {
            $healthy = Test-NpmPrefixHealthy -InstallPath $pkg.installPath -CmdName "pnpm.cmd"
            $tarOnly = (Get-ChildItem -LiteralPath $pkg.installPath -Filter "*.tar" -ErrorAction SilentlyContinue).Count -gt 0 -and -not $healthy
            if ($healthy) {
                Add-Check -Id "06a" -Name "pnpm install folder healthy" -Status "PASS" -Detail $pkg.installPath
            }
            elseif ($tarOnly) {
                Add-Check -Id "06a" -Name "pnpm install folder healthy" -Status "FAIL" -Detail "broken: .tar only at $($pkg.installPath)"
            }
            else {
                Add-Check -Id "06a" -Name "pnpm install folder healthy" -Status "FAIL" -Detail $pkg.installPath
            }
        }
        if ($pkg.type -eq "vite") {
            $healthy = Test-NpmPrefixHealthy -InstallPath $pkg.installPath -CmdName "vite.cmd"
            if ($healthy) {
                Add-Check -Id "07a" -Name "vite install folder healthy" -Status "PASS" -Detail $pkg.installPath
            }
            else {
                Add-Check -Id "07a" -Name "vite install folder healthy" -Status "FAIL" -Detail $pkg.installPath
            }
        }
    }
}

# --- [4] PHP ---
$phpShim = Join-Path $binDir "php.cmd"
$phpResult = Invoke-Shim -Path $phpShim -ShimArgs @("-v")
if ($phpResult.Ok) {
    $warn = $phpResult.Output -match "Invalid library|Unable to load.*opcache"
    if ($warn) {
        Add-Check -Id "08" -Name "PHP active (php.cmd -v)" -Status "WARN" -Detail "opcache/extension warning in output"
    }
    else {
        $ver = ($phpResult.Output -split "`n")[0].Trim()
        Add-Check -Id "08" -Name "PHP active (php.cmd -v)" -Status "PASS" -Detail $ver
    }
}
else {
    Add-Check -Id "08" -Name "PHP active (php.cmd -v)" -Status "FAIL" -Detail $phpResult.Output
}

$configRoot = Join-Path $StackrootRoot "config"
$activePhpId = if ($settings) { $settings.php.activeVersionId } else { $null }
if ($activePhpId) {
    $iniFlat = Join-Path $configRoot "php\$activePhpId.ini"
    if (Test-Path -LiteralPath $iniFlat) {
        $iniText = Get-Content -LiteralPath $iniFlat -Raw
        if ($iniText -match "zend_extension\s*=\s*opcache") {
            Add-Check -Id "08a" -Name "PHP opcache (zend_extension)" -Status "PASS" -Detail $iniFlat
        }
        elseif ($iniText -match "extension\s*=\s*opcache") {
            Add-Check -Id "08a" -Name "PHP opcache (zend_extension)" -Status "WARN" -Detail "uses extension=opcache - may warn on CLI"
        }
        else {
            Add-Check -Id "08a" -Name "PHP opcache (zend_extension)" -Status "WARN" -Detail "opcache line not found"
        }
    }
    else {
        Add-Check -Id "08a" -Name "PHP opcache (zend_extension)" -Status "WARN" -Detail "ini missing: $iniFlat"
    }
}

# --- [5] nginx ---
$nginxConf = Join-Path $configRoot "nginx\conf\nginx.conf"
if (Test-Path -LiteralPath $nginxConf) {
    Add-Check -Id "10" -Name "nginx config exists" -Status "PASS" -Detail $nginxConf
}
else {
    Add-Check -Id "10" -Name "nginx config exists" -Status "WARN" -Detail "missing - install/start nginx from Services"
}

$nginxPort = 80
if ($settings -and $settings.services -and $settings.services.nginx) {
    $nginxPort = [int]$settings.services.nginx.port
    if ($nginxPort -le 0) { $nginxPort = 80 }
}
if (Test-TcpPort -Port $nginxPort) {
    Add-Check -Id "10a" -Name "nginx port open" -Status "PASS" -Detail "127.0.0.1:$nginxPort"
}
else {
    Add-Check -Id "10a" -Name "nginx port open" -Status "WARN" -Detail "127.0.0.1:$nginxPort closed - start nginx from Services"
}

# --- [6] MySQL / MariaDB ---
$sqlPort = 0
$sqlName = "none"
if ($settings) {
    $engine = $settings.databases.activeSqlEngine
    if ($engine -eq "mysql" -and $settings.services.mysql) {
        $sqlPort = [int]$settings.services.mysql.port
        $sqlName = "mysql"
    }
    elseif ($engine -eq "mariadb" -and $settings.services.mariadb) {
        $sqlPort = [int]$settings.services.mariadb.port
        $sqlName = "mariadb"
    }
}
if ($sqlPort -gt 0) {
    if (Test-TcpPort -Port $sqlPort) {
        Add-Check -Id "11" -Name "SQL engine port open ($sqlName)" -Status "PASS" -Detail "127.0.0.1:$sqlPort"
    }
    else {
        Add-Check -Id "11" -Name "SQL engine port open ($sqlName)" -Status "WARN" -Detail "127.0.0.1:$sqlPort closed"
    }
}
else {
    Add-Check -Id "11" -Name "SQL engine port open" -Status "SKIP" -Detail "no active SQL engine in settings"
}

# --- Sites ---
$sitesPath = Join-Path $StackrootRoot "sites.json"
if (Test-Path -LiteralPath $sitesPath) {
    try {
        $sitesDoc = Get-Content -LiteralPath $sitesPath -Raw | ConvertFrom-Json
        $enabled = @($sitesDoc.sites | Where-Object { $_.enabled -ne $false })
        Add-Check -Id "12" -Name "Sites defined" -Status "PASS" -Detail "$($enabled.Count) enabled site(s)"
        foreach ($site in $enabled | Select-Object -First 3) {
            $domain = $site.domain
            if ($domain) {
                try {
                    $uri = "http://${domain}/"
                    $resp = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
                    Add-Check -Id "12a" -Name "Site HTTP $domain" -Status "PASS" -Detail "HTTP $($resp.StatusCode)"
                }
                catch {
                    Add-Check -Id "12a" -Name "Site HTTP $domain" -Status "WARN" -Detail $_.Exception.Message
                }
            }
        }
        if ($enabled.Count -eq 0) {
            Add-Check -Id "12a" -Name "Site HTTP probe" -Status "SKIP" -Detail "no enabled sites"
        }
    }
    catch {
        Add-Check -Id "12" -Name "Sites defined" -Status "FAIL" -Detail $_.Exception.Message
    }
}
else {
    Add-Check -Id "12" -Name "Sites defined" -Status "WARN" -Detail "sites.json missing"
}

# --- phpMyAdmin ---
$pmaPkg = $null
if ($installed -and $installed.packages) {
    $pmaPkg = $installed.packages | Where-Object { $_.type -eq "phpmyadmin" } | Select-Object -First 1
}
if ($pmaPkg -and (Test-Path -LiteralPath $pmaPkg.installPath)) {
    Add-Check -Id "13" -Name "phpMyAdmin package on disk" -Status "PASS" -Detail $pmaPkg.installPath
    $appDomain = if ($settings) { $settings.general.appDomain } else { "stackroot.test" }
    $pmaPath = if ($settings -and $settings.phpmyadmin) { $settings.phpmyadmin.path } else { "phpmyadmin" }
    if (-not $pmaPath) { $pmaPath = "phpmyadmin" }
    $portSuffix = if ($nginxPort -eq 80) { "" } else { ":$nginxPort" }
    $url = "http://${appDomain}${portSuffix}/${pmaPath}/"
    try {
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 8 -ErrorAction Stop
        Add-Check -Id "13a" -Name "phpMyAdmin HTTP" -Status "PASS" -Detail "$url -> $($resp.StatusCode)"
    }
    catch {
        Add-Check -Id "13a" -Name "phpMyAdmin HTTP" -Status "WARN" -Detail "$url | $($_.Exception.Message)"
    }
}
else {
    Add-Check -Id "13" -Name "phpMyAdmin package on disk" -Status "WARN" -Detail "not installed"
}

# --- Mailpit ---
$mailpitPort = 8025
if ($settings -and $settings.mailpit) {
    $mailpitPort = [int]$settings.mailpit.webPort
    if ($mailpitPort -le 0) { $mailpitPort = 8025 }
}
if ($settings -and $settings.mailpit -and $settings.mailpit.enabled -eq $true) {
    if (Test-TcpPort -Port $mailpitPort) {
        Add-Check -Id "15" -Name "Mailpit web port" -Status "PASS" -Detail "127.0.0.1:$mailpitPort"
    }
    else {
        Add-Check -Id "15" -Name "Mailpit web port" -Status "WARN" -Detail "127.0.0.1:$mailpitPort closed"
    }
}
else {
    Add-Check -Id "15" -Name "Mailpit web port" -Status "SKIP" -Detail "disabled in settings"
}

# --- Summary ---
Write-Host ("-" * 60)
$pass = @($script:Checks | Where-Object Status -eq "PASS").Count
$fail = @($script:Checks | Where-Object Status -eq "FAIL").Count
$warn = @($script:Checks | Where-Object Status -eq "WARN").Count
$skip = @($script:Checks | Where-Object Status -eq "SKIP").Count
Write-Host "Summary: PASS=$pass  FAIL=$fail  WARN=$warn  SKIP=$skip" -ForegroundColor Cyan

if ($fail -gt 0) {
    Write-Host ""
    Write-Host "Failures (fix these first - see docs/PARITY-PLAN.md section 4):" -ForegroundColor Red
    $script:Checks | Where-Object Status -eq "FAIL" | ForEach-Object {
        Write-Host "  $($_.Id) $($_.Name): $($_.Detail)"
    }
}

# Write machine-readable snapshot next to data root for diffing across sessions
$reportPath = Join-Path $StackrootRoot "smoke-last.json"
$report = @{
    generatedAt = (Get-Date).ToString("o")
    stackrootRoot = $StackrootRoot
    summary = @{ pass = $pass; fail = $fail; warn = $warn; skip = $skip }
    checks = $script:Checks
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host ""
Write-Host "Report saved: $reportPath"

exit $(if ($fail -gt 0) { 1 } else { 0 })
