#!/usr/bin/env pwsh
# Stage 1 - the Dark Matter engine and the MLS library under it. No infra,
# and the fastest suites in the repo, so they run before anything else.
#
# dotnet-mls is a submodule with its own suite. It is not in the solution
# filter, so nothing else in this file would ever run it -- and an engine
# change that breaks the library it sits on shows up here rather than as a
# confusing failure three stages later.
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path

Write-Host "[stage1] dotnet-mls library (RFC 9420 vectors included)"
dotnet test (Join-Path $repoRoot 'lib/dotnet-mls/tests/DotnetMls.Tests')
if ($LASTEXITCODE -ne 0) { throw "Stage 1 failed: dotnet-mls (exit $LASTEXITCODE)" }

Write-Host "[stage1] Scramble.Marmot.Tests (MarmotEngine + ConformanceVector)"
dotnet test (Join-Path $repoRoot 'tests/Scramble.Marmot.Tests')
if ($LASTEXITCODE -ne 0) { throw "Stage 1 failed: Scramble.Marmot.Tests (exit $LASTEXITCODE)" }
