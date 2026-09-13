#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Reports which mdk revision each live White Noise app ships, against the one
    our interop peer is built from.

.DESCRIPTION
    "Which mdk version does the deployed fleet run?" was on the list of things
    to ask Whitenoise. It does not need asking: every shipping client records
    its pin in the repository, so the answer is readable on demand and cannot go
    stale the way a remembered answer does.

    It is worth knowing because the spread is real. On 2026-09-13 the iOS and
    Android apps shipped mdk `fdd398a8` (marmotkit-v0.9.21) while the Mac app
    was five releases behind it and the Linux app eight, and our peer was one
    behind both phones. A suite green against one point on that range says less
    about the others than it looks.

    Note the direction that matters. A peer *older* than us agrees with us about
    everything we already agree on, so it cannot find a disagreement -- that is
    the trap `build-marmot-peers.ps1` was written to avoid. A shipping client
    *newer* than our peer is the opposite problem: it may have moved somewhere
    we have never tested.

.PARAMETER Peer
    The commit to compare against. Defaults to the `mdk.commit` label on the
    built interop image, which is the only answer that cannot drift from what
    the tests actually ran against.

.NOTES
    Needs `gh` authenticated against github.com.

    A pin this cannot read is a failure, not an unknown. These paths are not
    ours and will move; reporting "could not tell" in the same breath as "no
    drift" would let a moved file read as agreement.
#>
[CmdletBinding()]
param(
    [string]$Peer
)
$ErrorActionPreference = 'Stop'

$repo = 'marmot-protocol/mdk'

# Where each client records the mdk revision it was built from. Two spellings of
# the same file -- the Swift ones use `key: value`, Android uses `key=value` --
# and Linux has no such file at all, so its pin is read out of the lockfile.
$clients = @(
    @{ Name = 'whitenoise-ios';     Repo = 'marmot-protocol/whitenoise-ios';     Path = 'Packages/MarmotKit/MARMOT_VERSION' },
    @{ Name = 'whitenoise-android'; Repo = 'marmot-protocol/whitenoise-android'; Path = 'app/src/main/marmotkit/MARMOT_VERSION' },
    @{ Name = 'whitenoise-mac';     Repo = 'marmot-protocol/whitenoise-mac';     Path = 'Vendored/MarmotKit/MARMOT_VERSION' },
    @{ Name = 'whitenoise-linux';   Repo = 'marmot-protocol/whitenoise-linux';   Path = 'Cargo.lock' }
)

function Get-RemoteFile {
    param([string]$Repo, [string]$Path)

    $encoded = gh api "repos/$Repo/contents/$Path" -q '.content' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read $Path from $Repo. The file has moved, or gh is not authenticated.`n$encoded"
    }

    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($encoded -join '')))
}

function Get-Pin {
    param([hashtable]$Client)

    $content = Get-RemoteFile -Repo $Client.Repo -Path $Client.Path

    # A lockfile names the commit inside the source URL; a MARMOT_VERSION names
    # it outright, and may name the binding tag beside it.
    if ($Client.Path -like '*Cargo.lock') {
        if ($content -notmatch 'mdk\.git\?[^#]*#([0-9a-f]{40})') {
            throw "No mdk git source found in $($Client.Repo)/$($Client.Path)."
        }

        return @{ Sha = $Matches[1]; Tag = '' }
    }

    if ($content -notmatch 'mdk-sha[:=]\s*([0-9a-f]{40})') {
        throw "No mdk-sha found in $($Client.Repo)/$($Client.Path)."
    }

    $sha = $Matches[1]
    $tag = if ($content -match 'mdk-tag[:=]\s*(\S+)') { $Matches[1] } else { '' }

    return @{ Sha = $sha; Tag = $tag }
}

if (-not $Peer) {
    # Asked of the image rather than of a file beside it, for the same reason
    # the label exists: what an image holds is answered by the image.
    $Peer = (docker inspect scramble-mdk-cli:latest `
            --format '{{index .Config.Labels "mdk.commit"}}' 2>&1) -join ''

    if ($LASTEXITCODE -ne 0 -or $Peer -notmatch '^[0-9a-f]{40}$') {
        throw "Could not read mdk.commit from scramble-mdk-cli:latest. Run ./scripts/build-marmot-peers.ps1 first, or pass -Peer <sha>."
    }
}

Write-Host "[pins] our interop peer: $($Peer.Substring(0, 8))"
Write-Host ''

$rows = foreach ($client in $clients) {
    $pin = Get-Pin -Client $client

    if ($pin.Sha -eq $Peer) {
        $relation = 'same commit'
    }
    else {
        $compare = gh api "repos/$repo/compare/$Peer...$($pin.Sha)" `
            -q '"\(.status) \(.ahead_by) \(.behind_by)"' 2>&1

        if ($LASTEXITCODE -ne 0) {
            throw "Could not compare $($pin.Sha) against $Peer in $repo.`n$compare"
        }

        $status, $ahead, $behind = ($compare -join '').Split(' ')

        $relation = switch ($status) {
            'ahead' { "$ahead ahead of our peer" }
            'behind' { "$behind behind our peer" }
            'diverged' { "diverged ($ahead ahead, $behind behind)" }
            default { $status }
        }
    }

    [pscustomobject]@{
        Client   = $client.Name
        Mdk      = $pin.Sha.Substring(0, 8)
        Tag      = if ($pin.Tag) { $pin.Tag } else { '(untagged)' }
        Relation = $relation
    }
}

$rows | Format-Table -AutoSize

if ($rows | Where-Object { $_.Relation -like '*ahead*' }) {
    Write-Host "[pins] a shipping client is ahead of the peer we test against."
    Write-Host "[pins] re-run ./scripts/build-marmot-peers.ps1 to move onto the newest release."
}
