# Shared download helper for CI/installer prerequisite scripts.
# Prefers curl.exe (better redirect + retry behavior for aka.ms) and falls back to
# Invoke-WebRequest with explicit retries. Partial files are removed between attempts.

function Download-FileWithRetry {
    param(
        [Parameter(Mandatory)]
        [string]$Uri,

        [Parameter(Mandatory)]
        [string]$OutFile,

        [int]$MaxAttempts = 5,

        [int]$RetryDelaySeconds = 3
    )

    $directory = Split-Path -Parent $OutFile
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    $lastError = $null

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        if (Test-Path -LiteralPath $OutFile) {
            Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
        }

        Write-Host "Download attempt $attempt/$MaxAttempts : $Uri"

        try {
            if ($null -ne $curl) {
                # --retry-all-errors covers truncated responses / connection resets that
                # aka.ms and download.visualstudio.microsoft.com occasionally produce on CI.
                & curl.exe `
                    --fail `
                    --silent `
                    --show-error `
                    --location `
                    --retry 3 `
                    --retry-all-errors `
                    --retry-delay $RetryDelaySeconds `
                    --connect-timeout 30 `
                    --max-time 600 `
                    --output $OutFile `
                    $Uri
                if ($LASTEXITCODE -ne 0) {
                    throw "curl.exe exited with code $LASTEXITCODE"
                }
            }
            else {
                Invoke-WebRequest -Uri $Uri -OutFile $OutFile -UseBasicParsing -TimeoutSec 600
            }

            if ((Test-Path -LiteralPath $OutFile) -and (Get-Item -LiteralPath $OutFile).Length -gt 0) {
                Write-Host ("Downloaded {0:N0} bytes -> {1}" -f (Get-Item -LiteralPath $OutFile).Length, $OutFile)
                return
            }

            throw "Downloaded file is missing or empty: $OutFile"
        }
        catch {
            $lastError = $_
            Write-Host "Download attempt $attempt failed: $($_.Exception.Message)"
            if (Test-Path -LiteralPath $OutFile) {
                Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
            }

            if ($attempt -lt $MaxAttempts) {
                Start-Sleep -Seconds ($RetryDelaySeconds * $attempt)
            }
        }
    }

    throw "Failed to download '$Uri' after $MaxAttempts attempts. Last error: $lastError"
}
