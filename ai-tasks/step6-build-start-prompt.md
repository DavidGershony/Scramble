> ⚠ **TEMPORARY — session handoff scratch prompt.**
> **Delete it once P0 has landed** (storage foundation merged on
> `feat/dark-matter`, unit tests green) and the plan doc's phase table is the
> only tracker you need.

# Step 6 — start the build: `Scramble.Marmot` P0 + three zero-cost unblockers

Paste the block below into a fresh session (or say: "run step 6 in
`ai-tasks/step6-build-start-prompt.md`"). Self-contained.

Recommended model: `fable`. This is the first step that writes engine code.

---

## PROMPT

You are starting the **build** phase of the Scramble → Dark Matter (Marmot)
migration. Planning is finished (steps 1–5, all ✅). Read these before writing
code — do not build from memory:

1. `ai-tasks/scramble-marmot-phased-plan-2026-08.md` — **the authoritative plan.**
   §3 phase table (you are doing the pre-tasks + **P0**), §4 dotnet-mls asks,
   §5 WN questions, §7 cutover rules.
2. `ai-tasks/00-START-HERE-dark-matter.md` — orientation, constraints, code state.
3. `ai-tasks/dark-matter-migration-scoping-2026-07.md` §10 (module layout), §12
   (dotnet-mls capability audit). Note its step-5 erratum header.
4. `ai-tasks/survives-rewrite-diff-2026-07.md` §2.1 (the storage + snapshot rows
   — ~70% survives) and §3 (the `Scramble.Marmot.Storage` extension list).
   Note its erratum header: `SendIntent::SelfUpdate` is back; `AppDataUpdate` is
   a hard blocker; the storage trait upstream grew 34 → 79 methods behind new
   sub-traits.
5. `CLAUDE.md` — repo invariants (I2 CI categories, I3 test-first on services,
   I4 landing discipline, I5 pivot freeze) and the "Dark Matter cutover rules".

**Hard constraints (do not violate):**
- Do NOT modify `lib/marmot-cs` — read-only reference until cutover.
- Do NOT modify `lib/dotnet-mls` without explicit user permission. Reading and
  building on it is free; gaps become permission-gated *generic* RFC-9420
  proposals, never inline edits, never Marmot semantics in the library.
- `Scramble.Marmot` is **standalone**: no project reference to `marmot-cs`;
  surviving codecs are **ported in** deliberately.
- Reference pin: **`wn-agent-v0.9.10`** (`marmot-protocol/mdk`, crates
  `cgka-engine` + `traits` + `transport-nostr-peeler`, via `gh api`). Upstream
  moves ~8 commits/day — do not silently re-pin; if you need a newer tag, say so.
- Branch: `feat/dark-matter`. Commits ≤ 8 files (I4).
- **No `Scramble.Marmot` types in `Scramble.Presentation`**, and generic Nostr
  crypto (NIP-44/59) goes in a non-Marmot namespace (plan §7).
- **Legacy account-identity-proof (`0xf2f1`) is OUT OF SCOPE** (user,
  2026-08-10): assume Whitenoise drops it and always will. Build **only** the
  Current `0x8009` construction. Do not build the Legacy TLV codec, its event
  template, or its vectors, and do not re-open the question.

### Task, in order

1. **`dotnet-mls` per-leaf accessor check** (plan §4 item 6). Read-only, ~1 hour,
   no permission needed. Confirm `LeafNode.Extensions` and `SignatureKey` are
   reachable for (i) every ratchet-tree leaf after processing a Welcome and
   (ii) leaves inside a staged commit's Add/Update proposals. Report present or
   absent with `file:line` evidence. If absent, write it up as a generic
   read-only accessor proposal — do not implement it.
2. **Implement the two approved `dotnet-mls` changes** — ✅ **permission was
   GRANTED by the user on 2026-08-10** (plan §4, the green callout): **(c)**
   PublicMessage produce (commit + proposal) and proposal-consume verification,
   including the 🔴 membership_tag-on-Proposal question against RFC 9420 §6.2
   (resolve it against the spec *before* relying on it); and **(e)** the
   `AppDataUpdate` proposal type (`0x0008`; note `SelfRemove` is `0x000a`, not
   adjacent). Both are already verified against source — do not re-derive them,
   and do **NOT** re-open safe-export (resolved and dropped from v1).
   **Bounds on the grant:** items 3–5 of plan §4 ((b) SelfRemove, (d) retained
   past-epochs, (f) staged-commit introspection) are **NOT** approved — ask
   separately. Land (c) and (e) as **separate commits** with tests (I4), as
   **generic** MLS features with no Marmot constants, no `0xf2..` IDs, no Nostr
   coupling. `lib/dotnet-mls` builds and tests independently of the rest of the
   solution, so this work is not blocked by breakage elsewhere.
3. **Draft the five Whitenoise questions** (plan §5) as a message the user can
   send. Do not send anything yourself.
4. **Build P0 — storage foundation.** Scope, exit criteria and tests are in the
   plan's phase table. Concretely: create the standalone `Scramble.Marmot`
   project(s) per scoping §10 + diff §3; port
   `MarmotCs.Storage.Abstractions` + `Sqlite`; add the DM record states
   (`Created/Retryable/PeelDeferred/Processed/Failed/EpochInvalidated/Sent`),
   `QueuedOutboundIntent`, `LeaveRequest`, validated-tree marker, group
   `removed` + `join_epoch`, storage transactions, and the **epoch-anchored
   snapshot API** (create/rollback/release/prune — prune by
   `current_epoch − max_rewind_commits`, not max-count). Split the interface
   along upstream's sub-trait lines now (`OutboundFanoutStorage`,
   `Disband*Storage`, `KeyPackageBundleStorage`, `MaintenanceStorage`,
   `ConvergencePassStorage`) even where implementations are stubs — retrofitting
   that split later is the expensive path.
5. **CI (I2).** Add the `MarmotEngine` test category to **both**
   `.github/workflows/integration.yml` and `docs/ci-setup.md` in the same PR
   that first uses it. (`ConformanceVector` and `DarkMatterInterop` follow at
   P2/P3 — don't add them speculatively.)
6. **Tests lead, not trail** (I3 + plan testing strategy): round-trip tests for
   every record type, and snapshot create/rollback/release/prune exercised under
   a transaction.
7. Do NOT delete this prompt file yourself — leave that to the user.
8. Verify every file edit is actually on disk (read back / `wc -l`) before
   ending your turn.

Report concisely (≤250 words): the accessor-check result, the two permission
asks (as questions for the user), the WN message draft, and P0's state against
its exit criteria.
