$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$NotesDir = Join-Path $Root "release-notes"
$OutputPath = Join-Path $NotesDir "CHANGELOG.md"
$Header = "# Stackroot release history"

function ConvertTo-Version([string]$Text) {
    try {
        return [version]$Text
    } catch {
        return $null
    }
}

function Get-ChangelogBody {
    if (-not (Test-Path -LiteralPath $OutputPath)) {
        return ""
    }

    $raw = (Get-Content -LiteralPath $OutputPath -Raw).TrimEnd()
    if ($raw -match '(?s)^#\s*Stackroot release history\s*\r?\n\r?\n(.*)$') {
        return $Matches[1].TrimEnd()
    }

    return $raw.TrimEnd()
}

function Test-ChangelogHasVersion([string]$Body, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Body)) {
        return $false
    }

    $pattern = "(?m)^##\s*Stackroot\s+$([regex]::Escape($Label))\s*$"
    return [regex]::IsMatch($Body, $pattern)
}

$existingBody = Get-ChangelogBody

$entries = Get-ChildItem -LiteralPath $NotesDir -Filter "*.md" -File |
    Where-Object {
        $_.Name -ne "next.md" -and
        $_.Name -ne "CHANGELOG.md" -and
        $_.BaseName -match '^\d+\.\d+\.\d+([-.].+)?$'
    } |
    ForEach-Object {
        $version = ConvertTo-Version $_.BaseName
        if ($null -eq $version) {
            return
        }

        [pscustomobject]@{
            Version = $version
            Label   = $_.BaseName
            Path    = $_.FullName
            Content = (Get-Content -LiteralPath $_.FullName -Raw).Trim()
        }
    } |
    Where-Object { $_ -ne $null } |
    Sort-Object Version -Descending

if ($entries.Count -eq 0) {
    throw "No versioned release notes found in release-notes/."
}

$latest = $entries[0]
$candidates = $entries | Select-Object -Skip 1
$toMerge = New-Object System.Collections.Generic.List[object]
$toDelete = New-Object System.Collections.Generic.List[string]

foreach ($entry in $candidates) {
    if (Test-ChangelogHasVersion $existingBody $entry.Label) {
        $toDelete.Add($entry.Path)
        continue
    }

    $toMerge.Add($entry)
    $toDelete.Add($entry.Path)
}

if ($toMerge.Count -eq 0 -and $toDelete.Count -eq 0) {
    Write-Host "Nothing to archive. Latest release notes kept: release-notes/$($latest.Label).md"
    exit 0
}

$newBody = if ($toMerge.Count -gt 0) {
    ($toMerge | ForEach-Object { $_.Content }) -join "`n`n---`n`n"
} else {
    ""
}

$combinedBody = if ([string]::IsNullOrWhiteSpace($newBody)) {
    $existingBody
} elseif ([string]::IsNullOrWhiteSpace($existingBody)) {
    $newBody.TrimEnd()
} else {
    ($newBody.TrimEnd() + "`n`n---`n`n" + $existingBody.TrimEnd())
}

$document = if ([string]::IsNullOrWhiteSpace($combinedBody)) {
    "$Header`n"
} else {
    "$Header`n`n$combinedBody`n"
}

Set-Content -LiteralPath $OutputPath -Value $document -Encoding UTF8

foreach ($path in $toDelete) {
    Remove-Item -LiteralPath $path -Force
}

if ($toMerge.Count -gt 0) {
    $mergedLabels = ($toMerge | ForEach-Object { $_.Label }) -join ", "
    Write-Host "Archived $($toMerge.Count) release note(s) into CHANGELOG.md: $mergedLabels"
}

$removedOnly = $toDelete.Count -gt $toMerge.Count
if ($removedOnly) {
    Write-Host "Removed $($toDelete.Count - $toMerge.Count) note file(s) already present in CHANGELOG.md."
}

Write-Host "Latest kept: release-notes/$($latest.Label).md"
