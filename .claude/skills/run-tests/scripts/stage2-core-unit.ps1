#!/usr/bin/env pwsh
# Stage 2 - Scramble.Core.Tests, excluding anything that needs a relay or Docker.
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path
$project  = Join-Path $repoRoot 'tests/Scramble.Core.Tests'

Write-Host "[stage2] Core unit tests (no infra)"
dotnet test $project --filter "Category!=Relay&Category!=Integration&Category!=MIP-Compliance"
if ($LASTEXITCODE -ne 0) { throw "Stage 2 failed (exit $LASTEXITCODE)" }
