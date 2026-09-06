---
name: run-tests
description: Run the Scramble test suites in the canonical fast-to-slow order — Dark Matter engine, core unit, UI, integration, Whitenoise diagnostics, then reference-client interop. Use when the user says "run the tests", "run all tests", "test everything", asks for the right order to run tests, or wants only the Dark Matter suites.
---

# run-tests

Canonical order for running the Scramble test suites. Order is **fast → slow** and **no-infra → infra-required**, so a failure stops you before paying the Docker startup cost.

All commands live in `scripts/` next to this file — that is the source of truth. Do not duplicate command flags into prose; if behavior changes, edit the script.

> Repo fact: `tests/Scramble.Core.Tests/xunit.runner.json` sets `parallelizeTestCollections: false`. Test collections run sequentially within a project — don't try to parallelize them yourself.

## The six stages

| # | Script | What it runs | Infra |
|---|---|---|---|
| 1 | `scripts/stage1-engine.ps1` | `dotnet-mls` (RFC 9420 vectors) + `Scramble.Marmot.Tests` | None |
| 2 | `scripts/stage2-core-unit.ps1` | `Scramble.Core.Tests` minus `Relay`/`Integration`/`MIP-Compliance` | None |
| 3 | `scripts/stage3-ui.ps1` | `Scramble.UI.Tests` (Avalonia headless) | None |
| 4 | `scripts/stage4-integration.ps1` | `Scramble.Core.Tests` with `Category=Integration\|Relay\|MIP-Compliance` | `nostr-rs-relay` on :7777 |
| 5 | `scripts/stage5-diagnostics.ps1` | `Scramble.Diagnostics` (Whitenoise interop, FullE2E, EpochSync, …) | Full Docker stack |
| 6 | `scripts/stage6-dark-matter.ps1` | `Scramble.Diagnostics` with `Category=DarkMatterInterop` | relay + `mdk-cli` + `wn-agent` |

Stages 4, 5 and 6 bring their own containers up — you don't need to start them separately.

> **Stage numbers moved.** Dark Matter work is on the critical path and its suites are the fastest in the repo, so they went to the front. What used to be stages 1–4 are now 2–5. `-StopAt 2` no longer means "unit + UI"; `-StopAt 3` does.

### Why `dotnet-mls` is in stage 1

It is a submodule with its own test project, outside `Scramble.Desktop.slnf`. **Nothing else in this skill runs it**, so an engine change that breaks the MLS library underneath would otherwise surface three stages later as a confusing failure — or not at all.

## Docker helpers

| Script | Purpose |
|---|---|
| `scripts/start-relay.ps1` | `docker compose up -d nostr-relay`, then wait until TCP :7777 accepts connections. |
| `scripts/start-whitenoise.ps1` | Calls `start-relay`, then brings up the `whitenoise-interop` container and waits for `State.Status = running`. |
| `scripts/start-marmot-peers.ps1` | Calls `start-relay`, brings up **both** Dark Matter peers, and proves each answers. |
| `scripts/stop-docker.ps1` | `docker compose down`. Pass `-Clean` to also drop the `relay-data` / `wn-data` volumes. |

## A skipped interop run reports success

The trap that makes stage 6 different from every other stage, and the reason `start-marmot-peers.ps1` proves the peers answer instead of just starting them:

- With the containers down, **every** `DarkMatterInterop` test self-skips and the run exits zero — `Skipped! Failed: 0, Passed: 0, Skipped: 17`. That line matches neither `Passed!` nor `Failed!`, so grepping for either finds nothing.
- Starting only `mdk-cli` is worse. The nine KeyPackage tests need `wn-agent`, so you get `Passed: 8, Skipped: 9`, which reads as a pass at a glance.

Stage 6 therefore **fails on any skip** rather than on the absence of passes. If you run the suite by hand instead, require `Skipped: 0`.

### Stale peer volumes after a pin bump

A newer peer binary opening a home written by an older one fails with `backend failure: file is not a database` — which reads like corruption and is not. The signature is distinctive: every group test fails while every KeyPackage test passes, because only the former needs the peer's own persisted state. After changing `MDK_REF`:

```powershell
docker compose -f docker-compose.test.yml rm -sf mdk-cli wn-agent
docker volume rm scramble_mdk-cli-data scramble_mdk-cli-logs scramble_wn-agent-data
```

Note the same message has a second, unrelated cause: two peer processes sharing one home, which corrupts it for real. The distinguishing question is whether the pin has just moved.

## Running

Run the whole thing, stopping on the first failure:

```powershell
pwsh -File .claude/skills/run-tests/scripts/run-all.ps1
```

Useful variants:

```powershell
# Dark Matter only: engine suites + reference-client interop (stages 1 and 6)
pwsh -File .claude/skills/run-tests/scripts/run-all.ps1 -DarkMatter

# Every no-infra suite (stages 1-3)
pwsh -File .claude/skills/run-tests/scripts/run-all.ps1 -StopAt 3

# Everything except Dark Matter interop, tear Docker down at the end on success
pwsh -File .claude/skills/run-tests/scripts/run-all.ps1 -StopAt 5 -TearDown

# Wipe Docker volumes before running so the relay's SQLite starts clean
pwsh -File .claude/skills/run-tests/scripts/run-all.ps1 -Clean

# Just one Whitenoise category from stage 5
pwsh -File .claude/skills/run-tests/scripts/stage5-diagnostics.ps1 -Category WhitenoiseInterop
```

`-DarkMatter` is the one to reach for while working on the `feat/dark-matter` branch: it covers everything that branch touches and skips the legacy-protocol stages entirely.

## Stop on first failure

The scripts use `$ErrorActionPreference = 'Stop'` and `throw` on non-zero exit, so `run-all.ps1` exits at the first failure. **Don't move on to the next stage manually** — a unit-test regression masked by integration-test noise is the failure mode this order exists to prevent.

## Tear-down

`run-all.ps1` leaves Docker running by default so re-runs are fast. Pass `-TearDown` to call `stop-docker.ps1 -Clean` after a successful run, or call it yourself:

```powershell
pwsh -File .claude/skills/run-tests/scripts/stop-docker.ps1 -Clean
```
