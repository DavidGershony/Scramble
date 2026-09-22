#!/usr/bin/env pwsh
# Brings up nostr-rs-relay + whitenoise from docker-compose.test.yml.
# Whitenoise has no exposed port, so we wait for the container State.Status = running.
$ErrorActionPreference = 'Stop'

& "$PSScriptRoot/start-relay.ps1"

$repoRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path
$compose  = Join-Path $repoRoot 'docker-compose.test.yml'

Write-Host "[start-whitenoise] docker compose up -d whitenoise"
docker compose -f $compose up -d whitenoise
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed (exit $LASTEXITCODE)" }

Write-Host "[start-whitenoise] waiting for whitenoise-interop container..."
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    $state = docker inspect -f '{{.State.Status}}' whitenoise-interop 2>$null
    if ($state -eq 'running') {
        Write-Host "[start-whitenoise] container is running."
        return
    }
    if ($state -eq 'exited' -or $state -eq 'dead') {
        docker logs --tail 50 whitenoise-interop
        throw "whitenoise-interop entered state '$state' before becoming healthy"
    }
    Start-Sleep -Milliseconds 500
}
throw "Timed out after 120s waiting for whitenoise-interop to start"
