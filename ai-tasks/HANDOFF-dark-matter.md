# HANDOFF — Dark Matter migration: you are here

**Updated:** 2026-08-26 (sixth revision) · **Branch:** `feat/dark-matter`
(pushed, in sync) · **Last commit at time of writing:** `4a5a96e`

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
state machine), P2 (account-identity proof) and P4 (app components) are done,
and P3's transport codecs are complete. What remains of P3 needs real MLS types
and arrives with the engine (§3b). Nothing is wired into
the running app yet: the new engine is entirely additive and nothing depends on
it, so it cannot break the shipping product. The first milestone that matters is **P6: engine v1 talking to a real
`wn-agent`**.

---

## 2. What exists now

Seven new projects, all standalone (no reference to `marmot-cs`), all in
`Scramble.sln` and `Scramble.Desktop.slnf`, all running in the fast unit gate.
**705 tests in `Scramble.Marmot.Tests`, all passing.**

| Project | Phase | Contains |
|---|---|---|
| `src/Scramble.Marmot.Abstractions` | P0/P1/P3 | Ids (`GroupId`, `EpochId`, `MessageId`, `MemberId`), storage contracts and records incl. `IKeyPackageStorage`, `EpochState`, `ITransportPeeler` + `PeeledMessage` |
| `src/Scramble.Marmot.Storage.Sqlite` | P0/P3 | SQLite provider, migrations, transactions, epoch-anchored snapshots, KeyPackage bundles + private material |
| `src/Scramble.Marmot.Engine` | P1 | `EpochManager` |
| `src/Scramble.Marmot.Identity` | P2 | `AccountIdentityProof` (`0x8009`), async signer seam |
| `src/Scramble.Nostr.Crypto` | P2/P3 | BIP-340, NIP-01 event ids, NIP-44 v2, NIP-59 gift wrap, ChaCha20-Poly1305 envelope, secp256k1 |
| `src/Scramble.Marmot.Wire.Nostr` | P3 | kind-445 codec, kind-444 Welcome, kind-30443 KeyPackage, `NostrGroupPeeler` |
| `src/Scramble.Marmot.AppComponents` | P4 | id registry, QUIC-varint codec, the four v1 schemas, app-data dictionary, Current-profile invariants, commit authorization, staged-commit component integrity, the shared relay-URL profile |

Also landed: the two approved `dotnet-mls` changes are **merged and released**
as **`v0.1.0-beta.8`** (2026-08-25) — AppDataUpdate proposal type, PublicMessage
produce/verify, plus a security fix where `CacheProposal` previously cached
unauthenticated proposals that a later Commit could then reference by hash. 384
of its tests pass, RFC 9420 vectors included. Reference a tag, never the branch.
Also a `wn-agent` interop peer in `docker-compose.test.yml`.

---

## 3. Do this next

### 3a. The crypto review is DONE and actioned — nothing to do here

An independent review ran on 2026-08-24 and all five of its defects are fixed
(`bd5d630`, `d44f254`, `30282fa`). Do not re-commission it. What it found, so
the reasoning is not lost:

1. **kind-445 events were never verified.** The id and Nostr signature are now
   checked before any field is trusted, and the transport id is bound to the
   verified hash — it keys deduplication, so an attacker-chosen value let a
   legitimate message be dropped as a duplicate.
2. **Emoji produced the wrong event id.** Every .NET JSON encoder escapes above
   the BMP as surrogate pairs; NIP-01 requires verbatim. The serialiser is now
   hand-written. **Do not replace it with `Utf8JsonWriter`** — no encoder
   setting fixes this.
3. **Malformed input escaped as the wrong exception type**, bypassing the
   retryable/terminal classification the engine branches on. Parsing is total now.
4. **NIP-44 lacked the 2026-06-28 extended length prefix**, so large messages
   could be neither sent nor read. `nip44.vectors.json` has not been regenerated
   since the amendment, so the vector file cannot catch this — the boundary
   vectors inline in `44.md` are what pin it.
5. **Relay URLs accepted userinfo, fragments and duplicates**, contrary to the
   Marmot profile.

It also verified as sound, against external vectors: all 15 BIP-340 cases, all
35 NIP-44 conversation keys, the account-proof fixture byte for byte, the
gift-wrap trust model, and the kind-445 tag shape against upstream.

**Still open, deliberately:** no key-material zeroization anywhere (the reviewer
noted rather than escalated it; it is consistent with the previous
implementation). Worth scoping properly rather than sprinkling.

**The lesson worth keeping:** one test asserted the defect *was* the behaviour,
because the fixture it used could only ever exercise the rejection path. When
code and tests share an author, the tests are not an oracle — pin to external
vectors, and get fresh eyes on cryptography.

### 3b. P3's transport codecs are DONE — what is left of P3 needs MLS

`c72dd91` added the kind-30443 codec and `f2143ee` the KeyPackage storage.
Both are green in the fast unit gate. What landed:

- Build (unsigned template) and verify-then-parse kind-30443 events, with the
  seven-tag cardinality rules, id-list spelling (`0x` + four lowercase hex
  digits, exact-string compared), and the `app_components`-must-carry-`0x8009`
  rule. Plus the transport candidate ranking (recency, then event id within a
  slot, then decoded KeyPackageRef across slots).
- No `encoding` tag in either direction. Inbound it is inert rather than
  rejected: kind 30443's tag set is **not closed** the way kind 445's is, so an
  unknown tag is carried past. Do not "tighten" this — rejecting unknown tags
  here invents a rule the spec does not state.
- `IKeyPackageStorage` + the SQLite table, keyed by KeyPackageRef with a second
  lookup by kind-30443 event id (what a Welcome actually names). Private
  material is persisted, and erasure is a one-way, idempotent transition; `Put`
  inserts and never replaces, so a stale record cannot resurrect erased key
  material.

**Two P3 bullets from the previous revision are NOT done, deliberately.** Both
need a decoded MLS KeyPackage, and no `Scramble.Marmot.*` project references
`dotnet-mls` yet:

- Attaching the `0x8009` proof to the LeafNode and advertising it in the leaf's
  own support list. The *transport* advertisement is done; the *leaf* one is
  KeyPackage construction.
- The two checks the codec deliberately surfaces instead of performing:
  KeyPackageRef equality against the decoded KeyPackage, and binding the event
  author to the credential identity. Both are mandatory. They are on
  `KeyPackagePublication` waiting for a caller.

These belong with KeyPackage generation, which is engine work (P6) and arrives
with the first `dotnet-mls` reference. **Do not let them fall off** — the
codec's XML docs name them, but nothing enforces them yet.

Also still open from P3's exit criterion: no `DarkMatterInterop` test that
publishes a KeyPackage a live `wn-agent` will fetch, and upstream ships no byte
fixture for kind 30443 — the codec is pinned to the spec text and to
`transport-nostr-adapter/src/key_package.rs`. If one is wanted before P6, it
needs the Amethyst-style generator (plan §3 tier 2b), not a hand-written file.

### 3c. P4 is DONE — next is P6

Landed: `6152a59` codec primitives, `c180392` the four v1 schemas, `648c751`
the app-data dictionary and Current-profile invariants, `37b076f` the routing
index, `d0a3f38` commit authorization, `2deab4c` the relay-URL dedupe,
`240ea0d` the two GroupContext refusals below, and `4a5a96e` the staged-commit
integrity rule that closed the phase.

Built only the v1 set, as scoped: `0x8001` profile, `0x8003` admin-policy,
`0x8004` routing, `0x8005` retention, `0x8009` proof carriage. Media
(`0x8002`/`0x8008`/`0x800b`), QUIC (`0x8006`), avatar (`0x8007`) and lifecycle
(`0x800c`) stay deferred to P12 — **do not build them**, and note that
`CurrentProfile.KnownGroupComponents` deliberately excludes them so a group
requiring one is rejected rather than joined-and-ignored.

**Two encoding facts that will bite whoever touches this next.**

1. **MLS varints are not QUIC varints.** MLS vector lengths (RFC 9420 §2.1.2)
   use 1, 2 or 4 bytes and treat the 8-byte form as invalid, capping at
   2^30-1; the QUIC varint the component payloads use internally allows all
   four widths, and mdk's helper is literally called `encode_quic_varint`. They
   agree at every realistic size, so a mix-up is invisible until it is not. The
   dictionary goes through `AppDataDictionary.WriteMlsLength`; component
   payloads go through `ComponentCodec.WriteVarint`. Keep them apart.
2. **64 is where a varint widens to two bytes.** Two 32-byte admin keys are
   exactly 64 payload bytes. A hand-built fixture using a literal `64` for the
   length prefix decodes as the one-byte value 0 — which still throws, just for
   the wrong reason. Build test fixtures through the codec.

**The rule the whole subsystem turns on:** producers canonicalise, decoders
reject. `Create()` sorts and deduplicates because nothing is committed yet;
`Decode()` refuses the same input. This is signed group state, so a member that
quietly repairs what it was given holds a canonical form nobody else has. The
admin list is the worst case — two members would disagree about who governs the
group while both believed their state valid.

**P4's last item landed in `4a5a96e`** —
`validate_app_component_integrity_for_staged_commit`, as
`AppComponentIntegrity`. Two rules that must be run **as a pair**, because
either alone leaves half the door open:

- `ValidateStagedCommit` takes the diff. The dictionary and the requirement
  list may never disappear, nor may the state of anything the **resulting**
  epoch requires — resulting, so one authorized commit can still unrequire and
  remove an optional component atomically. Every other changed entry must be
  accounted for by one of the commit's own `AppDataUpdate` operations, matched
  on the **resulting value**, not merely on the component id.
- `ValidateUpdateBatch` takes the operations: one per component, requirement
  list resolved across the whole batch before any removal is judged against it,
  payloads decoded under their schemas. Without it, corrupt bytes inside a
  perfectly "update-backed" change sail through.

The hole being closed is upstream's, not ours: MLS's own guard checks the
resulting dictionary against a commit's `AppDataUpdate` proposals and **returns
early when there are none**, so a `GroupContextExtensions`-only commit can swap
the admin set or drop the `app_data_dictionary` outright and MLS accepts it.

The operation hangs off `StagedProposal` rather than a second list beside it,
so an engine caller cannot fill one and forget the other; a proposal staged
without its operation is refused rather than read as a no-op. Both rules sit
behind `StagedCommitView`, so **P6's engine has to actually call them** — they
are pure functions with no caller yet, exactly like the two KeyPackage checks
in §3b. Do not let them fall off either.

**Two fail-open divergences found while porting and fixed** (`240ea0d`); both
would have had us join a group every current peer refuses, then sit in it
alone:

- `safe_aad` (`0x0002`) had been treated as presence-only. Upstream refuses
  those bytes as GroupContext state. It stays a *known* component — an unknown
  optional component is still carried opaquely; this one is an error.
- Encrypted-media v1 (`0x8008`) is **frozen, not deferred**: a Current-profile
  group may neither require it nor hold its state. Required, it already failed
  as unsupported; merely present, it fell through as an unknown optional id.
  It is the one id named without a codec behind it, and only so it can be
  refused.

**It is NOT permission-gated on `dotnet-mls`, despite looking like it should
be.** Checked in the source on 2026-08-25 rather than inferred, and the answer
is more specific than "needs staged-commit introspection":

- **dotnet-mls has no *inbound* staging at all.** `ProcessCommit` applies
  directly (`MlsGroup.cs` ~line 1193, "Apply the new state"). Staging exists
  only for *outbound* commits (`Commit` → `MergePendingCommit` /
  `ClearPendingCommit`), and `PendingCommitState` is `internal` besides.
- **But rollback works, with zero library changes.** `Export()` and `Import()`
  are public. So: snapshot, `ProcessCommit`, inspect the resulting dictionary,
  and re-`Import` the snapshot if the commit is Marmot-invalid. This is the same
  primitive the convergence deep-dive identified for snapshot/restore, applied
  to a different problem.
- **A throw inside `ProcessCommit` is already safe.** It computes into
  `tentative*` locals and assigns only at the end, so an MLS-invalid commit
  leaves state untouched. Rollback is needed only for commits that are
  MLS-valid but Marmot-invalid — a much narrower path than it first looks.
- **The round-trip is lossy in exactly two places, and neither matters here.**
  `Export()` omits `_proposalCache` — recoverable by re-caching, and
  `ProcessCommit` clears it anyway — and `_resumptionPsks`, which **Marmot v1
  does not use**. Checked, not assumed: the spec repo contains no occurrence of
  "resumption"; PSK appears only in `group-lifecycle-v1.md` (`0x800c`, deferred
  to P12) and the `features/multi-device.md` draft, which is marked "Status:
  branch draft" and says its bytes MUST NOT be implemented for interop yet —
  and which uses an **External** PSK (`MLS-Exporter("marmot", join_psk_id,
  KDF.Nh)`), not a resumption PSK. **So the rollback route is clean and P4's
  last item needed no library change** — and did not, in the end, need any MLS
  introspection at all: the rule is a pure function of two dictionaries and a
  proposal list, which is what the view types were for.

**Conformance vectors are live** (`9e1ea21`). Upstream's byte fixtures are
mirrored verbatim in `tests/Scramble.Marmot.Tests/vectors/marmot/` and run
under `Category=ConformanceVector` in the fast unit gate. They are the only
tests here not written by the author of the code they check. Three of them
matter beyond decoding: our encoder must reproduce upstream's bytes exactly
(reading them proves nothing about being read); the `component_data_hex` entry
independently pins the dictionary framing, which has no Marmot spec prose of
its own; and the Current-profile constants are compared against upstream's
declared contract rather than merely hardcoded.

**If a vector starts failing after a pin bump, that is the signal it exists to
give.** Refresh the fixture from the new tag deliberately and read the diff —
never edit one to make it pass.

Only the byte fixtures are mirrored. Upstream's scenario vectors
(`invite-member`, `convergence-*`, …) drive a whole engine through a step list
and become runnable at P6.

### 3e. The two `dotnet-mls` blockers are FIXED — the change is unreleased

**Status (2026-08-26):** permission granted for both, both implemented, and
they sit on the local branch `feat/app-data-dictionary-commits` in
`lib/dotnet-mls` as commit `abb7f3b`. **400 tests pass** (336 + 64 crypto), 16
of them new. **Not merged to `main`, not tagged, not pushed** — the release
decision (a `v0.1.0-beta.9` tag) is the user's, and until it happens the
Scramble side builds against it only because `UseLocalDotnetMls` is `true`.
The repo rule is to reference a tag, so this must be released before anything
depends on it in CI.

What was wrong, kept here because the reasoning is worth more than the fix:

**(1) `AppDataUpdate` is wire-format only. The group never applies it.** This
is the interop-fatal one. `47bb6d2` added the proposal struct, its codec and
its `ProposalType`; `MlsGroup` was not touched. In both `Commit`
(`MlsGroup.cs` ~342) and `ProcessCommit` (~844) the proposal chain is a series
of `else if`s over Add/Remove/Update/PSK/GroupContextExtensions/ExternalInit
with **no arm for `AppDataUpdate` and no final `else`** — it is silently
dropped. `tentativeExtensions` is a clone of the current extensions that only
a `GroupContextExtensions` proposal replaces, so the resulting GroupContext
keeps the old `app_data_dictionary`.

Every conformant peer computes the new one. The confirmation tag is computed
over the resulting GroupContext (~1183), so **any commit carrying an
`AppDataUpdate` fails its tag check on our side** — not a lost update, a
rejected commit.

This is not a corner case. Read off `message_processor/send.rs` at
`wn-agent-v0.9.15`: mdk couples an admin-policy `AppDataUpdate` into the same
commit on **invite-with-admin-grant** (:237) and on **removing a member who is
an admin** (:543). Both are P6 exit criteria. Plan §4 item (e) named
"commit-time app-data dictionary computation" as part of the ask; the released
half is the proposal type.

**(2) A Marmot KeyPackage cannot be built from outside the library.**
`MlsGroup.CreateKeyPackage` (:169) hardcodes `Extensions = Array.Empty<Extension>()`
on the leaf and takes only `supportedExtensionTypes` — no leaf extensions, no
proposal capabilities — and `SignLeafNode` (:2024) is `private`. A Marmot
KeyPackage needs the `0x8009` proof in the leaf's own `app_data_dictionary`
and capabilities advertising extension `0x0006` and proposal `0x0008`.

The alternative is reimplementing LeafNodeTBS signing on our side, which
duplicates security-critical serialisation the library already owns and would
diverge silently the first time either changes. **Do not do that.** The ask is
generic RFC 9420 — leaf extensions and capabilities are both RFC features, no
Marmot constants cross the boundary.

**How they were fixed.** The dictionary is now a first-class `AppDataDictionary`
type in `DotnetMls.Types` — a vector of `{uint16 component_id, opaque data<V>}`,
ordered by id, one entry per id, both enforced on decode — and both commit paths
apply the proposals to it. The application rule follows OpenMLS's
`extensions-draft` behaviour exactly, because that is what `wn-agent` runs: the
dictionary comes from the **current** GroupContext, is updated, and is written
into the resulting extension set, so a commit carrying both a
`GroupContextExtensions` proposal and `AppDataUpdate`s takes its extensions from
the former and its dictionary from the latter. `CreateKeyPackage` and
`CreateGroup` both take `leafExtensions` and `supportedProposalTypes` now, and
union a leaf's carried extension types into its advertised capabilities.

**One honest note on that work.** The sort into component-id order survived
mutation: operations on distinct ids commute and the sort is stable, so it
cannot change the result. It is kept for legibility against the reference
implementations and the XML doc says so rather than letting it take credit.

**What P6's first slice needs, read off `wn-agent-v0.9.15`** (`key_package.rs`,
`capabilities.rs`) so it does not have to be rediscovered:

- Current-profile leaf capabilities are extensions `{0x0003 required_capabilities,
  0x0006 app_data_dictionary}` and proposals `{0x0008 app_data_update}`, plus
  whatever the feature registry adds. `0x8009` is **not** an extension capability
  in Current — that was the Legacy profile's shape.
- The leaf's own `app_data_dictionary` carries the `0x8009` proof, and the
  proof is validated against the KeyPackage's **own** ciphersuite, not a
  default (mdk#747).
- KeyPackageRef is `hash_ref` over the KeyPackage struct, while the published
  bytes are an `MLSMessage` wrapping it (`WireFormat.MlsKeyPackage`). Do not
  hash the envelope.
- mdk marks generated KeyPackages **last-resort**. We have no last-resort
  extension yet; decide deliberately whether P6 needs one rather than
  discovering it at interop.

Everything else P6 needs is in place.

### 3d. Non-code items still open (not blocking)

- **Open a PR for `feat/dark-matter`.** **43 commits** ahead of `master` and
  growing; it is reviewable now and will not be after P6. This is the repo's
  documented flag-day failure mode (I4). *(The user has said a PR is not wanted
  — the plan is to keep going and merge at the end. Recorded here because I4
  names exactly this shape as the risk, not to re-litigate the decision.)*
- ~~**Merge `feat/generic-mls-additions`**~~ **✅ DONE (2026-08-25)** — reviewed,
  merged fast-forward into `main`, released as `v0.1.0-beta.8`.
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
  approved, done, and released as **`v0.1.0-beta.8`** — reference the tag, never
  the branch. **The two P6 blockers in §3e were granted and implemented on
  2026-08-26** and await a release. Still open, unchanged from the 2026-08-25
  review (plan §4):
  - **(b) SelfRemove — real, ask at P7.** `0x000a` is not expressible; the
    closed `ProposalType` enum stops at `AppDataUpdate = 8`. No workaround
    exists for a proposal type that cannot be encoded.
  - **(d) retained past-epochs — probably avoidable, do not ask yet.** A
    per-epoch `Export()` yields the `(KeyScheduleEpoch, SecretTree)` pair it
    asks for. One question is unsettled: ratchet advancement on decrypt from a
    restored snapshot. Blocks P8, so there is time to settle it.
  - **(f) staged-commit introspection — not a blocker**, as plan §4 always
    said. `Export()`/`Import()` rollback covers it.
  **Re-check whether an ask is still real before spending a permission on it.**
  Two of the three dissolved on a source read costing minutes, and a permission
  granted for something unnecessary is worse than not asking — it invites the
  change to be made.
- **Legacy `0xf2f1` account-identity proof is out of scope** (decided
  2026-08-10). Build only Current `0x8009`. Do not re-open it.
- **Safe-export is resolved and dropped from v1** (plan §4). Do not re-open it.
- **Small commits** (I4) and **tests land with their code** (I3).

**Verify, do not assume.** Three habits have repeatedly earned their keep here:

- **Mutation-check a regression test** by breaking the fix and confirming the
  test fails. It has caught weak tests several times — including one that
  asserted a limit existed without pinning where it was, and one whose fixture
  could only ever reach the rejection path.
- **A guard that survives mutation is not automatically the guard doing the
  work.** `last_epoch IS NOT NULL` in the routing prune looked load-bearing and
  is not — SQL's three-valued logic already excludes the row. Say so in the
  comment rather than letting it take credit.
- **Never trust an exit code alone.** `docker compose build` was observed
  exiting 0 having reached neither the daemon nor the registry.

---

## 5. Traps already paid for — do not rediscover these

| Trap | What happens | The rule |
|---|---|---|
| `NBitcoin.Secp256k1.SigVerifyBIP340` | Broken on .NET Android only | Use the BouncyCastle BIP-340 implementation in `Scramble.Nostr.Crypto`. Do not "simplify" it back to the library call. |
| `encoding` tag on 445/444/30443 | Current peers reject the whole event before any MLS processing | Never emit it. kind-445 carries exactly one `h` tag and at most one `expiration`, nothing else. |
| Assuming kind 30443 has a closed tag set like 445 | Rejecting an unknown tag invents a rule the spec does not state, and breaks against a peer that adds one | 445 says "no other tag"; 30443 constrains its seven tags and is silent on others. Carry unknowns past. Duplicates of the seven are still fatal. |
| Reading only the first of a repeated tag | An attacker prepends a value another implementation ignores, so two peers disagree about one signed event | Explicitly a MUST NOT. Reject the event. |
| Uppercase hex in tags | Decodes identically but changes the event id | Lowercase everywhere on the wire. |
| Dedup on the Nostr event id | Same MLS message under a different envelope is processed twice | Dedup on `MessageId.FromMlsBytes`; transport ids are a pre-filter only. |
| `wn-agent` + private-range relay | `connector request failed`, nothing in the agent log | The agent accepts plaintext `ws://` only for a *literal* loopback host, hence `network_mode: host` and `ws://127.0.0.1:7777`. |
| `wn-agent` socket dir at `0755` | `PermissionDenied` naming no path | The socket's parent directory must be `0700`. |
| `wn-agent serve` | `unrecognized subcommand` | There is no `serve`. Running `wn-agent` bare is what serves. |
| Git Bash + `docker exec` | Paths rewritten to `C:/Program Files/Git/...` | Prefix with `MSYS_NO_PATHCONV=1`. |
| Submodule left on another branch | `Scramble.Core` fails to compile with a missing symbol | `git submodule update --init --recursive` restores the recorded commit. |
| `Utf8JsonWriter` for NIP-01 canonical form | Emoji get surrogate-escaped, so the event id differs from everyone else's | Use the hand-written serialiser in `NostrEventTemplate.Serialize`. No encoder option fixes it. |
| Pinning only to `nip44.vectors.json` | Passes while missing the 2026-06-28 amendment the file predates | Check `44.md` prose and its inline vectors too. |
| Using a QUIC varint for an MLS vector length | Agrees at every realistic size, then silently diverges past 2^30 | MLS allows 1/2/4 bytes only. `AppDataDictionary.WriteMlsLength` for MLS lengths, `ComponentCodec.WriteVarint` inside component payloads. |
| Guessing a component id from its name | `0x8002` is the Blossom *image* component; encrypted-media v1 is `0x8008`. Freezing the wrong id would refuse a legal group and admit a frozen one | Read the constant out of `crates/traits/src/app_components/mod.rs`. Every id in `AppComponent` traces to a line there. |
| A literal `64` as a varint length prefix in a test fixture | Decodes as the one-byte value 0, so the test still throws — for the wrong reason | Build fixtures through the codec, never by hand. |
| A 500-character hostname in a URL-length test | .NET's own host-length limit trips first, so the test asserts the wrong thing | Pad the path instead; the relay profile permits one. |
| Literal U+2028/U+2029 in C# source | They are line terminators — the file will not compile | Build such strings at runtime from char codes. |
| Python `open(...,"w")` then an encode error | Truncates the file to zero bytes | Write to a temp file and move, or use the Edit tool. |

---

## 6. Upstream moves fast — re-check the pin

`mdk` landed ~8 commits/day through mid-2026, and the account-identity-proof
format hard-broke between two tags. Before relying on any reading of upstream:

```bash
gh api repos/marmot-protocol/mdk/tags --paginate -q '.[].name' | head
```

**Checked 2026-08-26: upstream is at `wn-agent-v0.9.15`, five tags ahead of our
`v0.9.10` pin, and the app-component drift diff over 0.9.11–0.9.15 has now been
run** — `crates/cgka-engine/src/app_components.rs` at both tags, whole-file.
Nothing was re-pinned, and nothing needs to be:

- **No component id, rule or encoding changed.** The diff is error *typing* —
  an orphaned-admin refusal moved from the unclassified `Other` bucket to
  `UnknownMember`, naming the key at fault — plus a new `admin_policy_is_empty`
  helper backing an `AdminDepletion` refusal we already have (`AdminPolicy`
  rejects an empty set on both encode and decode).
- The P4 work was therefore read at **0.9.15** and matches 0.9.10 line for line
  in every rule it ports.

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
