# Ensures Microsoft Visual C++ 2015-2022 Redistributable (x64) is installed during setup.
# Required for PHP on Windows (php.exe, php-cgi.exe) — provides VCRUNTIME140.dll.

param(
    [string]$BundledInstallerPath,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
} catch {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDir 'vc-redist-prereq.ps1')

function Test-VcRedistX64Installed {
    $system32 = Join-Path $env:windir 'System32'
    $dllPath = Join-Path $system32 'vcruntime140.dll'
    if (Test-Path -LiteralPath $dllPath) {
        return $true
    }

    $registryPaths = @(
        'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
    )

    foreach ($path in $registryPaths) {
        if (-not (Test-Path -LiteralPath $path)) {
            continue
        }

        $installed = (Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue).Installed
        if ($installed -eq 1) {
            return $true
        }
    }

    return $false
}

function Test-BundledInstallerAvailable {
    param([string]$InstallerPath)

    -not [string]::IsNullOrWhiteSpace($InstallerPath) `
        -and (Test-Path -LiteralPath $InstallerPath) `
        -and (Get-Item -LiteralPath $InstallerPath).Length -gt 0
}

function Install-FromFile {
    param([string]$InstallerPath)

    if (-not (Test-Path -LiteralPath $InstallerPath) -or (Get-Item -LiteralPath $InstallerPath).Length -eq 0) {
        throw "Installer file is missing or empty: $InstallerPath"
    }

    Write-Host "Running Visual C++ Redistributable installer: $InstallerPath"
    $process = Start-Process -FilePath $InstallerPath -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    # 3010 = restart required; 1638 = same or newer version already installed
    if ($process.ExitCode -notin 0, 3010, 1638) {
        throw "Visual C++ Redistributable installer exited with code $($process.ExitCode)."
    }

    if ($process.ExitCode -eq 3010) {
        Write-Host 'Installer reported success with reboot recommended (3010).'
    }
}

function Try-InstallFromUrl {
    param([string]$Url)

    $destination = Join-Path $env:TEMP ("vc-redist-x64-" + [guid]::NewGuid().ToString('n') + '.exe')
    Write-Host "Downloading $Url"

    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    $maxAttempts = 3
    $lastError = $null
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        if (Test-Path -LiteralPath $destination) {
            Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
        }

        try {
            if ($null -ne $curl) {
                & curl.exe --fail --silent --show-error --location --retry 2 --retry-all-errors `
                    --connect-timeout 30 --max-time 600 --output $destination $Url
                if ($LASTEXITCODE -ne 0) {
                    throw "curl.exe exited with code $LASTEXITCODE"
                }
            }
            else {
                Invoke-WebRequest -Uri $Url -OutFile $destination -UseBasicParsing -TimeoutSec 600
            }

            if ((Test-Path -LiteralPath $destination) -and (Get-Item -LiteralPath $destination).Length -gt 0) {
                Install-FromFile -InstallerPath $destination
                Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
                return
            }

            throw "Downloaded installer is missing or empty."
        }
        catch {
            $lastError = $_
            Write-Host "Download attempt $attempt/$maxAttempts failed: $($_.Exception.Message)"
            if ($attempt -lt $maxAttempts) {
                Start-Sleep -Seconds (2 * $attempt)
            }
        }
    }

    Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
    throw "Failed to download Visual C++ Redistributable after $maxAttempts attempts. Last error: $lastError"
}

function Assert-VcRedistReady {
    if (Test-VcRedistX64Installed) {
        return
    }

    throw 'Visual C++ Redistributable installation finished but VCRUNTIME140.dll was not detected (required for PHP on Windows).'
}

if (Test-VcRedistX64Installed) {
    Write-Host 'Visual C++ 2015-2022 Redistributable (x64) is already installed (required for PHP on Windows).'
    exit 0
}

if ($CheckOnly) {
    Write-Host 'Visual C++ 2015-2022 Redistributable (x64) is not installed. Required to run PHP (php.exe / php-cgi.exe) on Windows.'
    exit 2
}

Write-Host 'Installing Visual C++ 2015-2022 Redistributable (x64) for PHP on Windows...'

$hasBundledInstaller = Test-BundledInstallerAvailable -InstallerPath $BundledInstallerPath
if ($hasBundledInstaller) {
    Write-Host "Bundled fallback available: $BundledInstallerPath"
}

$onlineError = $null
try {
    if ([string]::IsNullOrWhiteSpace($VcRedistInstallerUrl)) {
        $onlineError = 'VcRedistInstallerUrl was not resolved.'
        Write-Host $onlineError
    } else {
        Try-InstallFromUrl -Url $VcRedistInstallerUrl
        if (Test-VcRedistX64Installed) {
            Write-Host 'Visual C++ Redistributable installed from the online build.'
            exit 0
        }

        $onlineError = 'Online installer finished but VCRUNTIME140.dll was not detected on disk.'
        Write-Host $onlineError
    }
}
catch {
    $onlineError = $_.Exception.Message
    Write-Host "Online install failed: $onlineError"
}

if (-not $hasBundledInstaller) {
    if ($onlineError) {
        throw "Could not install Visual C++ Redistributable online ($onlineError) and no bundled installer was available."
    }

    throw 'No bundled Visual C++ Redistributable installer was available and the online install did not succeed.'
}

if ($onlineError) {
    Write-Host 'Falling back to bundled offline Visual C++ Redistributable installer.'
}

Write-Host "Using bundled offline Visual C++ Redistributable installer: $BundledInstallerPath"
Install-FromFile -InstallerPath $BundledInstallerPath
Assert-VcRedistReady

Write-Host 'Visual C++ Redistributable installed from the bundled offline copy.'
exit 0
