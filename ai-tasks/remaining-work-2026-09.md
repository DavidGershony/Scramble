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
**Met.** `tests/Scramble.Marmot.Tests/CrashRecoveryTests.cs` states both criteria
and all 5 pass; it landed with `065ce31`. This paragraph used to say the file was
deliberately uncommitted until it could pass — a red test on the branch would
break the gate, and a skip reads as a pass (§3t). That held while it was being
written and stopped being true when the fix landed underneath it. Corrected
2026-09-19.

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

**P11 IS COMPLETE (2026-09-22).** Steps 1–5 all landed; `marmot-cs` is out of the
build and the app has one MLS engine, proved against the reference client in both
directions. The write-ups are handoff §3af (the flip), §3ah (step 3), §3ak (step 4)
and §3al (step 5). What is left is not P11: §14 (at-rest encryption) blocks a
release, an Android smoke test that *runs* is what lifts I5's freeze on evidence,
and `src/Scramble.Native` plus its CI steps are now dead weight.

**Step 2b landed 2026-09-19 — the flip is in, and I5's freeze is running.**
Every head registers `DarkMatterMlsService`; the desktop head is bugfix-only
until Android has an equivalent smoke test green in CI plus one week. What the
flip actually needed, including four changes this plan did not have and one that
would have made every account uninvitable, is `HANDOFF-dark-matter.md` §3af.
Steps 3–5 remain.

**Planned and decided in `p11-cutover-plan-2026-09.md`** (2026-09-15): existing
groups are abandoned rather than migrated, Android leads, no staged rollout.
There are no existing users, which removes the phase's only irreversible step.
Account identity is unaffected either way — nsec, contacts and relay lists live
in `StorageService`, which has no `MarmotCs` reference at all.

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
- ~~**`IRoutingIndexStorage` has no production caller either**~~ — **resolved by
  P11, verified 2026-09-22.** This said the fan-in above the session would be
  its caller, and that is what happened: `PutRoutingAsync` has two callers,
  `ResolveAsync` and `CurrentRoutingAsync` one each, and `HasTransportSeenAsync`
  — the pre-filter that avoids re-peeling a duplicate envelope — is read by
  `InboundFanIn.cs:168`.
  - **Still uncalled, and much smaller:** `ListRoutingAsync` and
    `PruneRoutingAsync`. The second is the one with a consequence — nothing
    prunes the routing index, so its rows accumulate for the life of a profile.
    Rotation makes that grow per epoch rather than per group. Not urgent at any
    realistic group count, but it is a table with no upper bound and no reaper.
- **The branch is 219 commits ahead of `master` with no PR** (2026-09-22).
  Recorded because I4 names exactly this shape as the risk; the decision not to
  open one is the user's and is not being re-litigated. Worth stating plainly
  once: every gate in this repo has been run locally and is green, and the one
  gate that has never run on this branch at all is GitHub's own.

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

## 12. Inbound commits are not authorization-checked — CLOSED 2026-09-19

**Closed by `CommitAdmission`**, the receive half of the rule
`MarmotGroupAdminPolicy` implements on the send half. Two halves, in this order,
before the group moves: authority judged against the epoch the commit was framed
on, then `ValidateUpdateBatch` + `ValidateStagedCommit` against the dictionary
read off a group that has *actually applied* it — never derived, which is the
circularity the send side already warns about.

**Three doors, not one.** The guard sits in `GroupHandshake.ApplyCommit`, and
also in `CandidateMaterializer.Extend` and `Reorg`. Convergence is a second and
third way into `ProcessCommit`, opening onto exactly the records ingest could not
apply, so a front-door-only guard would refuse a commit and then replay it.
`Reorg` needs its own because it re-selects stored commits independently of what
materialization scored.

### Newly refused

- Any privileged commit from a non-admin — `GroupContextExtensions`,
  `AppDataUpdate`, `Add`, `Remove`, `PreSharedKey`, `ReInit`, `ExternalInit`, and
  anything unrecognised. No new rule: this is exactly the complement of
  `CommitAuthorization.IsAllowedNonAdminCommit`.
- A privileged commit whose committer leaf resolves to no member.
- Any commit whose resulting dictionary changed outside its own `AppDataUpdate`
  proposals — **including an admin's**, which is what stops an admin writing
  component bytes no validator ever saw.
- A group carrying no `app_data_dictionary`, no `app_components` list, or no
  `0x8003`: frozen, not unrestricted.

### Deliberately still applying

Self-updates and SelfRemove-only commits, unchanged. Commits that remove us —
the probe throws and that is eviction, not refusal. And **commits framed against
another epoch are not judged here at all**: the admin list we hold is not the one
their author saw, so they are left to convergence, which judges them at their
real fork epoch.

### The refusal at ingest is not terminal, and that was a correction

The first implementation filed an unauthorized commit `Failed`. **That was
wrong**, and the reasoning is worth keeping because the same trap is one step
away from anyone touching this again.

The gate that lets the ingest check run is an epoch-**number** match, and a
number is not a state: after a fork, two branches sit at the same epoch with
different trees and different admin lists. So a peer's commit is judged with its
committer's leaf resolved in *our* tree and its authority read from *our* policy.
Where the fork raced an add, a remove or an admin change, that is a verdict about
the wrong member under the wrong policy — and `ConvergencePass` lists only
`Retryable`, so filing `Failed` would have hidden that branch for good.

It is now `Retryable`. That costs the refusal nothing, because both convergence
doors run the same admission on a probe restored to the true fork epoch — the
better-informed place — and a record a pass keeps refusing is retired past the
rewind horizon like any other. Pinned by
`IngestRefusesAnUnauthorizedCommitWithoutBurningIt`, and deleting the `Extend`
guard fails two tests, which is what makes the deferral safe rather than lax.

### One real gap left, and it is the library's

**`MlsGroup` does not serialise its proposal cache.** A probe restored from bytes
has an empty one, so no probe in this engine can resolve a proposal cited *by
reference* — in practice, leave commits. The **authorization** half is unaffected
(it reads the live group's cache); the **integrity** half is skipped for those
commits. Pre-existing rather than introduced — `Extend` already refused such
commits as `DoesNotApply` — and marked in `CommitAdmission` where it bites. The
ask, if it is ever worth making: serialise the cache in `WriteTo`/`ReadFrom`, or
expose `CacheProposal(Proposal, uint senderLeaf, byte[] reference)`.

### Two follow-ups this surfaced

- **`MarmotGroupInvite.Add`/`Remove` do not admin-gate the committer**, while
  `MarmotGroupAdminPolicy.Stage` does. So we can still *build* an invite our own
  peers now refuse. Send-side and engine-side.
- **`CommitAuthorization.RequireNoAdminSelfRemove` is still unwired**,
  deliberately: our own leave path does not gate it either, so enforcing it
  inbound would make us refuse a peer admin's ordinary departure. Both halves
  move together or not at all.

Ingest now applies each inbound commit twice — probe, then live. Accepted: every
cheaper route computes the resulting state instead of reading it, which is the
circularity.

---

## 12a. The original finding (2026-09-16)

Surfaced while building §P11's AppDataUpdate slice (`8823539`), confirmed
independently here. **This is the receive half of the rule that commit
implements on the send half, and it is missing entirely.**

`GroupHandshake.ApplyCommit` calls `group.ProcessCommit(commit)` and nothing
else. There is no authorization step before it, and MLS has no opinion here:
`GroupContextExtensions` is a legal proposal from *any* member, so the library
accepts it and rewrites the GroupContext — `app_data_dictionary` included, and
`0x8003` with it.

**`CommitAuthorization` is not the guard it looks like.** It has exactly one
production caller: `CommitOrdering.PriorityOf`, used by the convergence pass to
decide *which branch wins a fork race*. Nothing consults it to decide whether to
accept a commit at all. So a commit that no admin authored is classified,
ordered, and applied.

**`AppComponentIntegrity` had zero production callers before `8823539`**, and
still has exactly one — `MarmotGroupAdminPolicy`, on the send path. Its own
class comment (line 46) names a `GroupContextExtensions` proposal as precisely
the vector it exists to close. Nothing closes it.

`CurrentProfile.Validate` does not cover for this either: it runs at create
(`MarmotGroupBuilder`), at join (`GroupJoin`), at invite-validation
(`MarmotGroupInvite`), and now in the admin-policy rehearsal — **never per
inbound commit**.

So: any member can hand us a commit that makes themselves the sole admin, and we
apply it. Every subsequent admin check then passes, because it reads the
dictionary they just rewrote.

### What this is not

Not a doc/code mismatch. `GroupHandshake.Receive`'s remark about refusals is
about *proposal* authentication — signature and membership tag against the
sender's leaf — which MLS genuinely does, and which is a different rule. It
claims no commit-authorization guard. This is an omission, not a false label.

Not a convergence bug either. The rewind machinery would happily converge on the
malicious branch, since it is a well-formed commit from a real member.

### The shape of the fix

Run both halves at ingest, against the commit's own bytes, before the group
moves — the same order `MarmotGroupAdminPolicy.Stage` uses, which is now the
worked example:

1. `CommitAuthorization` on a view built from the framed commit, refusing a
   privileged commit whose committer is not an active admin **in the current
   epoch** (the pre-commit one — the epoch the committer had to have authority
   in).
2. `AppComponentIntegrity.ValidateUpdateBatch` / `ValidateStagedCommit` on the
   resulting dictionary.

The obstacle is the same one `Stage` hit: the resulting GroupContext is not
readable before applying, and `PendingCommitState` is internal. On the send side
that was solved by rehearsing on a throwaway import. Inbound, the probe already
exists — `CandidateMaterializer` builds exactly such a copy
(`CandidateMaterializer.cs:271`, `:355`) — so the piece to reuse is there.

**Do not derive the resulting dictionary by hand.** It makes the check circular,
for the reason `MarmotGroupAdminPolicy` records at its `Rehearse` call.

### Mutation that must catch it

A commit from a **non-admin** member carrying a `GroupContextExtensions`
proposal that replaces `0x8003` with the attacker's key alone. A test that only
exercises an admin's commit passes identically with no guard at all — which is
the shape §3 keeps recording.

Sequencing: this is receive-path and independent of the P11 blockers, but it is
security-relevant, so it should not sit behind the cutover.

## 13. The I2 integration suite is flaky, and the baseline hid it (2026-09-16)

Recorded because the number everyone quotes — **68 passed / 4 skipped** — reads
like a clean deterministic baseline, and it is not one. A future session that
sees 67 will go looking for what it broke, or worse, will see a real regression
and file it under "the usual flake."

Three runs on 2026-09-16, no code change between the last two:

| Run | Result |
|---|---|
| 1 | 67 passed, **1 failed**, 4 skipped |
| 2 | 67 passed, **3 failed**, 4 skipped — *different tests* |
| 3 (the failures only) | **4 passed**, 0 failed |

Every failure was the same shape: `MlsLifecycleTestBase.WaitForMessageAsync`
timing out at 20–30s with "never received message … in chat …". Seen in
`NonPowerOfTwoTreeTests.Group_OfSize_N_EveryMemberSendsOneMessage_EveryoneReceivesAll`
(memberCount 5 and 7) and
`EpochRatchetStressTests.FiveMembers_FiftyInterleaved_Ops_EpochsStayInSync`.

These are the heaviest tests in the suite — five to seven parties, fifty
interleaved operations, every message round-tripped through a real relay — so a
delivery-timing sensitivity is the likeliest cause rather than an MLS fault. Not
investigated further; it was not what this session was doing.

**It was not the Dark Matter work** *when this was written*, because
`DarkMatterMlsService` then had zero references outside its own file.
**That defence expired on 2026-09-19**, when the flip registered it on every
head. The three named tests were flaky before the engine was reachable from
them, so a failure in those three is still not evidence on its own — but the
reasoning is now "these three, historically", not "nothing can reach the new
engine". Any *fourth* test showing this shape is a regression until proved
otherwise, and giving these three their own category (below) is what would keep
the distinction honest.

### What to do about it

Not "re-run until green" as a habit — that is how a real regression gets
absorbed. Either stabilise the wait (the timeout is a fixed 20–30s against a
containerised relay with no readiness signal for delivery) or mark the three as
a separate category so the required gate stays deterministic and they run
alongside it. The second is cheaper and honest; the first is better.

Until then: **a failure in one of these three named tests is not evidence of a
regression on its own, and a pass is not evidence of its absence.** Any other
test failing is a real signal.

### One hypothesis tested and disproved (2026-09-18)

**It is not accumulated relay state.** The obvious guess was that a relay volume
full of events from earlier runs slows delivery past the fixed 20–30s wait. The
`scramble_relay-data` volume was destroyed and recreated, and
`EpochRatchetStressTests` **failed on the very next run anyway**. Recorded so
nobody spends the same hour on it.

What did hold: the same test **passes in isolation** (39s) and fails inside the
full suite, on a clean relay. So the trigger is contention during a full run
rather than anything durable — which points at the fixed timeout under load, and
makes "give these three their own category" the more honest fix of the two
offered above, not the lazier one.

### Two environment notes that cost a diagnostic cycle each

- **Killing an interop run mid-flight corrupts the peer container's SQLite.**
  Every later run then fails with `backend failure: file is not a database`,
  naming whichever command happened to run first — so it reads as a fault in an
  unrelated test. The collection's own doc comment predicts this shape for
  parallel access; an interrupted run produces it too. Fix: recreate the
  `scramble_mdk-cli-data` volume. A clean peer also ran the interop category in
  2m29s against 8m43s dirty, so it is worth doing when the suite drags.
- **`CLAUDE.md`'s local integration command had drifted from `integration.yml`**
  in both directions: it ran `FullE2E`, which CI does not, and omitted
  `DarkMatterInterop`, which CI does. Running it as documented skipped the entire
  interop suite — 72 tests locally against CI's 104. Fixed 2026-09-18.

---

## 14. The engine's store is not encrypted at rest (2026-09-19) — CLOSED 2026-09-22

Found while writing the flip's DI, by reading what the legacy registration did
rather than what the plan said about it.

**The plan's §2 table is wrong in the way that matters.** It lists at-rest
encryption as `ISecureStorage` (DPAPI, Android Keystore), "yes, engine-agnostic".
The *mechanism* survives — it still protects the `User` record and everything
else `StorageService` puts through it. What does not survive is the MLS store:

| | Legacy engine | Dark Matter engine |
|---|---|---|
| MLS group state | `EncryptedSqliteStorageProvider` → `ISecureStorage` | `groups.live_state`, plain BLOB |
| KeyPackage private material | protected | `key_packages.private_material`, plain BLOB |
| Exported epoch state | protected | `epoch_archive.group_state`, plain BLOB |
| Message plaintext / wire | protected | `messages.wire`, plain BLOB |
| Snapshots | n/a | `snapshots.data`, plain BLOB |

`SqliteMarmotStorageProvider` has no `ISecureStorage` seam and never had one; the
engine was built standalone and nothing in `Scramble.Marmot.*` knows that concept
exists. So the cutover moves ratchet state and leaf private keys from protected
fields to plaintext rows in the profile database.

**Why it did not block the flip.** Nothing ships from this branch — no PR, no
release, no users — and the fix is a storage-layer change that stands alone: it
does not need the flip reverted, and the flip does not need it to be proved. The
alternative was bolting a twelve-sub-interface decorator onto a pivot commit that
already needed a `Landing-Discipline-Exempt:` trailer.

**It does block a release.** On Android app-private storage hides most of it; on
desktop DPAPI was doing real work against a copied database file, and that
protection is simply gone.

**Two shapes, and the cheaper one is probably better.**

1. **An `ISecureStorage` decorator over `IMarmotStorageProvider`** — mirrors what
   the legacy engine did, and is twelve sub-interfaces wide. Every field that
   should be protected and is not is a silent leak, so it needs a test that
   enumerates the sensitive columns rather than a test per method.
2. **Encrypt the database file** — give the engine its own SQLite file keyed
   through `ISecureStorage` (SQLCipher via `SQLitePCLRaw.bundle_e_sqlcipher`, a
   `Password=` in the connection string). One seam instead of twelve, and no
   column-by-column judgement to get wrong. It is a native-dependency change that
   has to be proved on Android, and it gives up sharing one file per profile —
   which `DarkMatterMlsServiceFactory.TablePrefix` documents as a deliberate
   choice, so that choice would have to be revisited.

Decide before any release, not before step 3.

### Closed 2026-09-22: option 1, with a census instead of a per-method suite

`SecureMarmotStorageProvider` (in `Scramble.Core`, because `Scramble.Marmot.*`
must not depend on Core where `ISecureStorage` lives) sits between
`SqliteMarmotStorageProvider` and the engine, and
`DarkMatterMlsServiceFactory.Create` wires it. Option 2 was rejected on the
project's own history: the Rust uniffi backend was abandoned because Android
could not load ARM-cross-compiled natives, and SQLCipher is the same bet.

**Protected:** `groups.live_state`, `messages.wire`, `welcomes.wire`,
`outbound_intents.payload`, `key_packages.private_material`,
`epoch_archive.group_state`, `staged_commits.group_state`, and — beyond what
this section listed — `epoch_archive.tip_committer` and
`staged_commits.tip_committer`, which say which member drove a group to an epoch
and are the equivalent of the `Message.SenderIdentity` the legacy decorator
protected.

**`snapshots.data` is protected transitively, and that is load-bearing rather
than lucky.** The provider builds that JSON document *below* the decorator out of
rows it reads for itself, so what it serialises is already-protected bytes and
what `RollbackToSnapshotAsync` writes back is still protected — symmetric, with
the decorator never touching it. Moving snapshot capture above the decorator
would silently undo it, which is why the plaintext sweep searches Base64 forms
too: it is what notices.

**Left in the clear, and it has to be.** `Protect` is not deterministic (DPAPI is
not), so an encrypted identifier is a different value on every write — lookups
miss, `INSERT OR REPLACE` inserts, and the byte-equality the engine uses to
recognise its own staged commit and its own branch never holds. That rules out
every primary key and `WHERE` term: `groups.group_id`, `messages.id`,
`welcomes.id`, `routing_index.transport_group_id`, every `group_id` foreign key,
`messages.transport_id`, `key_packages.key_package_ref`/`event_id`, and the three
digests this section asked about — `epoch_states.staged_commit` (a
`StagedCommitHandle`, which is the commit id and is compared by bytes),
`commit_publish_attempts.commit_id`, and both `tip_commit` columns (`CommitTip`'s
`BranchId` is derived from it). All are content-derived digests of bytes a relay
already carries. `key_packages.public_bytes` is clear for the plainer reason
that it is published as a kind-30443 event.

**The test is a census, not a suite.** `SecureMarmotStorageProviderTests` reads
the real schema with `PRAGMA table_info` and requires *every* column — any type,
not only BLOB — to carry a written-down disposition, and walks
`IMarmotStorageProvider`'s signatures by reflection to require the same of every
byte-carrying record member. A migration that adds a column, or a record that
gains a `byte[]`, fails until somebody classifies it. Then it populates every
sensitive column, closes the store, opens the file and asserts the known
plaintexts appear nowhere in any column of any table. What it does not catch: a
secret written into a column already classified non-secret.

**No migration, and the legacy read is made legible rather than fatal.**
`Unprotect` returns a prefix-less value unchanged, so an unprotected row from
before this change reads back correctly and invisibly. The decorator notices
(the value is unchanged by unprotecting), logs once per field, and counts it in
`FieldsFoundUnprotected`. It does not throw: pre-cutover MLS state is abandoned
by decision (`p11-cutover-plan-2026-09.md` §2), so refusing to open would only
brick a developer's profile over state already written off — and the store heals
forward, because every rewrite protects. The factory *does* throw when
`IStorageService.SecureStorage` is null, which is the one case where proceeding
would create a fresh unprotected store.

---

## 15. The app's own inbound invite path had never faced a peer (2026-09-20)

Found while flipping, and it is the most interesting failure in this migration so
far because of *where* it hid rather than what it was.

**The bug.** `MessageService.HandleWelcomeEventAsync` parsed the kind-444 rumor
with marmot-cs's `WelcomeEventParser`, which requires an `["encoding","base64"]`
tag that no conformant peer emits, and returned silently when it threw. The app
could not accept an invite from anybody but itself. Fixed by reading through the
engine's `WelcomeEvent.Read`; see `HANDOFF-dark-matter.md` §3af.

**Why every gate was green.** Two suites each covered half the seam:

| Suite | Drives the app's inbound path? | Runs? |
|---|---|---|
| `WhitenoiseGroupInteropTests` (incl. `WhitenoiseCreatesGroup_ScrambleJoins`) | **yes** | **no** — 3 of the gate's 4 skips; that peer was retired |
| `FullE2EGroupInteropTests.E2E_3Users_2OC_1WN_FullFlow` | yes | no — the gate's 4th skip |
| `DarkMatterInterop.AdapterInboundJoinInteropTests` | **no** — unwraps with the engine's codec, enters at `IMlsService` | yes |
| every app-to-app test | yes | yes — and passed, because both sides emitted the same bad tag |

**The generalisable finding: a skipped suite is not neutral, it is a hole with a
name.** §3's "green can mean less than it looks" already records skipped suites;
this is the sharper version — the skip was *specifically* over the one path
nothing else exercised, and the suite that replaced that peer deliberately entered
below it. Worth asking of any suite retired in favour of another: **what did the
old one cover that the new one enters beneath?**

**Two things this leaves open.** *(The first is now closed — 2026-09-21.)*

- ~~**`WhitenoiseGroupInteropTests` still skips**~~ **✅ DONE 2026-09-21. The
  required gate has no permanent skips left.** The three in
  `WhitenoiseGroupInteropTests` lost their `Category=Integration` trait, so they
  leave the required whitelist while staying in the tree and runnable with
  `--filter "Category=WhitenoiseInterop"` — kept rather than deleted because the
  scenarios are still the right ones if that peer is ever revived, and a deleted
  test cannot be revived by reading it. The fourth,
  `FullE2EGroupInteropTests.E2E_3Users_2OC_1WN_FullFlow`, was **removed**: xUnit
  traits are additive and its class is `Integration` for the tests that do run, so
  it could not be retagged out, and its peer is archived upstream so it could never
  run again as written. Its dead `WhitenoiseDockerClient` fixture went with it.
- ~~**The app's gift-wrap path has still never faced a peer.**~~ **✅ CLOSED
  2026-09-21, both directions.** `OutboundWelcomeInteropTests` has the app invite the
  reference client through `MessageService` and `NostrService` — its own rumor, seal,
  NIP-44 and kind-1059 wrap — and the peer must list, accept, join and then read our
  traffic. Written as the harness for step 4's NIP-44 swap and green both before and
  after it. Original note below.
- **(original)** **The app's gift-wrap path has still never faced a peer.** `NostrService` seals
  and unwraps with marmot-cs's `Nip44Encryption`; the interop suite uses the
  engine's `Nip59GiftWrap`. `InboundWelcomeInteropTests` is the first test to put
  the app's unwrap in front of a real peer's gift wrap, so it covers this too —
  but only in the inbound direction, and only for kind 444.

---

## 16. Buffered messages are never replayed — FIXED 2026-09-20

**Closed by a split-and-mutate pass** (`IMlsService.ReplayBufferedMessagesAsync`,
`MessageService.DrainReplayedMessagesAsync`, called after every merge and every
rollback that cleared a commit). Eleven tests, six mutations, one of which survived
first time and found a real hole in the tests — see the handoff. **It surfaced a
second defect one layer down, which is not fixed: §17.** The original write-up
follows, because the reasoning about why no gate caught it is worth keeping.

Found during P11 step 3, deliberately not fixed there: the fix needs a contract
member `IMlsService` does not have, which is a change of its own rather than scope
creep into a classification fix.

**The gap.** The engine holds a message it cannot read yet — one that arrived while
a commit of ours was outstanding, or that belongs to a branch convergence has not
settled — and answers `Buffered`. The bytes are durable; the engine replays them
through `MessageIngest.ReplayAsync` once the group can read them. **Nothing in the
app ever calls it.** Verified by grep across `Scramble.Core`, `Scramble.Presentation`,
both UI heads and the macOS head:

| Engine entry point | App callers |
|---|---|
| `ReplayAsync` | **0** |
| `ConvergeAsync` | **0** |
| `DrainAsync` | **0** |

`MergeStagedAsync` and `ClearStagedAsync` do not trigger a replay either. So a
message buffered during any stage → publish → merge window stays on disk, unread,
indefinitely.

**Why this is worse than it sounds.** The engine's own documentation names the two
events that must drive replay — a publish finishing, and a convergence pass adopting
a branch — and warns that a caller which returns `Buffered` without scheduling a
drain *"has silently dropped the message while reporting that it kept it."* That is
exactly the app's present behaviour. It is message loss from the user's point of
view, with a durable copy on disk proving it did not have to be.

**Why it did not show up in any gate.** Buffering needs a commit of ours to be
outstanding when someone else's message arrives — a window the tests never open,
because they publish and merge in the same breath. The interop suite drives one
side at a time; the lifecycle tests do not interleave a send with a pending commit.

**What fixing it involves.**

1. A way to deliver replayed messages back to `MessageService`. `IMlsService` has no
   member for "here are messages that became readable" — `DecryptMessageAsync` is
   one-in-one-out. The honest shape is probably an observable or a drain call that
   returns the newly-readable messages, which is also the seam a real
   `IMessageRelay` would want later.
2. Calling it at the two named events: after `MergeStagedAsync` / `ClearStagedAsync`
   resolve a publish, and after a convergence pass adopts a branch.
3. A test that actually opens the window: stage a commit, deliver a peer's message
   while it is pending, merge, and assert the message arrives. That test is the
   whole value of the fix, and none of the existing suites can express it yet.

**Note the interaction with `remaining-work` §5's async question.** Giving the
adapter a real `IMessageRelay` is the named condition for converting `IMlsService`
to async. Whoever builds the replay path should read that item first: the two
changes want the same seam, and doing them separately means designing it twice.

---

## 17. A replayed commit advances the epoch and never writes it down (2026-09-20)

Found by the implementer of §16 while reading the engine, flagged rather than
fixed, and confirmed independently afterwards. **It is the same class of defect as
§16 — state the engine holds correctly and the caller never persists — one layer
further down.**

**The facts, each checked rather than inferred.**

- `MessageIngest`'s ingestibility gate sits **before** dispatch, so while one of our
  commits is staged and unacknowledged it buffers *everything*, handshakes included
  — not just application messages.
- `MarmotSession.IngestAsync` writes the group down when the epoch moves;
  `WriteLiveStateAsync` appears six times in that file. `ConvergeAsync` writes
  before it replays.
- `MarmotSession.ReplayAsync` is a bare `_ingest.ReplayAsync(_group, GroupId, ct)`,
  and `MessageIngest.ReplayAsync` contains **no live-state write at all** (grep: 0).

So a replay that applies a buffered commit advances the in-memory group, marks the
record `Processed` — making it ineligible for a future replay — and loses the epoch
on the next load.

**The reachable path is a rollback, and that is worth being precise about.** After a
*merge* of our own commit, a peer's commit that was buffered at the old epoch no
longer applies and comes back `Stale`, which is harmless. The damaging sequence is:
our publish fails → `RollbackStagedCommitAsync` clears our commit → the peer's
buffered commit now applies cleanly → the epoch advances in memory only → restart,
and the device is behind with no record left to replay. A failed publish is exactly
when other members' commits have been piling up behind ours.

**Why the §16 fix does not paper over it.** The service layer cannot honestly patch
this. `PersistRatchetAsync` is the obvious candidate and is wrong by its own
remarks: it deliberately skips the routing re-sync that only a commit needs. The
write belongs where the other five live, inside the session.

**What fixing it involves.** A live-state write in `MarmotSession.ReplayAsync` when
the replay moved the epoch, mirroring `IngestAsync`, plus the routing re-sync a
commit needs. The test is an engine-suite test: buffer a peer's commit behind a
staged commit of ours, clear ours, replay, reopen the store, assert the epoch
survived. That is `Scramble.Marmot.Tests` territory, not `Scramble.Core.Tests`.

---

## 18. A cached proposal's record is terminal — does the cache outlive it? (2026-09-22)

Raised by reading upstream's 0.10.4 fix (#1935, handoff §3am), not by a failure.

`MessageIngest.IngestHandshakeAsync` persists the record as
`MessageRecordState.Processed` and *then* returns `IngestOutcome.Buffered` when the
handshake outcome is `ProposalCached`. Processed is terminal: replay does not
reconsider it, and dedup will answer duplicate for that content id forever.

That is defensible on its face — the message *was* peeled and its proposal cached, so
this is not upstream's "a wrapper nobody opened became terminal". **The question is
what happens across a restart.** If the MLS proposal cache lives only in the
in-memory `MlsGroup` and is not part of `Export()`, then after a restart the proposal
is gone while its record says processed, and a commit citing it by hash has nothing
to resolve against — the same silent-loss shape as §16, reached by a different door.

**Not answerable by reading.** `MlsGroup.Export()` is in `lib/dotnet-mls`; whether it
serialises the proposal store needs checking there, and `lib/dotnet-mls` changes need
explicit permission.

**The test if it turns out to matter:** ingest a proposal alone (so it is cached but
no commit applies), close the store, reopen it, then ingest a commit that cites that
proposal by hash — and assert it applies. A commit citing a proposal by hash is also
the shape §5's trap table says to use for read-before-apply tests, because an Add
commit's inline proposals cannot tell the orderings apart.

Low urgency: it needs a proposal to arrive separately from its commit, which our own
paths do not currently produce — every commit we build carries its proposals inline.
A peer that sends them separately would find it.

---

## 19. Two UI heads are compiled by no gate (2026-09-22)

Not a defect yet. It is the condition that let two dead `using` lines in
`Scramble.Diagnostics` survive four consecutive green fast gates, and it now
applies to whole projects.

**`src/Scramble.Apple`** (macOS/iOS, `net10.0-macos`) is referenced by nothing:
not `Scramble.sln`, not any `.slnf`, not any workflow. `publish.yml`'s
`macos-arm64` job publishes `Scramble.Desktop`, which is the cross-platform
head — not this one. It cannot be built on Windows either (`NETSDK1147`: the
`macos` workload). So `AppleSecureStorage.cs` was edited in `8c596ae` and by
P11 step 5 wave 3 before it, in both cases without anything compiling the
result.

**`src/Scramble.Android`** is the abandoned legacy head, deliberately
uncompiled (I1-L, `OBSOLETE.md`). That one is fine as it stands — but it is
the reason a grep for "who else is unbuilt" returns two answers and only one
of them is intentional.

**What to do, in order of cost:**

1. Add `src/Scramble.Apple` to a solution filter and a `macos-latest` CI job
   that builds (not publishes) it. A compile-only job is minutes and closes
   the gap completely.
2. If nobody intends to ship a macOS/iOS head this cycle, delete the project
   the way `src/Scramble.Native` was deleted in `1313607` — an unbuilt head is
   a liability that accrues edits, and reviving it from git is cheap.

Either answer is fine. What is not fine is the current state, where the
project looks maintained (it was touched twice in September) and is verified
by nothing.

---

## 20. Android device-to-device transfer still copies the profile (2026-09-22)

`83eb4ae` set `android:allowBackup="false"` on the shipped head, which stops
cloud backup of the app's private data directory — the profile SQLite file with
its MLS ratchet state and leaf private keys.

**That is only half of it on API 31+.** Since Android 12, `allowBackup="false"`
no longer covers device-to-device transfer; suppressing that needs an
`android:dataExtractionRules` resource with an explicit `<device-transfer>`
section, declared on `<application>`.

**Why it is not done here:** it adds a `res/xml` resource whose effect is
observable only on a real device pair, and neither the emulator-less CI nor
this machine can demonstrate it working. Writing the file blind would produce
exactly the kind of claim §3ao spent a commit correcting.

Pairs naturally with the Android smoke test that I5's freeze is waiting on:
once a device or emulator is in the loop, both become checkable at once.

---

## 21. The Android smoke test needs its interaction half (2026-09-22)

`23b7d2d` (handoff §3ap) gets the shipped head onto an emulator in CI and checks it
starts, stays up, owns the screen, logs no fatal exception and is not killed for
memory. That answers "does it run at all", which nobody had ever asked.

**I5's freeze-exit condition asks for more than that:** "an equivalent smoke test
green in CI", which the tracking note spells out as a scripted create-group /
send-message pass. Startup exercises `StorageService`, the Keystore-backed
`ISecureStorage` and `DarkMatterMlsServiceFactory.Create`. It does not exercise a
single protocol path.

**What to add**, in the same `smoke` job, after the existing assertions:

1. Point the app at the ephemeral relay the integration gate already runs
   (`docker-compose.test.yml`, `nostr-relay`) — on an emulator that is
   `10.0.2.2:7777`, not `127.0.0.1`.
2. Drive the UI with `adb shell input` / `uiautomator dump`, or expose a debug
   intent on the head that creates a group and sends one message.
3. Assert the message appears in the chat list, and that logcat carries no
   `MlsIngestRefusedException`.

Option 2 is the one to prefer. UI coordinate-driving is the flakiest thing in any
Android suite, and a debug-only intent is both stabler and closer to what is
actually being tested — that the engine works on the device, not that a button is
at a particular pixel.

**Cheap now.** The emulator boot, the KVM setup, the install, the screenshot
artifact and the four startup assertions are all in place; this adds steps to a
working job rather than building one.
