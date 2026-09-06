#!/usr/bin/env pwsh
# Stage 2 - Scramble.UI.Tests (Avalonia headless). No infra required;
# the few real-relay tests in this project self-skip when the relay isn't up.
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path
$project  = Join-Path $repoRoot 'tests/Scramble.UI.Tests'

Write-Host "[stage2] UI / headless tests"
dotnet test $project
if ($LASTEXITCODE -ne 0) { throw "Stage 2 failed (exit $LASTEXITCODE)" }
