param(
    [switch]$Force
)

$ErrorActionPreference = "SilentlyContinue"

$targets = Get-CimInstance Win32_Process -Filter "name='dotnet.exe'" |
    Where-Object {
        $_.CommandLine -match 'MSBuild\.dll.*/nodemode:1' -or
        $_.CommandLine -match 'VBCSCompiler\.dll'
    }

if (-not $targets) {
    Write-Host "No lingering MSBuild/Roslyn dotnet workers."
    exit 0
}

foreach ($proc in $targets) {
    Write-Host "Stopping PID $($proc.ProcessId): $($proc.CommandLine.Substring(0, [Math]::Min(80, $proc.CommandLine.Length)))..."
    Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
}

Write-Host "Done."
