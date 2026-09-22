#!/usr/bin/env pwsh
# Stage 6 - DarkMatterInterop against the live mdk reference client.
# Brings up both peers and proves they answer before running anything.
$ErrorActionPreference = 'Stop'

& "$PSScriptRoot/start-marmot-peers.ps1"

$repoRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path
$project  = Join-Path $repoRoot 'tests/Scramble.Diagnostics'

Write-Host "[stage6] DarkMatterInterop"
$output = dotnet test $project --filter "Category=DarkMatterInterop" 2>&1 | Tee-Object -Variable lines
$output | Out-String | Write-Host
if ($LASTEXITCODE -ne 0) { throw "Stage 6 failed (exit $LASTEXITCODE)" }

# Require zero skips rather than the absence of failures. Every test in this
# suite self-skips when its peer is missing, so "no failures" is also what a
# suite that ran nothing reports.
$summary = ($lines | Where-Object { $_ -match '^(Passed|Failed|Skipped)!' }) -join "`n"
if ($summary -match 'Skipped:\s*([1-9]\d*)') {
    throw "Stage 6 skipped $($Matches[1]) test(s) -- a peer was not reachable. $summary"
}
Write-Host "[stage6] $summary"
