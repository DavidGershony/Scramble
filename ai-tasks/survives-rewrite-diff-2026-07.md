# Survives/rewrite diff — `marmot-cs` orchestration vs `cgka-engine` v0.9.4 (step 3)

**Date:** 2026-08-09. **Status: DONE.** This is the line-level confirmation of the
survives/rewrite split that scoping doc §8b flagged as "needs a line-by-line
confirm". It supersedes the 🟡 "~30% of `Mdk.cs` scaffolding reusable" estimate.

**Confidence key:** 🟢 verified against source this session · 🟡 informed
inference · 🔴 needs more work before relying on it.

> **⚠ ERRATUM (2026-08-09, step 5) — this doc is accurate as of `v0.9.4`; the
> reference is now pinned to `wn-agent-v0.9.10`.** The step-5 drift-diff
> (`scramble-marmot-phased-plan-2026-08.md` §2) confirms the structural
> analysis below still holds, with four corrections: **(D1)** §2.1's
> `StageSelfUpdateAsync` row is **wrong** — `SendIntent::SelfUpdate` exists
> again at HEAD (new `self_update.rs`), so it is not a DROP; **(D2)** §5's
> item (e) `AppDataUpdate` is a **hard blocker**, not just the biggest open
> question — proposal type `0x0008` is a RequiredCapabilities entry on every
> Current-profile group, so without it we can neither create nor join one;
> **(D4)** three new engine subsystems exist (`disband.rs`, `maintenance.rs`,
> `self_update.rs`) and the storage trait grew 34 → 79 methods behind new
> sub-traits; **(D5)** `IngestOutcome` gained `Ignored`/`Rejected` variants and
> a rejection taxonomy, so §2.1's `GroupResult` row should target the
> five-variant shape. Also: the kind-445 tag shape is now **strictly validated**
> upstream (exactly one `h`, at most one `expiration`, no other tags), which
> turns §2.2's "drop the `encoding` tag" note into a correctness gate. Not
> rewritten — the delta lives in the plan doc.

## How to use this doc (read this first if you are a fresh session)

- **Context:** Scramble is migrating from the `marmot-cs` engine (0.7-era Marmot)
  to Dark Matter (Rust `mdk` v0.9.4, crate `crates/cgka-engine`), by building a
  new standalone `Scramble.Marmot` project. See
  `ai-tasks/00-START-HERE-dark-matter.md` (orientation + constraints) and
  `ai-tasks/dark-matter-migration-scoping-2026-07.md` (the plan; §10 module
  layout, §12 dotnet-mls capability audit).
- **Constraints that bind everything here:** do NOT modify `lib/marmot-cs`
  (read-only reference); do NOT modify `lib/dotnet-mls` without explicit user
  permission; no Marmot semantics may leak into `dotnet-mls` (it stays generic
  RFC-9420); `Scramble.Marmot` is self-contained (ports codecs in, no project
  ref to marmot-cs).
- **Sources compared (exact versions):**
  - `lib/marmot-cs` @ submodule `a55e527` — `src/MarmotCs.Core/Mdk.cs` (1,822
    lines), `EpochSnapshotManager.cs`, `WelcomeValidation.cs`, `MdkBuilder.cs`,
    `MdkConfig.cs`, `IMdkCallback.cs`, `Results/GroupResult.cs`,
    `Errors/MdkException.cs`, `src/MarmotCs.Protocol/Mip03/CommitRaceResolver.cs`.
    Total orchestration ≈ 2,314 lines. All read in full 🟢.
  - `mdk` tag `v0.9.4`, `crates/cgka-engine/src/`: `engine.rs` (1,873),
    `message_processor/{mod,ingest,send,store}.rs` (973/1,969/821/436),
    `group_lifecycle.rs` (1,006), `publish.rs` (358), `wire_format.rs`,
    `auto_committer.rs`, `snapshot_guard.rs`, `pending_commit_guard.rs`, plus
    headers of `key_package.rs`, `identity.rs`, `update_group_data.rs`,
    `group_state_changes.rs`. All read in full this session except the four
    "headers of" files 🟢/🟡. (`epoch_manager.rs`, `fork_recovery.rs`,
    `convergence*`, `openmls_projection.rs` were read by the step-2 deep-dive —
    `ai-tasks/convergence-deepdive-2026-07.md` — and are cross-referenced, not
    re-read.)
- **What this doc gives you:** (1) a verdict table per marmot-cs file/function,
  (2) the implied `Scramble.Marmot.Engine` module list, (3) a build order,
  (4) new permission-gated `dotnet-mls` questions, (5) a size estimate for the
  engine-orchestration piece. Next steps after this doc: step 4
  (account-identity-proof v2) then step 5 (phased plan + date) per START-HERE.

---

## 1. Headline

- **`Mdk.cs` → REWRITE, ~15–20% line-shape reusable** (revised **down** from the
  🟡 ~30%). What survives is thin passthroughs and two good bones: the
  storage-snapshot primitive and the stage→publish→merge pending-commit pattern.
  The control flow, validation chain, dedup model, and event model are all
  different in Dark Matter. 🟢
- **`CommitRaceResolver` → REWRITE, 0% reusable — confirmed.** DM's same-epoch
  ordering key is `(priority, committer, SHA-256(commit bytes))` with **no
  timestamps and no relay ids** (deep-dive §3.6; `fork_recovery.rs`); our
  resolver is exactly the `created_at`/lex-event-id rule DM forbids
  (`CommitRaceResolver.cs:35-53`). 🟢
- **Two genuine pleasant surprises (SURVIVES):**
  1. `IMdkStorageProvider`'s **named group-snapshot API**
     (`CreateSnapshotAsync`/`RollbackToSnapshotAsync`/`ReleaseSnapshotAsync`/
     `PruneSnapshotsAsync`, wrapped by `EpochSnapshotManager.cs:26-47`) is the
     **same primitive** `cgka-engine` builds fork recovery and probe/rollback
     guards on (`storage.create_group_snapshot` / `rollback_group_to_snapshot` /
     `release_group_snapshot` — `snapshot_guard.rs:43-69`,
     `fork_recovery` per deep-dive §5). The C# storage abstraction ports almost
     directly; only naming/pruning policy changes (epoch-anchored names,
     rewind-horizon pruning instead of max-count). 🟢
  2. The **staged-commit flow** marmot-cs added for MIP-03
     (`StageAddMembersAsync` … `MergeStagedCommitAsync`, `Mdk.cs:1151-1514`) is
     a real, working precursor of publish-before-apply: commit staged unmerged,
     merged only after relay confirm. DM generalizes it (state machine, ordering
     stamp, buffered replay) but the dotnet-mls interaction pattern
     (`Commit()` → no `MergePendingCommit()` → `MergePendingCommit()` /
     `ClearPendingCommit()` on confirm/fail) carries over. 🟢
- **The engine is transport-agnostic in DM — a new seam we must add.** All Nostr
  wrapping/unwrapping lives behind a `TransportPeeler` trait
  (`engine.rs:95`, calls at `send.rs:167-177`, `ingest.rs:215`); the engine
  never sees kind-445. Our `GroupEventEncryption` (exact match, survives)
  becomes the core of the **peeler implementation**, not engine code. 🟢
- **Everything outbound is PublicMessage.** DM pins
  `PURE_PLAINTEXT_WIRE_FORMAT_POLICY` (`wire_format.rs:33`, rationale
  `:1-25`: the kind-445 ChaCha wrap is the confidentiality layer). This makes
  scoping §12(c) (dotnet-mls PublicMessage **produce** path missing) a
  **critical-path** gap, not a nice-to-have. 🟢

---

## 2. File/function-level verdict table

Verdicts: **SURVIVES** (port nearly as-is) · **MODIFY** (same shape, quantified
delta) · **REWRITE** (different model) · **NEW** (no marmot-cs analog) ·
**DROP** (no DM analog). Evidence is `file:line` on both sides.

### 2.1 `MarmotCs.Core` — the orchestrator

| marmot-cs (at `a55e527`) | Responsibility | cgka-engine equivalent (v0.9.4) | Verdict | ~reuse | Evidence / notes |
|---|---|---|---|---|---|
| `Mdk.cs:87-177` `CreateGroupAsync` | create group, 0xF2EE ext, immediate Stable, return KeyPackage | `group_lifecycle.rs:57-409` `do_create_group` | **REWRITE** | ~10% | DM: capability negotiation + mandatory engine-owned components (`:61-157`), admin-set validation + admin-leaf coupling (`:169-247`), app-data dictionary GC ext (`:188-202`), `PURE_PLAINTEXT` + `max_past_epochs` config (`:204-213`), staged founding commit + **PendingPublish even for create** (`:358-394`), welcome wrap via peeler (`:306-342`). Ours: none of that; random `nostrGroupId` + 0xF2EE (`Mdk.cs:102-135` — abandoned format). 🟢 |
| `Mdk.cs:237-328` `AddMembersAsync` (obsolete, auto-merge) | add members, merge immediately | none — DM forbids apply-before-publish | **DROP** | 0% | Already `[Obsolete]` in marmot-cs; DM never merges before confirm (`publish.rs:1-28`). 🟢 |
| `Mdk.cs:1159-1232` `StageAddMembersAsync` | stage add commit, no merge | `send.rs:72-299` `do_send_invite` | **REWRITE** (pattern survives) | ~25% of shape | Shared skeleton: parse KPs → `Commit()` staged → return bytes + welcome. DM adds: Stable-state gate (`send.rs:87-97`), admin gate (`:98`), per-KP capability validation (`:107-124`), fork-recovery snapshot + RAII guards (`:130-144`), ordering-key capture (`:267-279`), projected member record (`:228-231`), `begin_pending` state transition (`:245-253`), buffered `GroupStateChanged` (`:280-291`). 🟢 |
| `Mdk.cs:1238-1303` `StageRemoveMembersAsync` | stage remove commit | `send.rs:301-565` `do_send_remove_members` | **REWRITE** (pattern survives) | ~20% | DM adds admin-depletion guard (`send.rs:372-383`), admin-leaf coupling with coupled AppDataUpdate dropping de-leafed admins (`:404-464`), self-remove redirect (`:318-325`). 🟢 |
| `Mdk.cs:1309-1364` `StageSelfUpdateAsync` | rotate own leaf | no direct analog; leave = SelfRemove proposal + peer auto-commit (`send.rs:567-679`, `auto_committer.rs`) | **DROP/REWRITE** | 0% | DM has no user-facing self-update intent in `SendIntent` (`mod.rs:964-973`); own-leaf rotation happens via commits' path updates. Leaving is a **proposal**, committed by a *remaining* member (RFC 9420 §12.2 committer≠leaver, `auto_committer.rs:44-49`). 🟢 |
| `Mdk.cs:1378-1454` `StageGroupDataUpdateAsync` | replace 0xF2EE via GroupContextExtensions proposal | `update_group_data.rs` (AppDataUpdate proposal per component) | **REWRITE** | ~10% | Different proposal type entirely: DM uses OpenMLS `Proposal::AppDataUpdate` + `ComponentData` (`update_group_data.rs:18-23`), one component at a time, admin-gated. 0xF2EE replace-wholesale is the abandoned model. 🟢 |
| `Mdk.cs:1466-1514` `MergeStagedCommitAsync` | merge after relay OK | `publish.rs:42-257` `do_confirm_published` | **MODIFY** (concept) / REWRITE (detail) | ~20% | Same trigger and same dotnet-mls call (`MergePendingCommit`). DM wraps it in one durable transaction with: capability cache from staged commit, **ordering-stamp capture while staged commit still attached** (`publish.rs:112-121` — "the only durable source", subtle, must copy), record mirror, `Processed` mark; then state-machine `confirm_publish`, pending→incumbent recovery promotion, `replay_buffered_messages()` (`:255`). Retry-safety design (`:86-96`) is worth copying verbatim. 🟢 |
| `Mdk.cs:1752-1783` `MergePendingCommit`/`ClearPendingCommit` | low-level merge/clear | `publish.rs:259-333` `do_publish_failed` (clear side) | **MODIFY** | ~30% | Clear+rederive-record+rollback state machine+replay. Pending-HPKE-key promotion (`Mdk.cs:1485-1491`) disappears (keys live in MLS storage). 🟢 |
| `Mdk.cs:585-813` `ProcessMessageAsync` | parse → decrypt app msg or apply commit; dedup by **Nostr event id**; race-resolve on failure | `mod.rs:101-148` `do_ingest` + `ingest.rs:85-1133` `ingest_group_message` | **REWRITE** | ~5% | The heart of the diff. DM: durable+cached dedup on transport id (`mod.rs:108-130`) then **rebind to content-derived id `SHA-256(mls_bytes)`** (`ingest.rs:416-440`, `mod.rs:947-949` — dedup id MUST NOT be the Nostr event id, the exact opposite of `Mdk.cs:605` / `SaveProcessedMessageAsync(eventId…)`); epoch-state ingest gate w/ buffering (`ingest.rs:198-209`); peel + **snapshot-fallback peel** (`:254-330`, `:1753-1813`); pre-membership terminal classification (`:477-505`); convergence entry gate (`:507-550`); commit-apply **validation chain** — admin (`:775`), admin-leaf coupling (`:787`), app-component integrity (`:801`), per-leaf account-identity proofs (`:821`) — then transactional merge (`:873-881`), roster/admin/profile/retention **diff → `GroupStateChanged` events** (`:989-1084`); WrongEpoch → fork seam (below). 🟢 |
| `Mdk.cs:727-777` in-`ProcessMessageAsync` race retry + `Mdk.cs:1553-1622` `ProcessIncomingCommitAsync` | commit race via created_at/lex-id | `ingest.rs:585-675` WrongEpoch seam + `fork_recovery.rs` (deep-dive §5) | **REWRITE** | 0% | DM: only if *we* committed from that epoch (`ingest.rs:593`); probe the candidate against the pre-commit snapshot to recover `(priority, committer)` (`:1657-1722`), compare `CommitOrderingKey`, storage-level rollback + replay on candidate win, `ForkRecovered`/`GroupStateInvalidated` events (`:953-977`); fail-closed `ForkedEpoch` when no snapshot (`:1624-1655`). Relay `created_at` never consulted. 🟢 |
| `Mdk.cs:903-971` `PreviewWelcomeAsync` | decrypt welcome for preview, store pending | none — DM ingests welcome → joins immediately (`ingest.rs:52-83`) | **DROP from engine; keep as app-layer feature** | — | Preview-before-accept is Scramble UX, not protocol. DM's `join_welcome` consumes KeyPackage init key material **exactly once** (`group_lifecycle.rs:519-524` OpenMLS contract), so "process to preview, process again to accept" does not port. Scramble.Marmot: preview = peel + parse Welcome metadata **without** processing, or accept-on-view. 🟡 design decision to make. |
| `Mdk.cs:985-1048` `AcceptWelcomeAsync` | join group from stored welcome | `group_lifecycle.rs:423-730` `do_join_welcome` | **REWRITE** | ~15% | Shared skeleton: parse → ProcessWelcome → validate → persist → Stable. DM adds: envelope recipient check (`:446-461`), peeler unwrap (`:465-480`), two-step staged welcome + **stale-state clear for re-join** (`:494-548`, mdk#557), per-leaf credential + account-proof validation (`:555-559`), capability self-check (`:561-592`), **welcome-sender-must-be-admin** (`:594-598`), admin-leaf coupling (`:600-610`), retention event, quarantine clear, buffered replay (`:728`). Our 0xF2EE-required check (`WelcomeValidation.cs`, `Mdk.cs:1019`) is superseded by that chain. 🟢 |
| `Mdk.cs:854-887` `CreateKeyPackage` / `ParseKeyPackageEvent` | KP create + kind-30443 build/parse | `key_package.rs` + transport layer | **MODIFY** | ~50% | Builder/parser live in `MarmotCs.Protocol.Mip00` (ports, with fixes: drop `encoding` tag — still emitted at `KeyPackageEventBuilder.cs:95` — add `app_components` tag, add `0xf2f1` proof leaf ext). DM validates lifetime + proof on parse (`key_package.rs:30-60`). ⚠ marmot-cs discards `initPriv`/`hpkePriv` (`Mdk.cs:871` comment "should be persisted") — Scramble.Marmot must persist KP private material in MLS storage. 🟢 |
| `Mdk.cs:491-516` `GetExporterSecret` (both overloads) | MLS-Exporter secrets | `group_lifecycle.rs:39-53` consts + `engine.rs:1700-1812` `group_context()` | **SURVIVES w/ mods** | ~70% | Label/context/length **exact match** both sides: `"marmot"`/`"group-event"`/32 (`group_lifecycle.rs:41-42` vs `GroupEventEncryption.cs:26-32`). DM additions: staged-commit-aware epoch selection (`engine.rs:1713-1791` — exporter from `pending_commit()` when staged), extra contexts `encrypted-media` + `agent-text-stream-quic` (`group_lifecycle.rs:43-44`), and `safe_export_secret(component_id)` for app components (`engine.rs:1520-1567`). 🟢 |
| `Mdk.cs:427-482` `GetNostrGroupId`/`GetNostrGroupData` (0xF2EE) | routing id + group metadata | `app_components` `transport_group_id_of_group` / routing component `0x8004` | **REWRITE** | 0% | Known from scoping §4. Plus NEW: rotation-aware many-to-one `transport_group_id_index` (`engine.rs:184-206`, #740) — routing id can rotate; old id kept during overlap window. 🟢 |
| `Mdk.cs:1697-1742` `ExportGroupState`/`ImportGroupState` + `_groups` cache (`:40`) | in-memory group cache + manual state blob | none — DM loads `MlsGroup` from storage **per operation** (`send.rs:80-85`, `ingest.rs:151-156`, everywhere) | **REWRITE** | ~10% | Architectural: DM is storage-authoritative, no long-lived in-memory group objects — which is what makes snapshot/rollback coherent. Scramble.Marmot should do the same via `MlsGroup.Export()/Import()` against the storage provider (per `scramble-marmot-snapshot-restore-spec-2026-07.md`). The version-wrapped key blob (`Mdk.cs:1706-1712`) disappears (keys in MLS storage). 🟢 |
| `Mdk.cs:185-221, 818-835, 1068-1089` storage passthroughs (`GetGroup(s)`, `GetMessages`, `GetWelcome(s)`, `GetRelays`) | thin reads | `engine.rs:1464-1467` `group_record` etc. | **SURVIVES** | ~90% | Trivial; DM adds the quarantine gate `ensure_group_live` on every accessor (`engine.rs:1175-1180`). 🟢 |
| `Mdk.cs:42-43, 1813-1821` per-group `SemaphoreSlim` locks | serialize per-group mutation | none (Rust `&mut self`) | **SURVIVES** (C#-ism) | 100% | Keep; the C# engine needs it since DM's exclusivity comes from `&mut`. 🟢 |
| `EpochSnapshotManager.cs:9-48` + `IMdkStorageProvider` snapshot API | storage-level snapshot/rollback/release/prune | `snapshot_guard.rs:34-102`, `fork_recovery` snapshots (deep-dive §5) | **SURVIVES w/ mods** | ~70% | The pleasant surprise (headline). Changes: epoch-keyed anchor names (`retained_anchor_epoch_from_snapshot_name`, `ingest.rs:1815-1826`), prune by `current_epoch − max_rewind_commits` (deep-dive §5), add the two RAII guards (below). 🟢 |
| `IMdkCallback.cs:7-41` | push callbacks (rollback/epoch/member±) | `GroupEvent` buffer + `drain_events`/`drain_auto_publish`/`drain_auto_proposals`/`drain_pending_convergence_groups` (`engine.rs:1614-1628`) | **REWRITE** | ~10% | Pull-based drains; far richer event set: `GroupJoined`, `EpochChanged`, `GroupStateChanged{actor,change,origin_commit_id}`, `ForkRecovered`, `GroupStateInvalidated`, `PendingCommitRecovered`, quarantine events, `MessageReceived`. Events carry the data the app needs to build kind-1210 system rows. 🟢 |
| `Results/GroupResult.cs` | result records | `SendResult`/`IngestOutcome{Processed,Buffered,Stale{reason}}`/`SendIntent` (cgka-traits) | **REWRITE** | ~20% | New shapes: `SendResult::Queued` (durable outbound queue), `GroupEvolution{pending}` (pending ref for confirm/fail), typed `StaleReason`. Record style ports fine. 🟢 |
| `MdkBuilder.cs`/`MdkConfig.cs` | builder | `engine.rs:232-371` `EngineBuilder` | **MODIFY** | ~60% | Same pattern. New required deps: `TransportPeeler`, `AccountIdentityProofSigner`, `FeatureRegistry`, supported app components; ciphersuite locked to 0x0001 at build (`engine.rs:308-317`). 🟢 |
| `Errors/MdkException.cs` | typed exceptions | `EngineError` (typed enum incl. `ForkedEpoch`, `MissingRequiredCapabilities`, `AdminDepletion`, `InvalidTransition`) | **MODIFY** | ~40% | Port the pattern, adopt DM's richer taxonomy. 🟢 |

### 2.2 `MarmotCs.Protocol` sanity check (scoping said "survives" — confirmed)

Spot-checked, not re-derived 🟢: `GroupEventEncryption.cs` — ChaCha20-Poly1305,
key = exporter `("marmot","group-event",32)`, base64(nonce‖ct) — **exact match**
with DM's transport wrap (which lives in the *peeler*, engine-side consts at
`group_lifecycle.rs:41-49`). Port destination: the `TransportPeeler`
implementation in `Scramble.Marmot.Transport.Nostr`, not the engine.
`Nip44`/`Nip59` survive. Event builders (30443/444/445) still emit the
forbidden `encoding` tag (`GroupEventBuilder.cs:52`, `WelcomeEventBuilder.cs:48`,
`KeyPackageEventBuilder.cs:95`) — drop on port, per scoping §4.
`CommitRaceResolver.cs` is in Protocol but is a REWRITE (see §1).
`NostrGroupDataCodec`/`NostrGroupDataExtension` (0xF2EE) — REWRITE to
app-components (scoping §4, unchanged).

### 2.3 NEW subsystems in the engine scope (no marmot-cs analog)

Beyond the already-tracked Convergence (step 2, **L**), AppComponents,
AccountIdentityProof, feature registry:

| Subsystem | What it is | Evidence | v1-necessity |
|---|---|---|---|
| **Epoch state machine** | `Stable/PendingPublish/Merging/Recovering/Unrecoverable` single-owner manager; atomic multi-map transitions | deep-dive §4; `engine_state.rs:196-373` | **Required** — gates every send + ingest (`can_ingest`) |
| **Content-derived dedup + OwnEcho** | dedup id = `SHA-256(mls_bytes)`, transport id only a pre-filter; sent-id tracking incl. content markers | `mod.rs:938-949`, `ingest.rs:407-440`, `store.rs:68-162` | **Required** — spec MUST (wire-envelopes.md); our event-id dedup is non-conformant |
| **Queued outbound intents** | durable send queue; sends queue while convergence unsettled, drain on settle; commit results pause the drain | `mod.rs:150-256`, `:784-812` | **Required** (simple v1: queue only while non-Stable) |
| **Snapshot-fallback peel** | on decrypt-miss, retry peel under each retained past snapshot's exporter | `ingest.rs:254-330`, `:1753-1813` | High — this is how epoch-boundary messages survive; needs retained anchors |
| **Deferred-peel retry lifecycle** | `PeelDeferred` rows, per-group flood cap 256, fingerprint-gated sweeps, retry budget 32 → terminal | `mod.rs:34-97`, `:475-743` | Medium — v1 can start with naive retry; caps are DoS-hardening (mdk#339) |
| **Leave lifecycle** | durable `LeaveRequest`, leaving send-gate, auto-repropose per epoch; SelfRemove **proposal** (leaver) + **auto-commit** (remaining member, jittered) | `send.rs:567-739`, `ingest.rs:1092-1485`, `auto_committer.rs:57-130` | **Required** for leave interop |
| **Self-eviction realization + removed-copy gates** | mark local copy removed, tombstone view, purge queued intents, `SelfEvicted` classification | `ingest.rs:178-197`, `:676-691`, `:1487-1573`, `mod.rs:150-201` | **Required** for remove interop |
| **Hydration + quarantine + retry** | session-open group hydration, validated-tree marker (skip re-verifying schnorr per leaf), stranded-pending-commit crash recovery (non-removal only), per-group quarantine | `engine.rs:652-1260` | Medium — needed before production, not before first interop |
| **Transport-group-id index** | O(1) `nostr_group_id → GroupId`, rotation-aware many-to-one | `engine.rs:184-221`, `:698-721` | **Required** (small) — DM routing id ≠ MLS group id |
| **Group-state-change synthesis** | before/after roster/admin/profile/retention diffs → attributed events with `origin_commit_id` for fork invalidation | `ingest.rs:836-1084`, `group_state_changes.rs` | High — app-visible correctness under reorgs |
| **RAII guards** | `SnapshotRollbackGuard` (probe safety), `PendingCommitCleanupGuard` (orphan-window cleanup) | `snapshot_guard.rs:1-102`, `pending_commit_guard.rs:1-223` | **Required** — C# analog: `IDisposable`/`try-finally` with the same confirm-before-release discipline |
| **Forensics/audit recorder** | typed audit events at every decision point | `engine.rs` throughout, `audit_helpers.rs` (30KB) | **Optional for v1** — design the seam (no-op recorder), defer the taxonomy |

---

## 3. Implied `Scramble.Marmot.Engine` module list (refines scoping §10)

Scoping §10's layout holds; the line-level read refines the Engine box and adds
one project:

- `Scramble.Marmot.Engine` — split as DM does 🟢:
  - `Engine` (facade + builder + hydration/quarantine + accessors + event drains)
  - `EpochManager` (state machine; port the atomicity discipline + regression
    test shape from `epoch_manager.rs:317-358`, deep-dive §4)
  - `MessageProcessor.Ingest` (peel→classify→apply/buffer + validation chain +
    fork seam) — **the largest single piece**
  - `MessageProcessor.Send` (SendIntent dispatch, publish-before-apply staging)
  - `MessageProcessor.Store` (durable records, dedup classification, content ids)
  - `PublishLifecycle` (confirm/fail; durable-transaction discipline)
  - `ForkRecovery` (fast path: snapshots, `CommitOrderingKey`, probe)
  - `AutoCommitter` (SelfRemove policy) + `LeaveLifecycle`
  - `Guards` (SnapshotRollback / PendingCommitCleanup as IDisposable)
  - `Events` (GroupEvent + typed IngestOutcome/SendResult/StaleReason)
- **`Scramble.Marmot.Peeler` (NEW project/namespace, not in scoping §10):** the
  `ITransportPeeler` interface the engine depends on, with the Nostr
  implementation in `Scramble.Marmot.Transport.Nostr` (wraps ported
  `GroupEventEncryption` + NIP-44/59 + event builders). This is DM's actual
  engine/transport boundary and we should keep it — it is also what makes the
  engine testable with the upstream conformance vectors' `PeeledMessage` shape
  (deep-dive §2.4). 🟢
- `Scramble.Marmot.Storage` — extend the **ported** `MarmotCs.Storage.Abstractions`
  (it survives well): keep the snapshot API; add `MessageRecord` with DM's state
  set (`Created/Retryable/PeelDeferred/Processed/Failed/EpochInvalidated/Sent`),
  `QueuedOutboundIntent`, `LeaveRequest`, validated-tree marker,
  group record fields `removed` + `join_epoch`, storage transactions
  (`with_transaction` — SQLite transaction scope). 🟢
- Unchanged from scoping §10: `Wire.Nostr` (port + tag fixes),
  `Identity.AccountProof` (step 4), `AppComponents`, `Convergence` (step 2, L),
  `Transport.Nostr` glue.

---

## 4. Build order (what must exist before what)

Step-2's finding holds and is now sharpened by `ingest.rs:507-536`: the slow
convergence path is entered **only** for future-epoch commits or in-horizon
past-epoch commits with a retained anchor; the linear next-commit case and the
common same-epoch race go through the **fast path** (`fork_recovery`). So the
engine skeleton can interop before `Convergence` exists, with the send gate
(`has_unresolved_convergence_inputs`, `mod.rs:367-456`) implemented against an
initially-empty stored-input set. 🟢

1. **Storage foundation** — port `MarmotCs.Storage.Abstractions`+`Sqlite`, add
   the §3 extensions (records, states, transactions, snapshots). Everything
   depends on it.
2. **EpochManager** — pure in-memory, precisely specified (deep-dive §4),
   testable standalone. No dependencies beyond types.
3. **Identity + AccountProof (step 4)** — blocks any group that real peers will
   accept (MUST-reject without `0xf2f1`). Needed before first interop test, and
   it defines the `AccountIdentityProofSigner` seam the builder requires.
4. **Peeler + Wire.Nostr port** — codecs exist; drop `encoding`, add tags.
   Independent of 2–3; can go in parallel.
5. **AppComponents** — `0x8004` routing, admin policy, profile, retention +
   the validators the engine calls at every seam (`require_admin`,
   `admins_of_group`, `transport_group_id_of_group`, coupling/integrity
   checks, `commit_ordering_priority_for_staged`). The engine cannot ingest a
   single commit without these. Must precede 6.
6. **Engine v1 (fast-path only)** — create/join/send-app/invite/remove/leave +
   ingest with publish-before-apply, fork recovery, content dedup, validation
   chain. Convergence stubbed (`Settled` when no stored inputs). **This is the
   first interop-testable milestone against Whitenoise.**
7. **AutoCommitter + leave lifecycle completion** (small, sits on 6).
8. **Convergence (slow path)** — buildable in parallel with 6–7 once storage +
   a `CandidateMaterializer` skeleton exist; verified against upstream's
   portable JSON conformance vectors (deep-dive §8).
9. **Hydration/quarantine + deferred-peel hardening + queued-intent drain
   polish** — production hardening on top.
10. **Capabilities/feature registry + upgrade flow** — minimal static registry
    is needed at step 6 (create/invite/join checks); the full
    upgrade/auto-negotiation flow can trail.

---

## 5. `dotnet-mls` generic-capability questions (permission-gated, cross-checked against scoping §12 a–d)

Confirmed/raised by this read — **no edits made; all framed as generic RFC-9420 /
mls-extensions features**:

- **§12(b) SelfRemove — confirmed required, and slightly broader.** The whole
  leave path is built on it (`send.rs:656-658` `leave_group_via_self_remove`;
  `ingest.rs:1334-1343` `store_pending_proposal` +
  `:1360-1362` `commit_to_pending_proposals`). Beyond the proposal type itself,
  the engine needs generic **proposal-store mechanics**: store a received
  proposal, commit-to-stored-proposals, remove a stored proposal
  (`pending_commit_guard.rs:92-99` — stale stored SelfRemove + Remove for the
  same leaf panics OpenMLS 0.8.1; the C# port needs the same cleanup hook).
  dotnet-mls has `CacheProposal` (§12c audit) — verify store/remove/commit-to
  coverage. 🟡
- **§12(c) PublicMessage produce/verify — confirmed, and upgraded to
  critical-path.** `PURE_PLAINTEXT_WIRE_FORMAT_POLICY` (`wire_format.rs:33`)
  means **every commit and proposal DM emits is a PublicMessage**. dotnet-mls
  `Commit()` returns PrivateMessage only (§12c audit). Without the produce path
  there is no wire-compatible send at all. The membership_tag-on-Proposal spec
  question from §12(c) must be resolved in the same work item. 🟢
- **§12(d) retained past-epoch secrets — confirmed** (`max_past_epochs`
  plumbed through every group config, default 5, `wire_format.rs:38`,
  `group_lifecycle.rs:210`, `:518`). 🟢
- **Snapshot/restore/probe — NO new gap, reconfirmed.** All probe patterns seen
  this read (fork probe `ingest.rs:1657-1722`, hydrate SelfRemove probe
  `engine.rs:1040-1105`, snapshot-fallback peel `ingest.rs:1753-1813`) are
  storage-snapshot + process + rollback — expressible with `MlsGroup.Export()/
  Import()` + the ported storage snapshot API, per
  `scramble-marmot-snapshot-restore-spec-2026-07.md`. 🟢
- **NEW (e): AppDataUpdate proposal + app-data-dictionary component API.** DM
  uses OpenMLS's mls-extensions "safe application" machinery:
  `Proposal::AppDataUpdate(AppDataUpdateProposal)` (`send.rs:439-449`,
  `update_group_data.rs:21`), `ComponentData`, commit-time dictionary
  computation (`process_commit_with_app_data_updates`, `ingest.rs:562-564`),
  and `safe_export_secret(component_id)` incl. from a staged commit
  (`engine.rs:1550-1560`). For dotnet-mls this decomposes into: a new proposal
  type (closed-enum add, like SelfRemove — MEDIUM) + the safe-extension
  export derivation (🔴 verify the exact mls-extensions construction before
  sizing; possibly expressible via the existing generic exporter with a
  prescribed label/context, in which case it lives in `Scramble.Marmot`, not
  dotnet-mls). The dictionary bytes themselves ride capability (a) (opaque GC
  extension) — no library change. **This is the biggest new dotnet-mls
  question found by step 3.**
- **NEW (f): staged-commit introspection.** DM reads the staged commit
  extensively pre-merge: `pending_commit()` → `queued_proposals()`,
  `group_context()` (projected epoch), `export_secret` from staged
  (`engine.rs:1719-1755`), and validates it (`ingest.rs:775-832`). dotnet-mls
  exposes `HasPendingCommit`/`Merge`/`Clear` but (from the `Mdk.cs` usage) not
  the staged commit's contents. Generic accessor + export-from-staged —
  MEDIUM. 🟡 (verify against dotnet-mls source before proposing.)

---

## 6. Size / risk estimate — engine-orchestration piece

**Estimate: L** (same bucket as Convergence, step 2). 🟢 reasoning:

- Scope read this session ≈ **8,800 lines of dense, comment-heavy Rust**
  (engine 1,873 + message_processor 4,199 + group_lifecycle 1,006 + publish 358
  + guards 325 + auto_committer 165 + epoch_manager ≈ 430 + fork_recovery ≈ 470),
  replacing ≈ 2,314 lines of C# of which ~15–20% of shape survives.
- **Not XL because:** the reference implementation is precise and readable (it
  is effectively the spec); no novel algorithms in this piece (the hard
  algorithmic center is Convergence, already sized L separately); our codecs,
  storage abstraction, and staged-commit interaction with dotnet-mls survive;
  v1 can drop forensics, quarantine hardening, and deferred-peel caps.
- **Not M because:** publish-before-apply inverts control flow on every send
  path and in `Scramble.Core`'s `NostrService`/`ManagedMlsService` callers
  (publish-ack becomes an engine input); the ingest validation chain is deep
  and MUST-reject-grade (one miss = interop failure); the dedup model changes
  storage semantics; and the buffered-replay/reorg event model is new
  app-facing behavior.

**Top unknowns:**
1. dotnet-mls gap (e) — AppDataUpdate + safe-export construction (🔴 §5): sits
   on the critical path of every metadata commit and every inbound commit
   validation.
2. dotnet-mls gap (c) — PublicMessage produce/verify + membership_tag question:
   without it nothing we emit is wire-valid.
3. Peeler-seam fidelity — snapshot-fallback peel and routing-id rotation make
   the transport boundary stateful in ways our current `NostrService` glue is
   not; the integration surface into `Scramble.Core` is unscoped (deliberately —
   it is cutover work, not engine work).

**Fold-in for step 5 (overall estimate):** Engine **L** + Convergence **L**
(settled) + AppComponents **M** 🟡 + AccountProof **M** (step 4 will confirm) +
dotnet-mls generic items (b,c,d,e,f ≈ 2–4 MEDIUM work items, permission-gated)
+ Transport/Core integration (unscoped, likely **M**). 🟡
