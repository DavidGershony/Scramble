#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the Dark Matter interop peers, skipping the build when upstream has
    not moved.

.DESCRIPTION
    The peers track upstream's moving `wn-agent-latest` tag, which creates a
    problem Docker will not solve on its own: a RUN layer that fetches a tag
    keys its cache on the tag *string*, so it caches forever and never sees new
    commits. The build would be silently stale rather than merely old.

    So the tag is resolved here, outside the build, and the commit is passed in
    as MDK_COMMIT. That makes the cache key move exactly when upstream does --
    an unchanged commit reuses every layer, a new one rebuilds from the clone
    down. Each image also carries an `mdk.commit` label, so the question "what
    is this image actually built from?" is answered by the image rather than by
    a side file that can drift from it.

    A full build compiles a large Rust workspace and takes many minutes. A
    no-op check takes about a second.

.PARAMETER Ref
    An explicit ref to build. Omit it to track the newest wn-agent release.

    Note what is NOT used: upstream's `wn-agent-latest` tag. It reads like the
    thing to track and is not -- on 2026-09-10 it pointed at the same commit as
    `wn-agent-v0.9.12`, eight releases behind `v0.9.20`. Following it silently
    downgraded the peer. The newest semver tag is resolved instead.

.PARAMETER Force
    Rebuild even when the resolved commit matches what the images hold.
#>
[CmdletBinding()]
param(
    [string]$Ref,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot/..").Path
$compose = Join-Path $repoRoot 'docker-compose.test.yml'
$repoUrl = 'https://github.com/marmot-protocol/mdk.git'
$services = @(
    @{ Service = 'mdk-cli';  Image = 'scramble-mdk-cli:latest' },
    @{ Service = 'wn-agent'; Image = 'scramble-wn-agent:latest' }
)

if (-not $Ref) {
    Write-Host "[peers] finding the newest wn-agent release ..."

    # Every wn-agent-vX.Y.Z ref, with the peeled commit for annotated tags.
    # Captured whole rather than piped into Select-Object: piping terminates
    # the pipeline early, which kills git and leaves a non-zero $LASTEXITCODE
    # behind -- a success that reports as a failure.
    $refs = @(git ls-remote --tags $repoUrl 'wn-agent-v*' 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list tags at $repoUrl. Offline?"
    }

    $releases = @{}
    foreach ($entry in $refs) {
        if ($entry -notmatch '^([0-9a-f]{40})\s+refs/tags/wn-agent-v(\d+)\.(\d+)\.(\d+)(\^\{\})?$') {
            continue
        }

        $name = "wn-agent-v$($Matches[2]).$($Matches[3]).$($Matches[4])"
        $version = [version]("$($Matches[2]).$($Matches[3]).$($Matches[4])")

        # An annotated tag appears twice: the tag object, then "^{}" for the
        # commit it points at. The second is the one to build, so it wins.
        if ($Matches[5] -or -not $releases.ContainsKey($name)) {
            $releases[$name] = @{ Version = $version; Commit = $Matches[1] }
        }
    }

    if ($releases.Count -eq 0) {
        throw "No wn-agent-vX.Y.Z tags found at $repoUrl."
    }

    $newest = $releases.GetEnumerator() | Sort-Object { $_.Value.Version } | Select-Object -Last 1
    $Ref = $newest.Key
    $commit = $newest.Value.Commit

    Write-Host "[peers] newest release is $Ref"
}
else {
    Write-Host "[peers] resolving $Ref ..."
    $output = @(git ls-remote $repoUrl $Ref 2>&1)
    if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) {
        throw "Could not resolve '$Ref' at $repoUrl. Offline, or the ref does not exist."
    }

    # Prefer the peeled entry when the ref is an annotated tag.
    $peeled = @($output | Where-Object { $_ -match '\^\{\}$' })
    $line = if ($peeled.Count -gt 0) { $peeled[0] } else { $output[0] }
    $commit = ($line -split '\s+')[0]
}

if ($commit -notmatch '^[0-9a-f]{40}$') {
    throw "Resolved '$Ref' to something that is not a commit: $commit"
}

Write-Host "[peers] $Ref = $commit"

# Ask each image what it was built from. An image that predates the label
# reports empty and is treated as stale, which is the safe direction.
function Get-BuiltCommit([string]$image) {
    $value = docker image inspect --format '{{index .Config.Labels "mdk.commit"}}' $image 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return ($value | Out-String).Trim()
}

$stale = @()
foreach ($entry in $services) {
    $built = Get-BuiltCommit $entry.Image

    if ($Force) {
        $stale += $entry
        Write-Host "  $($entry.Service): rebuilding (forced)"
    }
    elseif ([string]::IsNullOrWhiteSpace($built)) {
        $stale += $entry
        Write-Host "  $($entry.Service): no image, or built before commits were labelled"
    }
    elseif ($built -ne $commit) {
        $stale += $entry
        Write-Host "  $($entry.Service): built from $($built.Substring(0, 12)), upstream moved"
    }
    else {
        Write-Host "  $($entry.Service): up to date"
    }
}

if ($stale.Count -eq 0) {
    Write-Host "[peers] nothing to build."
    return
}

# Volumes hold a SQLite home written by the binary that created it, and a newer
# binary opening an older one fails with "backend failure: file is not a
# database" -- which reads like corruption and is not. Dropping them is part of
# the upgrade, not a separate troubleshooting step somebody has to know about.
Write-Host "[peers] dropping peer volumes (a new binary cannot open an old home)"
docker compose -f $compose rm -sf @($stale.Service) 2>&1 | Out-Null
foreach ($name in @('scramble_mdk-cli-data', 'scramble_mdk-cli-logs', 'scramble_wn-agent-data')) {
    docker volume rm $name 2>&1 | Out-Null
}

foreach ($entry in $stale) {
    Write-Host "[peers] building $($entry.Service) at $($commit.Substring(0, 12)) -- this compiles Rust and is slow"

    docker compose -f $compose build `
        --build-arg "MDK_REF=$Ref" `
        --build-arg "MDK_COMMIT=$commit" `
        $entry.Service

    if ($LASTEXITCODE -ne 0) { throw "Building $($entry.Service) failed (exit $LASTEXITCODE)" }

    # `docker compose build` has been observed to exit 0 when it could not
    # reach the daemon or the registry, so confirm the image exists and holds
    # the commit we asked for rather than trusting the exit code.
    $built = Get-BuiltCommit $entry.Image
    if ($built -ne $commit) {
        throw "$($entry.Service) reports mdk.commit '$built' after building $commit."
    }

    Write-Host "[peers] $($entry.Service) built and labelled $($commit.Substring(0, 12))"
}

Write-Host "[peers] done."
