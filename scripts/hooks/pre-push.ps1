#!/usr/bin/env pwsh
# Optional local pre-push hook mirroring the CI anti-drift gate.
#
# Install:
#   1. Copy this file to .git/hooks/pre-push (no extension)
#   2. On non-Windows: chmod +x .git/hooks/pre-push
#
# The hook runs check-drift.ps1 against origin/master. To skip once
# (e.g. pushing a WIP branch), use `git push --no-verify`.

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir '..\..')

& (Join-Path $repoRoot 'scripts\check-drift.ps1') -BaseRef 'origin/master' -HeadRef HEAD
exit $LASTEXITCODE
