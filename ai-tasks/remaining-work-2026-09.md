# Remaining work — Dark Matter, 2026-09-14

A snapshot of everything left, in dependency order. **Provenance lives
elsewhere**: `scramble-marmot-phased-plan-2026-08.md` is the authoritative plan
and `HANDOFF-dark-matter.md` is the running record. This file exists to answer
one question the other two cannot — *what is actually left, and what blocks
what* — and it should be deleted or rewritten rather than allowed to drift.

---

## 0. The finding that shaped everything below — now addressed, with debt

`8ef1729` landed the session layer. **It carries debt that is real and must not
be forgotten**, so it is stated here as well as in the commit:

- ~~The mutation pass was never finished~~ **done** (`798a65e`). Nine mutations,
  five survivors. Three were one hole — nothing drove a reorg through
  `ConvergeAsync`, so keeping the pre-reorg group, never persisting the move and
  skipping the replay it owes all passed untouched; two tests close them. Two
  got a verdict instead of a test: the Abandon-path attempt clear is hygiene
  (labelled), and **`AdoptAsync`'s write ordering is load-bearing and still
  uncovered** — see below.
- ~~Two known defects in `OpenAsync` / `Restore`~~ **fixed** (`eab4303`), with
  the three-way refusal agreed in §7.
- It landed as one ~15-file commit under a recorded
  `Landing-Discipline-Exempt` trailer rather than an unverifiable split.

**One thing is now owed that was not visible before.** Three ordering claims —
`AdoptAsync`'s record-before-archive, and the equivalents in `CommitPublisher`
and `DurableEpochManager` — are each load-bearing and each untestable with what
exists, because the difference only shows in a crash between two storage writes.
**A storage provider that fails one nominated call would cover all three**, and
is small. Until it exists those three comments say plainly that they are
uncovered, rather than reading as verified.

The original finding, kept because it explains why this mattered:

## 0a. The finding

**The engine is essentially complete and nothing calls it.** Verified, not
assumed: no file outside `src/Scramble.Marmot.*` references the engine at all.

That is expected — the build has been deliberately additive so it cannot break
the shipping product. What is *not* expected is that **three separate phases are
each blocked on the same unscoped piece of work**:

- **P9's session-open hydration** needs something that owns opening a group.
- **P9's crash recovery** needs something that owns publishing one.
- **P11's cutover** needs something for `Scramble.Core` to talk to.

Call it the **session layer**: the thing that holds a live group, hydrates it at
open, archives it when it commits, runs a convergence pass when commits are
outstanding, replays what a reorg made readable, and drives publish through to
confirmation. Every engine component it would compose already exists and is
tested. None of them has a caller.

**No phase owns it.** This is the same shape as the convergence drain in §3y —
a piece everything depends on that fell between two phase boundaries — and it is
worth naming before it is discovered a third time.

A concrete illustration. `CrashRecoveryTests.NothingPersistsTheStateAGroupIsActuallyIn`
fails today, and the mechanism it needs is already built: `EpochArchive` persists
exported MLS state per epoch, and `CaptureIfAbsentAsync` covers the epoch a group
is created or joined at. The only gap is that **our own commits are never
archived**, because `StagedCommit.Applied()` is synchronous and holds no storage.
The durable-live-state problem is not a missing mechanism. It is a missing
caller.

---

## 1. P9 — Hardening (in progress)

| Item | State |
|---|---|
| Deferred-peel retry lifecycle + flood cap | **done** (`1d210a5`); the retry budget was dropped with its reasoning recorded |
| Epoch-state persistence | **done** (`b47f438`) |
| Durable publish intent | **done** (`bef320a`) — `CommitPublisher` owns the `Publishing()` seam; `ClassifyAsync` returns the Abandon / Reconcile / Adopt verdict item 3 needs |
| Session-open hydration | **landed** (`8ef1729`) — `MarmotSessionHost` / `MarmotSession`; live MLS state now persists on the group record |
| Stranded-pending-commit crash recovery | **landed** (`8ef1729`) — both P9 crash criteria green in `CrashRecoveryTests` |
| Quarantine | **dropped 2026-09-14**, and the decision was already made once — see §7. Replaced by an S-sized legibility fix on `OpenAsync`. |
| Snapshot-fallback peel | **not started**. `ISnapshotStorage` exists and nothing peels through it. This is the epoch-boundary case: a kind-445 sealed under an exporter secret from an epoch we have left. |
| Queued-intent drain polish | **misnamed**: there is no drain to polish, and no send path at all. See §8. |

**Exit criterion, from the plan:** kill between stage and confirm, and between
confirm and merge, and the group comes back consistent.
`tests/Scramble.Marmot.Tests/CrashRecoveryTests.cs` states both as failing tests
already and is deliberately uncommitted until it can pass — a red test on the
branch would break the gate, and marking it skipped is not available to us
(§3t: a skip reads as a pass).

---

## 2. P10 — Capabilities / feature registry

**Smaller than planned.** Two of its three parts are already resolved:

- The minimal static registry landed as a P4/P6 prerequisite — `CurrentProfile`,
  `RequiredCapabilities`, and component negotiation in `MarmotGroupBuilder` /
  `MarmotGroupProfile`.
- **`legacy_compatibility_profile` handling is dead**, not deferred. The plan
  made it conditional on §5 Q2, which is closed, and the 2026-09-13 scope
  decision — Dark Matter only, no dual-running window, backwards compatibility
  is not a goal — removes it for good.

**Left:** the profile upgrade flow, and making capability-mismatch rejections
match mdk's error taxonomy.

---

## 3. P11 — Cutover (the one with real exposure)

`CLAUDE.md` already calls this "the only phase with real I4/I5 exposure", and
the audit sharpens why. It replaces `marmot-cs` behind five `Scramble.Core`
services, and **four of the five are on the repo's own high-risk table**:

| File | Fix-density |
|---|---|
| `Services/ExternalSignerService.cs` | **0.59** |
| `Services/NostrService.cs` | 0.52 |
| `Services/ManagedMlsService.cs` | 0.44 |
| `Services/MessageService.cs` | 0.34 |
| `Services/EncryptedSqliteStorageProvider.cs` | — |

Plus both UI heads, and a data migration for existing local groups.

**It cannot start before §0 and §8 exist.** "Replace `marmot-cs` behind Core's
services" presumes something for those services to call — and a service that
cannot send a message has nothing to swap in.

Two invariants bind here specifically and should be planned for rather than
discovered: **I4** (no flag-day rewrites — split the refactor from the feature,
and eight files is the line) and **I5** (pivot freeze — the other UI head goes
bugfix-only until the first has an equivalent smoke test green plus a week).
ANALYSIS.md's worst two-week stretch in this repo's history was a pivot landed
without that discipline.

---

## 4. P12 — Deferred features

Sequenced after cutover. Partially built already, which is worth knowing before
estimating:

- **Disband** — the `0x800c` *state* is built and mandatory (§3h). The
  *protocol* is what remains.
- **KeyPackage maintenance + durable transport fanout** — `KeyPackagePublisher`
  and `KeyPackageLifetimePolicy` exist; the maintenance loop does not.
- **Media components** — ids and policies exist (`EncryptedMediaPolicy`,
  `0x8008` frozen, `0x800b` live); carriage and the app flow do not.
- **QUIC stream policy** — `AgentTextStreamPolicy` and `0x8006` exist; the
  transport does not.

---

## 5. Not code, and not blocked on us

- **Ask Whitenoise §5 Q4** — is there a freeze point or wire-stable tag before
  the flip. Per §7 this is the single biggest lever on the date band, and the
  band is wide because of upstream velocity rather than any unknown work.
- **§5a may not need asking at all.** Probed against `wn 0.9.21` on 2026-09-14
  (plan §5a): the peer no longer rewinds to the fork epoch and stop — that was
  `0.9.20` behaviour and it is gone. The two still do not converge, but the
  likely cause is now on our side: we choose once and never look again, while
  the peer's branch keeps growing deeper, and depth outranks every tie-break.
  **Re-run the probe after the session layer exists, before spending a question
  on Max.**
- **Watch the interop step's cost in CI** (§3g).
- **Two storage gaps found while reviewing, neither urgent.** The snapshot
  capture/restore in `SqliteMarmotStorageProvider.Snapshots.cs` covers groups,
  messages, intents and leave requests only — so a rollback leaves an
  `epoch_states` or `commit_publish_attempts` row describing a commit from a
  future the group no longer has. Nothing in the file says the omission is
  deliberate, which is the part that makes it look like a gap rather than a
  choice. And `IOutboundIntentStorage` is now dead code twice over: it has a
  table, two indexes and snapshot plumbing, and no production caller — worth
  deciding whether it is still the intended design for app-message sends before
  it accretes more support.
- **The branch is ~125 commits ahead of `master` with no PR.** Recorded because
  I4 names exactly this shape as the risk; the decision not to open one is the
  user's and is not being re-litigated.

---

## 6. Suggested order, and why

1. ~~Finish the publish-intent piece~~ **done.** Recovery can now tell "never
   left this device" from "may be on a relay" from "the relay took it" —
   `CommitPublisher.ClassifyAsync` answers Abandon / Reconcile / Adopt, which
   is exactly the discrimination `CrashRecoveryTests` needs.
2. ~~Scope and build the session layer (§0)~~ **done**, with its mutation pass
   finished and both `OpenAsync` defects fixed. Hydration and crash recovery
   came with it: P9's exit criterion holds.
3. ~~Build the send path (§8)~~ **done** (`4fcbf74`), split-and-mutate: brief,
   implementer, independent tests, then twelve mutations — all caught.
   `IOutboundIntentStorage` has a caller at last.
4. **A storage double that fails one nominated call.** Small, and it now closes
   **four** ordering claims — `AdoptAsync`, `CommitPublisher`,
   `DurableEpochManager`, and the send path's ratchet persistence — each
   load-bearing, each currently uncovered, each only observable in a crash
   between two writes. This is the best value left on the list.
5. **P9's remainder** — snapshot-fallback peel, and snapshot coverage for the
   two newest tables. Quarantine is dropped (§7).
6. **P10's remainder** — small, and independent of the rest.
7. **P11** — last, deliberately, and under I4/I5 rather than alongside them.

The one thing worth deciding early rather than late is **what the session layer
is**, because P11's shape is downstream of it and P11 is the phase with the
history.

---

## 7. Quarantine: investigated and dropped (2026-09-14)

**What it was.** Upstream's per-group *hydration* quarantine
(`crates/traits/src/engine.rs:481`): "Hydration is best-effort per group: one
corrupt, missing, or validation-failing group must not abort opening the whole
account." It quarantines a **group**, triggered **only** by session-open
hydration failure — never by inbound traffic — gates every read path through
`ensure_group_live`, and is left by a successful re-hydration. Not durable; it
is re-derived at each open.

**Why the plan had it, and why that was an accident of compression.** The row
came from `survives-rewrite-diff-2026-07.md:169`. **Line 321 of that same
document already ruled it out**: "v1 can drop forensics, quarantine hardening,
and deferred-peel caps." P9's scope kept the noun and lost the decision. In our
tree the concept exists only as two deliberately-unproduced enum members
(`IngestOutcome.cs:52`, `:99`) — verified by a full sweep of `src/`, `tests/`
and `lib/`; nothing produces, consumes, stores or tests it, and `marmot-cs` has
no equivalent under any name.

**Why it should stay dropped.** The need is a consequence of upstream's shape,
not of the protocol. Its `AccountDeviceSession::open` hydrates every group
eagerly, so one bad group aborts the account and it needs a container plus an
accessor gate on roughly twelve read paths — a footgun its own `AGENTS.md`
warns about. Ours is lazy and per-group: `MarmotSessionHost.OpenAsync(GroupId)`
returns a session that *is* the capability, so no session means no send, no
converge, no ingest. Failure isolation is the caller's loop, and costs nothing.
Note too that quarantine is in P9's *scope* but not its *exit criteria*.

**What is genuinely missing, and replaces it (S).** `OpenAsync` cannot say why
it failed: it returns `null` both for "no such group" and for "the record is
there and cannot be rebuilt", and `Restore` calls `MlsGroup.Import` uncaught, so
corrupt state **throws out of `OpenAsync`** rather than being classified — which
in the `foreach (id in ids) await OpenAsync(id)` that P11 will write reproduces
upstream's hazard at our app layer. Give it a three-way outcome carrying
upstream's reason values (a good taxonomy, worth keeping comparable) without the
state machine, the durable flag, the accessor gate, or a retry entry point:
**re-calling `OpenAsync` is the retry**, since it reads storage fresh.

Touches `Session/MarmotSession.cs`, one small type beside it, and its tests. No
schema, `EpochState` or `IngestOutcome` change. **Fold it into the session-layer
review rather than doing it concurrently.**

---

## 8. The engine cannot send a message (2026-09-15)

`GroupMessages.Send` — the function that encrypts an application message — has
**no production caller anywhere**. Every reference in the repo is a test. And
`MarmotSession` exposes `IngestAsync`, `ReplayAsync`, `ConvergeAsync`,
`CommitAsync` and `CloseAsync`: five verbs, and **no `SendAsync`**.

So the new engine can receive a chat message, converge a fork, publish a commit
and recover from a crash, and it cannot send a chat message.

### Why this was invisible

It hid in the gap between two phase rows, exactly as the convergence drain and
the session layer did.

- **P6's scope says `send-app`, and P6 is genuinely done** — at the level its
  exit criterion tests. That criterion is interop messaging, and the
  `DarkMatterInterop` suite proves it by calling `GroupMessages.Send` directly.
  The *function* works and is verified against a real peer. What was never built
  is the composition around it.
- **P9's scope says "queued-intent drain polish".** There is no drain to polish.
  The word presumes a thing that does not exist — the same failure as
  "quarantine" in §7, a noun carried forward without the sentence that gave it
  meaning.
- **P11 assumes sending works.** Its row is about replacing `marmot-cs` behind
  `Scramble.Core`'s services; a service that cannot send has nothing to swap in.

### `IOutboundIntentStorage` is not dead code — it is unreached

It was written for exactly this path and its doc says so: *"Dark Matter queues
sends while a group is not in a settled state and drains the queue once it
settles, so this is durable state rather than an in-memory buffer."*

Two earlier pieces of work declined to use it and both were right to —
persisting epoch state and recording publish intent are different problems — but
that is not evidence against it. It should be **kept**. Deleting it would
discard a considered design for a path we still have to build, and we would
rebuild something close to it. Two details in it look right: `ClearIntentsAsync`
exists so queued sends are never drained into a group we have been evicted from,
which mirrors the `Removed` gate on the inbound side; and `Payload` is opaque,
so the schema stays out of the engine's business.

### What building it involves, and the one thing that makes it easier than commits

`MarmotSession.SendAsync`, a drain on settle, and the eviction gate. Ordering
follows the rule already established twice: the queue row lands before the bytes
reach a transport, and is removed after.

**The three-way outcome that `CommitPublisher` needed does not apply here.** A
commit cannot be reissued — MLS refuses to let a member process a commit it
authored — which is why an indeterminate transport answer forces a
reconciliation. An application message has no such constraint: dedup is
content-derived (`MessageId.FromMlsBytes`), so **re-sending is free and a
receiver drops the duplicate**. An indeterminate send can simply be retried.
That asymmetry is worth stating in the code, because the obvious move is to copy
the commit path wholesale and inherit a complication that buys nothing here.

**Size: S–M.** Touches the session, the queue, and a transport seam.

### Where it belongs

Nowhere, currently — which is why it is here. It is the fifth verb the session
layer should have had, and it should be sequenced **before P11**, since P11's
whole premise is that `Scramble.Core` has something to call.

### §8 closed (2026-09-15)

`4fcbf74`. Two things found in the building that are worth keeping:

- **Sealing an envelope advances the MLS sender ratchet**, so the live state is
  persisted before the relay call. Without it a crash rewinds the generation
  counter and the next message reuses a key and nonce — AEAD nonce reuse, not
  hygiene. Only observable across a restart, so it is untested and labelled as
  such: the fourth claimant for the fault-injecting storage double.
- **A queued intent's id is fresh random bytes**, a second meaning for
  `MessageId`, whose doc says content-derived. Forced rather than chosen: the
  MLS bytes do not exist at queue time, and hashing the event collides for two
  identical messages a second apart, so the second would silently never send. A
  distinct `IntentId` wrapper is the right shape if this is ever cleaned up.
