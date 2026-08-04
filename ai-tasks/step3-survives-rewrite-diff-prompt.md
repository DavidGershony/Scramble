> ⚠️ **TEMPORARY — DO NOT COMMIT THIS FILE.** Session handoff scratch prompt.
> **Delete it once step 3 (the survives/rewrite diff) is done** and its output
> doc (`survives-rewrite-diff-2026-07.md`) exists.

# Step 3 — `marmot-cs` vs `cgka-engine` survives/rewrite diff (session handoff prompt)

Paste the block below into a fresh session (or say: "run the survives/rewrite
diff in `ai-tasks/step3-survives-rewrite-diff-prompt.md`"). Self-contained.

---

## PROMPT

You are continuing the **Scramble → Dark Matter (Marmot)** migration. Before
doing anything, read these files for full context — do not plan from memory:

1. `ai-tasks/00-START-HERE-dark-matter.md` — orientation, decisions,
   constraints, next-steps (this task is step 3).
2. `ai-tasks/dark-matter-migration-scoping-2026-07.md` — the authoritative
   plan. §4 ("what survives"), §8b/§9/§10 (Option A′, proposed
   `Scramble.Marmot` layout) especially.
3. `ai-tasks/convergence-deepdive-2026-07.md` — the convergence subsystem
   deep-dive (already done, step 2). Skim it; you don't need to redo it. Note
   its §10 correction and `ai-tasks/scramble-marmot-snapshot-restore-spec-2026-07.md`
   — `dotnet-mls`'s `MlsGroup.Export()`/`Import()` already provide group
   snapshot/restore/non-mutating-replay-probe, so do **not** re-raise that as
   a gap.
4. `CLAUDE.md` — repo/platform rules.

**Hard constraints (do not violate):**
- Do **NOT** modify `lib/marmot-cs` — it's the live engine, read-only
  reference for this diff.
- Do **NOT** modify `lib/dotnet-mls` without explicit user permission.
- **No Marmot protocol may leak into `dotnet-mls`** — it stays generic
  RFC-9420. Any capability gap found is a permission-gated, generically-framed
  proposal (per the boundary rule in scoping doc §12), not an inline edit.
- This step is **research/documentation only — write no engine code.** Output
  is a written analysis doc.
- Branch is `feat/dark-matter` (cut from `master`, does not contain the old
  `feat/marmot-batch1-protocol-v3` 0.8.0-era commits). Work there.

### Your task: line-level diff of `marmot-cs`'s orchestration layer vs `cgka-engine`'s equivalent modules

The scoping doc's module-level read (§4, §8b) already classified
`MarmotCs.Core`'s `Mdk.cs` (+ `CommitRaceResolver`) as "rewrite" at a coarse
grain — "~30% scaffolding reusable" was a 🟡 inference, never verified
line-by-line. Your job is to make that verified (🟢) and produce the actual
module list + build order for `Scramble.Marmot.Engine`.

**Sources:**
- `lib/marmot-cs` submodule (local, currently at `a55e527`; read-only —
  `git log`, `git show`, or just read files directly under
  `lib/marmot-cs/...`). Focus: `MarmotCs.Core/Mdk.cs`, `CommitRaceResolver`
  (wherever it lives — grep for the class), and anything in `MarmotCs.Core`
  that orchestrates group lifecycle / message send-receive / commit
  application. Also skim `MarmotCs.Protocol` just enough to confirm the
  scoping doc's "survives, reuse" call for the codecs (`GroupEventEncryption`,
  MIP event builders) still holds — you don't need to re-derive that, just
  sanity-check it.
- Rust `mdk` v0.9.4 `cgka-engine` via `gh api` (confirm `gh auth status`
  first): `crates/cgka-engine/src/engine*`, `message_processor*`,
  `group_lifecycle*`. (`convergence*`/`epoch_manager*`/`fork_recovery*` are
  already covered by the step-2 deep-dive — cross-reference, don't re-read
  from scratch, unless you find engine.rs calls into something the deep-dive
  didn't cover.)

**Method:** For each `marmot-cs` orchestration file/class, identify the
`cgka-engine` module(s) that own the equivalent responsibility in Dark
Matter, and classify line-by-line (or function-by-function, where that's the
more meaningful unit):
- **Survives as-is** (rare — flag if found, it'd be a pleasant surprise)
- **Survives with modification** (same shape, different details — quantify
  the delta)
- **Rewrite** (different model entirely — e.g. `CommitRaceResolver`'s
  relay-`created_at` tiebreak vs convergence's witness-quorum model, already
  known to be a rewrite per scoping §4)
- **New** (Dark Matter concept with no `marmot-cs` analog — e.g. epoch state
  machine states, publish-before-apply two-phase send)

**Deliverable — write to `ai-tasks/survives-rewrite-diff-2026-07.md`:**
- A **file/class-level table**: `marmot-cs` file → responsibility → `cgka-engine`
  equivalent → verdict (survives/modify/rewrite/new) → estimated %-reusable
  where applicable → evidence (`file:line` both sides).
- The **`Scramble.Marmot.Engine` module list** this implies — cross-check
  against the proposed layout in scoping doc §10 (`Engine`, `AppComponents`,
  `Identity.AccountProof`, etc.) and refine it if the line-level read reveals
  something the coarse-grained layout missed.
- A **build order**: what has to exist before what (e.g. can `Engine`'s
  message-processing skeleton be stubbed before `Convergence` is fully built,
  given step 2's finding that a v1 could skip the fast-path per convergence
  deep-dive §9?).
- Any **new `dotnet-mls` generic-capability questions** this surfaces, framed
  as permission-gated proposals per the existing boundary rule — cross-check
  against the four items in scoping §12 (a-d) so you don't duplicate; only
  flag genuinely new gaps.
- A revised **size/risk estimate** for the engine-orchestration piece
  specifically (S/M/L/XL + top 2-3 unknowns), to fold into the overall
  Dark Matter estimate alongside step 2's convergence estimate (**L**,
  already settled — see START-HERE step 2).
- Every nontrivial claim tagged 🟢 verified / 🟡 inferred / 🔴 needs-more,
  with the evidence path (`file:line` or `gh` path) backing it.

**When done:**
1. Update `ai-tasks/00-START-HERE-dark-matter.md`'s next-step list: mark
   step 3 done (same `~~strikethrough~~ **✅ DONE (date)**` style already used
   for steps 1-2), with a one-line pointer + headline finding. Point at step 4
   (account-identity-proof v2) as next, and note whether step 5 (phased build
   order + date-with-confidence-band) can now be drafted given steps 2+3 are
   both done.
2. Do NOT delete this prompt file yourself — leave that to the user.
3. Verify both file edits are actually on disk (read them back) before
   ending your turn — a prior session in this migration once claimed
   completion without having written anything; don't repeat that.

Report a concise summary (under 250 words): the headline survives/rewrite
split, the resulting module list, the build-order shape, and the new size
estimate.
