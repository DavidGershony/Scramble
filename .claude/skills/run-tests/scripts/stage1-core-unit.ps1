#!/usr/bin/env pwsh
# Stage 1 - Scramble.Core.Tests, excluding anything that needs a relay or Docker.
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path
$project  = Join-Path $repoRoot 'tests/Scramble.Core.Tests'

Write-Host "[stage1] Core unit tests (no infra)"
dotnet test $project --filter "Category!=Relay&Category!=Integration&Category!=MIP-Compliance"
if ($LASTEXITCODE -ne 0) { throw "Stage 1 failed (exit $LASTEXITCODE)" }
