# Ensures .NET Desktop Runtime is installed during setup.
# Tries the latest online installer first; falls back to the bundled offline copy.

param(
    [string]$BundledInstallerPath,
    [int]$MajorVersion = 0,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
} catch {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDir 'dotnet-prereq.ps1')

if ($MajorVersion -le 0) {
    $MajorVersion = $DotNetDesktopMajor
}

if ($MajorVersion -le 0) {
    throw 'DotNetDesktopMajor was not resolved. dotnet-prereq.ps1 may be missing or invalid.'
}

function Get-DotNetSharedFamilyRoots {
    param([string]$FrameworkFamily)

    $roots = [System.Collections.Generic.List[string]]::new()
    $dotnetBases = @()

    if ($env:ProgramW6432) {
        $dotnetBases += Join-Path $env:ProgramW6432 'dotnet'
    }

    $dotnetBases += Join-Path $env:ProgramFiles 'dotnet'
    $dotnetBases += Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'

    foreach ($base in ($dotnetBases | Select-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace($base)) {
            continue
        }

        $shared = Join-Path $base "shared\$FrameworkFamily"
        if (Test-Path -LiteralPath $shared) {
            $roots.Add($shared)
        }
    }

    return $roots
}

function Test-SharedFrameworkOnDisk {
    param(
        [int]$Major,
        [string]$FrameworkFamily
    )

    foreach ($sharedRoot in Get-DotNetSharedFamilyRoots -FrameworkFamily $FrameworkFamily) {
        foreach ($dir in Get-ChildItem -LiteralPath $sharedRoot -Directory -ErrorAction SilentlyContinue) {
            if ($dir.Name -like "$Major.*") {
                return $true
            }
        }
    }

    return $false
}

function Test-StackrootRuntimesInstalled {
    param([int]$Major)

    # Registry alone is not trusted — stale keys can skip install while apps still fail to start.
    $coreInstalled = Test-SharedFrameworkOnDisk -Major $Major -FrameworkFamily 'Microsoft.NETCore.App'
    $desktopInstalled = Test-SharedFrameworkOnDisk -Major $Major -FrameworkFamily 'Microsoft.WindowsDesktop.App'

    if (-not $coreInstalled) {
        Write-Host "Missing on disk: Microsoft.NETCore.App $Major.x (required by Stackroot launcher)."
    }

    if (-not $desktopInstalled) {
        Write-Host "Missing on disk: Microsoft.WindowsDesktop.App $Major.x (required by Stackroot app)."
    }

    return $coreInstalled -and $desktopInstalled
}

function Wait-ForStackrootRuntimes {
    param(
        [int]$Major,
        [int]$Attempts = 15,
        [int]$DelaySeconds = 2
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        if (Test-StackrootRuntimesInstalled -Major $Major) {
            return $true
        }

        Write-Host "Waiting for .NET $Major runtimes to register ($attempt/$Attempts)..."
        Start-Sleep -Seconds $DelaySeconds
    }

    return $false
}

function Test-BundledInstallerAvailable {
    param([string]$InstallerPath)

    -not [string]::IsNullOrWhiteSpace($InstallerPath) `
        -and (Test-Path -LiteralPath $InstallerPath) `
        -and (Get-Item -LiteralPath $InstallerPath).Length -gt 0
}

function Get-LatestDesktopRuntimeDownloadUrl {
    param([int]$Major)

    $metadataUrl = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/$Major.0/releases.json"
    Write-Host "Checking for latest .NET $Major Desktop Runtime: $metadataUrl"
    $metadata = Invoke-RestMethod -Uri $metadataUrl -TimeoutSec 30
    $latestVersion = $metadata.'latest-runtime'
    if ([string]::IsNullOrWhiteSpace($latestVersion)) {
        return $null
    }

    $runtimeEntry = $metadata.runtime | Where-Object { $_.version -eq $latestVersion } | Select-Object -First 1
    if ($null -eq $runtimeEntry) {
        return $null
    }

    $file = $runtimeEntry.files | Where-Object { $_.name -eq 'windowsdesktop-runtime-win-x64.exe' } | Select-Object -First 1
    if ($null -eq $file -or [string]::IsNullOrWhiteSpace($file.url)) {
        return $null
    }

    Write-Host "Latest .NET Desktop Runtime: $latestVersion"
    return $file.url
}

function Install-FromFile {
    param([string]$InstallerPath)

    if (-not (Test-Path -LiteralPath $InstallerPath) -or (Get-Item -LiteralPath $InstallerPath).Length -eq 0) {
        throw "Installer file is missing or empty: $InstallerPath"
    }

    Write-Host "Running installer: $InstallerPath"
    $process = Start-Process -FilePath $InstallerPath -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    # 3010 = restart required; 1638 = same or newer version already installed
    if ($process.ExitCode -notin 0, 3010, 1638) {
        throw ".NET Desktop Runtime installer exited with code $($process.ExitCode)."
    }

    if ($process.ExitCode -eq 3010) {
        Write-Host 'Installer reported success with reboot recommended (3010).'
    }
}

function Try-InstallFromUrl {
    param([string]$Url)

    $destination = Join-Path $env:TEMP ("windowsdesktop-runtime-latest-" + [guid]::NewGuid().ToString('n') + '.exe')
    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $destination -UseBasicParsing
    Install-FromFile -InstallerPath $destination
    Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
}

function Assert-StackrootRuntimesReady {
    param([int]$Major)

    if (Wait-ForStackrootRuntimes -Major $Major) {
        return
    }

    $searched = @()
    foreach ($family in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        foreach ($root in Get-DotNetSharedFamilyRoots -FrameworkFamily $family) {
            $searched += $root
        }
    }

    $searched = $searched | Select-Object -Unique
    $detail = if ($searched.Count -gt 0) {
        "Searched: $($searched -join '; ')"
    } else {
        'Searched: no dotnet\shared folders found under Program Files or LocalAppData.'
    }

    throw ".NET runtime installation finished but Microsoft.NETCore.App and Microsoft.WindowsDesktop.App were not detected on disk. $detail"
}

if (Test-StackrootRuntimesInstalled -Major $MajorVersion) {
    Write-Host ".NET $MajorVersion runtimes are already installed (Core + Desktop)."
    exit 0
}

if ($CheckOnly) {
    Write-Host ".NET $MajorVersion runtimes are not ready (Core + Desktop required)."
    exit 2
}

Write-Host "Installing .NET $MajorVersion Desktop Runtime..."

$hasBundledInstaller = Test-BundledInstallerAvailable -InstallerPath $BundledInstallerPath
if ($hasBundledInstaller) {
    Write-Host "Bundled fallback available: $BundledInstallerPath"
}

$onlineError = $null
try {
    $latestUrl = Get-LatestDesktopRuntimeDownloadUrl -Major $MajorVersion
    if ([string]::IsNullOrWhiteSpace($latestUrl)) {
        $onlineError = 'Could not resolve the latest .NET Desktop Runtime download URL.'
        Write-Host $onlineError
    } else {
        Try-InstallFromUrl -Url $latestUrl
        if (Test-StackrootRuntimesInstalled -Major $MajorVersion) {
            Write-Host '.NET Desktop Runtime installed from the latest online build.'
            exit 0
        }

        $onlineError = 'Online installer finished but required .NET runtimes were not detected on disk.'
        Write-Host $onlineError
    }
}
catch {
    $onlineError = $_.Exception.Message
    Write-Host "Online install failed: $onlineError"
}

if (-not $hasBundledInstaller) {
    if ($onlineError) {
        throw "Could not install .NET Desktop Runtime online ($onlineError) and no bundled installer was available."
    }

    throw 'No bundled .NET Desktop Runtime installer was available and the online install did not succeed.'
}

if ($onlineError) {
    Write-Host "Falling back to bundled offline .NET Desktop Runtime installer."
}

Write-Host "Using bundled offline .NET Desktop Runtime installer: $BundledInstallerPath"
Install-FromFile -InstallerPath $BundledInstallerPath
Assert-StackrootRuntimesReady -Major $MajorVersion

Write-Host '.NET Desktop Runtime installed from the bundled offline copy.'
exit 0
