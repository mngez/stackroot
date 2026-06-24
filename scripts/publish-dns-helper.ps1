param(
    [string]$Configuration = "Debug",
    [string]$Runtime = "win-x64",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$helperProject = Join-Path $repoRoot "src/Stackroot.DnsHelper/Stackroot.DnsHelper.csproj"
$outputDir = Join-Path $repoRoot "src/Stackroot.App/bin/$Configuration/net8.0-windows/dns-helper"
$helperExe = Join-Path $outputDir "StackrootDnsHelper.exe"

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

if (-not $Force -and (Test-Path $helperExe)) {
    $exeTime = (Get-Item $helperExe).LastWriteTimeUtc
    $inputs = @(
        $helperProject
        (Join-Path $repoRoot "src/Stackroot.Core.Dns/Stackroot.Core.Dns.csproj")
    )
    $sourceFiles = Get-ChildItem -Path @(
        (Join-Path $repoRoot "src/Stackroot.DnsHelper")
        (Join-Path $repoRoot "src/Stackroot.Core.Dns")
    ) -Recurse -File -Include *.cs, *.csproj, *.json -ErrorAction SilentlyContinue

    $latestInput = ($sourceFiles | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
    foreach ($input in $inputs) {
        if ((Test-Path $input) -and (Get-Item $input).LastWriteTimeUtc -gt $latestInput) {
            $latestInput = (Get-Item $input).LastWriteTimeUtc
        }
    }

    if ($exeTime -ge $latestInput) {
        Write-Host "DNS helper publish skipped (up to date)."
        exit 0
    }
}

dotnet build $helperProject -c $Configuration -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "dotnet build Stackroot.DnsHelper failed ($LASTEXITCODE)" }

$publishArgs = @(
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "false",
    "-p:PublishSingleFile=true",
    "-p:DebugType=none",
    "--no-restore",
    "-o", $outputDir
)

dotnet publish $helperProject @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish Stackroot.DnsHelper failed ($LASTEXITCODE)" }

Get-ChildItem $outputDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
Write-Host "DNS helper published to $outputDir"
