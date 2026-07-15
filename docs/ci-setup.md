# CI setup — branch protection

This project ships several GitHub Actions workflows. Three of them **must be
required checks** for the merge gates in `CLAUDE.md` to actually gate merges.

## Workflows

| File | Job name | When it runs | Required check? |
|---|---|---|---|
| `.github/workflows/dotnet-desktop.yml` | `build` | Every PR / push to master | **Yes** |
| `.github/workflows/integration.yml` | `integration` | PR/push touching MLS/Nostr/signer paths | **Yes** (path-conditional) |
| `.github/workflows/drift.yml` | `drift` | PR touching `src/Scramble.Android/**` or `src/Scramble.Mobile.Android/**` | **Yes** (path-conditional) |
| `.github/workflows/integration-windows-nightly.yml` | `integration-windows` | Nightly cron 04:00 UTC | No |
| `.github/workflows/publish.yml` | (release build) | Tag push | No |

## Configure branch protection (one-time)

**GitHub UI:** `Settings → Branches → Branch protection rules → master →
Require status checks to pass before merging`.

Add these required status checks:

```
build (Debug)
build (Release)
integration
drift
```

`integration` and `drift` are both path-conditional — on PRs that don't
touch the relevant paths, GitHub reports the check as **skipped**, which
counts as passing for branch protection.

Enable **Require branches to be up to date before merging** so the required
checks are computed against `master`'s current tip.

## Why `integration` is path-conditional

The `integration.yml` workflow only fires when a PR touches one of these
paths:

- `src/Scramble.Core/**`
- `src/Scramble.Presentation/**`
- `lib/marmot-cs/**`
- `lib/dotnet-mls/**`
- `tests/Scramble.Diagnostics/**`
- `tests/Scramble.Core.Tests/**`
- `tests/Scramble.UI.Tests/**`
- `.github/workflows/integration.yml`
- `docker-compose.test.yml`

A PR that only touches UI-XAML, docs, or launcher icons will not fire the
integration job. GitHub reports the required check as **skipped**, which
counts as passing for branch protection. This keeps the ~10-min integration
cost off PRs that can't affect protocol behaviour.

If you add a new subsystem whose regressions matter, add its path to both
the `push:` and `pull_request:` filters in `integration.yml`.

## Rationale — why this exists

See `ANALYSIS.md` STEP 6 for the full data. Summary: for Feb–May 2026 the
build workflow ran only unit tests and explicitly excluded every test
tagged `Category=Relay` or `Category=Integration`. The MIP-00..04 interop
tests, cross-MDK relay tests, DeviceSync E2E, Outbox model, and Whitenoise
interop tests all existed in the repo but were not required to merge —
which is how `ManagedMlsService.cs` regressed 15 times in 11 days, and how
DeviceSync stayed silently broken for ~7 weeks.

**Every category enumerated in `integration.yml`'s `--filter` clause exists
because a bug slipped through when it wasn't required.** Do not remove
categories from the filter without adding equivalent coverage elsewhere.

## Adding a new integration test category

1. Add `[Trait("Category", "<YourCategory>")]` to the test class.
2. Add the category to the `--filter` union in `integration.yml` **and**
   `integration-windows-nightly.yml`.
3. Add it to the exclusion list in `dotnet-desktop.yml`'s Diagnostics step
   so it doesn't also run as a unit test.
4. Update this document's [Rationale](#rationale--why-this-exists) with a
   one-line reason.

## Local reproduction

```powershell
# Boot the relay container
docker compose -f docker-compose.test.yml up -d nostr-relay

# Run the integration suite
dotnet test tests/Scramble.Diagnostics/Scramble.Diagnostics.csproj `
  --configuration Release -p:DesktopOnly=true `
  --filter "Category=Integration|Category=MIP-Compliance|Category=ProtocolCompliance|Category=FullE2E|Category=EpochSync|Category=DeviceSync|Category=OutboxModel|Category=Notifications|Category=RelayHarness|Category=ExporterSecret"

docker compose -f docker-compose.test.yml down -v
```
