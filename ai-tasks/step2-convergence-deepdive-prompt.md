> ⚠️ **TEMPORARY — DO NOT COMMIT THIS FILE.** It is a session handoff scratch prompt.
> **Delete it once step 2 (the convergence deep-dive) is done** and its output doc
> (`convergence-deepdive-2026-07.md`) exists.

# Step 2 — Convergence deep-dive (session handoff prompt)

Paste the block below into a fresh session (or say: "run the convergence deep-dive
in `ai-tasks/step2-convergence-deepdive-prompt.md`"). It is self-contained.

---

## PROMPT

You are continuing the **Scramble → Dark Matter (Marmot)** migration. Before doing
anything, read these three files for full context — do not plan from memory:

1. `ai-tasks/00-START-HERE-dark-matter.md` — orientation + constraints + next-steps.
2. `ai-tasks/dark-matter-migration-scoping-2026-07.md` — the authoritative plan;
   **§11 (risks)** and **§12 (capability-check results, already DONE)** especially.
3. `CLAUDE.md` — repo/platform rules.

**Hard constraints (do not violate):**
- Do **NOT** modify `lib/marmot-cs` (it's the live engine; clean cutover later).
- Do **NOT** modify `lib/dotnet-mls` without explicit user permission.
- **No Marmot protocol may leak into `dotnet-mls`** — it stays generic RFC-9420.
- This step is **research/documentation only — write no engine code.** Output is a
  written analysis doc.

### Your task: deep-dive Dark Matter's distributed-convergence subsystem

This is the **prime timeline risk** (scoping §11.1) and the least-understood module —
it has **no analog** in our code (`CommitRaceResolver` is a *discarded* approach that
Dark Matter forbids: you may NOT pick group state by relay `created_at`/event-id/
arrival-order). Goal: understand it well enough to **size the rewrite** and design
`Scramble.Marmot.Convergence`.

**Sources (reach via authenticated `gh api`; confirm `gh auth status` first):**
- Spec: GitHub `marmot-protocol/marmot`, file `protocol-core/convergence.md` (+ any
  neighboring `protocol-core/*` it references: canonicalization, epoch/commit rules).
- Rust: GitHub `marmot-protocol/mdk`, tag **`v0.9.4`**, crate `crates/cgka-engine`,
  modules: `src/distributed_convergence*`, `src/canonicalization*`, `src/convergence*`,
  `src/fork_recovery*`, `src/epoch_manager*`, and how `src/engine*` drives them. Also
  check for a conformance/simulator harness (tests or a `sim`/`conformance` module).
- Cross-check (optional, same-shape Kotlin reference): `vitorpamplona/amethyst`,
  package `com.vitorpamplona.quartz.marmot` — how *they* implement convergence.

**Suggested method:** fan out parallel read-only agents (they can call `gh api`),
one per concern, then synthesize. Concerns to cover:
1. **Branch-selection algorithm** — witness quorum, rewind horizon/window, tip
   priority, settlement/quiescence. What exactly decides the winning branch, and on
   what inputs (NOT relay ordering)?
2. **Canonicalization** — how commits/epochs are canonically ordered/hashed; the
   deterministic tiebreak.
3. **Epoch state machine** (`epoch_manager`) — states
   `Stable/PendingPublish/Merging/Recovering/Unrecoverable` and transitions.
4. **Fork recovery** — same-epoch commit rollback/replay; ties to retained-past-epochs
   (scoping §12(d), currently ABSENT in dotnet-mls).
5. **Publish-before-apply** — the two-phase send gated on relay ack; control-flow
   inversion vs our current apply-then-publish.
6. **Conformance simulator** — does upstream ship one? Can we mirror its vectors
   (cf. Amethyst's `mdk-vector-gen` pattern) to test `Scramble.Marmot.Convergence`?

**Deliverable — write to `ai-tasks/convergence-deepdive-2026-07.md`:**
- A **module + data-flow map** of the convergence subsystem (what calls what, with
  the key state/inputs).
- The **branch-selection + canonicalization algorithm** in prose + pseudocode,
  precise enough to reimplement.
- **What `Scramble.Marmot.Convergence` must contain** (module list) and where it
  plugs into the engine/transport.
- **Dependencies on generic-MLS gaps** already identified in §12 (esp. retained
  past-epochs (d), PublicMessage (c)) — flag anything convergence needs from
  `dotnet-mls` as a **permission-gated, generic** proposal.
- A **size/risk estimate** (S/M/L/XL + the top 2–3 unknowns) to feed the
  date-with-confidence-band for Whitenoise.
- Every nontrivial claim tagged 🟢 verified / 🟡 inferred / 🔴 needs-more, with the
  `gh` path or `file:line` evidence.

When done, update `ai-tasks/00-START-HERE-dark-matter.md`'s next-step list (mark
step 2 done, point to steps 3–4: survives/rewrite diff + account-identity-proof v2).
