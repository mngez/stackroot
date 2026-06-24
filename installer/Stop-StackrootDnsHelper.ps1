# Clears stale helper status after sc stop during setup.
# Without this, Stackroot may think Test DNS is still running and skip auto-start.

param(
    [int]$WaitSeconds = 45
)

$ErrorActionPreference = 'Stop'

$ServiceName = 'StackrootDnsHelper'
$StatusPath = Join-Path $env:ProgramData 'Stackroot\dns-helper-status.json'

function Get-ServiceState {
    $output = & sc.exe query $ServiceName 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        return 'Missing'
    }

    if ($output -match 'RUNNING') {
        return 'Running'
    }

    if ($output -match 'STOPPED') {
        return 'Stopped'
    }

    return 'Unknown'
}

function Stop-ServiceQuiet {
    & sc.exe stop $ServiceName 2>&1 | Out-Null
}

function Wait-ForNotRunning {
    param([int]$Seconds)

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-ServiceState) -ne 'Running') {
            return $true
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Clear-StaleStatusFile {
    $payload = @{
        listenerRunning = $false
        nrptActive      = $false
        lastError       = $null
        updatedAt       = (Get-Date).ToUniversalTime().ToString('o')
    }
    $json = $payload | ConvertTo-Json -Compress
    $dir = Split-Path -Parent $StatusPath
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    Set-Content -LiteralPath $StatusPath -Value $json -Encoding UTF8
}

$state = Get-ServiceState
if ($state -eq 'Missing' -or $state -eq 'Stopped') {
    Clear-StaleStatusFile
    exit 0
}

Write-Host "Stopping $ServiceName..."
Stop-ServiceQuiet
if (Wait-ForNotRunning 15) {
    Clear-StaleStatusFile
    exit 0
}

Write-Host "Requesting administrator approval to stop $ServiceName..."
try {
    $process = Start-Process `
        -FilePath 'sc.exe' `
        -ArgumentList "stop $ServiceName" `
        -Verb RunAs `
        -Wait `
        -PassThru `
        -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        Write-Error "sc.exe stop returned exit code $($process.ExitCode)."
        exit 1
    }
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}

if (Wait-ForNotRunning $WaitSeconds) {
    Clear-StaleStatusFile
    exit 0
}

Write-Error @"
Could not stop $ServiceName. Test DNS may still be active.
Disable Test DNS in Stackroot, stop the service from Windows Services, then run setup again.
"@
exit 1
