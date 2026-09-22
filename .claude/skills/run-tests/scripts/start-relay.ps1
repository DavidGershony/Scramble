#!/usr/bin/env pwsh
# Brings up the nostr-rs-relay container from docker-compose.test.yml
# and waits until it accepts TCP on :7777.
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path
$compose  = Join-Path $repoRoot 'docker-compose.test.yml'

Write-Host "[start-relay] docker compose up -d nostr-relay"
docker compose -f $compose up -d nostr-relay
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed (exit $LASTEXITCODE)" }

Write-Host "[start-relay] waiting for tcp://localhost:7777..."
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $client.Connect('localhost', 7777)
        $client.Close()
        Write-Host "[start-relay] relay is accepting connections."
        return
    } catch {
        Start-Sleep -Milliseconds 500
    }
}
throw "Timed out after 30s waiting for relay on :7777"
