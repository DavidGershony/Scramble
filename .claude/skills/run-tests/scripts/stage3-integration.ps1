#!/usr/bin/env pwsh
# Stage 3 - Integration / Relay / MIP-Compliance tests in Scramble.Core.Tests.
# Ensures nostr-rs-relay is up first.
$ErrorActionPreference = 'Stop'

& "$PSScriptRoot/start-relay.ps1"

$repoRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path
$project  = Join-Path $repoRoot 'tests/Scramble.Core.Tests'

Write-Host "[stage3] Integration / Relay / MIP-Compliance"
dotnet test $project --filter "Category=Integration|Category=Relay|Category=MIP-Compliance"
if ($LASTEXITCODE -ne 0) { throw "Stage 3 failed (exit $LASTEXITCODE)" }
