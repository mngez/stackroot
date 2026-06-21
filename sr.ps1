param(
    [Parameter(Position = 0)]
    [string]$Cmd = "dev",
    [Parameter(Position = 1)]
    [string]$Arg,
    [Parameter(Position = 2)]
    [string]$Flag
)

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
    "^push$" {
        if ([string]::IsNullOrWhiteSpace($Arg)) {
            Write-Host "Usage: ./sr push {version} [+]"
            Write-Host "Example: ./sr push 0.2.6      # csproj must already match"
            Write-Host "         ./sr push 0.2.6 +    # update csproj, commit, then push"
            exit 1
        }

        $bump = ($Flag -eq '+') -or ($Arg.TrimEnd() -match '\+$')
        $version = $Arg.Trim().TrimEnd('+').Trim()
        & "$r/scripts/push-release.ps1" -Version $version -BumpVersion:$bump
        exit $LASTEXITCODE
    }
    "^changelog$" {
        & "$r/scripts/merge-release-notes.ps1"
        exit $LASTEXITCODE
    }
    default {
        Write-Host "Usage: ./sr dev|pack|push {version} [+]|changelog"
        exit 1
    }
}
