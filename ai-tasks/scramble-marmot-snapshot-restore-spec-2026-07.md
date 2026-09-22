# Scramble.Marmot.Convergence — snapshot/restore/replay-probe spec (2026-07)

**Verdict: lives entirely in `Scramble.Marmot`. No `dotnet-mls` changes needed.**

This corrects `convergence-deepdive-2026-07.md` §5/§6/§10, which claimed dotnet-mls
lacked a "snapshot a group, restore it, process a commit against a retained
snapshot without mutating live state" capability and proposed it as a
permission-gated generic-MLS addition. That capability **already exists** —
the deep-dive agent apparently didn't find it. 🟢 (verified by reading
`lib/dotnet-mls/src/DotnetMls/Group/MlsGroup.cs` directly)

## Evidence

`MlsGroup` (`Group/MlsGroup.cs`) already exposes:

- `byte[] Export()` (:2237) / `static MlsGroup Import(byte[] data, ICipherSuite cs)`
  (:2245) — full-state serialize/deserialize (custom TLS-ish binary format,
  version-tagged). `Import` builds a **wholly independent** instance: every
  field (`_groupContext`, `_tree`, `_transcriptHash`, `_keySchedule`,
  `_secretTree`, `_extensions`, my leaf keys) is freshly deserialized from
  bytes via `ReadFrom` (:2127), not shared/aliased with the source group.
- `Commit()` (:285) → stages a `PendingCommitState` (doesn't mutate live
  state) → `MergePendingCommit()` (:550) applies it, or `ClearPendingCommit()`
  (:572) discards it. Already the exact two-phase stage/merge/discard shape
  Dark Matter's publish-before-apply flow needs.
- `ProcessCommit(PrivateMessage)` / `ProcessCommit(PublicMessage)` (:591, :604)
  — can be called on any `MlsGroup` instance, including a throwaway one built
  via `Import()`.

## The pattern (all in `Scramble.Marmot`, zero library changes)

```
Snapshot:  bytes = liveGroup.Export()
           store bytes keyed by (groupId, epoch) in Scramble.Marmot.Storage

Restore:   restored = MlsGroup.Import(bytes, cipherSuite)
           // fresh, independent instance — safe to use standalone

Probe:     probe = MlsGroup.Import(snapshotBytes, cipherSuite)   // throwaway
           probe.ProcessCommit(candidateCommitBytes)              // mutates only `probe`
           inspect probe.GroupContext / probe.KeySchedule / new epoch, tree hash, etc.
           // no rollback call needed — `probe` is never referenced again, GC'd
```

No `SnapshotRollbackGuard`/RAII pattern is needed either (the deep-dive's §10
suggestion, mirroring mdk's `snapshot_guard.rs`) — that pattern exists in mdk
because Rust's `fork_recovery.rs` mutates one shared storage-backed group
in place and must guarantee rollback on panic. Because the C# approach here
never mutates the live object for a probe, there's nothing to roll back and
no guard needed for the *probe* case. A cleanup guard may still be worth
adopting around a real merge (`MergePendingCommit`/`ClearPendingCommit`)
against orphaned staged commits on a cancelled send — that's a separate,
narrower concern than the snapshot/replay one, tracked in
`convergence-deepdive-2026-07.md` §10.

## Scope caveats — what `Export()`/`Import()` do NOT round-trip 🟢

Read directly off `WriteTo`/`ReadFrom` (:2036-:2232):

- `_proposalCache` (uncommitted pending proposals cache) — **not serialized**,
  resets to empty on `Import()`.
- `_resumptionPsks` / `_externalPsks` — **not serialized**.
- `_pendingCommit` (an in-flight staged-but-unmerged commit) — **not
  serialized**; `Export()` only ever captures *settled* state.

**Implication:** snapshots must be taken at settled/merged epoch boundaries
(exactly when fork-recovery/candidate-materialization want an anchor —
"state right before a racing commit is applied"), not mid-proposal. If a
candidate commit references a resumption/external PSK the snapshot didn't
carry, `ProcessCommit` on the restored probe will fail to resolve it —
acceptable for now (PSK-referencing commits are an edge case), but worth a
`🟡` flag for whoever builds `CandidateMaterializer`.

## Correction to §12(d) framing (retained past-epochs) 🟡

The deep-dive tied this snapshot/restore gap to scoping doc §12(d)
("retained past-epochs ABSENT — a `PrivateMessage` from epoch N can't be
decrypted after advancing to N+1"). With `Scramble.Marmot` maintaining its
own external per-epoch snapshot store, §12(d) may be **less load-bearing
than scoped**: to decrypt/process a late message against an old epoch,
`Scramble.Marmot` can `Import()` that epoch's stored snapshot into a
throwaway instance rather than requiring `dotnet-mls` to hold multiple
epochs' secrets live simultaneously in the *same* group object. This doesn't
fully retire §12(d) — a live group still can't decrypt an old-epoch message
in place without going through the snapshot store — but it means the fix can
plausibly be an app-level (`Scramble.Marmot`) snapshot-store lookup instead
of a `dotnet-mls` library change. Needs a closer look when `CandidateMaterializer`
is actually built; not re-scoping it definitively here.

## What `Scramble.Marmot.Storage` needs to add

Per `dark-matter-migration-scoping-2026-07.md` §10's existing storage scope
("pending-commit durability, routing-state history") — extend with:

- `SnapshotStore`: `Save(groupId, epoch, bytes)` / `Load(groupId, epoch) -> bytes?`
  / prune policy (bound by `max_rewind_commits`-equivalent window, per
  `convergence-deepdive-2026-07.md` §5).
- A thin `GroupSnapshot` wrapper around `MlsGroup.Export()`/`Import()` — no
  dotnet-mls surface beyond the existing public API.

## Net effect on the deep-dive's risk/size estimate

- Removes the **#1 flagged unknown** ("🔴 The dotnet-mls snapshot/restore/
  non-mutating-replay-probe capability — not previously scoped, no size
  estimate, blocks the entire slow-path pipeline") as a *library* risk. It's
  now ordinary `Scramble.Marmot` application code (S-M), not a `dotnet-mls`
  permission-gated ask.
- `CandidateMaterializer` (§6, previously sized L-XL and "blocked on the
  dotnet-mls gap") should be re-sized once `openmls_projection.rs` (93KB, still
  unread) is actually read — that read is now the real remaining unknown for
  that module, decoupled from any library-permission question.
- Overall size estimate (deep-dive said "L, trending XL, primarily because of
  the snapshot/restore gap") should come down accordingly — recommend
  re-running that estimate after `openmls_projection.rs` gets its own read.
