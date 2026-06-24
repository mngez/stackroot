# Best-effort restart after upgrade when Test DNS was enabled before setup stopped the helper.

$ErrorActionPreference = 'Stop'

$ServiceName = 'StackrootDnsHelper'
$ConfigPath = Join-Path $env:ProgramData 'Stackroot\dns-helper.json'

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    exit 0
}

try {
    $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
}
catch {
    exit 0
}

if (-not $config.enabled) {
    exit 0
}

$query = & sc.exe query $ServiceName 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    exit 0
}

if ($query -match 'RUNNING') {
    exit 0
}

& sc.exe config $ServiceName start= delayed-auto 2>&1 | Out-Null

& sc.exe start $ServiceName 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Stackroot DNS Helper did not start during setup (exit $LASTEXITCODE). Stackroot will retry on launch."
    exit 0
}

$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
    $state = & sc.exe query $ServiceName 2>&1 | Out-String
    if ($state -match 'RUNNING') {
        exit 0
    }

    Start-Sleep -Milliseconds 500
}

Write-Host "Stackroot DNS Helper is starting slowly; Stackroot will finish on launch."
exit 0
