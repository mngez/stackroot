param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DotnetArgs
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent

# Belt-and-suspenders for agents/CI shells that bypass Directory.Build.rsp.
$env:MSBUILDDISABLENODEREUSE = "1"
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = "1"

$buildArgs = @(
    "build",
    "$repoRoot/Stackroot.sln",
    "/nodeReuse:false",
    "/p:UseSharedCompilation=false"
) + $DotnetArgs

dotnet @buildArgs
exit $LASTEXITCODE
