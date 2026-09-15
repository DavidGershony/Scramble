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
| Snapshot-fallback peel | **done** — and it is the *archive*, not `ISnapshotStorage`. Third stale noun. See §10. |
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

**The upgrade flow is inert, not dead — and my first reading of it was wrong.**
I took "upgrade flow" to mean a *profile* upgrade, because it sits beside
`legacy_compatibility_profile` in P10's row, and concluded it died with `Legacy`.
That is the same mistake this document keeps cataloguing, made from the other
side: a noun read by its neighbour rather than by its provenance.

The referent is the **capability** upgrade.
`survives-rewrite-diff-2026-07.md:249` — *"Capabilities/feature registry +
upgrade flow … the full upgrade/auto-negotiation flow can trail"* — and
`mdk-parity-plan-2026-07.md:277`, Session 8: `group_capability_upgrade_status`,
`upgrade_group_capabilities`, *"lets an admin upgrade a mixed group to require
SelfRemove once all members are modern."* It is alive upstream at the pin
(`crates/cgka-engine/src/upgrade.rs`, `FeatureStatus::Upgradeable`), and the
2026-09-13 scope decision has no bearing on it.

**It still should not be built now**, for a reason that has nothing to do with
profiles: we have no *optional* group components. `desired` is always
`DefaultComponents` and `MandatoryComponents` is the same set, so no capability
can occupy the "advertised by every member, not yet required" state the flow
exists to promote. **It becomes real the first time something is
desired-but-not-mandatory**, and the candidates — `0x8005` retention, `0x8006`
QUIC, `0x800b` media v2 — are all P12. Recorded as inert rather than closed so
P12 does not rediscover it.

**Left: the exit criterion alone** — capability-mismatch rejections matching
mdk's error taxonomy. Today every component and capability refusal funnels
through `AppComponentException(string message)`: one type, carrying only prose.
Upstream classifies (§6 records a refusal moving from an unclassified `Other`
bucket to `UnknownMember`, naming the key at fault).

Two reasons it is worth doing rather than dropping, and they pull the same way.
A caller has to branch on the reason — P11 puts this behind `Scramble.Core`, and
a UI needs "this invitee lacks a mandatory component" to be distinguishable,
while the cutover rules forbid Marmot types reaching `Scramble.Presentation`. And
error typing has bitten this project before: §3a, *"malformed input escaped as
the wrong exception type, bypassing the retryable/terminal classification the
engine branches on."*

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

**Planned in `p11-cutover-plan-2026-09.md`** (2026-09-15), including the three
questions that must be answered before step 1.

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
- ~~**Two storage gaps found while reviewing**~~ — the intent queue got a caller
  (§8), and the snapshot omission was investigated and is a **choice**, now
  written into the file rather than left to look like an oversight. See §10.
- **`IRoutingIndexStorage` has no production caller either**, and unlike the
  intent queue nothing is scheduled to give it one. It is the rotation-aware
  routing-id → group map, which is the first lookup a real receive path makes;
  `MarmotSession.ReceiveAsync` sidesteps it because a session already knows
  which group it is. Whoever writes the fan-in above the session (P11) is its
  caller. `HasTransportSeenAsync` is in the same position: it exists, its doc
  says it is the pre-filter that avoids re-peeling a duplicate envelope, and
  nothing reads it.
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
4. ~~A storage double that fails one nominated call~~ **done** (`0fc2f0a`), and
   it found a real bug on the way (`065ce31`, §9). Only one of the four claims
   actually needed it; two were already covered and one cannot be tested that
   way at all.
5. ~~P9's remainder~~ **done.** The fallback peel landed (§10); the snapshot
   coverage turned out to be a deliberate choice and now says so in the file.
   Quarantine was dropped (§7). **P9 is complete.**
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

---

## 9. The ordering audit found a live bug (2026-09-15)

`CommitPublisher` cleared its attempt row after `staged.Applied()`, and *"cleared
last, never first"* was true inside `PublishAsync` and **false where it
mattered**. `Applied()` closes an in-memory state machine; the move a restart can
see — the archived checkpoint and the group's live state — belongs to
`MarmotSessionHost.ConfirmAsync` and had not happened yet.

Between them lay a window in which the relay had the commit, every other member
applied it, and we came back with **no attempt row**. `ClassifyAsync` reads that
absence as the positive claim *nobody can have seen this commit*, abandons it,
and MLS will not let a member process a commit it authored — so it was
unrecoverable. Measured over real SQLite by killing at each write of one
`CommitAsync`: **two of nine kill points revived an epoch behind**, having
discarded a commit the group had adopted.

Fixed by not clearing there. `ConfirmAsync` already drops both rows after the
durable move, so the ordering was right everywhere except in doing it twice, the
first time too early.

**Three things worth carrying forward:**

- **A comment can be true at its own scope and false at the one that matters.**
  The rule was correct within the method that stated it. Nothing in that method
  could see that the move it was ordering against was not the move a restart
  observes.
- **Two tests had encoded the old behaviour**, one asserting outright that an
  accepted publish leaves no row behind. The sixth instance in this repo of a
  test pinning a defect as the contract.
- **The audit's premise was half wrong, which is the cheaper finding.** Only
  `AdoptAsync` needed fault injection. `CommitPublisher` and
  `DurableEpochManager` were already covered by probes at their seams, and
  `DurableEpochManager` *cannot* use fault injection — its second step is an
  in-memory move, not a write, so a crash-after-the-row test would pass under
  both orderings. That test was correctly not written.

---

## 10. Snapshot-fallback peel, and the snapshot tables (2026-09-15)

Two P9 items, investigated together because both turned on the same question:
which durable thing actually holds past-epoch state.

### The plan named the wrong source, and it is the third stale noun

"Snapshot-fallback peel" said `ISnapshotStorage`. It could never have worked.

- **What fails is the transport wrap, not MLS.** `MlsGroup.RetainCurrentEpoch`
  keeps a past epoch's secret tree so an application message sent in it stays
  readable, and **deliberately drops that epoch's exporter secret** along with
  the init, membership and confirmation secrets — "retaining them would let old
  key material be used to act in the present". So the inner MLS message is
  readable and the kind-445 seal around it is not.
- **`ISnapshotStorage` holds Marmot-layer rows plus the group's *current* live
  state.** The current state is the one that already failed. It has no
  per-epoch MLS state at all.
- **`EpochArchive` holds exactly the right thing.** A checkpoint is
  `MlsGroup.Export()`, which writes all fourteen key-schedule secrets, so an
  import yields a group whose `ExportSecret` is that epoch's.
- **A test had already hand-rolled it.** `ConvergenceInteropTests`
  `WaitForCompetingCommitAsync` keeps its own epoch → export dictionary and
  peels through it, commenting "reading the race epoch's exporter secret needs
  the group as it was then." The mechanism was proven against a live peer
  before the engine had it.

So: `MarmotSession.ReceiveAsync`, backed by `RetainedTransportKeys`. Try the
live key, and on a retryable failure try each retained epoch's, newest first.

### It is not mainly about chat, which is how the scope was mis-stated

The criterion says "epoch-boundary **messages** survive". The larger case is
commits. A competing commit is framed at the epoch it forks from and sealed
under that epoch's key — so **without this, a fork is invisible at the
transport layer**: nothing peels, no `Retryable` record is written,
`ConvergencePass` reads an empty candidate list, and a split group reports
itself settled. Convergence was reachable only because every test peeled with
the sender's own group.

### The bound needed no new constant

`MaxRewindCommits`, `AppMessagePastEpochLimit` and `MarmotGroupSettings.MaxPastEpochs`
are all 5, and `RequireWindowMatches` already refuses a policy where they
disagree. The archive's retention window is therefore exactly the set of epochs
whose inner messages the group can still read. Retrying is additionally gated on
the envelope naming an address this group has used inside that window, so
somebody else's traffic costs one signature check rather than six.

**Not covered, and cannot be:** an envelope sealed under an epoch we have *not
yet reached*. No key exists for it and nothing keeps the envelope — the durable
records are MLS bytes, and there are none until it peels. Left to redelivery.

### The snapshot tables are a choice, not a gap

Investigated and **deliberately left out**, now written into
`SqliteMarmotStorageProvider.Snapshots.cs` so it is not re-opened. Two reasons:

- **`epoch_states`, `commit_publish_attempts`, `staged_commits` describe a
  commit's exposure to the outside world.** A rollback can undo what this device
  knows; nothing local undoes what a relay holds. An attempt row's absence is a
  positive claim `ClassifyAsync` abandons on — §9's bug at one write's distance,
  which cost two of nine kill points an epoch. `epoch_states` adds its own
  reason: it is the write-through shadow of an in-memory `EpochManager` a
  storage rollback does not touch.
- **`epoch_archive` is key material whose destruction is deliberate.** Restoring
  a snapshot taken at epoch N while at N+3 resurrects checkpoints the
  forward-only prune had already destroyed — whole exported groups. Its window
  is anchored on the live tip, not on the snapshot's epoch, besides.

And the file's own header was **stale in the opposite direction**: it said
snapshots "deliberately do NOT capture MLS state" while `GroupDto.LiveState`
has carried the exported group since V010. Corrected.

**Nothing in production calls `CreateSnapshotAsync`, and the design it was
written for was not adopted** — convergence rebuilds from the archive and
*invalidates* superseded records rather than rolling a table back. Recorded as
a finding; not deleted, because the reasoning in it is worth more than the rows
it would save.

---

## 11. We do not check an invitee's capabilities the way peers check ours

Found 2026-09-15 while classifying refusals; **not built**, because it changes
who can join and that deserves its own commit rather than riding a typing
change.

Upstream's `validate_invitee_capabilities`
(`crates/cgka-engine/src/key_package.rs:340-369` at `fdd398a8`) refuses a
KeyPackage whose leaf advertises anything in RFC 9420 §7.2's implicit ranges —
*"an intentional admission policy beyond RFC 9420 section 7.3 / OpenMLS
validation"*. §3ab records us discovering this the hard way: we stopped
advertising `0x0003` on 2026-09-09, four days before upstream made it fatal.

**We fixed our own advertisement and never built the check.**
`MarmotLeaf.DefaultExtensionTypes` and `DefaultProposalTypes` exist, and are only
ever used to assert our *own* leaf — never to judge an invitee's. So Scramble
would add a member that every current peer refuses to add, and the group would
then differ depending on who did the inviting.

It has a single call site upstream, in the KeyPackage-for-membership path rather
than in ingest, so this is an admission-policy divergence and not a fork. When it
lands it is the natural second variant of `AppComponentRejection`: upstream's
`InvalidKeyPackageCapabilities { member }` / `invalid_key_package_capabilities`.
