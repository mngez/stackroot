#!/usr/bin/env pwsh
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "Restoring and building solution..."
dotnet build "$repoRoot\Stackroot.sln" -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Running unit tests (Stackroot.Core.Tests)..."
dotnet test "$repoRoot\tests\Stackroot.Core.Tests\Stackroot.Core.Tests.csproj" -c Release --verbosity minimal -- --parallel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Running integration tests (all - fresh install E2E + live-seeded repair)..."
Write-Host "  Missing packages download automatically into tests/fixtures/cache on first run."
dotnet test "$repoRoot\tests\Stackroot.Integration.Tests\Stackroot.Integration.Tests.csproj" -c Release --verbosity minimal
exit $LASTEXITCODE
