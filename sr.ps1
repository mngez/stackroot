param([string]$Cmd = "dev")
$ErrorActionPreference = "Stop"
$r = $PSScriptRoot

switch -Regex ($Cmd.ToLower()) {
    "^(dev|run)$" {
        & "$r/scripts/ensure-pie.ps1" -RepoRoot $r | Out-Null
        dotnet run --project "$r/src/Stackroot.App/Stackroot.App.csproj"
        exit $LASTEXITCODE
    }
    "^(pack|build)$" {
        & "$r/scripts/pack-release.ps1"
        exit $LASTEXITCODE
    }
    "^(ship|release)$" {
        & "$r/scripts/pack-release.ps1" -Publish
        exit $LASTEXITCODE
    }
    default {
        Write-Host "Usage: ./sr dev|pack|ship"
        exit 1
    }
}
