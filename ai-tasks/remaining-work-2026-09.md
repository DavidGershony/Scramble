# Remaining work — Dark Matter, 2026-09-14

A snapshot of everything left, in dependency order. **Provenance lives
elsewhere**: `scramble-marmot-phased-plan-2026-08.md` is the authoritative plan
and `HANDOFF-dark-matter.md` is the running record. This file exists to answer
one question the other two cannot — *what is actually left, and what blocks
what* — and it should be deleted or rewritten rather than allowed to drift.

---

## 0. The finding that shapes everything below

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
| Session-open hydration | **not started** — blocked on §0 |
| Stranded-pending-commit crash recovery | **not started** — blocked on §0 and on publish intent |
| Quarantine | **not started**, and undefined: nothing in the tree implements it and the plan does not say what it isolates or on what evidence. Needs a decision before an estimate. |
| Snapshot-fallback peel | **not started**. `ISnapshotStorage` exists and nothing peels through it. This is the epoch-boundary case: a kind-445 sealed under an exporter secret from an epoch we have left. |
| Queued-intent drain polish | **not started** — follows publish intent |

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

**It cannot start before §0 exists.** "Replace `marmot-cs` behind Core's
services" presumes something for those services to call.

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
- **Ask about §5a** — whether a member past an epoch ever replays history to
  peel a competing commit framed at it. Now askable as a concrete difference
  rather than a symptom, because we have built the retry path we suspect is
  missing.
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
2. **Scope and build the session layer (§0).** Now the only thing between the
   engine and P9's exit criterion. Everything else is behind it, and
   it is currently nobody's. It does not need to be large to unblock: hydrate,
   archive our own commits, drive publish, run a pass, replay.
3. **P9's remainder** — hydration and crash recovery fall out of §0 almost
   immediately, which is why `CrashRecoveryTests` is written and waiting.
   Quarantine needs a definition before it needs an estimate.
4. **P10's remainder** — small, and independent of the rest.
5. **P11** — last, deliberately, and under I4/I5 rather than alongside them.

The one thing worth deciding early rather than late is **what the session layer
is**, because P11's shape is downstream of it and P11 is the phase with the
history.
