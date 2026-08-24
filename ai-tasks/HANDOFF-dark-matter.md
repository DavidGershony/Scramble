# HANDOFF — Dark Matter migration: you are here

**Updated:** 2026-08-24 · **Branch:** `feat/dark-matter` (pushed, in sync) ·
**Last commit at time of writing:** `7647847`

Read this first. It tells you exactly what exists, what is next, and how to do
it. It supersedes `step6-build-start-prompt.md`, which described the state
before P0–P2 landed.

**Orientation (read once, in this order):**
1. This file.
2. `ai-tasks/scramble-marmot-phased-plan-2026-08.md` — the authoritative plan.
   §3 is the phase table; §4 the `dotnet-mls` asks; §5 the Whitenoise
   questions; §7 the binding cutover rules.
3. `CLAUDE.md` — repo invariants (I1–I5) and the Dark Matter cutover rules.

---

## 1. The one-paragraph situation

Scramble is replacing its Marmot engine with a standalone Dark Matter
implementation (`Scramble.Marmot.*`), built fresh against Rust `mdk` pinned at
**`wn-agent-v0.9.10`**. Planning is finished. Phases P0 (storage), P1 (epoch
state machine) and P2 (account-identity proof) are done; P3 (transport) is
mostly done. Nothing is wired into the running app yet — the new engine is
entirely additive and nothing depends on it, so it cannot break the shipping
product. The first milestone that matters is **P6: engine v1 talking to a real
`wn-agent`**.

---

## 2. What exists now

Six new projects, all standalone (no reference to `marmot-cs`), all in
`Scramble.sln` and `Scramble.Desktop.slnf`, all running in the fast unit gate.
**363 tests in `Scramble.Marmot.Tests`, all passing.**

| Project | Phase | Contains |
|---|---|---|
| `src/Scramble.Marmot.Abstractions` | P0/P1/P3 | Ids (`GroupId`, `EpochId`, `MessageId`, `MemberId`), storage contracts and records, `EpochState`, `ITransportPeeler` + `PeeledMessage` |
| `src/Scramble.Marmot.Storage.Sqlite` | P0 | SQLite provider, migrations, transactions, epoch-anchored snapshots |
| `src/Scramble.Marmot.Engine` | P1 | `EpochManager` |
| `src/Scramble.Marmot.Identity` | P2 | `AccountIdentityProof` (`0x8009`), async signer seam |
| `src/Scramble.Nostr.Crypto` | P2/P3 | BIP-340, NIP-01 event ids, NIP-44 v2, NIP-59 gift wrap, ChaCha20-Poly1305 envelope, secp256k1 |
| `src/Scramble.Marmot.Wire.Nostr` | P3 | kind-445 codec, kind-444 Welcome, `NostrGroupPeeler` |

Also landed: two approved `dotnet-mls` changes on branch
`feat/generic-mls-additions` (AppDataUpdate proposal type; PublicMessage
produce/verify), and a `wn-agent` interop peer in `docker-compose.test.yml`.

---

## 3. Do this next

### 3a. First: check the crypto review

An independent review of the ported crypto was commissioned on 2026-08-24
(the code and its tests share an author, so the tests are not an oracle).
**Find its findings and act on them before building anything new** — a defect
in BIP-340, NIP-44, the gift wrap or the proof invalidates work layered on top.
If you cannot find the review output, say so rather than assuming it was clean.

### 3b. Finish P3 — the kind-30443 KeyPackage event

The last transport piece, and the point where P2's proof reaches the wire.

- Build and parse kind-30443 KeyPackage events.
- **Drop the `encoding` tag.** The previous implementation emitted it on
  30443/444/445; a current peer rejects such events at the envelope.
- Add the `app_components` tag, and NIP-40 `expiration`.
- Attach the `0x8009` account-identity proof (P2) to the leaf, and advertise
  `0x8009` in the leaf's `app_components` support list.
- Reference: `mdk@wn-agent-v0.9.10` `crates/cgka-engine/src/key_package.rs`;
  spec `transports/nostr.md`. Prior art to port from, with the tag fixes:
  `lib/marmot-cs/src/MarmotCs.Protocol/Mip00/KeyPackageEventBuilder.cs`.
- ⚠ The old code discarded KeyPackage private material (`initPriv`/`hpkePriv`).
  The new engine must persist it: a Welcome consumes it exactly once, and
  without it a join cannot complete.

### 3c. Then P4 — AppComponents

Scope is in plan §3. Build only the v1 set: `0x8001` profile, `0x8003`
admin-policy, `0x8004` routing, `0x8005` retention, `0x8009` proof carriage.
Media (`0x8002`/`0x8008`/`0x800b`), QUIC (`0x8006`), avatar (`0x8007`) and
lifecycle (`0x800c`) are deferred to P12 — do not build them.

P4 also owns the Current-profile group invariants: `RequiredCapabilities` must
list extension `0x0006` and proposal `0x0008`, and the required-components set
must contain `0x8003` and `0x8009`.

### 3d. Non-code items still open (not blocking)

- **Open a PR for `feat/dark-matter`.** 24 commits and growing; it is
  reviewable now and will not be after P6. This is the repo's documented
  flag-day failure mode (I4).
- **Merge `feat/generic-mls-additions`** into `dotnet-mls` `main`. It is pushed
  but unreviewed. Any NuGet tag must come after that merge, never from the
  branch.
- **Send Whitenoise the questions** in plan §5 (deployed tag, flip date,
  wire-stable tag, disband-for-interop). Q2 on legacy proofs is closed.

---

## 4. How to work here

**Commands.**

```powershell
# Fast unit gate — includes every Scramble.Marmot test
dotnet test Scramble.Desktop.slnf --filter "Category!=Relay&Category!=Integration" -p:DesktopOnly=true

# Just the engine tests
dotnet test tests/Scramble.Marmot.Tests

# Integration gate (needs Docker; required by CI on engine paths)
docker compose -f docker-compose.test.yml up -d nostr-relay
dotnet test tests/Scramble.Diagnostics/ --filter "Category=Integration|Category=MIP-Compliance|Category=ProtocolCompliance|Category=FullE2E|Category=EpochSync|Category=DeviceSync|Category=OutboxModel|Category=Notifications|Category=RelayHarness|Category=ExporterSecret"

# The Dark Matter interop peer
docker compose -f docker-compose.test.yml up -d --build wn-agent
docker exec wn-agent-interop wn-agent bootstrap --home /data/marmot-agent `
  --socket /run/marmot-agent/wn-agent.sock --no-quic --json

./scripts/check-drift.ps1
```

**Adding a project.** Add it to `Scramble.sln`, to `Scramble.Desktop.slnf` (or
it will not run in the unit gate), and to the path triggers in
`.github/workflows/integration.yml` (invariant I2 — engine code must not land
without the interop suite running).

**Rules that bind, and why.**
- **Standalone.** No reference to `marmot-cs` from any `Scramble.Marmot.*`
  project. Codecs are ported in. This duplicates code still in
  `Scramble.Core`; that is intended, and Core's copies go at cutover (P11).
- **No Marmot types in `Scramble.Presentation`.** Engine types stop at the
  service layer.
- **Generic Nostr crypto stays out of Marmot namespaces** — it lives in
  `Scramble.Nostr.Crypto` so a future non-Marmot provider can reuse it.
- **`lib/dotnet-mls` needs explicit permission per change.** Two items are
  approved and already done. Items (b) SelfRemove, (d) retained past-epochs and
  (f) staged-commit introspection are **not** approved — ask separately.
- **Legacy `0xf2f1` account-identity proof is out of scope** (decided
  2026-08-10). Build only Current `0x8009`. Do not re-open it.
- **Safe-export is resolved and dropped from v1** (plan §4). Do not re-open it.
- **Small commits** (I4) and **tests land with their code** (I3).

**Verify, do not assume.** Two habits earned their keep this session and are
worth keeping: mutation-check a regression test by breaking the fix and
confirming the test fails; and never trust an exit code alone — `docker compose
build` was observed exiting 0 having reached neither the daemon nor the
registry.

---

## 5. Traps already paid for — do not rediscover these

| Trap | What happens | The rule |
|---|---|---|
| `NBitcoin.Secp256k1.SigVerifyBIP340` | Broken on .NET Android only | Use the BouncyCastle BIP-340 implementation in `Scramble.Nostr.Crypto`. Do not "simplify" it back to the library call. |
| `encoding` tag on 445/444/30443 | Current peers reject the whole event before any MLS processing | Never emit it. kind-445 carries exactly one `h` tag and at most one `expiration`, nothing else. |
| Uppercase hex in tags | Decodes identically but changes the event id | Lowercase everywhere on the wire. |
| Dedup on the Nostr event id | Same MLS message under a different envelope is processed twice | Dedup on `MessageId.FromMlsBytes`; transport ids are a pre-filter only. |
| `wn-agent` + private-range relay | `connector request failed`, nothing in the agent log | The agent accepts plaintext `ws://` only for a *literal* loopback host, hence `network_mode: host` and `ws://127.0.0.1:7777`. |
| `wn-agent` socket dir at `0755` | `PermissionDenied` naming no path | The socket's parent directory must be `0700`. |
| `wn-agent serve` | `unrecognized subcommand` | There is no `serve`. Running `wn-agent` bare is what serves. |
| Git Bash + `docker exec` | Paths rewritten to `C:/Program Files/Git/...` | Prefix with `MSYS_NO_PATHCONV=1`. |
| Submodule left on another branch | `Scramble.Core` fails to compile with a missing symbol | `git submodule update --init --recursive` restores the recorded commit. |

---

## 6. Upstream moves fast — re-check the pin

`mdk` landed ~8 commits/day through mid-2026, and the account-identity-proof
format hard-broke between two tags. Before relying on any reading of upstream:

```bash
gh api repos/marmot-protocol/mdk/tags --paginate -q '.[].name' | head
```

If the pin needs to move, do it deliberately: bump `MDK_REF` in
`tests/wn-agent-docker/Dockerfile`, re-run the drift diff over the modules in
plan §2, and record the new pin in the plan doc. An unpinned or silently
bumped reference retargets every interop test at once.

---

## 7. The date band, for context

Wire interop with `wn-agent`: **mid-Nov 2026 / late Dec 2026 / mid-Feb 2027**
(optimistic / expected / pessimistic). Production cutover: **mid-Mar / mid-May /
mid-Sep 2027**. Assumptions and the three risks driving the width are in plan
§6. The band is wide chiefly because of upstream velocity, not because any
single piece is unknown.
