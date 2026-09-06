---
name: run-tests
description: Run the Scramble test suites in the canonical fast-to-slow order — unit, then UI, then integration, then Whitenoise diagnostics. Use when the user says "run the tests", "run all tests", "test everything", or asks for the right order to run tests.
---

# run-tests

Canonical order for running the Scramble test suites. Order is **fast → slow** and **no-infra → infra-required**, so a failure stops you before paying the Docker/Whitenoise startup cost.

All commands live in `scripts/` next to this file — that is the source of truth. Do not duplicate command flags into prose; if behavior changes, edit the script.

> Repo fact: `tests/Scramble.Core.Tests/xunit.runner.json` sets `parallelizeTestCollections: false`. Test collections run sequentially within a project — don't try to parallelize them yourself.

## The four stages

| # | Script | What it runs | Infra |
|---|---|---|---|
| 1 | `scripts/stage1-core-unit.ps1` | `Scramble.Core.Tests` minus `Relay`/`Integration`/`MIP-Compliance` | None |
| 2 | `scripts/stage2-ui.ps1` | `Scramble.UI.Tests` (Avalonia headless) | None |
| 3 | `scripts/stage3-integration.ps1` | `Scramble.Core.Tests` with `Category=Integration\|Relay\|MIP-Compliance` | `nostr-rs-relay` on :7777 |
| 4 | `scripts/stage4-diagnostics.ps1` | `Scramble.Diagnostics` (Whitenoise interop, FullE2E, EpochSync, …) | Full Docker stack |

Stages 3 and 4 invoke the Docker scripts below themselves — you don't need to run them separately.

## Docker helpers

| Script | Purpose |
|---|---|
| `scripts/start-relay.ps1` | `docker compose up -d nostr-relay`, then wait until TCP :7777 accepts connections. |
| `scripts/start-whitenoise.ps1` | Calls `start-relay`, then brings up the `whitenoise-interop` container and waits for `State.Status = running`. |
| `scripts/stop-docker.ps1` | `docker compose down`. Pass `-Clean` to also drop the `relay-data` / `wn-data` volumes. |

## Running

Run the whole thing, stopping on the first failure:

```powershell
pwsh -File .claude/skills/run-tests/scripts/run-all.ps1
```

Useful variants:

```powershell
# Unit + UI only (no Docker required)
pwsh -File .claude/skills/run-tests/scripts/run-all.ps1 -StopAt 2

# Skip stage 4, tear Docker down at the end on success
pwsh -File .claude/skills/run-tests/scripts/run-all.ps1 -StopAt 3 -TearDown

# Wipe Docker volumes before running so the relay's SQLite starts clean
pwsh -File .claude/skills/run-tests/scripts/run-all.ps1 -Clean

# Just one Whitenoise category from stage 4
pwsh -File .claude/skills/run-tests/scripts/stage4-diagnostics.ps1 -Category WhitenoiseInterop
```

## Stop on first failure

The scripts use `$ErrorActionPreference = 'Stop'` and `throw` on non-zero exit, so `run-all.ps1` exits at the first failure. **Don't move on to the next stage manually** — a unit-test regression masked by integration-test noise is the failure mode this order exists to prevent.

## Tear-down

`run-all.ps1` leaves Docker running by default so re-runs are fast. Pass `-TearDown` to call `stop-docker.ps1 -Clean` after a successful run, or call it yourself:

```powershell
pwsh -File .claude/skills/run-tests/scripts/stop-docker.ps1 -Clean
```
