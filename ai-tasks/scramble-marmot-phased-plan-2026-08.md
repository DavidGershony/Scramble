# `Scramble.Marmot` — phased build plan + date with confidence band (step 5)

**Date:** 2026-08-09. **Status: DONE — this is now the authoritative next-steps
source for the Dark Matter migration.** It supersedes the "next step" sections of
`00-START-HERE-dark-matter.md` and `dark-matter-migration-scoping-2026-07.md` §9.

**Confidence key:** 🟢 verified against source this session · 🟡 informed
inference · 🔴 needs verification before code lands.

**Reference pin (NEW): `wn-agent-v0.9.10`** (was `v0.9.4`). Rationale and drift
findings in §2.

**Inputs** (all read; do not re-derive from memory):
`00-START-HERE-dark-matter.md` · `dark-matter-migration-scoping-2026-07.md`
(§10 layout, §12 dotnet-mls audit) · `survives-rewrite-diff-2026-07.md`
(§4 build order, §6 estimate) · `convergence-deepdive-2026-07.md` (§9, §11–12) ·
`account-identity-proof-v2-2026-08.md` (§0, §6) ·
`protocol-agnostic-report-2026-08.md` §6 · `CLAUDE.md` (I2, I4, I5 + cutover
rules).

---

## 1. Headline

- **The date, in one line:** first **wire-interop milestone against deployed
  Whitenoise ≈ late December 2026** (band: mid-Nov 2026 → mid-Feb 2027);
  **full production cutover ≈ mid-May 2027** (band: mid-Mar 2027 → mid-Sep
  2027). Assumptions, arithmetic, and the three risks driving the band are in
  §6. **Give WN both milestones, not just the second one** — the first is the
  one that de-risks their planning.
- **The re-pin was necessary and is not a one-off.** `v0.9.4` → `wn-agent-v0.9.10`
  is **151 commits in 19 days**, and upstream has landed **84 more commits in the
  11 days since** that tag (HEAD active 2026-08-09). 🟢 That velocity — roughly
  **7–8 commits/day sustained** — is the single largest risk to any date we give,
  and it is the reason §6's pessimistic arm is far from the expected one.
- **Good news: nothing in the drift invalidates the step-2 or step-3 analysis.**
  The convergence v1 policy constants are byte-identical, the branch-selection
  comparator is untouched, app-component IDs `0x8001`–`0x8008` are unchanged,
  `NostrRoutingV1 = 0x8004` holds, and the engine's module decomposition is the
  same. The drift is **additive** (new subsystems) plus **one strictness sweep**
  (profile validation, wire-shape validation). Details and 🟢/🟡/🔴 per finding
  in §2.
- **One estimate moves up, one moves down.** AppComponents goes **M → M/L**
  (`app_components.rs` grew +1,405/−145 and now carries the Current-profile
  invariant validators and a terminal-disband workflow). AccountProof stays
  **S** (step 4's pin survives drift intact 🟢).
- **The dotnet-mls picture sharpened into a hard blocker.** Gap (e)
  `AppDataUpdate` is not merely "the biggest new question" — proposal type
  `0x0008` is a **RequiredCapabilities entry on every Current-profile group**
  (`app_components.rs:41`, `:1042-1052`; `capabilities.rs:83`). Without it in
  `dotnet-mls`, Scramble cannot **create or join** a Current-profile group at
  all — not just "cannot update metadata". It ranks alongside (c) PublicMessage
  produce as a phase-zero permission ask. 🟢

---

## 2. Re-pin + drift-diff: `v0.9.4` → `wn-agent-v0.9.10`

**Method:** `gh api` compare for the file inventory, then per-file fetch of both
tags and a local diff over every module the step-2/step-3 analyses relied on:
`engine.rs`, `epoch_manager.rs`, `fork_recovery.rs`, `group_lifecycle.rs`,
`publish.rs`, `wire_format.rs`, `app_components.rs`, `canonicalization.rs`,
`convergence.rs`, `distributed_convergence.rs`, `message_processor/{mod,ingest,
send,store}.rs`, `capabilities.rs`, `capability_manager.rs`, plus the
`crates/traits` seam files (`engine.rs`, `ingest.rs`, `peeler.rs`, `storage.rs`,
`app_components/mod.rs`) and `crates/transport-nostr-peeler`. Line counts:
**+7,629 / −971** across the engine modules alone. 🟢

Findings, worst-first. 🔴 = invalidates a prior finding; 🟡 = changes an
estimate or adds scope; 🟢 = confirms a prior finding.

| # | Finding | Impact | Evidence |
|---|---|---|---|
| D1 🔴 | **`SendIntent::SelfUpdate` exists again.** Step 3 recorded `StageSelfUpdateAsync` as **DROP, 0% reuse**, on the basis that DM had no user-facing self-update intent. HEAD adds one (own-leaf rotation for key-material maintenance), backed by a new `self_update.rs` (250 lines) with `force_self_update(true)` + `consume_proposal_store(false)`. | Step-3 §2.1 row is wrong; the marmot-cs shape partially survives after all. Small **+S** to the engine, and it is a **prerequisite for the key-package maintenance lifecycle** (D4). | `traits/engine.rs` `SendIntent::SelfUpdate`; `cgka-engine/src/self_update.rs:1-5` |
| D2 🔴 | **`AppDataUpdate` (proposal type `0x0008`) is a Current-profile group invariant, not just a feature.** Every Current-profile GroupContext MUST list it in `RequiredCapabilities.proposal_types`, validated on create, join, and every staged commit. RFC 9420 then requires every member leaf to advertise it. | Elevates dotnet-mls gap (e) from "biggest open question" to **hard blocker for create *and* join**. Was already true at v0.9.4 (`capabilities.rs` inserted it) but neither step 3 nor scoping §12 flagged it as a *required capability*. | `app_components.rs:41`, `:1042-1052`; `capabilities.rs:83`; `group_lifecycle.rs:239-345` |
| D3 🟡 | **Strict kind-445 tag shape is now enforced by the reference peeler.** A kind-445 event MUST carry exactly one `h` tag (32 bytes lowercase hex) and **at most one** `expiration` tag, and **no other tags at all** — anything else is rejected at peel. | Confirms and hardens the known "drop the `encoding` tag" port fix (scoping §4): our builders emit `encoding` on 445/444/30443, so at HEAD every message we send would be **rejected outright**, not merely non-conformant. Makes the Wire.Nostr port a correctness gate, not cleanup. | `transport-nostr-peeler/src/event.rs:59-132`, test table `:362-440` |
| D4 🟡 | **Three new engine subsystems**, none of which existed at v0.9.4: `disband.rs` (636 lines — durable terminal group-disband via new app-component `GroupLifecycleV1 0x800c`), `maintenance.rs` (237 lines — key-package lifecycle, durable group evolution, durable transport fanout, periodic maintenance policy), `self_update.rs` (250 lines, see D1). Storage trait grew **34 → 79 methods** (+45), mostly behind new sub-traits (`OutboundFanoutStorage`, `DisbandRequestStorage`, `DisbandCandidateStorage`, `DisbandTombstoneStorage`, `KeyPackageBundleStorage`, `MaintenanceStorage` (optional), `ConvergencePassStorage`). | **+M** of new scope that did not exist when the engine was sized **L**. All of it is **deferrable past first interop** (see phase table): disband and maintenance are lifecycle features, not wire prerequisites. Storage abstraction should be designed with the sub-trait split now to avoid a later refactor. | `cgka-engine/src/{disband,maintenance,self_update}.rs`; `traits/storage.rs:70-448` |
| D5 🟡 | **`IngestOutcome` gained two variants and a rejection taxonomy**: `Ignored { InputRejectionCategory }` and `Rejected { ProposalRejectionCategory }`, alongside `Processed`/`Buffered`/`Stale`. `LocalIngestState` is a new enum. Peeler contract wording changed (`StaleReason::NotForThisClient` → `InputRejectionCategory::WrongRecipient`). | Shape change to the engine's outward result model — cheap if adopted now, annoying if retrofitted. Step-3 §2.1's `GroupResult` row should target the new five-variant shape. | `traits/ingest.rs:12-70`; `traits/peeler.rs:100-104` |
| D6 🟡 | **Protocol profile is now threaded through the whole lifecycle.** `EngineBuilder::protocol_profile()` / `legacy_compatibility_profile()`; create/invite gates require the KeyPackage profile to equal the group profile; join/reopen still accept both; `strict_cutover_rejects_legacy_group_addition` exists in the message processor. Current-profile leaves **no longer advertise** the `0xf2f1` extension type — they advertise app-component `0x8009` (`CTCapability::AppComponent`). `ExtensionType::LastResort` was dropped from leaf capabilities. | Confirms step-4 §0: implement Current as primary. Adds a concrete API shape to copy for the builder, and one detail step 4 did not state: for Current profile the *capability advertisement* moves from extension-type space into app-component space. | `capabilities.rs:20-90`; `group_lifecycle.rs:254-345`, `:483-589`; `message_processor/mod.rs` |
| D7 🟢 | **Convergence v1 policy constants unchanged, now *pinned by construction*.** `max_rewind_commits=5`, `witness_quorum_senders_per_epoch=2`, `witness_quorum_epochs=1`, `max_witness_override_depth=1` are byte-identical, promoted to named `V1_*` constants, and a new `ensure_pinned_v1()` **rejects any non-v1 policy** outside a test-only feature flag. New: `V1_APP_MESSAGE_PAST_EPOCH_LIMIT=5`, `V1_SETTLEMENT_QUIESCENCE_MS=1000`, `V1_MAX_CONVERGENCE_PASS_MS=5000`, and `ensure_app_window_matches()` — `DEFAULT_MAX_PAST_EPOCHS` is now *derived from* the convergence app-message window so the two cannot diverge. | Step-2 §3.1's constants survive verbatim. New requirement for us: our policy loader must enforce the same pin and the same window equality, or we will silently diverge. Cheap, but a MUST. | `convergence.rs:12-40`; `canonicalization.rs` (`V1_*`, `ensure_acceptable`); `wire_format.rs:35-42` |
| D8 🟢 | **Branch selection, fork recovery, and the peeler seam are structurally unchanged.** `fork_recovery.rs` diff is 17 lines (a snapshot-counter restore fix + two new storage trait methods). `select_canonical_branch` and the `(priority, committer, SHA-256(commit))` ordering key are untouched. `TransportPeeler` remains the engine/transport boundary. | Step-2 §3 and step-3 §1 hold in full. The `Scramble.Marmot.Peeler` seam decision stands. | `fork_recovery.rs` diff; `canonicalization.rs` re-exports |
| D9 🟢 | **App-component IDs are stable.** `0x8001` profile, `0x8002` blossom-image, `0x8003` admin-policy, **`0x8004` NostrRouting**, `0x8005` retention, `0x8006` agent-text-stream-QUIC, `0x8007` avatar-url, `0x8008` encrypted-media-v1 — all unchanged. Added: **`0x8009` account-identity-proof** (step 4 already has it), `0x800b` encrypted-media-v2, `0x800c` group-lifecycle. `0x8008` (media v1) is **frozen and now rejected** in Current-profile groups. | Step-3 §2.1's routing rewrite target is correct. Only build `0x8003`, `0x8004`, `0x8009`, profile `0x8001`, retention `0x8005` for v1; media/QUIC/lifecycle are out of v1 scope. | `traits/app_components/mod.rs:82-108`; `app_components.rs:1060-1069` |
| D10 🟢 | **Epoch state machine: additive.** New terminal `Disbanded` state and `PendingKind::Disband`; `repair_to_stable()` as the only legal exit from `Unrecoverable`; `restore_pending()` for crash recovery; `set_stable()` now refuses to overwrite `Unrecoverable`/`Disbanded`; a fixed bug where a rolled-back pending left a phantom `committed_from` entry (new `owns_committed_from` flag) that "poisons fork detection for later same-epoch siblings". | Step-2 §4's transition table is still the right base; add two states/one flag. **Copy the `owns_committed_from` fix directly** — it is exactly the class of bug we would otherwise ship and spend a week finding. | `epoch_manager.rs:36-44`, `:107-124`, `:291-311`, `:350-385` |

**Net effect on the plan:** no phase is removed, no prior estimate is
invalidated downward. Add ~**+M** of deferrable scope (D4), **+S** (D1),
promote one dotnet-mls item to blocker (D2), and treat the Wire.Nostr tag-shape
fix as a correctness gate (D3). This is folded into §6's arithmetic as the
"drift delta" line.

**Erratum discipline:** one-line errata have been added to the headers of
`survives-rewrite-diff-2026-07.md`, `convergence-deepdive-2026-07.md`,
`account-identity-proof-v2-2026-08.md`, and
`dark-matter-migration-scoping-2026-07.md`. Those documents were **not**
rewritten — they remain accurate as of `v0.9.4`, with the delta recorded here.

---

## 3. Phased build plan

Ordering follows `survives-rewrite-diff-2026-07.md` §4, adjusted for §2. Each
phase is sized, has an exit criterion you can point CI at, and names its tests.

**Two conventions apply to every phase:**
- **I4 (no flag-day rewrites).** Every phase lands as multiple commits ≤ 8 files.
  `Scramble.Marmot` is a new project, so most of this work is greenfield and
  touches no existing subsystem — the I4 risk concentrates in **P11 only**.
- **I2 (integration coverage).** New test categories must be added to **both**
  `.github/workflows/integration.yml` and `docs/ci-setup.md` in the same PR that
  first uses them. Three new categories are proposed: **`MarmotEngine`** (engine
  unit/behavioural), **`ConformanceVector`** (upstream-mirrored JSON vectors),
  **`DarkMatterInterop`** (live wn-agent interop over docker-compose).

### Testing strategy (applies across phases)

Three tiers, in increasing cost:

1. **Unit** — pure C#, no I/O. Covers the scorer, epoch state machine, codecs,
   proof construction.
2. **Conformance vectors** — mirror upstream's portable, semantic JSON vector
   format (`crates/cgka-conformance-simulator/vectors/*.v1.json`, indexed by
   `manifest.v1.json`; includes convergence-specific vectors). Two sources:
   (a) copy upstream's committed vectors verbatim as fixtures; (b) an
   **Amethyst-style `mdk-vector-gen`** — a small Rust harness pinned to
   `wn-agent-v0.9.10` that emits vectors for cases upstream does not ship
   (notably the **Legacy proof construction, which has no official vector**,
   step-4 §2). Category `ConformanceVector`. This tier is the cheapest
   defence against silent divergence and should lead each phase, not trail it.
3. **Interop** — against the deployed `wn-agent` binary in
   `docker-compose.test.yml`, alongside the existing nostr-relay service.
   Category `DarkMatterInterop`. Requires a compose service addition (P3 exit
   criterion).

### Phase table

| # | Phase | Size | Scope | Exit criteria | Tests |
|---|---|---|---|---|---|
| **P0** | **Storage foundation** | M | Port `MarmotCs.Storage.Abstractions` + `Sqlite`; add DM's record states (`Created/Retryable/PeelDeferred/Processed/Failed/EpochInvalidated/Sent`), `QueuedOutboundIntent`, `LeaveRequest`, validated-tree marker, group `removed`+`join_epoch`, storage transactions, and the **epoch-anchored snapshot API** (survives ~70%, step-3 §2.1). Split the interface along DM's **sub-trait lines now** (D4) even where the implementations are stubs. | Round-trip tests green for every record type; snapshot create/rollback/release/prune under a transaction; sub-trait split reviewed against `traits/storage.rs`. | Unit (`MarmotEngine`) |
| **P1** | **EpochManager** | S–M | Pure in-memory state machine per step-2 §4 + D10's additions (`Disbanded`, `repair_to_stable`, `restore_pending`, `owns_committed_from`). No dependencies beyond types. | Full transition table exercised, including the illegal-transition arms; the `owns_committed_from` rollback case has a named regression test. | Unit |
| **P2** | **Identity + AccountProof (Current `0x8009`)** | S | Step-4 §1 construction: 104-byte component, kind:450 template, NIP-01 canonical id, BIP-340 verify. `IAccountIdentityProofSigner` **async** seam bridging local-key and Amber/NIP-46 (step-4 §3). Producer-side byte-exact response verification. **Legacy `0xf2f1` is NOT built — decided, not pending** (§5 Q2). | The **official spec test vector** (step-4 §1.3) passes byte-for-byte, both verify-the-vector and round-trip-our-own-signature. Validation checklist §1.4 items 1–10 each have a rejecting test. | Unit + `ConformanceVector` |
| **P3** | **Peeler + `Wire.Nostr` port** | S–M | `ITransportPeeler` seam; Nostr implementation wrapping ported `GroupEventEncryption` (exact match, survives), NIP-44/59, and kind 445/444/30443 builders — **`encoding` tag dropped**, strict tag cardinality per D3, `app_components` tag + NIP-40 expiration added. Generic Nostr crypto lands in a **non-Marmot namespace** (cutover rule §7.2). Parallelisable with P1–P2. | A kind-445 event we build passes the reference peeler's exact tag-shape table (D3); exporter-derived ChaCha20-Poly1305 round-trips against a vector. **`wn-agent` service added to `docker-compose.test.yml`** and reachable from CI. | Unit + `ConformanceVector`; first `DarkMatterInterop` scaffolding |
| **P4** | **AppComponents** | **M/L** ⬆ | `0x8001` profile, `0x8003` admin-policy, `0x8004` routing, `0x8005` retention, `0x8009` proof carriage; the app-data-dictionary codec; and the validators the engine calls at every seam (`require_admin`, `admins_of_group`, `transport_group_id_of_group`, admin-leaf coupling, component integrity, `commit_ordering_priority_for_staged`), plus **Current-profile group invariants** (D2/D6). Media (`0x8002`/`0x8008`/`0x800b`), QUIC (`0x8006`), avatar (`0x8007`), lifecycle (`0x800c`) are **out of v1**. | Every validator has a rejecting test; a Current-profile GroupContext we construct passes `validate_current_profile_group_context`'s equivalent checks; rotation-aware `transport_group_id` index resolves many-to-one. | Unit + `ConformanceVector` |
| **P5** | **dotnet-mls generic additions** | M (×N items) | The permission-gated items in §4, in the order given there. **Gated on user permission — start the ask at P0, not here.** | Each item merges into `dotnet-mls` as a generic RFC-9420 / mls-extensions feature with no Marmot constants (scoping §12 boundary rule). | Library's own tests + a `MarmotEngine` consumer test per item |
| **P6** | **Engine v1 (fast path)** ⭐ | **L** | Create/join/send-app/invite/remove/leave + ingest with publish-before-apply, fork recovery, **content-derived dedup** (`SHA-256(mls_bytes)`, *not* the Nostr event id), the full ingest validation chain, RAII guards as `IDisposable`, the new `IngestOutcome` shape (D5), `SelfUpdate` (D1). Convergence **stubbed** — `Settled` when the stored-input set is empty (step-3 §4 confirms the fast path is entered for the common cases). | **⭐ FIRST INTEROP MILESTONE.** Scramble creates a group that `wn-agent` joins and can message, and joins a group `wn-agent` created; invite/remove/leave round-trip both directions; a same-epoch commit race resolves identically on both sides. | Unit + `ConformanceVector` + **`DarkMatterInterop`** |
| **P7** | **AutoCommitter + leave lifecycle** | S–M | SelfRemove proposal + jittered peer auto-commit (RFC 9420 §12.2 committer ≠ leaver), durable `LeaveRequest`, leaving send-gate, self-eviction realisation + removed-copy gates. Sits directly on P6. | A `wn-agent` member leaving a Scramble-hosted group is committed by Scramble and vice versa. | `DarkMatterInterop` |
| **P8** | **Convergence (slow path)** | **L** | Per step-2 §9: policy (with D7's pin + window-equality enforcement), `BranchCandidate` + scorer, `CanonicalizationPipeline`, and `CandidateMaterializer` (the L-sized piece — candidate-path BFS, DoS replay budget, own-commit replay workaround, crash-safe two-level reorg apply). Buildable **in parallel** with P6–P7 once P0 and a materializer skeleton exist — but see §6's single-developer assumption. | Upstream's convergence-tagged vectors pass unmodified; the `tip_digest`/`CommitOrderingKey` byte-identity round-trip against mdk passes (step-2 §11 risk 3). | `ConformanceVector` (primary) + `DarkMatterInterop` |
| **P9** | **Hardening** | M | Session-open hydration, quarantine, stranded-pending-commit crash recovery, deferred-peel retry lifecycle with the flood cap (256) and retry budget (32), queued-intent drain polish, snapshot-fallback peel. | Crash-recovery tests (kill between stage and confirm, between confirm and merge) leave a consistent group; epoch-boundary messages survive via snapshot-fallback peel. | Unit + `DarkMatterInterop` |
| **P10** | **Capabilities / feature registry** | S–M | Minimal static registry is a **P4/P6 prerequisite** (create/invite/join gates); this phase completes profile negotiation, the upgrade flow, and `legacy_compatibility_profile` handling if §5 Q2 says we need it. | Capability-mismatch rejections match mdk's error taxonomy. | Unit + `ConformanceVector` |
| **P11** | **Cutover into Scramble** | M | Replace `marmot-cs` behind `Scramble.Core`'s services; publish-ack → engine `ConfirmPublished`; `GroupEvent` drains → app events (kind-1210 system rows); migrate or re-key existing local groups. **The only phase with real I4/I5 exposure.** | Desktop + Android smoke tests green; existing chats still open; a full-stack E2E passes on both heads. | Existing `Integration`/`FullE2E`/`DeviceSync` categories + `DarkMatterInterop` |
| **P12** | **Deferred DM features** | M | Disband lifecycle (`0x800c`), key-package maintenance + durable transport fanout, media components, QUIC stream policy (D4). Sequenced **after** cutover unless WN requires disband for interop. | Feature-by-feature; no cutover gate. | Per feature |

**Critical path to first interop (P6/⭐):** P0 → P1 → P2 → P4 → P6, with P3 in
parallel and **P5 items (c) and (e) as hard prerequisites**. P8 is *not* on the
path to first interop — that is the single most important scheduling fact in
this plan.

---

## 4. `dotnet-mls` permission-gated proposals, sequenced

All framed as **generic RFC-9420 / mls-extensions** features. No Marmot
constants, no `0xf2..` IDs, no Nostr coupling (scoping §12 boundary rule). The
library is **not to be modified without explicit permission** — this is the ask
list, in the order the build needs them.

> ✅ **PERMISSION GRANTED (user, 2026-08-10) for items 1 and 2 only** —
> **(c) PublicMessage produce/verify** and **(e) the `AppDataUpdate` proposal
> type**. The user accepted the known caveat that these are
> `draft-ietf-mls-extensions` mechanisms rather than ratified RFC 9420, and that
> mdk itself can only reach them through a **fork** of OpenMLS
> (`erskingardner/openmls` @ `59e7d3b`, feature `extensions-draft`) — so a draft
> renumber or reshape is rework in the library.
> **Scope limits that still bind:** items 3–5 ((b) SelfRemove, (d) retained
> past-epochs, (f) staged-commit introspection) are **NOT** covered by this
> grant and must be asked for separately. Both approved items land as
> **generic** MLS features — no Marmot constants — as **separate commits**
> (I4), each with tests, sequenced as P0-adjacent work once the build is green.

| Order | Item | Size | Why this position |
|---|---|---|---|
| **1** | **(c) PublicMessage produce + verify.** Expose PublicMessage framing for **produced** commits and proposals (`Commit()` currently returns PrivateMessage only; propose* return unframed `Proposal`), and add signature/membership verification on proposal consume (`CacheProposal` assumes pre-verified). Includes resolving the 🔴 **membership_tag-on-Proposal** question against RFC 9420 §6.2 before relying on wire-compat. | M | `PURE_PLAINTEXT_WIRE_FORMAT_POLICY` means **everything we emit** is a PublicMessage. Without this there is no wire-valid send at all. Blocks P6. |
| **2** | **(e) `AppDataUpdate` proposal type.** Add the `AppDataUpdate` proposal (`0x0008`) — a closed-enum extension exactly like SelfRemove (`ProposalType.cs` stops at `GroupContextExtensions = 7` and throws on anything else) — plus commit-time app-data dictionary computation. **Safe-export is NO LONGER part of this ask — see the note below.** | M | **Promoted to blocker by D2:** `0x0008` is a *RequiredCapabilities* entry on every Current-profile group, so without it we can neither create nor join one. Blocks P4 and P6. |
| **3** | **(b) SelfRemove proposal + proposal-store mechanics.** The proposal type itself — ⚠ **SelfRemove is `0x000a`, not `0x0008`**; the two draft proposal types are *not* adjacent (`AppDataUpdate = 8`, `AppEphemeral = 9`, `SelfRemove = 0x000a` — OpenMLS `messages/proposals.rs:208-233`). Closed `ProposalType` enum, decode throws on unknown, plus generic store/remove/commit-to-stored-proposals. Include the cleanup hook for the stale-stored-SelfRemove-plus-Remove-for-the-same-leaf case that panics OpenMLS 0.8.1. | M | The whole leave path sits on it. Blocks P7, not P6 — so it can trail (1) and (2). |
| **4** | **(d) Retained past-epoch secrets.** Epoch → `(KeyScheduleEpoch, SecretTree)` window with pruning and epoch-keyed decrypt. Size the window to match the convergence app-message window (D7 ties `max_past_epochs` to `V1_APP_MESSAGE_PAST_EPOCH_LIMIT = 5`). | M–L | Needed for out-of-order delivery under convergence and for witness app-payload decryption. Blocks P8, not P6. |
| **5** | **(f) Staged-commit introspection.** Generic read access to a staged commit's queued proposals, projected `GroupContext`, and export-from-staged. 🟡 **verify against `dotnet-mls` source before proposing** — `Mdk.cs` usage suggests it is absent, but that is inference, not a read. | M | Needed by the ingest validation chain and by `publish.rs`'s ordering-stamp capture. Workaround exists (`Export()`/`Import()` probe), so this is an efficiency/clarity ask, not a blocker. |
| **6** | **Per-leaf accessor check (step-4 §4 🔴).** *Not a proposal — a read-only check.* Confirm `dotnet-mls` exposes `LeafNode.Extensions` + `SignatureKey` for (i) every ratchet-tree leaf post-Welcome and (ii) leaves inside a staged commit's Add/Update proposals. Expected present. | XS | **Do this first, before any of the above** — it costs an hour, needs no permission, and any gap turns into a trivially generic read-only accessor ask that would otherwise surface mid-P2. |

### 4a. Item (d) SETTLED — 2026-09-03, and it is a P6 bug not a P8 one

**The question as posed had a false premise.** It asked whether decrypting from a
restored snapshot should persist the advanced SecretTree state back, or re-derive
per message. OpenMLS does neither. `DecryptionRatchet` (fork `59e7d3b`,
`openmls/src/tree/sender_ratchet.rs`) holds
`past_secrets: VecDeque<Option<RatchetKeyMaterial>>`, truncated to
`out_of_order_tolerance`, with each entry `take`n when used — so a used
generation key is consumed exactly once and older ones stay available. It
**retains**; it never rewinds. Snapshot round-tripping is therefore not the
mechanism and the concern does not arise in the form it was written.

**And the scope was wrong.** This was filed as "blocks P8, not P6". The
across-epoch half does block P8. The **within-epoch** half is broken right now:

```
System.InvalidOperationException :
  Generation 0 has already been consumed. Current generation is 2.
```

That is two application messages from one sender arriving in the other order —
which a Nostr relay is under no obligation to prevent. The earlier message is
**permanently undecryptable**, not delayed. `SecretTree.GetKeyAndNonceForGeneration`
fast-forwards, `Array.Clear`s each intermediate secret, and throws on anything
below the head.

Three things in `dotnet-mls` make this worse than a missing feature:

- **`Message/SenderRatchet.cs` is dead code.** It implements exactly the
  seen-generation window that would tolerate reordering, and **nothing ever
  constructs it**.
- **`MlsGroupConfig.OutOfOrderTolerance` and `MaxForwardDistance` are inert.**
  They are serialised by `Export`/`Import` and read by nothing.
- So the library presents a configurable out-of-order tolerance that has no
  effect, and the real behaviour is a tolerance of zero.

**Upstream hit this after our pin and their fix is the mechanism we lack.**
`DEFAULT_OUT_OF_ORDER_TOLERANCE = 100` and `DEFAULT_MAXIMUM_FORWARD_DISTANCE =
1000` are **new between `wn-agent-v0.9.10` and `v0.9.17`**, with the reason
stated in `cgka-engine/src/wire_format.rs`: *"Marmot transports do not provide
total ordering, so the OpenMLS default of 5 is too small for ordinary relay
reordering and offline catch-up floods."* Their comment for the knob is explicit
about the semantics — *"Number of prior within-epoch application-message
generations **retained** for out-of-order delivery."*

**The ask this produces**, and it is a new permission-gated `dotnet-mls` item
rather than the one originally written:

1. **Within-epoch retention (P6 severity).** Give `SecretTree` a bounded
   per-leaf ring of past generation keys, single-use, sized by
   `OutOfOrderTolerance`; wire the config through; either use or delete
   `SenderRatchet`. Generic RFC 9420 work, no Marmot constants.
2. **Across-epoch retention (P8 severity).** The `max_past_epochs` window, which
   is the same retention idea one level up. Upstream pins it to
   `V1_APP_MESSAGE_PAST_EPOCH_LIMIT` and `ensure_app_window_matches` refuses any
   engine whose two windows disagree — so whatever we build must enforce that
   equality too, or we silently diverge on which messages are deliverable.

**Values to match: `out_of_order_tolerance = 100`, `maximum_forward_distance =
1000`, `max_past_epochs = 5`.** Our current defaults of 5 and 1000 in
`MlsGroupConfig` are the stock OpenMLS ones and are not what Marmot runs.

### Upstream drift check — 2026-09-03

Pinned at `wn-agent-v0.9.10`; upstream is at **`v0.9.17`**. What matters for us:

- **Nothing we have built has moved.** `git diff v0.9.10..v0.9.17` over
  `traits/src/app_components.rs`, `traits/src/capabilities.rs`,
  `traits/src/agent_text_stream.rs`, `cgka-engine/src/capabilities.rs` and
  `cgka-engine/src/key_package.rs` is **empty**. KeyPackage shape, component
  ids, the role capabilities and the Current-profile floor are all unchanged, so
  every interop conclusion from §3g–§3r still holds at 0.9.17.
- **The sender-ratchet defaults are new**, as above. This is the one change that
  demands work.
- **Convergence observability is new**: `DeferredMessage`,
  `DeferredMessageReason` (`NonSelectedEligibleBranch`, `MissingCandidateParent`)
  and `replay_probe_count` in `traits`. Relevant to P8's canonicalization
  contract, nothing to do before it.
- `traits/src/transport_adapter.rs` (+789) and `traits/src/message.rs` (+685)
  grew substantially. Not yet reviewed; neither is on the P8 path.

### Safe-export: RESOLVED, and dropped from v1 🟢 (2026-08-10)

The 🔴 on `safe_export_secret(component_id)` is closed. Two findings, the second
of which removes the item:

1. **The guess was wrong.** It is *not* the plain MLS exporter under a
   prescribed label/context. `MlsGroup::safe_export_secret` (OpenMLS fork
   `erskingardner/openmls` @ `59e7d3b`, feature `extensions-draft`,
   `group/mls_group/exporting.rs:63-86`) derives from a stateful,
   forward-secure **`application_export_tree`** that is **mutated and
   persisted on every export** (`storage.write_application_export_tree(...)`),
   with a separate `safe_export_secret_from_pending` arm for staged commits.
   Had we needed it, this would have been a genuine and non-trivial
   `dotnet-mls` gap — not a label.
2. **We do not need it for v1.** Nothing in the engine's hot paths uses it:
   `grep safe_export` over `message_processor/*`, `group_lifecycle.rs`,
   `publish.rs`, `app_components.rs`, and `canonicalization.rs` at
   `wn-agent-v0.9.10` returns **nothing**. Every caller is an app-facing
   pass-through (`engine.rs:2276`, `cgka-session`, `marmot-account`), and the
   exporter contexts around them are `ENCRYPTED_MEDIA_EXPORTER_CONTEXT` and
   `AGENT_TEXT_STREAM_EXPORTER_CONTEXT` (`engine.rs:2528-2596`) — i.e. the
   encrypted-media (`0x8008`/`0x800b`) and agent-text-stream-QUIC (`0x8006`)
   components, **both deferred to P12**.

**Consequence:** ask (e) reduces to the `AppDataUpdate` proposal type alone, and
**every remaining item in this list is now verified against source** — there is
nothing left to decide on incomplete information. If media or QUIC components
are ever brought into scope, safe-export returns as a fresh, separately-sized
`dotnet-mls` ask (expect **M–L**, given the persisted export tree).

⚠ Related supply-chain note for risk 1: mdk does not depend on released
OpenMLS. It exact-pins a **fork** — `erskingardner/openmls` @
`59e7d3b27a7e95237879dd5478de1fd90eff7ada` with the `extensions-draft`
feature — across all five OpenMLS crates, with a comment that this is "the
draft-10 last-resort KeyPackage fix" pending upstream release. Anything we
port that depends on `extensions-draft` behaviour is tracking a fork of a
draft, not a standard.

**Recommended action now:** run item 6 immediately; raise items 1 and 2 with the
user as a single permission request during P0, since both block the critical
path and both need lead time. **Both are now fully verified** — no part of the
ask rests on an unchecked assumption.

### Status update — 2026-08-25

**Items 1 and 2 are done and released.** Reviewed, merged fast-forward into
`dotnet-mls` `main`, tagged **`v0.1.0-beta.8`**. 384 of the library's tests
pass, RFC 9420 vectors included. Reference the tag, never the branch. The
merge also carried a security fix that was not part of the original ask:
`CacheProposal` previously cached unauthenticated proposals, which a later
Commit could then reference by hash.

**Items 3–5 were re-examined before asking for any of them.** The list shrinks
to one:

| Item | Verdict | Basis |
|---|---|---|
| **(b) SelfRemove** | **Real. Ask for it, at P7.** | `ProposalType` runs `Add=1 … GroupContextExtensions=7, AppDataUpdate=8`. SelfRemove is `0x000a` and is simply not expressible — there is no workaround for a proposal type that cannot be encoded. Blocks P7, not P6. |
| **(d) Retained past-epochs** | ~~Probably avoidable. Do not ask yet.~~ **SETTLED 2026-09-03 — it is real, it is bigger than stated, and it is not a P8 item. See §4a.** | The original note asked whether to persist ratchet advances back or re-derive per message. **Neither: OpenMLS retains.** And the within-epoch half of this is a live P6 bug, not a P8 blocker. |
| **(f) Staged-commit introspection** | **Not a blocker — as this document already said.** | The `Export()`/`Import()` probe works: snapshot, `ProcessCommit`, inspect the resulting dictionary, re-`Import` if Marmot-invalid. Confirmed in source: dotnet-mls has no *inbound* staging at all (`ProcessCommit` applies directly), and `PendingCommitState` is `internal`. A throw inside `ProcessCommit` is already safe — it assigns only at the end — so rollback is needed only for commits that are MLS-valid but Marmot-invalid. |

**The `Export()` round-trip is lossy in exactly two places**, and both were
checked rather than assumed: it omits `_resumptionPsks` and `_proposalCache`.

- `_proposalCache` — recoverable by re-caching, and `ProcessCommit` clears it
  anyway. Not a concern.
- `_resumptionPsks` — **irrelevant to Marmot v1.** The spec repo contains no
  occurrence of "resumption" at all. PSK appears in two documents only:
  `app-components/group-lifecycle-v1.md` (`0x800c`, deferred to P12) and
  `features/multi-device.md`, which is marked *"Status: branch draft"* and
  states its byte-level definitions "MUST NOT be implemented for interop yet".
  Even that draft uses an **External** PSK — `MLS-Exporter("marmot",
  join_psk_id, KDF.Nh)`, supplied out of band — not a resumption PSK, so the
  key-schedule resumption secret is not involved either way. In mdk,
  "resumption" appears only where it *deletes* OpenMLS-owned rows during
  re-join; nothing produces or consumes one.

**So the standing ask list is one item: (b) SelfRemove, when P7 approaches.**
Frame it generically exactly as `AppDataUpdate` was — closed enum, decode
throws on unknown — plus the store/remove/commit-to-stored-proposals mechanics
and the cleanup hook for the stale-stored-SelfRemove-plus-Remove-for-one-leaf
case that panics OpenMLS 0.8.1.

**The lesson worth carrying:** two of the three remaining asks dissolved on a
source read costing minutes. Re-check whether an ask is still real before
spending a permission request on it — a granted permission for something
unnecessary is worse than not asking, because it invites the change to be made.

---

## 5. Questions for Whitenoise — send early

These gate real decisions, and two of them can *remove* work. Send with the date
answer, not after it.

1. **Which mdk tag is your deployed fleet running?** (We have re-pinned to
   `wn-agent-v0.9.10`; HEAD has moved 84 commits past it.) — *Decides what we
   test interop against, and whether our pin is already behind yours.*
2. ~~**Do any production groups still require the legacy `0xf2f1`
   account-identity proof?**~~ **DECIDED (user, 2026-08-10): Legacy is out of
   scope.** We assume WN drops `0xf2f1` and always will, until they tell us
   otherwise. Scramble implements **only** the Current (`0x8009`) construction;
   the second proof construction, its TLV codec, and its generated vectors are
   **not built**. Still worth *informing* WN of this assumption so a
   contradiction surfaces early — but it is no longer a question that gates the
   plan. **Reversal cost if WN contradicts us: +S** (step-4 §2 has the full
   construction pinned; only the vectors would need generating).
3. **When does deployed Whitenoise flip to Dark Matter, and is there a
   dual-running window?** — *This is our hard deadline. It also decides whether
   Scramble needs any 0.7/0.8-era compatibility during transition.*
4. **Do you intend to stabilise the wire before that flip — i.e. is there a
   freeze point or a "wire-stable" tag we can build against?** — *At 7–8
   commits/day, this is the single biggest driver of our pessimistic arm (§6).
   A stable tag we can target moves our expected date earlier and narrows the
   band substantially.*
5. **Is `Disband` (component `0x800c`) required for interop, or optional?** —
   *Decides whether P12 stays deferred or moves before cutover.*

---

## 6. Date with confidence band

### Assumptions (state these to WN alongside the date)

- **One primary developer** with heavy AI assistance, ~**4 productive days per
  week** (≈0.8 FTE). If a second developer joins, P8 (Convergence) parallelises
  with P6–P7 and the expected date moves in by roughly 6 weeks; nothing else
  parallelises cleanly.
- **Review latency ~1 day per PR**, absorbed into the sizes.
- **`dotnet-mls` permission turnaround is not on the clock** — items 1 and 2 in
  §4 are assumed approved during P0. Each week of delay there is a week on the
  critical path.
- **The reference pin holds at `wn-agent-v0.9.10`** for the duration of the
  build, with **one re-pin + drift-diff cycle budgeted** before cutover. This is
  the assumption most likely to break (§ risk 1).
- Sizes: S ≈ 0.5–1 work-week, M ≈ 1.5–3, L ≈ 4–7.
- Legacy proof construction is **not** built — decided 2026-08-10, not an open
  question (§5 Q2). If WN later contradicts this, add **+S** to both milestones.

### Arithmetic (work-weeks)

| Phase | Opt | Exp | Pess |
|---|---|---|---|
| P0 Storage foundation | 1.0 | 1.5 | 2.5 |
| P1 EpochManager | 0.5 | 1.0 | 1.5 |
| P2 AccountProof (Current) | 0.5 | 1.0 | 1.5 |
| P3 Peeler + Wire.Nostr | 0.5 | 1.0 | 2.0 |
| P4 AppComponents | 1.5 | 2.5 | 4.0 |
| P5 dotnet-mls items (c)(e)(b)(f) | 1.5 | 3.0 | 5.0 |
| P6 Engine v1 fast path | 4.0 | 6.0 | 9.0 |
| Drift delta (D1 SelfUpdate, D3, D5 reshape) | 0.5 | 1.0 | 2.0 |
| Vector harness + `mdk-vector-gen` + CI categories | 0.5 | 1.0 | 2.0 |
| **Subtotal → ⭐ first interop (P6)** | **10.5** | **18.0** | **29.5** |
| P7 AutoCommitter + leave | 0.5 | 1.0 | 1.5 |
| P8 Convergence (incl. dotnet-mls (d)) | 4.0 | 6.0 | 9.0 |
| P9 Hardening | 1.0 | 2.0 | 3.5 |
| P10 Capabilities / registry completion | 0.5 | 1.0 | 2.0 |
| P11 Cutover into Scramble.Core + both UI heads | 1.5 | 2.5 | 4.0 |
| Second re-pin + drift-diff cycle | 0.5 | 1.0 | 2.5 |
| **Subtotal → production cutover** | **18.5** | **31.5** | **52.0** |

At 0.8 FTE, calendar weeks = work-weeks ÷ 0.8, from **2026-08-09**:

| Milestone | Optimistic | **Expected** | Pessimistic |
|---|---|---|---|
| ⭐ **Wire interop against `wn-agent`** (create/join/send/invite/remove/leave, fast path) | ~13 weeks → **mid-Nov 2026** | ~23 weeks → **late Dec 2026** | ~37 weeks → **mid-Feb 2027** (allowing for holidays) |
| **Production cutover** (convergence, hardening, both UI heads on the new engine) | ~23 weeks → **mid-Mar 2027** | ~39 weeks → **mid-May 2027** | ~65 weeks → **mid-Sep 2027** |

**What to actually tell WN:** *"Wire interop with your agent around the turn of
the year — realistically December, mid-November if the dotnet-mls work goes
cleanly. Production cutover in Q2 2027. The band is wide mostly because mdk is
moving at ~8 commits a day; a wire-stable tag we can target would narrow it
materially."*

### Top three risks driving the band

1. **Upstream velocity (dominant).** 151 commits in 19 days between our old and
   new pins, 84 more in the 11 days since; the re-pin itself surfaced a
   hard-broken wire format (the proof) plus three new engine subsystems. Every
   month we build, the target moves. **Mitigations:** build against a frozen
   pin; budget one re-pin cycle (already in the arithmetic); lead every phase
   with conformance vectors so drift shows up as a red test rather than a field
   failure; ask §5 Q4. **If WN cannot offer a stability point, the pessimistic
   arm is the honest one** — and the FFI fallback (scoping §6) deserves a
   re-look, because this risk is precisely the parity-chase the original
   analysis warned about.
2. **`dotnet-mls` gaps (c) and (e) — blocking and permission-gated (but no
   longer unverified).** Nothing wire-valid can be sent without (c); nothing
   Current-profile can be created or joined without (e). Both are now confirmed
   against source, and the one 🔴 they carried — the safe-export construction —
   is **resolved and dropped from v1** (§4). The residual risk is therefore
   schedule, not scope: every week the permission decision waits is a week on
   the critical path, because both block P4/P6 and neither blocks P0.
   **Mitigation:** run §4 item 6 today and raise items 1–2 at the start of P0.
3. **Byte-exactness across two MLS implementations.** `tip_digest` /
   `CommitOrderingKey` determinism between `dotnet-mls`'s TLS codec and
   OpenMLS's (step-2 §11 risk 3) fails **silently** — as "always picks the wrong
   branch", not as a crash — and the same class of failure applies to the
   104-byte proof and the strict kind-445 tag shape (D3). **Mitigation:** the
   round-trip byte-identity test is an early P3/P8 task, not a late one, and
   every codec gets a vector before it gets a caller.

---

## 7. Cutover rules (binding — restated from `CLAUDE.md`)

These are decided, not open. They cost nothing if followed from P0 and are
expensive to retrofit.

1. **No `Scramble.Marmot` types in `Scramble.Presentation`.** ViewModels bind
   only to protocol-neutral models (`Chat`, `Message`, `Member`, `Role`,
   `ChatCapabilities`) surfaced by `Scramble.Core`, with a `protocol`
   discriminator on the chat record. Engine types (`SendIntent`,
   `IngestOutcome`, `GroupEvent`, epoch/commit state) **stop at the service
   layer**. Enforced in P11's review.
2. **Generic Nostr crypto is not Marmot-namespaced.** `Nip44Encryption`,
   `GiftWrap`, and other generic Nostr primitives go in a namespace with no
   Marmot semantics (e.g. `…Nostr.Crypto`), so a future non-Marmot provider can
   reuse them. Decided at P3, one naming decision, no extra code.
3. **Do not build** `IConversationProvider`, Concord, or NIP-29 code during this
   migration. Interface extraction happens after a second concrete provider
   exists. Post-cutover checkpoint per `protocol-agnostic-report-2026-08.md` §6.4.
4. **Do not modify `lib/marmot-cs`** (read-only reference until cutover) and do
   **not** modify `lib/dotnet-mls` without explicit permission (§4).
5. **I5 pivot freeze applies to P11.** While the cutover lands, the non-cutover
   UI head is bugfix-only until the new engine has an equivalent smoke test
   green in CI plus one week of stabilisation.

---

## 8. What this plan does not cover

- **The `feat/marmot-batch1-protocol-v3` branch.** START-HERE's recommendation
  stands: leave it as historical, start `Scramble.Marmot` fresh, port the
  surviving codecs deliberately (P3). Not re-litigated here.
- **Media, QUIC agent streams, avatar-url, and disband** — deferred to P12
  pending §5 Q5.
- **The FFI fallback (scoping §6).** Still the fallback, not the plan — but
  risk 1 is the exact condition under which it wins. Worth a genuine re-read if
  WN's answer to §5 Q4 is "no stability point".
- **Per-commit work breakdown.** Phases are the planning unit; commits are
  bounded by I4 at implementation time.
