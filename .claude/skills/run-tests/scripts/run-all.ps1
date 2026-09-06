#!/usr/bin/env pwsh
# Runs the test stages in canonical fast->slow order, stopping on the first failure.
#
#   -StopAt N    Stop after stage N (default 4 = run everything).
#   -Clean       Wipe Docker volumes before starting.
#   -TearDown    Run stop-docker -Clean at the end (only on success).
#
# Examples:
#   pwsh -File run-all.ps1                # all 4 stages, leave Docker running
#   pwsh -File run-all.ps1 -StopAt 2      # unit + UI only, no Docker
#   pwsh -File run-all.ps1 -Clean -TearDown
[CmdletBinding()]
param(
    [ValidateRange(1, 4)]
    [int]$StopAt = 4,
    [switch]$Clean,
    [switch]$TearDown
)
$ErrorActionPreference = 'Stop'

if ($Clean) { & "$PSScriptRoot/stop-docker.ps1" -Clean }

if ($StopAt -ge 1) { & "$PSScriptRoot/stage1-core-unit.ps1" }
if ($StopAt -ge 2) { & "$PSScriptRoot/stage2-ui.ps1" }
if ($StopAt -ge 3) { & "$PSScriptRoot/stage3-integration.ps1" }
if ($StopAt -ge 4) { & "$PSScriptRoot/stage4-diagnostics.ps1" }

Write-Host "[run-all] all requested stages passed."

if ($TearDown) { & "$PSScriptRoot/stop-docker.ps1" -Clean }
