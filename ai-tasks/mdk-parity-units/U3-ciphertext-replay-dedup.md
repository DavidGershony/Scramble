# U3 — Ciphertext replay dedup in commit-race resolution

**Type:** READY (with a mandatory approval checkpoint) · **Size:** M ·
**Depends-on:** none

> This unit touches stateful commit/epoch processing. **Do NOT implement blind.**
> Step 1–2 are a read + written plan you MUST report and get approved before
> writing production code (step 3+). If anything is ambiguous, STOP and report.

## Goal

Match Rust MDK PR #246: when resolving competing commits for the same epoch,
reject a commit whose **outer kind:445 event content is byte-identical to one
already seen this epoch** (a re-wrapped replay) *before* running the MIP-03
timestamp/lex-id tiebreaker. Prevents replay-triggered rollbacks.

## Background (all you need)

- `lib/marmot-cs/src/MarmotCs.Protocol/Mip03/CommitRaceResolver.cs` — has
  `ResolveWinner(...)` (the MIP-03 created_at/lex-id tiebreaker). Pure function
  today.
- `lib/marmot-cs/src/MarmotCs.Core/Mdk.cs` — calls
  `CommitRaceResolver.ResolveWinner(...)` in the message/commit processing path
  (there are call sites around the `ProcessMessageAsync` / commit-handling code).
- The "outer event content" is the base64 `content` field of the kind:445 event
  (nonce‖ciphertext). Its SHA-256 is the dedup key.

## Files you may touch

- `lib/marmot-cs/src/MarmotCs.Core/Mdk.cs`
- `lib/marmot-cs/src/MarmotCs.Protocol/Mip03/CommitRaceResolver.cs` (only if you
  add a pure helper — see below)
- `lib/marmot-cs/tests/MarmotCs.Core.Tests/` or `MarmotCs.Protocol.Tests/`
  (add tests)

## Steps

1. **Read** `CommitRaceResolver.cs` fully and every `ResolveWinner` call site in
   `Mdk.cs`. Identify: where a commit's outer content is available; where
   per-epoch state is tracked (snapshots / pending commit); and the cleanest
   place to record + check a per-epoch set of seen SHA-256 hashes.
2. **Write a short plan and STOP.** Report: (a) the exact insertion point(s);
   (b) where the per-epoch `HashSet<string>` (hex SHA-256) will live and how it
   is scoped/reset per epoch; (c) the exact behavior on a duplicate (reject the
   commit as already-processed, no rollback, no error to the user); (d) the test
   you will add. **Wait for approval before step 3.**
3. (After approval) Implement: compute `SHA-256` of the outer content; if its hex
   is already in the epoch's seen-set, treat the commit as a duplicate and skip
   it (do not feed it to the tiebreaker, do not roll back). Otherwise record the
   hash and proceed as today.
4. Add a unit test proving: the same outer-content commit processed twice is
   deduped the second time; two *different* commits at the same epoch still race
   via `ResolveWinner` as before.

## Verify (exact commands)

```bash
cd lib/marmot-cs
dotnet test tests/MarmotCs.Core.Tests/MarmotCs.Core.Tests.csproj \
  -c Debug -p:UseLocalDotnetMls=true
dotnet test tests/MarmotCs.Protocol.Tests/MarmotCs.Protocol.Tests.csproj \
  -c Debug -p:UseLocalDotnetMls=true
```

## Acceptance criteria

- New dedup test passes; existing `CommitRaceResolver` tiebreaker tests still
  pass; both suites green.
- No behavior change for distinct commits (the tiebreaker still decides).

## Scope guards

- Do not alter the tiebreaker ordering (created_at then lex-smallest id).
- Do not introduce a global/cross-group cache — scope the seen-set per group and
  per epoch, matching MDK (epoch snapshots).
- Keep the dedup silent (a duplicate is "already handled," not an error).

## Report back

At step 2: the plan (for approval). At the end: the diff, the new test, and both
suite results. Do not commit.
