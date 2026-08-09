> ⚠️ **TEMPORARY — session handoff scratch prompt.**
> **Delete it once step 5 is done** and its output doc
> (`scramble-marmot-phased-plan-2026-08.md`) exists.

# Step 5 — `Scramble.Marmot` phased build plan + date-with-confidence-band (session handoff prompt)

Paste the block below into a fresh session (or say: "run step 5 in
`ai-tasks/step5-phased-plan-prompt.md`"). Self-contained.

Recommended model: `fable` (standard context is enough — inputs are the five
planning docs plus targeted upstream diffs, not bulk source).

---

## PROMPT

You are continuing the **Scramble → Dark Matter (Marmot)** migration. This is
**step 5, the final planning step**: produce the phased build order and a
**date with a confidence band** for Whitenoise (WN). Read these before doing
anything — do not plan from memory:

1. `ai-tasks/00-START-HERE-dark-matter.md` — orientation; steps 1–4 all ✅ done.
2. `ai-tasks/dark-matter-migration-scoping-2026-07.md` — the plan (§10 layout,
   §12 dotnet-mls audit).
3. `ai-tasks/survives-rewrite-diff-2026-07.md` — step 3. **§4 is the build-order
   skeleton, §6 the engine estimate (L) — your primary inputs.**
4. `ai-tasks/convergence-deepdive-2026-07.md` — step 2 (Convergence = L). Skim
   §9, §11–12 only.
5. `ai-tasks/account-identity-proof-v2-2026-08.md` — step 4. **§0 (stale-pin
   finding) and §6 (actions) feed directly into this step.**
6. `ai-tasks/protocol-agnostic-report-2026-08.md` §6 — cutover rules (binding,
   also in CLAUDE.md).
7. `CLAUDE.md` — repo invariants (esp. I2 CI categories, I4 landing discipline)
   and the "Dark Matter cutover rules" section.

**Hard constraints (do not violate):**
- Do NOT modify `lib/marmot-cs` (read-only). Do NOT modify `lib/dotnet-mls`
  without explicit user permission — gaps are permission-gated generic
  proposals only.
- `Scramble.Marmot` is standalone: no project ref to marmot-cs; codecs ported in.
- No engine code this step — this is the plan, not the build.
- Branch: `feat/dark-matter`.

### Task, in order

1. **Re-pin the upstream reference.** `v0.9.4` is stale (proof format hard-broke
   at `wn-agent-v0.9.5` — see proof doc §0). Pick the latest `wn-agent-v0.9.x`
   tag (was 0.9.10 on 2026-08-09; check `gh api repos/marmot-protocol/mdk/tags`).
2. **Drift-diff v0.9.4 → the new pin** over the modules already analyzed:
   `engine.rs`, `message_processor/*`, `group_lifecycle.rs`, `publish.rs`,
   `epoch_manager.rs`, `fork_recovery.rs`, `convergence*`/`canonicalization*`,
   `wire_format.rs`, `app_components.rs`. Use `gh api` compare or per-file
   fetch + diff. You are looking for **semantic/wire changes** that invalidate
   step-2/3 findings (like the proof break), not refactors. Record each finding
   with 🟢/🟡/🔴 + evidence; update the affected docs' headers with a one-line
   erratum if anything material changed (do not rewrite them).
3. **Draft the phased build plan** → `ai-tasks/scramble-marmot-phased-plan-2026-08.md`:
   - Phases from the diff doc §4 skeleton (storage → EpochManager →
     AccountProof → Peeler/Wire → AppComponents → engine v1 fast-path →
     auto-committer/leave → Convergence (parallel) → hardening → capabilities),
     adjusted for anything the drift-diff found.
   - Per phase: scope, exit criteria, **tests** (unit / conformance-vector /
     interop — the vectors strategy: mirror upstream's JSON conformance
     vectors + Amethyst-style `mdk-vector-gen`; interop against the deployed
     `wn-agent` over `docker-compose.test.yml`), and the CI category work
     (I2: new category added to `integration.yml` + `docs/ci-setup.md`).
   - The **dotnet-mls permission-gated proposal list**, sequenced: (c)
     PublicMessage produce/verify [critical-path], (b) SelfRemove + proposal
     store, (d) retained past-epochs, (e) AppDataUpdate + safe-export 🔴
     construction check first, (f) staged-commit introspection 🔴 verify, plus
     the per-leaf accessor check (proof doc §4). Each framed generically.
   - **Questions for WN** (send early): which mdk tag is deployed? do any
     production groups still require `0xf2f1` legacy proofs? when does deployed
     WN flip? Their answers gate Legacy-proof support and the hard deadline.
   - **Date with confidence band**: aggregate Engine L + Convergence L +
     AppComponents M + AccountProof S + dotnet-mls items + integration/cutover
     M + the drift-diff's deltas. Give optimistic / expected / pessimistic
     with the top 3 risks driving the band. State assumptions (person-count,
     review latency) explicitly.
4. **Update `ai-tasks/00-START-HERE-dark-matter.md`**: mark step 5 done (same
   strikethrough style), point at the plan doc as the new authoritative
   next-steps source, and update the "holding answer for WN" with the real
   date band.
5. Do NOT delete this prompt file yourself — leave that to the user.
6. Verify every file edit is actually on disk (read back / `wc -l`) before
   ending your turn.

Report a concise summary (≤250 words): the new pin + drift-diff headline, the
phase list with the first interop milestone, the WN questions, and the date
band.
