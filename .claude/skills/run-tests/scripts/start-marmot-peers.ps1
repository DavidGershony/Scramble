#!/usr/bin/env pwsh
# Brings up both Dark Matter interop peers and proves each one answers.
#
# The proof is the point. With the containers down every DarkMatterInterop
# test SKIPS and the run still exits zero -- "Skipped! Failed: 0, Passed: 0,
# Skipped: 17" -- which matches neither Passed! nor Failed! if you grep for
# them. Starting only mdk-cli is worse: the KeyPackage tests need wn-agent,
# so you get "Passed: 8, Skipped: 9", which reads as a pass at a glance.
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path
$compose  = Join-Path $repoRoot 'docker-compose.test.yml'

& "$PSScriptRoot/start-relay.ps1"

Write-Host "[peers] docker compose up -d mdk-cli wn-agent"
docker compose -f $compose up -d mdk-cli wn-agent
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed (exit $LASTEXITCODE)" }

# A newer binary opening a home written by an older one fails with "backend
# failure: file is not a database", which reads like corruption and is not.
# If you have just bumped MDK_REF, drop the volumes:
#   docker compose -f docker-compose.test.yml rm -sf mdk-cli wn-agent
#   docker volume rm scramble_mdk-cli-data scramble_mdk-cli-logs scramble_wn-agent-data
Write-Host "[peers] checking mdk-cli answers..."
$version = docker exec mdk-cli-interop wn --version 2>&1
if ($LASTEXITCODE -ne 0) { throw "mdk-cli is not answering: $version" }
Write-Host "[peers] mdk-cli: $version"

Write-Host "[peers] checking wn-agent answers..."
# Out-String because docker exec returns an ARRAY of lines, and -notmatch on
# an array filters it rather than testing it -- a non-empty result is truthy,
# so the check fires even when a line does match.
$bootstrap = (docker exec wn-agent-interop wn-agent bootstrap `
    --home /data/marmot-agent `
    --socket /run/marmot-agent/wn-agent.sock `
    --no-quic --json 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { throw "wn-agent is not answering: $bootstrap" }

# Idempotent: a second bootstrap reports created:false and still says whether a
# KeyPackage is published. That is the readiness signal -- without one on the
# relay, every KeyPackage test would skip.
if ($bootstrap -notmatch '"key_package_published"\s*:\s*true') {
    throw "wn-agent has no published KeyPackage: $bootstrap"
}
Write-Host "[peers] wn-agent published its KeyPackage."
