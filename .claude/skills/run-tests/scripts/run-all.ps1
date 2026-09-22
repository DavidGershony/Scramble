#!/usr/bin/env pwsh
# Runs the test stages in canonical fast->slow order, stopping on the first failure.
#
#   -StopAt N    Stop after stage N (default 6 = run everything).
#   -DarkMatter  Only the engine and interop stages (1 and 6).
#   -Clean       Wipe Docker volumes before starting.
#   -TearDown    Run stop-docker -Clean at the end (only on success).
#
# Examples:
#   pwsh -File run-all.ps1                # all 6 stages, leave Docker running
#   pwsh -File run-all.ps1 -StopAt 3      # every no-infra suite
#   pwsh -File run-all.ps1 -DarkMatter    # engine + reference-client interop
#   pwsh -File run-all.ps1 -Clean -TearDown
[CmdletBinding()]
param(
    [ValidateRange(1, 6)]
    [int]$StopAt = 6,
    [switch]$Clean,
    [switch]$TearDown,
    # Just the Dark Matter path: the engine suites and the interop suite,
    # skipping everything that only exercises the legacy protocol.
    [switch]$DarkMatter
)
$ErrorActionPreference = 'Stop'

if ($Clean) { & "$PSScriptRoot/stop-docker.ps1" -Clean }

if ($DarkMatter) {
    & "$PSScriptRoot/stage1-engine.ps1"
    & "$PSScriptRoot/stage6-dark-matter.ps1"
    Write-Host "[run-all] Dark Matter stages passed."
    if ($TearDown) { & "$PSScriptRoot/stop-docker.ps1" -Clean }
    return
}

if ($StopAt -ge 1) { & "$PSScriptRoot/stage1-engine.ps1" }
if ($StopAt -ge 2) { & "$PSScriptRoot/stage2-core-unit.ps1" }
if ($StopAt -ge 3) { & "$PSScriptRoot/stage3-ui.ps1" }
if ($StopAt -ge 4) { & "$PSScriptRoot/stage4-integration.ps1" }
if ($StopAt -ge 5) { & "$PSScriptRoot/stage5-diagnostics.ps1" }
if ($StopAt -ge 6) { & "$PSScriptRoot/stage6-dark-matter.ps1" }

Write-Host "[run-all] all requested stages passed."

if ($TearDown) { & "$PSScriptRoot/stop-docker.ps1" -Clean }
