param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$envPath = Join-Path $RepoRoot ".env"
if (-not (Test-Path $envPath)) {
    return
}

Get-Content $envPath -Encoding UTF8 | ForEach-Object {
    $line = $_.Trim()
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
        return
    }

    $eq = $line.IndexOf('=')
    if ($eq -lt 1) {
        return
    }

    $name = $line.Substring(0, $eq).Trim()
    $value = $line.Substring($eq + 1).Trim()
    if ($value.Length -ge 2) {
        $quote = $value[0]
        if (($quote -eq '"' -or $quote -eq "'") -and $value.EndsWith($quote)) {
            $value = $value.Substring(1, $value.Length - 2)
        }
    }

    if ([string]::IsNullOrWhiteSpace($name)) {
        return
    }

    $existing = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($existing)) {
        Set-Item -Path "Env:$name" -Value $value
    }
}
