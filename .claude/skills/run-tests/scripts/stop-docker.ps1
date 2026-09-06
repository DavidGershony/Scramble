#!/usr/bin/env pwsh
# Stops the docker-compose.test.yml stack. Use -Clean to drop volumes too.
[CmdletBinding()]
param(
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path
$compose  = Join-Path $repoRoot 'docker-compose.test.yml'

if ($Clean) {
    Write-Host "[stop-docker] docker compose down -v (volumes will be wiped)"
    docker compose -f $compose down -v
} else {
    Write-Host "[stop-docker] docker compose down (volumes retained)"
    docker compose -f $compose down
}
if ($LASTEXITCODE -ne 0) { throw "docker compose down failed (exit $LASTEXITCODE)" }
