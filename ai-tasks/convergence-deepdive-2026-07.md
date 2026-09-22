# Convergence deep-dive (2026-07)

**Status:** research/documentation only. No engine code written. `lib/marmot-cs` and
`lib/dotnet-mls` were not touched (read-only `gh api` research against upstream
GitHub repos only).

**Purpose:** deep-dive Dark Matter's distributed-convergence subsystem (scoping doc
§11 risk #1 — the prime timeline unknown) well enough to size the rewrite and design
`Scramble.Marmot.Convergence`.

**Confidence key:** 🟢 verified this session (read the actual source/spec text,
citation given) · 🟡 informed inference (reasoned from verified material, not itself
directly read) · 🔴 needs-more (flagged gap, do not plan a date from this).

**Sources used** (all fetched via authenticated `gh api`, pinned to exact refs):
- Spec: `marmot-protocol/marmot` @ `master`, `protocol-core/{convergence,
  retained-history,publish-lifecycle,group-state,inbound-processing,
  member-departure,group-messaging,group-setup,joining}.md`.
- Rust: `marmot-protocol/mdk` @ tag `v0.9.4` = commit `e391adc133a9b60e420da7a0446f014a180ac8d2`,
  crate `crates/cgka-engine/src/{lib,convergence,canonicalization,
  distributed_convergence,epoch_manager,fork_recovery,publish,wire_format,
  message_disposition}.rs`, crate `crates/traits/src/engine_state.rs`, and the
  design docs `docs/marmot-architecture/{cgka-engine-canonicalization-contract.md,
  distributed-convergence.md,overview/cgka-engine-quality-and-vectors.md}`.
- Conformance: crate `crates/cgka-conformance-simulator` (README, vectors/,
  vectors/manifest.v1.json, sample vectors `convergence-committer-selected.v1.json` /
  `convergence-witness-selected.v1.json`), plus `formal/tamarin/distributed_convergence_v0.spthy`.
- Amethyst (`vitorpamplona/amethyst`) cross-check was scoped out of this deep-dive
  per user direction — one line only, see §7.

---

> **⚠ ERRATUM (2026-08-09, step 5) — analysed at `v0.9.4`; reference now pinned
> to `wn-agent-v0.9.10`.** The step-5 drift-diff
> (`scramble-marmot-phased-plan-2026-08.md` §2) **confirms this document in
> full**: the v1 policy constants (§3.1) are byte-identical, the branch-selection
> comparator (§3.5) is untouched, and `fork_recovery.rs` changed by 17 lines.
> Three additions to fold in when building: **(a)** the v1 constants are now
> pinned *by construction* — `ensure_pinned_v1()` rejects any non-v1 policy
> outside a test-only feature flag, and `ensure_app_window_matches()` requires
> `app_message_past_epoch_limit == max_past_epochs` (`DEFAULT_MAX_PAST_EPOCHS`
> is now derived from `V1_APP_MESSAGE_PAST_EPOCH_LIMIT = 5`); our policy loader
> must enforce both or diverge silently. **(b)** New pinned constants
> `V1_SETTLEMENT_QUIESCENCE_MS = 1000`, `V1_MAX_CONVERGENCE_PASS_MS = 5000`,
> plus a new `convergence_input.rs` and a `ConvergencePassStorage` trait.
> **(c)** §4's epoch state machine gains a terminal `Disbanded` state,
> `repair_to_stable()` as the only exit from `Unrecoverable`, `restore_pending()`
> for crash recovery, and a fixed phantom-`committed_from` bug on rollback
> (`owns_committed_from`) that is worth copying verbatim.

## 1. Headline finding

Dark Matter's convergence subsystem is **two complementary mechanisms**, not one:

1. **Fast path — `fork_recovery.rs`** ("the direct seam"): before applying any
   commit that advances the local epoch `N → N+1`, the engine snapshots group
   state. If a second, competing commit for the *same* epoch `N` shows up, it is
   compared against the already-applied one using a small ordering key
   (`CommitOrderingKey`: priority, committer, digest) and, if it wins, storage is
   rolled back to the pre-commit snapshot and the winner is applied instead. 🟢
   (`crates/cgka-engine/src/fork_recovery.rs:1-17,139-175`)
2. **Slow path — `distributed_convergence.rs` + `canonicalization.rs` +
   `convergence.rs`** ("stored convergence"): a batched pipeline that
   materializes a **candidate-state graph** from retained past-epoch snapshots by
   *replaying* MLS commit bytes, scores every branch with the same ordering
   criteria, and applies the winner — used for returning clients, multi-epoch
   forks, and any backlog bigger than a single same-epoch race. 🟢
   (`crates/cgka-engine/src/distributed_convergence.rs:823-830` explicitly calls
   this pairing out: "the convergence-path analog of the direct seam's
   `GroupEvent::ForkRecovered`")

Both paths use the **same deterministic branch-scoring rule** (§3). The spec
(`protocol-core/convergence.md`) describes only the general algorithm; the
two-seam split is an *implementation* optimization (🟡 inferred from code
comments — not itself asserted as a required architecture by the spec, so
`Scramble.Marmot.Convergence` could plausibly implement only the slow path for a
v1 and treat every same-epoch race as a 1-branch instance of the general
algorithm — see §8 sequencing note).

The core scoring algorithm itself is **small, deterministic, and precisely
specified** — both the spec prose and the Rust executable model were read in
full and match line-for-line (§3). It is **not** the size driver. The size
driver is the **storage/replay foundation** underneath it — a capability that
does not exist in `dotnet-mls` today and is *larger* than scoping doc §12(d)
described (§6).

---

## 2. Module + data-flow map

### 2.1 Module list (from crate root)

🟢 `crates/cgka-engine/src/lib.rs:13-32` (doc comment, verbatim module list):

```
engine              — Engine<S> state machine + EngineBuilder (top-level driver)
engine_metrics       — diagnostic post-settle reorg telemetry
identity             — local signer + credential bundle
account_identity_proof — 0xf2f1-equivalent account-key proof (out of scope here)
feature_registry     — runtime feature registry
wire_format          — PURE_PLAINTEXT_WIRE_FORMAT_POLICY, DEFAULT_MAX_PAST_EPOCHS
provider             — OpenMlsProvider adapter (crypto + storage)
group_lifecycle      — create_group, join_welcome, group records
message_processor    — inbound `ingest` / outbound `send` (mod.rs, ingest.rs, send.rs, store.rs)
distributed_convergence — stored-message convergence entry points ("slow path")
canonicalization + convergence — executable branch-selection policy model
openmls_projection   — bytes-first bridge between OpenMLS and the convergence model (largest file in the crate, 93KB)
epoch_manager        — per-group EpochState transitions + pending-publish bookkeeping
fork_recovery        — same-epoch commit rollback/replay ("fast path")
publish              — publish-confirm / publish-failed lifecycle (two-phase send)
capability_manager, capabilities, upgrade — capability policy
auto_committer       — SelfRemove auto-commit eligibility
app_components       — Marmot app-component state (out of scope here)
```

### 2.2 Call-graph (module-level arrows)

🟢 verified via doc comments + function bodies read directly, 🟡 where marked
(inferred from module shape, not a full read of `engine.rs`/`ingest.rs`, which
are 82KB/94KB respectively and were only grepped for structure, not read whole):

```
message_processor::ingest  --(Commit/Proposal/AppMessage arrives)-->
  epoch_manager::can_ingest()  [gate: buffered if PendingPublish/Merging]
  --> fork_recovery::resolve_fork_candidate()  [fast path: same-epoch race only]
       --> storage.rollback_group_to_snapshot() on candidate win
  --> (backlog / multi-epoch / non-live-branch input) buffered as stored message
       --> distributed_convergence::buffer_openmls_convergence_message()

distributed_convergence::converge_stored_openmls_messages()  [slow path, driven
    periodically / on drain, up to 16 passes per drain — comment at
    distributed_convergence.rs:112-117]
  --> openmls_projection::canonicalize_stored_openmls_messages()  🟢 (confirmed by a
      parallel research pass this session: the orchestration functions
      `canonicalize_stored_openmls_messages` / `apply_openmls_canonicalization_result`
      that `distributed_convergence.rs` calls live in `openmls_projection.rs`, NOT in
      `canonicalization.rs` itself — `canonicalization.rs` is a pure, storage-free
      policy/model module. Three-way split: `convergence.rs`+`canonicalization.rs` =
      bytes-agnostic policy, `openmls_projection.rs` = bytes-first bridge that runs
      the policy against stored OpenMLS wire records, `distributed_convergence.rs` =
      Engine-level orchestration (quarantine/Unrecoverable checks, epoch_manager
      updates, audit/metrics side effects). Worth preserving this 3-way split in the
      C# port rather than collapsing it.)
       --> materialize_candidate_graph()          [dedupe by message_id, build BranchCandidate list via a fixed-point worklist loop over pending commits, order-independent]
       --> attach_app_witnesses()                 [count distinct senders per epoch; expiry evaluated against each CANDIDATE's own tip_epoch, not the global current tip — required so witness counting can't diverge between replicas at different tips]
       --> convergence::select_canonical_branch()  [pure scoring function, §3]
       --> convergence::select_canonical_branch_traced()  [same selection + audit trace]
  --> openmls_projection::apply_openmls_canonicalization_result()
  --> apply selected branch: GroupEvent::EpochChanged / CommitRolledBack /
      GroupStateInvalidated, storage writes, message dispositions,
      engine_metrics::note_applied_selection() [diagnostic reorg telemetry only —
      explicitly documented as never feeding back into branch selection]

Gate on when a commit enters the slow path at all (🟢
`message_processor/ingest.rs`, `commit_should_enter_convergence`): only if the
commit's epoch is `>= current_epoch`, OR it's a past-epoch commit that is within
`max_rewind_commits`, wasn't self-committed from that epoch, and a retained
anchor snapshot exists for it. The ordinary linear case (next commit in
sequence) and the common same-epoch race both go through the **fast path**
(`fork_recovery.rs`, direct `process_message` + `WrongEpoch` handling) — the
slow path is reserved for genuinely divergent/multi-epoch/backlog input. This
sharpens §1: the fast path is not a rarely-used optimization, it is the
*default* mechanism for the common case; the slow path is the general-case
fallback.

message_processor::send  --(local group-state change prepared)-->
  epoch_manager::begin_pending()  [Stable -> PendingPublish, requires Stable]
  --> (caller publishes to transport; blocks/queues further local commits) --> confirm/fail:

publish::do_confirm_published()  -->
  epoch_manager::confirm_publish()  [PendingPublish -> Merging -> Stable{new_epoch}]
  --> fork_recovery::track_pending_commit_for_recovery() / promote_pending_commit_for_recovery()
  --> replay_buffered_messages()  [re-runs anything the engine deferred while PendingPublish/Merging]

publish::do_publish_failed()  -->
  epoch_manager::rollback_publish()  [PendingPublish -> Stable{prior_epoch}]
  --> fork_recovery::forget_pending_commit_for_recovery()
  --> replay_buffered_messages()
```

### 2.3 Pipeline boundary (from the canonicalization contract doc)

🟢 `docs/marmot-architecture/cgka-engine-canonicalization-contract.md:14-22`
(verbatim):

```
transport adapter -> peeler -> CGKA engine canonicalization -> application events/results
```

"The application consumes accepted app messages and invalidation records after
canonicalization. It does not decide which commit branch is canonical." Any
ordering the transport adapter supplies is **advisory only** — branch selection
depends solely on MLS replay, retained anchors, and the pinned policy (§3).

### 2.4 The one logical operation

🟢 `docs/marmot-architecture/cgka-engine-canonicalization-contract.md:23-28,97-110`:

```
canonicalize(engine_state, pending_messages, outbound_intents, policy, clock)
  -> CanonicalizationResult
```

Inputs are `PeeledMessage { message_id, group_id, sender, kind: Commit|Proposal|
AppMessage, source_epoch, mls_bytes }` — transport-independent; `message_id` "MAY
be a digest of the peeled protocol bytes" and "MUST NOT be a Nostr event id."
This is the executable-model input shape; the production engine derives the same
inputs from stored OpenMLS wire bytes via `openmls_projection`.

---

## 3. Branch-selection + canonicalization algorithm

This is precise enough to reimplement — the spec text and the Rust executable
model were both read in full and agree exactly.

### 3.1 Convergence policy (pinned constants, v1)

🟢 `protocol-core/convergence.md:55-66` (spec table) — **every client MUST use
exactly these values; not group-tunable, not carried in group state**:

| Field | Value | Meaning |
|---|---|---|
| `max_rewind_commits` | `5` | rollback horizon — how far back from tip a branch MAY fork and stay eligible |
| `app_payload_past_epoch_limit` | `5` | how many past epochs MAY still produce app-payload witnesses |
| `settlement_quiescence_ms` | `1000` | min. time without selection-relevant input before a pass MAY settle |
| `max_convergence_pass_ms` | `5000` | hard deadline for one collection window, from pass start, never extended |
| `witness_quorum_senders_per_epoch` | `2` | distinct senders needed for one branch-epoch to count toward quorum |
| `witness_quorum_epochs` | `1` | number of branch-epochs that MUST meet sender quorum |
| `max_witness_override_depth` | `1` | max commit-depth boost a branch MAY get from witness quorum |

Invariant: `max_witness_override_depth` MUST NOT exceed `max_rewind_commits` — the
witness boost can never push a branch past the rollback horizon. 🟢 Enforced in
code: `ConvergencePolicy::validate()`, `crates/cgka-engine/src/convergence.rs:49-63`,
returns `ConvergencePolicyError::WitnessOverrideExceedsRewind` — checked "when a
stored policy is decoded and when a group policy is set."

Note the spec's 7-field policy is split across two Rust structs: `ConvergencePolicy`
(4 fields: rewind/quorum/override — `convergence.rs:14-19`, `Default` = the table
above) and `CanonicalizationPolicy` (wraps it + `app_message_past_epoch_limit: 5`,
`settlement_quiescence_ms: 1000` — `canonicalization.rs:17-30`).
`max_convergence_pass_ms` was not located in the fetched files 🔴 needs-more —
likely lives in the engine-level scheduler (`engine.rs`, not read in full).

### 3.2 Candidate branch — data model

🟢 `crates/cgka-engine/src/convergence.rs:65-97` (exact struct):

```rust
struct BranchCandidate {
    id: String,
    fork_epoch: u64,       // epoch the branch diverged from retained canonical state
    tip_epoch: u64,        // epoch reached after replaying the branch's valid commits
    tip_priority: CommitOrderingPriority,  // Privileged | Ordinary
    tip_committer: Vec<u8>,   // authenticated 32-byte x-only secp256k1 pubkey
    tip_digest: [u8; 32],     // SHA-256 of the tip commit's serialized MLS message bytes
    app_witnesses: Vec<AppWitness { epoch: u64, sender: Vec<u8> }>,
}
```

`tip_priority` is derived from **authorization rule**, not operation category:
🟢 `protocol-core/convergence.md:217-221` — "A Commit is `privileged` exactly
when its applicable Marmot authorization rule requires its committer to be an
active admin in the candidate parent state... A component change whose owning
document explicitly permits a non-admin committer is therefore `ordinary`."

`tip_digest` is always the tip commit's own digest regardless of branch length —
"both are SHA-256 over that tip Commit's MLS bytes... only a final tie-breaker
after fixed authenticated metadata." 🟢 (`convergence.md:224-230`)

`tip_priority` classification is concrete and mechanical, not a general
authorization lookup: 🟢 a parallel research pass this session read
`app_components.rs:538-586` (`commit_ordering_priority_for_staged`) — a staged
commit is `Ordinary` **only** if it is exactly one of two shapes: (a) a bare
self-update with zero by-reference proposals, or (b) one-or-more `SelfRemove`
proposals and nothing else. Every other commit shape (Add/Remove/Update-by-ref/
PSK/ReInit/ExternalInit/GroupContextExtensions/AppDataUpdate/etc.) is
`Privileged`. This is a small, closed classification a C# port can copy
directly rather than re-deriving from first principles.

**Internal mdk doc/code discrepancy worth flagging** (🟢, found by a parallel
research pass): `docs/marmot-architecture/cgka-engine-canonicalization-contract.md`'s
own 5-item branch-scoring summary (§2.2 above, "Branch scoring follows
distributed-convergence.md") **omits** `tip_priority` and `tip_committer`,
jumping straight from `app_witness_score` to "lower tip commit digest." The
actual code (`compare_scores`, §3.5 below) and the protocol spec
(`protocol-core/convergence.md`) both have 6 real criteria (7 with the
redundant raw-depth fallback), including `tip_priority` and `tip_committer` as
separate rungs before the digest fallback. **The spec and the code agree with
each other; only mdk's own internal contract-doc summary is stale/simplified.**
Treat `protocol-core/convergence.md` + `convergence.rs`'s `compare_scores` as
authoritative for the C# port, not the contract doc's condensed list.

### 3.3 Eligibility (which branches are even scored)

🟢 `convergence.rs:110-116` = spec `convergence.md:241-247`:

```text
is_branch_eligible(current_tip_epoch, branch, policy) =
    current_tip_epoch - branch.fork_epoch <= policy.max_rewind_commits
```

Uses the **frozen `pass_base_epoch`** (epoch at pass start, does not move during
the pass) for eligibility — NOT the live tip. A *separate* formula, using the
**live** canonical tip, ages out deferred commits whose parent hasn't arrived
yet:

```text
deferred_commit_is_stale =
    canonical_tip_epoch - commit_source_epoch > max_rewind_commits
```

🟢 `convergence.md:192-203`, explicit rationale quoted: "Deferred expiry
deliberately uses the live canonical tip so obsolete input can age out as
canonical state advances across completed passes. Branch eligibility uses the
frozen `pass_base_epoch` instead so an open pass cannot change its rollback
horizon while comparing candidates." **This live-vs-frozen distinction is easy to
get wrong in a reimplementation — flag as a specific test case.**

### 3.4 Witness scoring

🟢 `convergence.rs:349-376` = spec `convergence.md:275-301`, byte-identical
logic:

```text
witnesses_by_epoch(witnesses) = group witnesses into { epoch -> set<distinct sender> }

epoch_witness_score(epoch) =
    min(distinct_valid_app_senders_at_epoch, witness_quorum_senders_per_epoch)

app_witness_score = sum over branch epochs of epoch_witness_score(epoch)

witness_quorum_met =
    count(epochs where |distinct_senders(epoch)| >= witness_quorum_senders_per_epoch)
    >= witness_quorum_epochs

effective_commit_depth =
    (tip_epoch - fork_epoch)                      // raw_commit_depth
    + (witness_quorum_met ? max_witness_override_depth : 0)
```

One sender cannot inflate a branch's score by sending many messages in one
epoch (per-epoch dedupe by sender identity); a witness must decrypt against the
candidate branch state and pass full payload validation — "decryption alone is
not a witness." 🟢 (`convergence.md:257-273`)

Group-size-scaled quorum is speculative future work, not yet used: 🟢
`docs/marmot-architecture/cgka-engine-canonicalization-contract.md:221-237`
defines a `DerivedWitnessQuorum` formula
(`clamp(ceil(active_members * sender_fraction_bps / 10000), min, max)`) but the
same doc says "Until such a component exists, v0 engines use the local default
policy." Not needed for a v1 C# port.

### 3.5 Branch selection — the exact comparator

🟢 `convergence.rs:129-138`, matches spec `convergence.md:306-322` exactly
(spec lists 6 criteria; code's `valid_commit_depth`/raw-depth entry is a
no-op-when-tied fallback the spec explicitly calls out as redundant —
"`raw_commit_depth` has no separate comparison step... if effective depth and
witness-quorum status are both tied, a further raw-depth comparison is
necessarily tied as well," `convergence.md:317-319`):

```text
compare(a, b) =                                          // higher wins for 1-3, LOWER wins for 4-6
    1. a.effective_commit_depth  cmp  b.effective_commit_depth
  then 2. a.witness_quorum_met      cmp  b.witness_quorum_met      // true > false
  then 3. a.valid_commit_depth      cmp  b.valid_commit_depth      // redundant tie-fallback, spec-noted no-op
  then 4. a.app_witness_score       cmp  b.app_witness_score
  then 5. b.tip_priority            cmp  a.tip_priority            // privileged (lower enum value) wins over ordinary
  then 6. b.tip_committer           cmp  a.tip_committer           // LOWER 32-byte pubkey wins (lexicographic)
  then 7. b.tip_digest              cmp  a.tip_digest              // LOWER 32-byte digest wins (final fallback)

select_canonical_branch(current_tip_epoch, candidates, policy) =
    candidates.filter(is_branch_eligible).max_by(compare)
```

**Absolute exclusion** (repeated 5+ times in the spec, load-bearing): 🟢
`convergence.md:327-328` — "Transport arrival order, transport timestamps, outer
transport event ids, and local receive order MUST NOT participate in branch
selection." Every value used in the comparator MUST come from "MLS-valid bytes,
retained state, decrypted app payloads, or the pinned convergence policy."
(`convergence.md:324-325`) Parentage/fork_epoch is derived **only** by MLS replay
against retained states, never trusted from transport metadata.
(`convergence.md:210-211`, `162-179`)

### 3.6 Same-epoch races — not a second algorithm

🟢 `convergence.md:330-355`: two commits advancing the *same* candidate parent
are just two one-commit branches; they go through the **same** comparator above.
Only once depth/quorum/witness-score are tied does the comparison fall through to
`CommitOrderingSuffix { priority, committer, commit_digest }` — exactly criteria
4-6 of §3.5. This is the structure `fork_recovery.rs`'s fast path implements as
`CommitOrderingKey` (candidate vs. incumbent, `>=` means incumbent keeps
winning — `fork_recovery.rs:153-161`).

### 3.7 Pipeline (executable canonicalize())

🟢 `crates/cgka-engine/src/canonicalization.rs:213-290` (`canonicalize_internal`,
read in full):

```text
1. Dedupe pending_messages against state.seen_message_ids by message_id
   -> AlreadySeen list; unique_messages continue.
   (exception: an already-delivered app message re-admitted for witness
    purposes is exempted from dedupe-skip but still resolves AlreadySeen)
2. materialize_candidate_graph(unique_messages, materialized_candidates)
   -> BranchCandidate list (replay-derived; production path via openmls_projection)
3. attach_app_witnesses(candidate_graph, unique_messages, policy)
   -> populates BranchCandidate.app_witnesses
4. select_canonical_branch(current_tip_epoch, candidates, policy)   [§3.5]
   select_canonical_branch_traced(...)  -> audit trace (per-candidate score,
       eligibility, decisive-rule trail vs. runner-up — for forensic/debug output)
5. Build CanonicalizationResult { previous_tip, selected_tip, selected_fork_epoch,
   selected_branch_id, convergence_status, accepted_commits/proposals/app_messages,
   invalidated_app_messages, dropped_messages, already_seen,
   queued_outbound_intents, publishable_outbound_messages, errors }
```

### 3.8 Applying the selection — disposition rules

🟢 `convergence.md:357-409` (verbatim rule set, condensed):
- Commits on the selected path: `accepted`. On a losing-but-still-eligible
  branch: `deferred` (may win a later pass). Once permanently ineligible: `stale`.
- Proposals: `accepted` only if consumed by a selected commit; otherwise same
  deferred/stale rule.
- App messages: `accepted` + delivered only if they decrypt on the selected
  branch; if they decrypt **only** on a losing branch: `invalidated`, and any
  already-delivered payload is **withdrawn** from application output.
- **Supersession** (the important, easy-to-miss rule): if branch selection
  later supersedes a commit the client *already applied and confirmed —
  including its own published commit* — the client MUST emit an explicit
  invalidation naming the superseded commit, and every state notification
  attributed to it is withdrawn ("the application treats the changes it
  announced as not having happened"). 🟢 confirmed in code:
  `distributed_convergence.rs:888-906` names this exact scenario (issue #363)
  as a bug class it specifically fixes — "the client's OWN commit... gets no
  disposition at all... the confirm-time `GroupStateChanged` rows survive as
  the issue #363 lie" — i.e. upstream itself found and fixed a real bug where
  this rule was missed. **This is a concrete regression-test case
  `Scramble.Marmot.Convergence` must have from day one.**

---

## 4. Epoch state machine

🟢 The `EpochState` enum lives in `crates/traits/src/engine_state.rs:196-209`
(shared trait crate, not `cgka-engine` itself), full transition set read at
`engine_state.rs:236-373`:

```rust
enum EpochState {
    Stable { epoch: EpochId },
    PendingPublish(PendingPublish),   // { epoch, pending: StagedCommitHandle, pending_ref }
    Merging(Merging),                 // { epoch }
    Recovering(Recovering),           // { last_stable_epoch, buffered: Vec<PeeledMessage> }
    Unrecoverable(Unrecoverable),     // { last_stable_epoch }
}
```

Exact transition table (method name → precondition → effect):

| From | Method | To | Precondition |
|---|---|---|---|
| `Stable` | `begin_pending(new_epoch, pending, ref)` | `PendingPublish` | **only** legal from `Stable` |
| `PendingPublish` | `confirm_publish()` | `Merging` | transport publish confirmed |
| `PendingPublish` | `rollback_pending(prior_epoch)` | `Stable{prior_epoch}` | transport publish failed |
| `Merging` | `merge_to_stable(next_epoch)` | `Stable{next_epoch}` | local MLS apply completed |
| *(any)* | `detect_fork(buffered)` | `Recovering` | **always legal** — fork detected |
| *(any)* | `to_unrecoverable()` | `Unrecoverable` | **always legal** — `MissingRetainedAnchor` inside rollback horizon, or unrepairable |
| `Unrecoverable` | `repair_to_stable(epoch)` | `Stable{epoch}` | **only** legal exit from `Unrecoverable` |

`can_ingest()`: `Stable` and `Recovering` accept inbound; `PendingPublish` and
`Merging` buffer; `Unrecoverable` rejects (must repair first). 🟢
(`engine_state.rs:228-235`)

**Divergence from the assumed 5-state list**: the spec's `protocol-core/group-state.md`
(read by a parallel research pass this session, 🟢) defines **six** lifecycle
states including a terminal `Disbanded`, with legal transitions:

```
Stable -> PendingPublish -> Merging -> Stable
Stable -> Recovering -> Stable | Disbanded | Unrecoverable
Stable -> Unrecoverable -> Stable
Disbanded -> (none, terminal)
```

But the Rust `EpochState` enum has **no `Disbanded` variant**. 🟡 inferred: disband
is handled as a terminal *group-record* event (`convergence.md:382-388`:
"the client emits exactly one actor-attributed `group_disbanded` notification...
The client then releases live MLS and convergence state") rather than a fifth
`EpochState` case — i.e. disbanding **removes** the group's `EpochState` entry
rather than transitioning it to a new variant. Not independently confirmed by
reading the disband-apply code path (out of scope of the files fetched this
session) — 🔴 needs-more if `Scramble.Marmot.Engine`'s epoch-state enum needs to
model this exactly; low risk either way since disband is a rare terminal event.

Also notable: the spec table has **no `Merging -> Recovering` edge** — if a fork
is discovered while a locally-confirmed commit is being merged, that merge
completes to `Stable` first, and only then does the `Stable -> Recovering` rule
fire on the next admitted input. 🟢 (spec, confirmed structurally consistent with
the Rust enum, which also has no such edge — `Merging`'s only transition method is
`merge_to_stable`).

**`EpochManager`** (`crates/cgka-engine/src/epoch_manager.rs`, read in full) is
the single owner of all `EpochState` mutation for the engine — "no engine
subsystem can construct non-`Stable` variants directly" (doc comment,
`epoch_manager.rs:1-16`). Key structures beyond the state map:
- `committed_from: HashMap<GroupId, BTreeSet<EpochId>>` — records every epoch
  *this client itself* committed from, used by fork detection to distinguish "a
  benign late-arriving commit at an epoch we didn't commit from" vs. "a real
  fork" (a `WrongEpoch` for an epoch we committed from, after we've since
  advanced).
- `pending: HashMap<PendingStateRef, PendingMeta>` — sidecar tracking which
  group/prior-epoch/audit-context a given in-flight publish belongs to, so
  `confirm_publish`/`rollback_publish` can find the right target and the right
  rollback point.
- All multi-step transitions (`begin_pending`, `confirm_publish`,
  `rollback_publish`) are implemented **atomically over the in-memory maps**: the
  fallible inner state transition runs *before* any map mutation, so a failed
  transition leaves every map untouched — a documented regression fix (`mdk#146`)
  for a bug where a failed `begin_pending` orphaned a group to "unknown."
  **Worth copying this exact atomicity discipline into the C# port** — the
  regression test at `epoch_manager.rs:317-358` is a good template.

---

## 5. Fork recovery (fast path)

🟢 `crates/cgka-engine/src/fork_recovery.rs`, read in full.

**Mechanism**: before applying any commit that advances the local epoch `N ->
N+1`, `Engine::do_confirm_published` calls
`retain_current_epoch_snapshot_for_group` (via
`ForkRecoveryManager::create_snapshot` → `storage.create_group_snapshot`) —
**a full storage-level snapshot of the group's state at that epoch**, not just a
secret/key retention. If a second commit for the *same source epoch* later
arrives, `resolve_fork_candidate` builds a `CommitOrderingKey` for the candidate
and compares it against the already-applied incumbent's key (§3.6's tie-break
suffix). If `candidate_key < incumbent_key` (candidate wins — lower key wins,
matching §3.5's criteria 5-7 direction):

```text
storage.rollback_group_to_snapshot(group_id, incumbent_snapshot_name)
storage.release_group_snapshot(group_id, incumbent_snapshot_name)
mark incumbent's stored message state = EpochInvalidated
epoch_manager.set_stable(group_id, source_epoch)   // back to the pre-commit epoch,
                                                     // caller re-processes candidate normally
```

Snapshots are pruned via `prune_fork_recovery_for_group`:
`oldest_retained_epoch = current_epoch - max_rewind_commits` (i.e. the same
`max_rewind_commits = 5` window bounds *both* candidate-branch eligibility
(§3.3) *and* how many past-epoch full-state snapshots are retained for fork
recovery). 🟢 (`fork_recovery.rs:376-394`)

**Retained-past-epochs is broader than scoping doc §12(d) scoped it.** §12(d)
framed the `dotnet-mls` gap narrowly as "decrypt a `PrivateMessage` from a past
epoch after advancing" (i.e. OpenMLS's native `max_past_epochs` secret-tree
retention — confirmed as a *separate*, already-solved mechanism on the mdk side:
`wire_format.rs:35-38,52-58`, `DEFAULT_MAX_PAST_EPOCHS = 5`, wired through
`MlsGroupJoinConfig::max_past_epochs`). But fork recovery and candidate-branch
materialization (§6 below) need a **second, larger** capability that
`max_past_epochs` does not provide: **snapshot the entire group object
(ratchet tree, key schedule, Marmot metadata) at an epoch and be able to roll
back to it wholesale** — this is a storage/state capability, not a
secret-retention capability, and per the scoping doc's own audit
(`dotnet-mls`'s `MlsGroup.cs:1092-1099`, "`_keySchedule` + `_secretTree` are
overwritten with no history") **`dotnet-mls` has zero support for either
half today.**

---

## 6. Candidate materialization / MLS replay (the other big gap)

🟡 `openmls_projection.rs` is the largest file in the crate (93KB) and was
**not read in full this session** (grepped for structure only via the module
doc comment and cross-references from `canon-contract.md`) — flag as 🔴
needs-more for a line-level read before committing to a size number.

What is verified 🟢 (`docs/marmot-architecture/cgka-engine-canonicalization-contract.md:139-172`,
`241-271`): candidate materialization means, for each pending Commit, **replaying
its MLS bytes against a retained snapshot** to see whether it validates as an
edge from that snapshot — without mutating live group state ("rollback probe
state" is an explicit step in the documented flow):

```text
Retained MLS snapshot -> Replay peeled MLS bytes -> Process message with OpenMLS
  -> Observe proposal refs / staged commit -> Record symbolic edge -> Rollback probe state
```

This requires a capability dotnet-mls's current `MlsGroup` design does not
appear to have: **process a commit against an arbitrary (non-current, possibly
non-most-recent) retained state as a side-effect-free probe**, then discard the
probe result without committing it to live state. dotnet-mls's `Commit()` /
`ProcessCommit()` methods mutate the group in place (per scoping doc §12(c)/(d)
audit). This is materially the same underlying need as fork recovery's snapshot
requirement (§5) but applied to *multiple* competing candidates per pass rather
than one incumbent — i.e. **both gaps point at the same missing primitive**:
*generic* MLS state snapshot + restore + non-mutating replay-probe.

**This is the single biggest unscoped unknown found this session** — bigger in
scope than any of scoping doc §12(b)/(c)/(d) individually, and it underlies both
of Dark Matter's two convergence seams (§1). Recommend treating it as its own
permission-gated generic-MLS proposal, separate from (and larger than) §12(d)'s
narrower framing — see §9.

---

## 7. Publish-before-apply (two-phase send)

🟢 `crates/cgka-engine/src/publish.rs`, read in full; spec
`protocol-core/publish-lifecycle.md` (read by a parallel research pass, 🟢).

Spec shape (`publish-lifecycle.md`, verbatim structure):

```text
prepare local commit -> retain pending state -> produce publish obligation
  -> publish required bytes -> confirm or fail publication
  -> apply or discard pending state
```

Rule: "A locally generated group-state change MUST NOT become local canonical
state until the client has confirmed that its publish obligation succeeded" —
applies to group creation, invites, admin removal, profile/capability/policy
updates, self-updates, and remaining-member SelfRemove-only commits. **Not**
applied to the leaver's own SelfRemove *proposal* (no pending state — someone
else commits it). A publish obligation succeeds only on "an acknowledged accept
... from at least one endpoint in the recipient scope" — queued-but-unacked
never counts. Epoch-0 group creation (and its immediately-following founding Add
commit) has an empty, auto-satisfied obligation since no peers exist yet to fork
against.

Rust implementation (`publish.rs:1-29` doc comment, confirm/fail flows read in
full):
- **Confirm flow**: load `MlsGroup` (staged commit still attached) → snapshot
  pre-commit epoch for fork recovery → one durable transaction: cache invitee
  capabilities, capture the `CommitOrderingKey` stamp *while the staged commit is
  still attached* ("stored convergence cannot re-derive priority... from the
  wire bytes later — MLS refuses to process own commits, so this is the only
  durable source" — `publish.rs:112-121`, a genuinely subtle implementation
  note worth carrying forward), `merge_pending_commit`, refresh Marmot
  record/capability caches, mark origin commit `Processed` → hand off to
  `EpochManager::confirm_publish` (`PendingPublish -> Merging -> Stable`) →
  emit `GroupEvent::GroupCreated`/`EpochChanged` → `replay_buffered_messages()`.
- **Fail flow**: `clear_pending_commit` discards the staged commit, roll back
  the Marmot record's projected fields by re-deriving from (now-reverted) MLS
  state, hand off to `EpochManager::rollback_publish` (`PendingPublish ->
  Stable{prior_epoch}`), forget the fork-recovery pending entry,
  `replay_buffered_messages()`.

**Control-flow inversion vs. Scramble's current model** (framing, 🟡): today
Scramble applies a commit locally and *then* publishes it; Dark Matter *stages*
the commit (OpenMLS `pending_commit`, never merged) and *only* merges on an
external confirm signal from the transport layer. Every outbound send path
becomes two-phase: `prepare -> stage -> [publish; wait for ack] -> confirm|fail`.
While a group has an in-flight pending publish, that group's `EpochState` is
`PendingPublish`/`Merging` and **no new local commit can be prepared for it**
(`begin_pending` requires `Stable`) — inbound messages are still accepted and
buffered (`can_ingest()` is true for `PendingPublish`? — **correction**: per
§4's table, `can_ingest()` is true only for `Stable`/`Recovering`; `PendingPublish`
buffers rather than rejects, per `convergence.md:84-86`: "While the lifecycle is
`PendingPublish` or `Merging`, inbound input is retained but the scheduler MUST
NOT admit it into a new pass"). On confirm/fail, `replay_buffered_messages()`
drains that backlog against the now-updated state.

Outbound gating tied to `ConvergenceStatus` (not just `EpochState`): 🟢
`docs/marmot-architecture/cgka-engine-canonicalization-contract.md:335-349` —
outbound intents (`SendAppMessage`, `CreateCommit`, `PublishProposal`) queue
while status is `Syncing`/`Resolving`/`Blocked`; app-message intents are
encrypted **only after** `Settled`; commit intents are **regenerated** (not
just released) after `Settled`, because any commit prepared before sync may
have targeted a stale epoch. `advance_convergence(group_id)` is the
application-facing pump that runs a pass and returns regenerated
`SendResult`s; a regenerated commit pauses further draining until its own
publish-confirm/fail resolves.

---

## 8. Conformance simulator and portable test vectors

🟢 **Upstream ships a real, dedicated conformance-simulator crate — this is
usable by Scramble, and convergence is one of its primary targets**, confirmed
by direct reads of `quality-and-vectors.md`, the crate's tree listing, and one
full sample vector JSON:

- `crates/cgka-conformance-simulator/` — a sibling crate with `TransportBus`
  (deterministic in-memory multi-client bus: seeded scheduling, partition/heal,
  delay/duplicate/reorder/drop faults), `HarnessClient` (wraps a real
  `Engine<SqliteAccountStorage>` behind the actual Nostr peeler — kind-445
  envelopes, NIP-59 gift wraps), `ScenarioSpec` v1 (serializable JSON,
  declarative multi-client scripts: `create_group`, `invite_members`,
  `update_group_data`, `send_app_message`, `leave`, `deliver_all`,
  `set_partition`, `duplicate_queued`, `reorder_queued`, `restart_client`, ...),
  and `VectorFixture` (portable JSON pairing a `ScenarioSpec` with either an
  exact `expected_trace` or a semantic `expected_outcomes`).
- `crates/cgka-conformance-simulator/vectors/manifest.v1.json` catalogs 25
  vectors, most marked `status: "portable"`. **Convergence-specific vectors
  exist**: `convergence-committer-selected.v1.json` and
  `convergence-witness-selected.v1.json` — read in full; example structure
  (`convergence-committer-selected.v1.json`, verbatim shape):
  ```json
  { "scenario_name": "convergence-committer-selected/v1",
    "conformance_version": "0.9.4",
    "scenario": { "clients": [...], "steps": [ {"type": "create_group", ...},
        {"type": "invite_members", ...}, {"type": "deliver_all"}, {"type": "tick", ...} ] },
    "expected_outcomes": [
      { "type": "convergence_decision", "client": "carol", "selected_tip_epoch": 2,
        "decisive_rule": "tip_committer", "witness_quorum_met": false },
      { "type": "pending_resolution", ... },
      { "type": "client_state", "client": "carol", "epoch": 2, "member_count": 4, ... } ] }
  ```
  This is a **directly portable format**: declarative scenario steps +
  semantic expected outcomes (not byte-exact — deliberately, since MLS
  signatures/HPKE ciphertexts/timestamps legitimately differ across
  independent implementations, per `quality-and-vectors.md:85-88`). A C# test
  harness that can (a) drive `Scramble.Marmot.Convergence` through the same
  step vocabulary and (b) assert the same `convergence_decision` /
  `decisive_rule` / `client_state` fields would directly consume these
  fixtures.
- Also present: generated adversarial chaos families
  (`convergence-chaos/v1`, 11 chaos classes incl. invite/group-data forks,
  publish rollback, partitions, 20+-client message storms, restart+duplicate
  delivery, with a greedy failure-minimizer), and a **Tamarin formal-verification
  model** of the selector (`formal/tamarin/distributed_convergence_v0.spthy`,
  15 lemmas incl. deterministic convergence, rewind-bound, bounded witness
  override, delivery-order robustness — `docs/marmot-architecture/distributed-convergence.md:245-300`).
- Gap upstream itself acknowledges (`quality-and-vectors.md:71-106`): no
  full byte-level wire-vector suite yet; whole-scenario byte stability is
  explicitly rejected as "not the right portability contract for MLS group
  histories."

**Recommendation**: mirror the `ScenarioSpec` + `VectorFixture` shape (not the
Rust code) as `Scramble.Marmot.Convergence`'s own test harness input format,
and treat `vectors/manifest.v1.json`'s convergence-tagged entries as a
starter conformance suite to port by hand (the JSON is small, ~2-3KB per
vector, and semantic — no Rust/OpenMLS dependency to satisfy).

**Amethyst cross-check** (scoped down per user direction): Amethyst
(`com.vitorpamplona.quartz.marmot`) implements its own, much simpler
same-epoch tiebreak (lowest `created_at` then lowest event id) — **not**
equivalent to Dark Matter's algorithm and not a usable independent
cross-check for this subsystem. 🟡 (one-line summary from prior research this
session; not re-verified in depth per scope-down instruction.)

---

## 9. What `Scramble.Marmot.Convergence` must contain

Module list (maps onto scoping doc §10's proposed layout), with plug-in points:

1. **`ConvergencePolicy` / `CanonicalizationPolicy`** — pinned v1 constants
   (§3.1), persisted per-group, loaded before any convergence work after
   restart (spec requirement: `canon-contract.md:201-206`). Validates
   `max_witness_override_depth <= max_rewind_commits` on set/load.
2. **`BranchCandidate` + scorer** — direct C# port of §3.2/3.4/3.5. This is
   the smallest, best-specified piece — pure functions, no I/O, ~150-250 LOC
   in Rust. **Low risk.**
3. **`CanonicalizationPipeline`** — dedupe → materialize-candidate-graph →
   attach-witnesses → select → build-result (§3.7), operating on the
   `PeeledMessage`/`CanonicalizationResult` contract types (§3.7-3.8). Plugs
   into `Scramble.Marmot.Engine`'s message-processor as the "slow path" convergence
   pass, driven by an `advance_convergence(group_id)`-shaped API (§7) called
   periodically and on quiescence.
4. **`CandidateMaterializer`** (the MLS-replay bridge, mdk's
   `openmls_projection`) — replays commit bytes against retained snapshots to
   produce `BranchCandidate`s without mutating live state. **Depends on the
   generic-MLS gap in §10 below — this is the module most likely to slip.**
5. **`ForkRecoveryManager`** ("fast path", §5) — optional for v1 (§3.6 shows
   same-epoch races are just a 1-branch case of the general algorithm; the
   fast path is a performance optimization over the slow path, not a
   distinct correctness requirement — see sequencing note below).
6. **`EpochStateMachine`** (§4) — the `EpochState` enum + atomic transition
   methods; owns the `PendingPublish`/`Merging`/`Recovering`/`Unrecoverable`
   lifecycle and the `committed_from` fork-detection bookkeeping. Plugs
   directly under `Scramble.Marmot.Engine` (per scoping §10, engine owns the
   epoch manager) but the transition *rules* themselves belong to Convergence
   since only convergence outcomes (fork detected / `MissingRetainedAnchor` /
   branch applied) legally drive `Recovering`/`Unrecoverable`/back-to-`Stable`.
7. **`PublishLifecycle`** (§7) — two-phase confirm/fail handlers; plugs into
   `Scramble.Marmot.Transport.Nostr`'s publish-ack callback per scoping §10
   ("publish-ack → engine `ConfirmPublished`").
8. **Retained-state store** — the snapshot/rollback + candidate-replay
   storage described in §5/§6; plugs into `Scramble.Marmot.Storage` (scoping
   §10 already calls out "pending-commit durability, routing-state history" —
   extend that scope to include full per-epoch group-state snapshots, not
   just message records). Worth copying two defensive RAII patterns found
   this session (🟢, `pending_commit_guard.rs`/`snapshot_guard.rs`, read by a
   parallel research pass): a **`PendingCommitCleanupGuard`** armed around
   every commit-staging send path so a cancelled/early-returned send can never
   leave an orphaned staged OpenMLS commit or an orphaned fork-recovery
   snapshot; and a **`SnapshotRollbackGuard`** (create-on-construction,
   explicit commit-or-Drop-rolls-back) around any probe/replay-and-discard
   sequence, so a panic mid-probe can't leave storage half-mutated. Both are
   small, mechanical patterns (RAII/`IDisposable` translates directly) worth
   adopting verbatim rather than re-deriving.
9. **Conformance harness** — a C# port of the `ScenarioSpec`/`VectorFixture`
   shape (§8), seeded off the convergence-tagged entries in
   `vectors/manifest.v1.json`.

**Sequencing note (🟡, a scoping recommendation, not a spec requirement)**:
because §3.6 shows same-epoch races are a degenerate 1-branch case of the
general branch-selection algorithm, a v1 `Scramble.Marmot.Convergence` could
plausibly implement **only** the slow/general path (module 3+4) and skip the
fast-path snapshot optimization (module 5) entirely for a first cut — every
race, same-epoch or multi-epoch, goes through the same candidate-materialize +
score pipeline. This trades some performance (no fast in-place resolution of
the common case) for a materially smaller v1 surface (one code path to build,
test, and get byte-compatible instead of two). Flag this as a build-order
decision for whoever plans the phased build order (scoping doc §9 step 5).

---

## 10. Dependencies on generic-MLS gaps

Cross-referencing scoping doc §12's four capabilities against what this
deep-dive found convergence actually needs:

| §12 item | Original framing | What convergence needs | Status |
|---|---|---|---|
| (a) opaque leaf/GroupContext extensions | ✅ present | Not directly used by convergence itself (used by app-components/identity-proof layers) | No new dependency found |
| (b) SelfRemove proposal | ❌ absent, MEDIUM | Convergence treats SelfRemove-only commits identically to any other `ordinary`-priority commit (§3.2) — no *convergence-specific* SelfRemove logic beyond the generic proposal type existing | Confirms MEDIUM estimate, no change |
| (c) PublicMessage produce/verify | ⚠️ partial, MEDIUM | Publish-before-apply (§7) stages a commit as `pending_commit` and needs to serialize/verify it as `PublicMessage` bytes for the `PURE_PLAINTEXT_WIRE_FORMAT_POLICY` Dark Matter uses (`wire_format.rs:1-33`) — **directly needed**, not incidental | **Elevates §12(c) from "needed for handshakes generally" to "needed specifically by the publish-before-apply flow convergence depends on"** |
| (d) retained past-epochs | ❌ absent, MEDIUM-LARGE, framed narrowly as "decrypt PrivateMessage from prior epoch" | Convergence needs OpenMLS-native `max_past_epochs` secret retention (§12(d)'s original framing — for app-payload witness decryption, §3.4). The second capability this row originally claimed (full per-epoch snapshot + restore + non-mutating replay-probe) is **not** a `dotnet-mls` gap — see correction below. | §12(d) stands as originally scoped (MEDIUM-LARGE, `max_past_epochs`-equivalent secret retention only) |

> **⚠ CORRECTION (2026-07, later session)** — the paragraph below and the
> "snapshot/restore" row above **overstated a dotnet-mls gap that does not
> exist.** `MlsGroup` already exposes `Export()`/`Import(byte[], ICipherSuite)`
> (`Group/MlsGroup.cs:2237,2245`) — a full-state serialize/deserialize into a
> **wholly independent instance** — plus an existing `Commit()` →
> `MergePendingCommit()`/`ClearPendingCommit()` stage/merge/discard model
> (`:285,550,572`) and a `ProcessCommit()` callable on any instance
> (`:591,604`). Together these already provide snapshot, restore, and
> non-mutating replay-probe (`Import()` into a throwaway instance, run
> `ProcessCommit` on it, drop it) with **zero `dotnet-mls` changes**. This
> capability belongs entirely in `Scramble.Marmot.Storage` as an application-level
> `SnapshotStore` wrapper. Full writeup, evidence, and scope caveats (what
> `Export`/`Import` do *not* round-trip: `_proposalCache`, PSK caches,
> `_pendingCommit`) in
> `scramble-marmot-snapshot-restore-spec-2026-07.md`. **This was the single
> biggest flagged unknown in §11 below — it is now retired as a library risk.**
> The original paragraph is struck through for the record:
>
> ~~**New permission-gated generic-MLS proposal to add to the queue** (per the
> architectural boundary rule — expressed as standard RFC-9420 mechanism, no
> Marmot constants): *"`MlsGroup` snapshot/restore: capture an opaque, restorable
> handle to a group's full state (ratchet tree, key schedule, extensions) at its
> current epoch; restore a group to a previously captured handle; process a
> commit against a captured handle without mutating the live group (non-committing
> probe)."* This is generic RFC-9420 state management, not Marmot-specific, and
> is a prerequisite for both fork recovery and candidate-branch materialization —
> i.e. **it blocks essentially all of `Scramble.Marmot.Convergence`'s slow path**,
> making it the single highest-priority `dotnet-mls` permission-gated proposal to
> raise with the user, ahead of (b) and (c).~~

---

## 11. Size / risk estimate

**Overall: L, confirmed (not trending XL) — the `openmls_projection.rs` read
is done (§13) and closes out the last size-driving unknown.** The original
driver — a claimed snapshot/restore/replay-probe gap in `dotnet-mls` — is
**retired** (see §10 correction; `scramble-marmot-snapshot-restore-spec-2026-07.md`).
The core scoring algorithm itself (§3) is small and precisely specified —
**not** the risk driver, and was never the risk driver. `CandidateMaterializer`
(§13) turns out to be real work — a BFS candidate-path search with a DoS-budget
guard, a subtle own-commit replay workaround, and a two-level crash-safe reorg
apply — but it is now *known* complexity, read and characterized line-by-line,
not *unknown* complexity. That is what moves the overall estimate off the
L-vs-XL fence and settles it at **L**.

Rough shape, assuming the sequencing recommendation in §9 (slow path only for
v1, no fast path):

| Piece | Size | Confidence |
|---|---|---|
| `BranchCandidate` scorer + policy (§3) | S | 🟢 spec + code both read in full and agree; near-zero ambiguity |
| `EpochStateMachine` (§4) | S-M | 🟢 full transition table read; atomicity discipline documented and copyable |
| `CanonicalizationPipeline` (§3.7-3.8, dedupe/materialize/witness/select/dispose) | M | 🟢 pipeline shape read in full; supersession rule (§3.8) is a known sharp edge with a documented upstream regression to test against |
| Publish-before-apply / two-phase send (§7) | M | 🟢 confirm/fail flows read in full; mainly a control-flow-inversion/threading exercise through the existing send/ingest paths |
| `CandidateMaterializer` (MLS replay bridge, §6, §13) | **L** | 🟢 `openmls_projection.rs` read in full (§13) — real complexity (candidate-path BFS with a DoS-replay budget, an own-commit replay workaround via confirm-time stamping + per-epoch retained snapshots, a crash-safe two-level reorg apply) but fully characterized end to end; no dotnet-mls gap, no remaining unknown-unknowns |
| Fork recovery fast path (§5) | (deferred per §9 sequencing) | — |
| Conformance harness port (§8) | S-M | 🟢 vector format read and is small/portable; mechanical port work |

**Top 2-3 unknowns that should gate a date-with-confidence-band:**

1. ~~**🔴 The `dotnet-mls` snapshot/restore/non-mutating-replay-probe
   capability (§6, §10)**~~ **RETIRED (2026-07) — see §10 correction and
   `scramble-marmot-snapshot-restore-spec-2026-07.md`.** `MlsGroup.Export()`/
   `Import()` already provide this; it's ordinary `Scramble.Marmot` application
   code (S-M), not a `dotnet-mls` gap.
2. ~~**🔴 `openmls_projection.rs` (93KB, not read)** — the actual shape of the
   MLS-replay bridge in the reference implementation.~~ **RESOLVED (2026-07,
   later session) — see §13.** Read in full, 2322 lines. Sizes to
   `CandidateMaterializer: L` (table above). No new `dotnet-mls` gap surfaced
   — the module builds entirely on the already-available `Export()`/
   `Import()`/`ProcessCommit()` primitives (§10 correction). One existing item
   is confirmed more load-bearing than previously scoped, not new: §12(b)'s
   closed `ProposalType` enum gap also blocks Marmot's `AppDataUpdate`
   app-component proposal, not just `SelfRemove` (§13). A second, genuinely
   new but *answered* (not blocking) finding: `dotnet-mls`'s `ProcessCommit`
   applies atomically in place with no stage-then-inspect-before-merge step
   (confirmed by direct read, `lib/dotnet-mls/.../MlsGroup.cs:591-1102`) —
   unlike OpenMLS's `process_message` → `StagedCommitMessage` →
   `merge_staged_commit` split that mdk uses to run Marmot-policy checks
   (admin-gating, app-component integrity, identity-proof) *before* committing
   to a candidate. Workable today via `Export()`-before / `Import()`-to-discard
   around `ProcessCommit`, so **not a new permission-gated ask** — but it's a
   concrete shared implementation pattern `Scramble.Marmot` needs once, used by
   both live ingest and `CandidateMaterializer` (§13).
3. **🟡 Byte-exact `tip_digest`/`CommitOrderingKey` determinism** across a
   differently-implemented MLS stack (dotnet-mls's TLS codec vs. OpenMLS's) —
   spec claims determinism follows from "Marmot pins one handshake wire
   format" (`convergence.md:225-227`), which should hold given dotnet-mls's
   codec is already TLS-canonical (scoping doc §4, "SURVIVES — exact match"),
   but this specific claim (commit-serialization byte-identity across the two
   MLS implementations) was not independently verified this session and is
   worth a targeted round-trip test early, since a single canonical-id
   mismatch fails silently as "always picks the wrong branch," not as a crash.
4. (Minor, 🟡) Whether the fast-path/slow-path split (§1, §9) is worth
   building for v1 or can be deferred — a scoping decision, not a technical
   unknown, but it changes near-term size by roughly one module (§9 item 5).

---

## 12. Summary for the reader in a hurry

- The **branch-selection algorithm** (witness quorum + rewind horizon + tip
  priority + lexicographic digest tiebreak) is small, fully specified, and
  the spec text and Rust code agree exactly — **this is not the hard part**,
  contrary to how it was originally framed as "the hardest module." A faithful
  C# port of §3 alone is an S-sized, low-risk task.
- ~~The **actual hard part**, newly surfaced this session, is that both of
  Dark Matter's convergence mechanisms... depend on a generic MLS capability
  dotnet-mls does not have...~~ **RETIRED (2026-07).** `MlsGroup` already
  exposes `Export()`/`Import()` plus a stage/merge/discard commit model —
  snapshot/restore/non-mutating-probe is buildable entirely in
  `Scramble.Marmot` with zero `dotnet-mls` changes. See
  `scramble-marmot-snapshot-restore-spec-2026-07.md`. **`openmls_projection.rs`
  has since been read in full (§13, later session)** — `CandidateMaterializer`
  sizes to **L**: real complexity (a candidate-path BFS with a DoS-replay
  budget, an own-commit replay workaround, a crash-safe two-level reorg apply)
  but fully characterized, with no new `dotnet-mls` gap. The overall
  convergence-subsystem estimate settles at **L**, not XL.
- Upstream ships a genuinely useful, **portable, semantic** conformance-vector
  format (JSON `ScenarioSpec`/`VectorFixture`, including convergence-specific
  vectors) that Scramble can mirror cheaply — this de-risks *testing* the C#
  port even though it doesn't de-risk *building* it.

---

## 13. `openmls_projection.rs` read — `CandidateMaterializer` resize (2026-07, later session)

**Source:** `gh api repos/marmot-protocol/mdk/contents/crates/cgka-engine/src/openmls_projection.rs?ref=v0.9.4`,
base64-decoded, read in full — all 2322 lines (93KB), including the two
`#[cfg(test)]` modules at the tail. Cross-referenced against
`lib/dotnet-mls/src/DotnetMls/Group/MlsGroup.cs` (`ProcessCommit`/
`ProcessCommitCore`, read directly this session) to check one specific,
checkable claim (see §13.4). Line numbers below are the fetched file's own
line numbers (`crates/cgka-engine/src/openmls_projection.rs:N`) unless
otherwise noted.

### 13.1 Public entry points

🟢 Seven public functions, all generic over `S: StorageProvider` except the
first (pure bytes):

| Function | Signature (elided generics) | Role |
|---|---|---|
| `project_mls_message` (:421) | `(bytes: &[u8]) -> Result<OpenMlsMessageProjection>` | Bytes-only classify: TLS-decode an `MlsMessageIn`, return `{kind: Application\|Proposal\|Commit\|Welcome\|Other, source_epoch, message_digest}`. No storage, no group. |
| `replay_openmls_messages` (:458) | `(storage, group_id, messages: &[TransportMessage]) -> Result<Vec<OpenMlsReplayObservation>>` | Non-mutating probe: RAII-snapshot the group, replay `messages` against it, always roll back (`SnapshotRollbackGuard`, :477-492). |
| `materialize_openmls_candidate_paths` (:495) | `(storage, group_id, paths: &[OpenMlsCandidatePath]) -> Result<Vec<OpenMlsMaterializedCandidate>>` | For each candidate path (a message sequence), replay it (via the function above) and reduce the observations to one `BranchCandidate`-shaped struct. Unlimited replay budget — used directly by conformance vectors/tests. |
| `canonicalize_openmls_batch` (:587) | `(storage, group_id, batch: OpenMlsCanonicalizationBatch) -> Result<CanonicalizationResult>` | Lower-level entry: caller supplies `candidate_paths` explicitly (not derived from stored messages); materializes them and hands off to the bytes-agnostic `canonicalization::canonicalize_with_materialized_candidates`. |
| **`canonicalize_stored_openmls_messages`** (:661) | `(storage, group_id, state, outbound_intents, policy, now_ms) -> Result<CanonicalizationResult>` | **The production entry point** — this is the function `distributed_convergence.rs` calls (confirmed in §2.2 of this doc). Reads every stored message row for the group, splits into commit/pending/app buckets, decides whether the pass needs a historical/multi-epoch rewind or can run from the current tip, and either way returns a plan (`CanonicalizationResult`) — **does not mutate anything itself** (see §13.3). |
| **`apply_openmls_canonicalization_result`** (:1147) | `(storage, group_id, result: &CanonicalizationResult, max_retained_anchor_rewind: u64) -> Result<Vec<OpenMlsReplayObservation>>` | **The only function in this file that mutates live group state.** Takes the plan from the function above and actually applies it — see §13.3, the most complex single function in the file. |
| `persist_openmls_canonicalization_dispositions` (:1228) | `(storage, result: &CanonicalizationResult) -> Result<()>` | Writes the `MessageState` (`Processed`/`EpochInvalidated`/`Retryable`/`Failed`) for every message the result touched. Called internally by the apply function but also exposed standalone — 🟡 inferred to be reusable by the fast-path (`fork_recovery.rs`) for the same bookkeeping, not independently confirmed by reading that file this session. |

`canonicalize_stored_openmls_messages` / `apply_openmls_canonicalization_result`
being defined here rather than in `canonicalization.rs` or
`distributed_convergence.rs` matches — and is now fully confirmed by a direct
read rather than a cross-reference — the three-way split this doc already
recorded at §2.2: `canonicalization.rs` is pure/bytes-agnostic policy, this
file is the bytes-first bridge, `distributed_convergence.rs` is engine-level
orchestration.

### 13.2 How it bridges OpenMLS to the candidate-graph model

🟢 The bridge is a **replay-and-observe** pattern, not a data-conversion
pattern. There is no function that takes an OpenMLS object and maps its
fields onto a `BranchCandidate`; instead:

1. A **candidate path** (`OpenMlsCandidatePath { branch_id, messages: Vec<TransportMessage> }`)
   is a hypothesis: "if I replay this Commit sequence starting from the
   retained anchor at its fork epoch, what state results?"
2. `build_stored_openmls_candidate_paths` (:971-1084) is a **BFS/worklist
   graph search** over the stored commit rows: starting from a seed empty
   path at the anchor epoch, it repeatedly tries to extend every frontier
   path with every stored commit whose `source_epoch` matches the path's
   current tip, probing each extension via a real replay
   (`probe_candidate_path` → `materialize_openmls_candidate_paths_budgeted`
   → `replay_openmls_messages_prevalidated`, a throwaway-snapshot round trip
   each time), and keeps only extensions that actually validate. Paths that
   can't be extended further become "completed" candidate paths. This is a
   real graph-construction algorithm, not a lookup — the candidate graph in
   mdk's convergence model is *discovered by repeated MLS replay*, not
   pre-existing structure.
3. `materialize_openmls_candidate_paths_budgeted` (:509-585) turns the
   observation list from one successful replay into an
   `OpenMlsMaterializedCandidate` by scanning for `CommitStaged` observations
   and pulling `fork_epoch`/`tip_epoch`/`tip_priority`/`tip_committer`/
   `tip_digest` straight off them — `OpenMlsMaterializedCandidate::branch_candidate()`
   (:75-85) is the actual one-line struct-literal conversion into
   `convergence::BranchCandidate` (per this doc's §3.2). **This is the only
   place an actual field-by-field "map onto BranchCandidate" happens, and
   it's trivial** — all the real work is producing the *inputs* to that
   struct literal via replay.
4. **Bounded by design**: a `ReplayBudget` (:158-202) caps total replay round
   trips per pass at `commits × (max_rewind_commits + 1) × 4 + 32`
   (`CANDIDATE_REPLAY_BUDGET_SLACK = 4`, `_FLOOR = 32`, :168-170), fails
   closed with `ReplayBudgetExceeded` rather than degrading silently — an
   explicit anti-DoS bound on the BFS "so attacker-driven same-epoch commit
   branching cannot amplify into unbounded CPU/IO" (:158-160, referencing
   upstream issue #635). This is a **specific, concrete edge case a C# port
   must reproduce**, not an incidental detail — without it, `Scramble.Marmot`'s
   BFS is a `B^D` DoS vector on adversarial input.

### 13.3 Duplication vs. orchestration — and the reorg-apply machinery

🟢 **No MLS cryptographic/tree/key-schedule logic is reimplemented.** Every
byte of signature verification, path-secret derivation, tree-hash computation,
and confirmation-tag checking stays inside OpenMLS's `process_message`/
`unprotect_message`/`merge_staged_commit`. This file only ever calls into
OpenMLS, never re-derives MLS state by hand — confirming the "purely
orchestrates" half of the question.

But it is **not thin orchestration** either — three genuinely non-trivial
things happen on top of the OpenMLS calls, all Marmot-specific, none
delegable to a generic MLS library:

**(a) A second validation layer runs on every replayed commit.**
`process_openmls_messages_inner` (:1782-2054), on a `StagedCommitMessage`
(:1916-2005), calls out to four separate Marmot policy checks *before*
deciding to `merge_staged_commit`: `app_components::require_admin_for_staged_commit`
(admin-gating), `validate_admin_leaf_coupling_for_staged_commit`,
`validate_app_component_integrity_for_staged_commit`, and
`account_identity_proof::validate_staged_commit_account_identity_proofs`
(:1917-1983). Any failure returns `UnauthorizedCommit`/`InvalidCommit` — the
candidate path is dropped without merging (surfaces as a `DroppedMessage` at
the BFS level, :1028-1043). **These are the same helper functions the live
inbound-ingest path calls** (🟡 inferred from naming/signature match, not
independently confirmed by reading `message_processor/ingest.rs` this
session, which this doc's §2.2 already flagged as ungrepped) — i.e. mdk
shares one set of Marmot-policy validators between live ingest and replay,
which is the right design for `Scramble.Marmot` to copy rather than
duplicating admin/identity/app-component checks per call site.

**(b) The own-commit-cannot-self-process workaround.** This is the single
most interesting mechanism in the file and was not anticipated by this doc's
earlier sections. MLS's `process_message` receiver path cannot process a
device's own already-authored commit — not an implementation quirk but an
**RFC 9420 protocol property**: the committer directly derives its new path
secrets when it *creates* a commit, and the wire-format `UpdatePath` only
encrypts to *other* members' copath nodes, never back to the sender's own
leaf, so there is nothing for the committer to decrypt on replay. 🟢
confirmed identically true of `dotnet-mls` by direct read (§13.4) — this is
not an OpenMLS-specific limitation, it will bite any RFC 9420 implementation.

This matters because candidate materialization must sometimes replay a path
containing *this device's own* confirmed commit (e.g. rebuilding a branch
after restart, or verifying a branch that happens to include your own tip).
mdk's fix (`PrevalidatedOwnCommits`, :320-364; `own_commit_stamp`, :369-386;
the splice at :1817-1864):
1. **At confirm time** (in `publish.rs`, not this file — but the stamp type
   and builder live here), while the staged commit is still attached, capture
   an `OwnCommitConvergenceStamp { committer, priority, consumed_proposal_refs }`
   — this doc's §7 already flagged *that* this capture must happen at confirm
   time ("MLS refuses to process own commits... this is the only durable
   source"); this session locates the *exact* mechanism.
2. Persist the stamp alongside the commit's stored wire row
   (`stamp_processed_own_commit_record`, :392-419, upgrades the row to
   `StoredMessagePayload::OwnCommitWire`).
3. **At replay time**, if the next path-message digest matches a stamped own
   commit *and* every commit replayed so far in the path stayed canonical
   (`prefix_canonical`, :1799-1826) — i.e., the own commit's stamp is only
   trustworthy if nothing before it on this path diverged from what actually
   produced that commit — **skip `process_message` entirely** and instead
   roll the live-loaded group forward to the **retained-anchor snapshot at
   the commit's resulting epoch** (`retained_anchor_snapshot_name(epoch)`,
   reloading the `MlsGroup` from storage after rollback, :1834-1854),
   synthesizing the `CommitStaged` observation from the stamp rather than
   from a fresh MLS-verified replay.

**Implication for `Scramble.Marmot.Storage`'s snapshot design**: this needs a
retained-anchor snapshot **at every settled epoch within the rewind window**,
not just at fork points — the earlier `scramble-marmot-snapshot-restore-spec-2026-07.md`
mostly discusses a single anchor/probe pattern; this file confirms mdk
actually retains a rolling *window* of per-epoch snapshots
(`retain_current_group_epoch_snapshot` / `prune_retained_anchor_snapshots`,
:1350-1391, pruned to `retained_epoch - max_rewind_commits`). This is a
refinement of that spec's storage scope, not a contradiction of it — same
`Export()`/`Import()` primitives, just "one snapshot per epoch in the
window" instead of "one snapshot." 🟢

**(c) A crash-safe, reorg-capable two-level apply.** `apply_openmls_canonicalization_result`
(:1147-1226) is the most structurally complex function in the file:
- Computes the **already-applied prefix** (:1272-1297) — leading `Processed`
  commits below the live tip, which includes this device's own commits and
  therefore cannot be re-replayed (same protocol constraint as (b)) — and
  skips replaying them.
- Determines `apply_start_epoch`: usually the current live epoch (normal
  forward apply), but if the winning branch's first *new* commit sits at an
  epoch *below* the current live tip, this is a genuine **reorg**: the
  function rolls the live group backward to that epoch's retained anchor and
  restores the message/queue records the anchor didn't originally carry
  (:1186-1206) — i.e. it can discard already-applied, already-confirmed local
  state, including the device's own previously-applied commit, in favor of a
  different branch. This is the concrete mechanism behind this doc's §3.8
  "supersession" rule (the issue #363 regression it references).
- Wraps the whole thing in an **outer** full-group storage snapshot
  (create → apply → release-on-success / rollback-on-error, :1181-1226) for
  in-process error recovery, plus an **inner** `storage.with_transaction`
  (:1411-1425) around the actual OpenMLS mutation + group-record refresh +
  disposition writes, explicitly to guard against a **hard crash mid-merge**
  (SIGKILL/OOM/power loss) leaving the persisted group torn between epochs —
  documented inline as fixing upstream issues #157/#424 (:1399-1410). Two
  distinct failure modes (in-process error vs. hard crash), two distinct
  guards, deliberately not nested into one.

None of (a)-(c) exists anywhere else in the reference — this file is where
all three live. It is a genuine integration hub: besides OpenMLS itself, it
calls into `app_components`, `account_identity_proof`, `identity`, and
`group_lifecycle` (:1500, mirroring app-component state into the Marmot group
record post-replay) — i.e. it is the one place that exercises nearly every
other Marmot subsystem during a convergence pass.

### 13.4 What it needs from OpenMLS beyond the already-covered primitives

Re-checked against `scramble-marmot-snapshot-restore-spec-2026-07.md`'s list
(`Export`/`Import`/`Commit`/`MergePendingCommit`/`ClearPendingCommit`/
`ProcessCommit`) plus scoping doc §12:

- **`store_pending_proposal`-equivalent** (mdk :1905-1909, caching an observed
  bare Proposal so a later Commit in the same replay pass can resolve it by
  reference) — 🟢 already covered: this is exactly `dotnet-mls`'s
  `CacheProposal(PublicMessage)` (`MlsGroup.cs:645-664`), already flagged in
  scoping doc §12(c) ("Consume proposal: UNVERIFIED — does no sig/membership
  check, assumes pre-verified"). No new need; the existing §12(c) gap (produce
  PublicMessage commit/proposal + verify-on-consume) is what's still open,
  unchanged by this read.
- **A custom `AppDataUpdate` proposal type**, threaded through a *separate*,
  lower-level OpenMLS API (`unprotect_message` → inspect
  `committed_proposals()` → validate/build an `app_data_dictionary_updater`
  → `process_unverified_message_with_app_data_updates`, all in
  `process_commit_with_app_data_updates`, :2105-2169) rather than plain
  `process_message`. This confirms mdk's OpenMLS dependency is a **fork with
  Marmot-specific proposal/component support baked into the library layer**
  (`openmls::component::ComponentData`, `AppDataUpdateOperation`) — not
  vanilla RFC 9420. Per this project's architectural boundary rule ("no
  Marmot leaks into `dotnet-mls`"), `Scramble.Marmot` must NOT replicate that
  fork; instead it needs `dotnet-mls`'s **generic** custom-proposal
  extensibility (scoping doc §12(b), currently ❌ absent — closed
  `ProposalType` enum) to carry its own app-component update proposal as
  opaque bytes, with all `AppDataUpdate` semantics/validation staying in
  `Scramble.Marmot`. **This is not a new gap** — §12(b) already flagged the
  closed-enum problem at MEDIUM size — **but this session confirms it's
  load-bearing for the whole app-components subsystem, not just `SelfRemove`
  as originally framed**, which raises its priority among the permission-gated
  `dotnet-mls` asks without changing its size estimate.
- **Stage-then-inspect-before-merge for inbound commits** — 🟢 genuinely new
  observation this session, confirmed by direct code comparison. OpenMLS's
  `process_message` on a Commit returns a `StagedCommitMessage` (:1916) that
  mdk inspects and can reject *without merging* (§13.3(a)). `dotnet-mls`'s
  `ProcessCommit(PrivateMessage)` / `ProcessCommit(PublicMessage)`
  (`MlsGroup.cs:591,604` → `ProcessCommitCore:670-1102`) has **no equivalent
  staging point** — it validates cryptographically (signature, confirmation
  tag) and then applies directly in the same call (`MlsGroup.cs:1091-1101`,
  "Apply the new state" mutates `_tree`/`_epoch`/`_keySchedule`/... unconditionally
  once the crypto checks pass; there is no returned staged object to inspect
  Marmot-policy questions against before that mutation happens). **This is
  workable without a `dotnet-mls` change**: because `Export()`/`Import()`
  already give a full undo, `Scramble.Marmot` can snapshot
  (`bytes = group.Export()`) immediately before calling `ProcessCommit`, and
  on a post-hoc Marmot-policy rejection, discard the mutated instance and
  reload from `bytes` instead of continuing to use it — the mutation already
  happened, but it's cheaply undoable. **Not a new permission-gated
  `dotnet-mls` ask** — but a concrete implementation pattern
  `Scramble.Marmot` needs to build once and share between live inbound
  processing and `CandidateMaterializer`'s replay path, since both need
  "process, then possibly reject without keeping the mutation."
- **Own-commit-cannot-self-process** — 🟢 independently confirmed as a
  protocol-level fact in `dotnet-mls` too, not just inferred from mdk's
  workaround existing. `ProcessCommitCore`'s path-secret decryption step
  (`MlsGroup.cs:944-981`) searches the *filtered copath* for a node the
  caller holds a private key for; a commit's own committer is by construction
  excluded from its own `UpdatePath`'s encryption targets, so this search
  cannot succeed for a device's own commit and the method throws
  (`"Cannot find decryptable path secret..."`, :979-981). So
  `Scramble.Marmot.Convergence`'s `CandidateMaterializer` needs the same
  own-commit-stamp-and-splice workaround as mdk (§13.3(b)) — a mechanical
  port, not a new library requirement, since `dotnet-mls`'s failure mode here
  is identical in kind to OpenMLS's.

**Net: no new `dotnet-mls` permission-gated proposal.** One existing item
(§12(b), closed `ProposalType` enum) is confirmed to matter more broadly than
previously scoped. Everything else this file needs is either already
available (`Export`/`Import`/`ProcessCommit`/`CacheProposal`) or buildable in
`Scramble.Marmot` using those primitives (own-commit splice, reject-via-
Export/Import-discard).

### 13.5 Surprising complexity — flags for whoever builds `CandidateMaterializer`

- **The replay budget is load-bearing, not decorative** — §13.2 point 4. A
  port that omits it is a DoS vector, not just a missed optimization.
- **Two different "temporarily rewind, always restore" shapes exist and must
  not be confused**: `canonicalize_stored_openmls_messages_from_retained_anchor`
  (:821-853) rewinds to compute a *plan* and unconditionally restores the live
  snapshot afterward, whatever the outcome (`canonicalize_*` never mutates
  live state — confirmed by re-reading; the whole "historical" branch is
  itself just a bigger read-only probe). `apply_openmls_canonicalization_result`
  (§13.3(c)) is the only function that keeps a rewind (a real reorg) instead
  of always restoring. Conflating "compute" and "apply" here would silently
  turn every convergence pass into a mutation.
- **A reuse/skip-replay optimization exists** (`can_reuse_bfs_materialization`,
  :872-878, upstream issue #635): when the BFS already fully materialized
  every candidate path and there are no pending application messages, the
  canonicalization core reuses those materialized candidates instead of
  replaying every completed path a second time. This is explicitly **only a
  performance optimization** (the doc comment says so directly, :855-871) —
  🟡 recommend treating it as deferrable for a `Scramble.Marmot` v1, same
  disposition this doc already gave the fast-path/slow-path split (§9
  sequencing note): correctness doesn't depend on it, a v1 port can always
  full-materialize and add the reuse path later as a perf pass.
- **App-message replay failure handling is deliberately permissive in one
  direction, strict in the other** (:1870-1897): a `ValidationError` or
  `UseAfterEviction` while replaying an application message against a
  candidate branch is treated as "this message doesn't belong on this
  branch" (`Ignored`, not fatal) — but any *other* error propagates as a real
  bug. Getting this match arm's error-class boundary wrong in a C# port would
  either mask genuine corruption or make every branch probe fragile to
  ordinary cross-branch app messages.
- **Historical-vs-current dispatch has a subtlety**: `historical_replay_start_epoch`
  (:922-933) only considers *unresolved* commits (`Sent`/`Created`/`Retryable`)
  strictly below the current epoch; if the earliest unresolved commit is
  actually at-or-above current epoch, the whole pass runs the (cheaper)
  "from current" path even though older, *already-`Processed`* commits exist
  in storage — those are read for own-commit prevalidation bookkeeping only,
  never re-walked as a rewind trigger. Easy to get backwards in a reimplementation.

### 13.6 `CandidateMaterializer` size — final

**L** (up from the *provisional* "S-M, ordinary application code" framing the
snapshot-restore-spec doc's §10 correction implied once the dotnet-mls gap was
retired — that framing was correct that no library change is needed, but this
read shows the module built on top is not small). Confidence 🟢 — full line-
by-line read, cross-checked one concrete claim against `dotnet-mls` directly.
Composition:
- Bytes classification + trivial `BranchCandidate` struct-literal conversion: S.
- Candidate-path BFS + replay-budget DoS guard: M — a real graph-search
  algorithm, well-specified now, mechanical but not small to port and test.
- Own-commit replay workaround (stamp capture, persistence, prefix-canonical
  tracking, per-epoch retained-anchor snapshot window): M — subtle, easy to
  get wrong, but now fully specified rather than an open question.
- Shared Marmot-policy validation hook (admin/leaf-coupling/app-component/
  identity-proof) called from replay: mostly counted against the
  app-components/identity-proof modules' own estimates (out of scope here per
  this doc's original framing) — this file's marginal cost is just the call
  sites and the reject-without-merge plumbing (§13.4), not the validators
  themselves.
- Two-level crash-safe reorg apply (outer snapshot, inner transaction,
  backward-rewind support, disposition persistence): M-L on its own — the
  single most structurally involved piece in the file.

No sub-piece is individually XL, and none is an open unknown anymore; the
sum is a solid **L**, matching the revised table in §11.
