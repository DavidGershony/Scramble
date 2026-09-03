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

## Dark Matter engine categories

The `Scramble.Marmot.*` projects (the Dark Matter engine — see
`ai-tasks/scramble-marmot-phased-plan-2026-08.md`) introduce their own
categories.

| Category | Gate | Needs |
|---|---|---|
| `MarmotEngine` | **Unit** (`Scramble.Desktop.slnf`) | nothing |
| `ConformanceVector` | **Unit** (`Scramble.Desktop.slnf`) | committed JSON fixtures (live since 2026-08-25) |
| `DarkMatterInterop` | **Integration** (live since 2026-08-31) | relay + the `wn-agent` container |

`MarmotEngine` and `ConformanceVector` are deliberately **not** in
`integration.yml`'s `--filter` union. Neither needs a relay or Docker, so both
run in the fast unit gate via `Scramble.Desktop.slnf` — where a broken engine
invariant is caught in seconds instead of minutes. Putting them in the
integration filter would slow the signal down without making it stronger.

`ConformanceVector` carries the fixtures copied verbatim from
`mdk@wn-agent-v0.9.10`'s `crates/cgka-conformance-simulator/vectors/`, in
`tests/Scramble.Marmot.Tests/vectors/marmot/`. They are the only tests in the
repo not written by whoever wrote the code they check, which is the entire
point: everything else can confirm only that an implementation does what its
author expected. **A fixture that starts failing after a pin bump is the signal
it exists to give — refresh the fixture from the new tag deliberately, never
edit one to make it pass.** Only upstream's byte fixtures are mirrored so far;
the scenario vectors drive a whole engine through a step list and become
runnable at P6.

`DarkMatterInterop` joined `integration.yml`'s filter on 2026-08-31, together
with a step that builds and starts the `wn-agent` container. It runs our
KeyPackage stack against a KeyPackage the reference implementation actually
published — upstream ships no byte fixture for kind 30443, so this is the only
oracle for that codec.

Two things about it are worth knowing before touching either:

- **The suite skips when the container is absent**, so a build failure would
  otherwise turn it green-but-empty. The workflow's explicit readiness check is
  what prevents that, and it must not be removed as redundant.
- **It is the slowest step in the workflow**, because the peers are built from a
  pinned mdk ref with a cargo release build. If that becomes the reason PRs are
  slow, move the suite to a nightly *Ubuntu* workflow rather than dropping it —
  the existing nightly is Windows and cannot run the containers at all.

**There are three peer images and they are not interchangeable.** Reaching for
the wrong one costs a day, so:

| Image | What it is | Use it for |
|---|---|---|
| `tests/mdk-cli-docker` | **mdk's own `wn`/`wnd` CLI** | **Everything.** A full client — groups, invites, messages, sync. This is what the interop suite drives. |
| `tests/wn-agent-docker` | the agent connector | KeyPackage-shape checks only. It connects to a relay solely to publish and never subscribes, so it can never receive an invite. |
| `tests/whitenoise-docker` | `whitenoise-rs` | The **legacy** protocol only. Upstream archived that repository and it pins `mdk-core 0.8.0`. Keep while `Scramble.Core` still ships on it. |

Two settings on the `mdk-cli` service are load-bearing rather than tidy:
`WN_ALLOW_LOOPBACK_RELAYS=1`, without which every relay URL a container can be
given is rejected as `invalid_relay_url` (there is no CLI flag for it), and
`network_mode: host`, because the stack accepts a plaintext `ws://` relay only
for a literal loopback address.

Every interop test class must join `DarkMatterInteropCollection`. They share one
peer container, xUnit runs classes in parallel, and two at once corrupts its
SQLite outright.

What *is* wired into `integration.yml` is the **path trigger**: changes under
`src/Scramble.Marmot.Abstractions/**`, `src/Scramble.Marmot.Storage.Sqlite/**`,
`src/Scramble.Marmot.AppComponents/**`, `src/Scramble.Marmot.Engine/**`,
`src/Scramble.Marmot.Identity/**`, `src/Scramble.Marmot.Wire.Nostr/**`,
`src/Scramble.Nostr.Crypto/**` and `tests/Scramble.Marmot.Tests/**` require the
integration suite, per
invariant I2. The engine is protocol code; it must not be able to land
without the interop suite having run.

`LeaveInteropTests` is the only place either side's **handshake** framing is
checked against the other. Everything else bootstraps a group from a Welcome and
then exchanges application messages, which never puts a commit or a proposal on
the wire — so if it is ever deleted or left skipped, our `PublicMessage` framing
and our kind-445 wrap of a handshake go back to being untested against the
reference while the suite still reports green.

**The suite must stay at zero skips.** It reached that on 2026-09-02, when the
inbound-join test went green and the last permanently-skipped test
(`GroupInviteInteropTests`, the `wn-agent` invite, which that peer can never
complete) was deleted rather than left standing. A skip here reads as coverage
and is not: prefer deleting a test the peer cannot run, or fixing it.

### The two interop peers

There are deliberately two, because the migration runs against both protocols
at once:

| Service | Container | Peer | Used by |
|---|---|---|---|
| `whitenoise` | `whitenoise-interop` | pre-Dark-Matter `whitenoise-rs` daemon | the existing `FullE2E` / Whitenoise interop tests |
| `wn-agent` | `wn-agent-interop` | Dark Matter reference agent from `mdk` | `Scramble.Marmot` interop, from P6 on |

The `whitenoise` service stays until the old protocol is retired. Neither
replaces the other.

`wn-agent` is **pinned to a tag** (`wn-agent-v0.9.10`), not a branch. Upstream
lands roughly eight commits a day, so an unpinned build would silently
retarget every interop test between runs — including through wire-format
changes. Bump it deliberately, together with the reference pin recorded in
`ai-tasks/scramble-marmot-phased-plan-2026-08.md`, and re-run the drift diff:

```powershell
# Build against a different upstream ref without editing the compose file
$env:MDK_REF = "wn-agent-v0.9.11"
docker compose -f docker-compose.test.yml build wn-agent
```

Its control plane is a Unix socket inside the container, so tests drive it with
`docker exec` rather than over the network — the same shape
`WhitenoiseDockerClient` already uses. The container runs with `--debug-controls`,
`--dev-allow-any-invites` and `--allow-loopback-relays`, none of which are safe
outside a throwaway test container.

Three configuration details are load-bearing, each found the hard way because
the agent reports them as bare errors that name no cause:

1. **`network_mode: host`, with the relay addressed as `ws://127.0.0.1:7777`.**
   The agent accepts plaintext `ws://` endpoints only for a *literal* loopback
   host and rejects private ranges outright, so it will not talk to the relay
   at its bridge address or by compose service name — both resolve into
   `172.x`. Symptom: `bootstrap` fails with `app_error: connector request
   failed` while the agent logs nothing.
2. **The socket's parent directory must be mode `0700`** (matching
   `--socket-dir-mode`). A directory left at the usual `0755` makes startup
   fail with `PermissionDenied` naming no path.
3. **`MARMOT_RELAYS` must point at the test relay.** Otherwise `bootstrap`
   emits an invite `nprofile` advertising the public WhiteNoise relays, which
   nothing in the test network can reach — and unlike the two above, this one
   fails silently and only shows up later as an invite no peer can act on.

There is no `serve` subcommand: running `wn-agent` with no subcommand is what
serves. Only `bootstrap` and `import-identity` are subcommands.

Verify the service by hand with:

```powershell
docker compose -f docker-compose.test.yml up -d --build wn-agent
docker exec wn-agent-interop wn-agent bootstrap `
  --home /data/marmot-agent --socket /run/marmot-agent/wn-agent.sock --no-quic --json
```

A working agent reports `"key_package_published": true` and a `relays` list
containing only the test relay. (From Git Bash, prefix with `MSYS_NO_PATHCONV=1`
or the container paths get rewritten into Windows ones.)

⚠ First build compiles a large Rust workspace from source and takes a while.
CI should cache the image layer rather than rebuild it per run — and should
assert the image exists afterwards, because `docker compose build` has been
observed to exit 0 when it could not reach the daemon or the registry at all.

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
