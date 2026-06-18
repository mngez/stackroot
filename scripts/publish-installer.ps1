param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src/Stackroot.App/Stackroot.App.csproj"

if (-not (Test-Path $projectPath)) {
    throw "Could not find project file at $projectPath"
}

$projectXml = [xml](Get-Content $projectPath -Raw)
$targetFramework = $projectXml.Project.PropertyGroup |
    ForEach-Object { $_.TargetFramework } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "TargetFramework missing in $projectPath"
}

Write-Host "Publishing Stackroot.App ($Configuration / $Runtime)..."
dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained

$publishOutput = Join-Path $repoRoot "src/Stackroot.App/bin/$Configuration/$targetFramework/$Runtime/publish"
if (-not (Test-Path $publishOutput)) {
    throw "Publish output not found at $publishOutput"
}

$resourcesSource = Join-Path $repoRoot "resources/packages"
$resourcesDestination = Join-Path $publishOutput "resources/packages"
New-Item -ItemType Directory -Path $resourcesDestination -Force | Out-Null

& (Join-Path $PSScriptRoot "ensure-pie.ps1") -RepoRoot $repoRoot | Out-Null

foreach ($name in @("catalog.json", "php-extensions.json", "pie.phar")) {
    $source = Join-Path $resourcesSource $name
    if (-not (Test-Path $source)) {
        throw "Missing bootstrap resource: $source"
    }

    Copy-Item $source (Join-Path $resourcesDestination $name) -Force
}

$sevenZipSource = Join-Path $repoRoot "resources/tools/7zip/7za.exe"
if (Test-Path $sevenZipSource) {
    $sevenZipDestination = Join-Path $publishOutput "resources/tools/7zip"
    New-Item -ItemType Directory -Path $sevenZipDestination -Force | Out-Null
    Copy-Item $sevenZipSource (Join-Path $sevenZipDestination "7za.exe") -Force
}

$iconsSource = Join-Path $repoRoot "assets/icons"
$iconsDestination = Join-Path $publishOutput "assets/icons"
New-Item -ItemType Directory -Path $iconsDestination -Force | Out-Null
Copy-Item (Join-Path $iconsSource "*") $iconsDestination -Recurse -Force

Write-Host "Publish completed."
Write-Host "Output: $publishOutput"
return $publishOutput
