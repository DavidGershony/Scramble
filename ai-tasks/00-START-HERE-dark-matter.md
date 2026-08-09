# START HERE — Marmot / Dark Matter migration (state as of 2026-07)

Orientation for a clean session. Read this first; it tells you what's current,
what's superseded, where the code stands, and what to do next.

## The situation in one paragraph

Whitenoise (the **Marmot reference implementation**) is fully committed to the
"**Dark Matter**" rewrite (Rust `mdk` 0.9.x, the `cgka-engine` monorepo) and has
asked us for a migration date. Dark Matter is therefore Scramble's target; the
0.7/0.8-era Marmot protocol is legacy. Dark Matter keeps the **RFC 9420 MLS core**
but **rewrites the Marmot layer above it** (engine, convergence, epoch state
machine, feature registry, account-identity-proof, app-components, new
wire-format policy).

## The decision (settled with the user)

- **Target:** Dark Matter (mdk `v0.9.4` `cgka-engine`), not the 0.8.0 line.
- **Approach:** **Option A′** — build the Dark Matter engine as a **new
  Scramble-internal project (`Scramble.Marmot`)**, reusing our proven Nostr
  crypto/codecs, and **retire `marmot-cs`'s engine** (the `marmot-cs` library
  never got external adoption, so there's no library API to preserve).
- **`dotnet-mls` stays as-is** — it is a **generic RFC 9420 MLS library, NOT
  Marmot code.** Marmot-specifics (`0xf2f1` leaf ext, app-components, identity
  proof) are built in `Scramble.Marmot` *above* it. Do not Marmot-ify dotnet-mls.
- **Not chosen (fallback only):** FFI-binding the Rust `mdk`. Revisit only if the
  hand-roll parity-chase proves unsustainable.

## Authoritative document

➡ **`dark-matter-migration-scoping-2026-07.md`** — the current plan. Contains: what
Dark Matter is, the evidence-based **reusability map** (what survives vs rewrite),
the proposed `Scramble.Marmot` module layout, the top rewrite risks, and the next
steps. **This is the doc to work from.**

## Superseded documents (do NOT plan from these)

The Dark Matter pivot obsoleted the earlier spec/0.8.0-parity work. Kept for
history only:

- `marmot-protocol-compliance.md` — original spec-based plan. SUPERSEDED.
- `marmot-protocol-compliance-delta-2026-07.md` — spec delta. SUPERSEDED.
- `mdk-parity-plan-2026-07.md` — 0.8.0 parity plan. SUPERSEDED.
- `mdk-parity-units/` — 0.8.0 work-order units. SUPERSEDED (a couple of findings,
  e.g. the account-identity-proof v2 construction, were carried forward into the
  scoping doc).

## Code state (git)

- Branch **`feat/marmot-batch1-protocol-v3`** (parent `34c297f` → `8e21e4e`;
  submodule `lib/marmot-cs` `7be4f20` → `a55e527`). Not pushed.
- It contains committed 0.8.0-era work. **Heads-up — some of it targets formats
  Dark Matter abandoned:**
  - `C.1.a` extension-v3 `disappearing_message_secs` on the `0xf2ee`
    `NostrGroupData` extension → **Dark Matter dropped `0xf2ee`**; group routing is
    now app-component `0x8004`, disappearing-messages is a `message-retention`
    app-component. Largely obsolete for Dark Matter.
  - `C.1.d` encoding-tag *require*, `C.3.c` 0xF2EE-on-accept → old model.
  - **Survives:** the MLS core (`dotnet-mls`), `GroupEventEncryption`, NIP-44/59,
    the min-length-28 fix (C.3.a), inner-sender verification (C.2.d).
- Decision to make in a fresh session: leave the branch as-is (historical) and
  start `Scramble.Marmot` fresh, vs. cherry-pick the surviving pieces. Recommend
  **start fresh, port the surviving codecs deliberately.**

## What survives vs rewrite (headline — full matrix in the scoping doc §10)

- **Survives/port:** `dotnet-mls` (as-is, generic MLS); `GroupEventEncryption`
  (kind:445 ChaCha20-Poly1305, exact match); NIP-44 + NIP-59 gift wrap; kind
  445/444/30443 event builders (port — must **drop the `encoding` tag**, enforce
  spec tag cardinality, add `app_components` tag + NIP-40 expiration).
- **Rewrite/new:** `NostrGroupData 0xf2ee` codec (→ app-components); the
  `Mdk.cs` engine (→ epoch state machine + publish-before-apply); `CommitRaceResolver`
  (→ convergence — DM **forbids** choosing state by relay created_at/id);
  account-identity-proof `0xf2f1`; the app-components subsystem; and the hard
  center — **distributed convergence/canonicalization** (has a conformance
  simulator upstream; no analog in our code).

## Constraints (user, 2026-07-21)

- Build the Dark Matter engine as a **standalone `Scramble.Marmot`** project
  **without touching `lib/marmot-cs`** — it stays the live impl; switch over only
  when the new engine is ready (clean cutover). `Scramble.Marmot` must be
  self-contained (no project ref to marmot-cs); surviving codecs are **ported in**.
- **Do not modify `lib/dotnet-mls` without explicit permission.** Read/build on it
  freely (generic RFC-9420 MLS). Needed generic-MLS additions are surfaced as
  permission-gated proposals, not inline edits.

## Next step (start of the next working session)

1. ~~**`dotnet-mls` generic-capability check**~~ **✅ DONE (2026-07-21) — see scoping
   doc §12.** Result: (a) opaque custom leaf/GroupContext extensions **PRESENT** (no
   lib change needed — the big relief); (b) SelfRemove **ABSENT** (closed enum,
   generic add, MEDIUM); (c) PublicMessage framing **PARTIAL** — consume-commit
   present, but produce-commit/proposal + proposal-verify missing, plus a
   membership_tag-on-Proposal spec item to confirm (MEDIUM); (d) retained
   past-epochs **ABSENT** (generic add, MEDIUM-LARGE). All gaps are generic
   RFC-9420, not Marmot — and permission-gated per the constraint above.
2. ~~**Convergence deep-dive**~~ **✅ DONE (2026-08-04), corrected and resized
   same day — see `convergence-deepdive-2026-07.md`.** Headline: the
   branch-selection algorithm itself (witness quorum + rewind horizon + tip
   priority + digest tiebreak) is small and precisely specified (spec text and
   Rust code agree exactly) — **not** the risk driver. An initial pass claimed
   `dotnet-mls` was missing a snapshot/restore/non-mutating-replay-probe
   capability; that was **wrong and has been retired** — `MlsGroup` already
   exposes `Export()`/`Import()` plus a stage/merge/discard commit model,
   which covers the need entirely inside `Scramble.Marmot` with **zero
   `dotnet-mls` changes**. See `scramble-marmot-snapshot-restore-spec-2026-07.md`.
   A follow-up read of `openmls_projection.rs` (§13 of the deep-dive) sized
   `CandidateMaterializer` at **L**. **Overall convergence-subsystem estimate:
   L** (not XL). Upstream ships a portable, semantic JSON conformance-vector
   format (including convergence-specific vectors) Scramble can mirror cheaply
   for testing.
3. ~~**Confirm the survives/rewrite split**~~ **✅ DONE (2026-08-09) — see
   `survives-rewrite-diff-2026-07.md`.** Headline: `Mdk.cs` → rewrite with
   ~15–20% line-shape reusable (revised **down** from ~30%); `CommitRaceResolver`
   → rewrite, 0%. Two pleasant surprises survive: the storage-provider
   **snapshot API** (same primitive DM's fork recovery uses) and the
   staged-commit (stage→publish→merge) dotnet-mls interaction pattern. New
   findings: DM's engine is transport-agnostic behind a `TransportPeeler` seam
   (add a `Scramble.Marmot.Peeler` boundary); PURE_PLAINTEXT makes the
   dotnet-mls PublicMessage-produce gap (§12c) **critical-path**; one new
   dotnet-mls question (AppDataUpdate proposal + safe-export construction, 🔴).
   Engine-orchestration sized **L**. Build order in the doc §4: engine v1
   (fast-path only) is interop-testable before Convergence lands.
4. **Pin down account-identity-proof v2** — the kind:450 canonical-event Schnorr
   signing construction (MUST-reject on target; needed early). **← next.**
5. Then draft the `Scramble.Marmot` phased build order + a **date-with-confidence-
   band** for WN. **Steps 2+3 are both done, so step 5 is unblocked once step 4
   confirms the AccountProof size** — the diff doc §4 already contains the
   build-order skeleton and §6 the per-piece estimates to fold in. The
   dotnet-mls snapshot/restore spike flagged in an earlier version of this step
   is **no longer needed** — that capability already exists (see step 2's
   correction above; reconfirmed by step 3 against all three probe patterns).

**Holding answer for WN meanwhile:** "Committed to Dark Matter. Building it as a
fresh engine in our stack, reusing our proven codecs; sizing the convergence/engine
rewrite now — date in ~N weeks."

**Related decision (2026-08-09):** evaluated making `Scramble.Marmot`
protocol-agnostic (Concord / NIP-29, Armada-style) before finishing the
migration — **decided NO**; agnosticism belongs at the app-layer conversation
seam, not in the engine. Two zero-cost rules adopted for the cutover (no Marmot
types in ViewModels; generic Nostr crypto in a non-Marmot namespace). See
`protocol-agnostic-report-2026-08.md`.

## How the reference sources are reached

- Rust MDK: GitHub `marmot-protocol/mdk` (monorepo), tag `v0.9.4`, crate
  `crates/cgka-engine`. Use `gh api` (authenticated).
- Spec: GitHub `marmot-protocol/marmot` — surface-organized docs
  (`foundation/`, `protocol-core/`, `app-components/`, `transports/nostr.md`); the
  old MIP-00…05 files are deprecated (`mip-coverage.md` maps them).
- Reference peer: **Amethyst** (`vitorpamplona/amethyst`, `com.vitorpamplona.quartz.marmot`)
  is an independent Kotlin Marmot impl — a same-shape reference; note its
  `quartz/tools/mdk-vector-gen` test-vector pattern (generate vectors from mdk,
  test your impl) — adopt it for `Scramble.Marmot`.
