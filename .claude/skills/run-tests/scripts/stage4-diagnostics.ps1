#!/usr/bin/env pwsh
# Stage 4 - Scramble.Diagnostics (Whitenoise interop, FullE2E, EpochSync, etc).
# Brings up the full Docker stack first.
# Use -Category to scope to one trait, e.g. -Category WhitenoiseInterop.
[CmdletBinding()]
param(
    [string]$Category
)
$ErrorActionPreference = 'Stop'

& "$PSScriptRoot/start-whitenoise.ps1"

$repoRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path
$project  = Join-Path $repoRoot 'tests/Scramble.Diagnostics'

if ($Category) {
    Write-Host "[stage4] Diagnostics, category=$Category"
    dotnet test $project --filter "Category=$Category"
} else {
    Write-Host "[stage4] Diagnostics (full)"
    dotnet test $project
}
if ($LASTEXITCODE -ne 0) { throw "Stage 4 failed (exit $LASTEXITCODE)" }
