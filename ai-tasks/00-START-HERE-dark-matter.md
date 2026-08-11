# START HERE — Marmot / Dark Matter migration (state as of 2026-08-09)

Orientation for a clean session. Read this first; it tells you what's current,
what's superseded, where the code stands, and what to do next.

> **Planning is complete (steps 1–5 all ✅).** The authoritative next-steps
> document is **`scramble-marmot-phased-plan-2026-08.md`** — phased build order,
> tests and CI categories per phase, the sequenced `dotnet-mls` permission asks,
> the questions for Whitenoise, and the date band. Read this file for
> orientation, then work from that one.

## The situation in one paragraph

Whitenoise (the **Marmot reference implementation**) is fully committed to the
"**Dark Matter**" rewrite (Rust `mdk` 0.9.x, the `cgka-engine` monorepo) and has
asked us for a migration date. Dark Matter is therefore Scramble's target; the
0.7/0.8-era Marmot protocol is legacy. Dark Matter keeps the **RFC 9420 MLS core**
but **rewrites the Marmot layer above it** (engine, convergence, epoch state
machine, feature registry, account-identity-proof, app-components, new
wire-format policy).

## The decision (settled with the user)

- **Target:** Dark Matter (mdk `cgka-engine`), not the 0.8.0 line. **Reference
  pin: `wn-agent-v0.9.10`** (re-pinned from `v0.9.4` at step 5, 2026-08-09 —
  the account-identity-proof format hard-broke at `wn-agent-v0.9.5`).
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

➡ **`scramble-marmot-phased-plan-2026-08.md`** (step 5, 2026-08-09) — **the doc to
work from.** Phased build order P0–P12, per-phase exit criteria + tests + CI
categories, the sequenced `dotnet-mls` permission asks, the questions for
Whitenoise, and the date band. Planning is finished; this is the build plan.

Supporting (read for depth, don't plan from):
- `dark-matter-migration-scoping-2026-07.md` — what Dark Matter is, the
  reusability map, `Scramble.Marmot` module layout (§10), the `dotnet-mls`
  capability audit (§12). Its §9 "next steps" is **superseded** by the plan doc.
- `survives-rewrite-diff-2026-07.md` (step 3) · `convergence-deepdive-2026-07.md`
  (step 2) · `account-identity-proof-v2-2026-08.md` (step 4) — the evidence
  behind the sizes. All three carry a step-5 erratum header; read it first.
- `protocol-agnostic-report-2026-08.md` §6 — the cutover rules (also in
  `CLAUDE.md`, also restated in plan §7).

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

- **Current branch: `feat/dark-matter`** — planning only so far (steps 1–5:
  scoping, convergence deep-dive, survives/rewrite diff, account-identity-proof,
  phased plan). **No engine code has been written yet.** The build starts at
  plan §3 phase **P0**.
- Older branch **`feat/marmot-batch1-protocol-v3`** (parent `34c297f` → `8e21e4e`;
  submodule `lib/marmot-cs` `7be4f20` → `a55e527`). Not pushed. **Leave it as
  historical** — decision settled: start `Scramble.Marmot` fresh and port the
  surviving codecs deliberately (plan P3).
- That older branch contains committed 0.8.0-era work. **Heads-up — some of it
  targets formats Dark Matter abandoned:**
  - `C.1.a` extension-v3 `disappearing_message_secs` on the `0xf2ee`
    `NostrGroupData` extension → **Dark Matter dropped `0xf2ee`**; group routing is
    now app-component `0x8004`, disappearing-messages is a `message-retention`
    app-component. Largely obsolete for Dark Matter.
  - `C.1.d` encoding-tag *require*, `C.3.c` 0xF2EE-on-accept → old model.
  - **Survives:** the MLS core (`dotnet-mls`), `GroupEventEncryption`, NIP-44/59,
    the min-length-28 fix (C.3.a), inner-sender verification (C.2.d).

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

> **Steps 1–5 are planning and are all ✅ done — skim them for context, then go
> to step 6 at the bottom, which is where the next session actually starts.**

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
4. ~~**Pin down account-identity-proof v2**~~ **✅ DONE (2026-08-09) — see
   `account-identity-proof-v2-2026-08.md`.** Headline: the construction is fully
   pinned with an official spec test vector; size revised **M → S**; designed
   for external signers (Amber/NIP-46 signs the kind:450 template — good news);
   zero new dotnet-mls needs (one small read-accessor check flagged 🔴).
   **⚠ Strategic finding: our `v0.9.4` pin is stale** — the proof format
   hard-broke at `wn-agent-v0.9.5` (0xf2f1 ext → app-component `0x8009`, no
   fallback); mdk's live tag series is now `wn-agent-v0.9.x` (latest 0.9.10).
5. ~~Draft the `Scramble.Marmot` phased build order + a **date-with-confidence-
   band** for WN.~~ **✅ DONE (2026-08-09) — see
   `scramble-marmot-phased-plan-2026-08.md`. ➡ That doc is now the
   authoritative next-steps source; planning is finished, building starts.**
   Headline: reference **re-pinned `v0.9.4` → `wn-agent-v0.9.10`**; the
   drift-diff over every already-analyzed module found the step-2/step-3
   analysis **intact** (convergence v1 constants byte-identical, branch
   selection untouched, app-component IDs stable incl. `0x8004` routing) with
   four corrections: `SendIntent::SelfUpdate` is **back** (step 3 wrongly had it
   as DROP); `AppDataUpdate` (`0x0008`) is a **RequiredCapabilities entry on
   every Current-profile group**, promoting dotnet-mls gap (e) to a hard blocker
   on create *and* join; three new engine subsystems (disband, maintenance,
   self-update) plus a storage trait grown 34 → 79 methods; and `IngestOutcome`
   gained a rejection taxonomy. Also: kind-445 tag shape is now strictly
   validated upstream (exactly one `h`, at most one `expiration`, nothing
   else), so our `encoding` tag would be rejected outright — the Wire.Nostr
   port is a correctness gate. Twelve phases (P0 storage → P6 engine v1 =
   **first interop milestone** → P8 convergence → P11 cutover); Convergence is
   **not** on the path to first interop.

6. **Start the build: `Scramble.Marmot` P0 + the three zero-cost unblockers.**
   **← THE NEXT SESSION STARTS HERE.** Handoff prompt:
   `ai-tasks/step6-build-start-prompt.md`. Work from
   `scramble-marmot-phased-plan-2026-08.md` §3. In order:
   - **(a) `dotnet-mls` per-leaf accessor check** — read-only, no permission
     needed, ~1 hour (plan §4 item 6). Confirm `LeafNode.Extensions` +
     `SignatureKey` are reachable for every post-Welcome ratchet-tree leaf and
     for leaves inside a staged commit's Add/Update proposals. Any gap becomes a
     trivially generic read-only accessor ask — better found now than mid-P2.
   - **(b) Raise the two blocking `dotnet-mls` permission asks with the user**
     (plan §4 items 1–2): PublicMessage produce/verify, and the `AppDataUpdate`
     proposal type + safe-export construction. Both block the critical path and
     both need lead time. **Do not edit `lib/dotnet-mls` before permission.**
   - **(c) Send Whitenoise the questions** (plan §5). Q2 (legacy proof) is
     **decided and closed** — user, 2026-08-10: assume WN drops `0xf2f1` and
     always will; Scramble builds **only** the Current `0x8009` construction.
     Inform them of the assumption, don't ask. Q5 (disband-for-interop) can
     still *remove* work.
   - **(d) Then P0 — storage foundation** (plan §3 phase table): port
     `MarmotCs.Storage.Abstractions` + `Sqlite` into the new standalone
     `Scramble.Marmot`, add DM's record states / queued intents / leave requests
     / snapshot API, and split the interface along DM's sub-trait lines from the
     start. Exit criteria and tests are in the phase table.
   - Bind by plan §7's cutover rules and `CLAUDE.md` I2/I4/I5 from the first
     commit — they are free to follow now and expensive to retrofit.

**Answer for WN (real date band, 2026-08-09):** "Committed to Dark Matter,
building it as a fresh engine in our stack. **Wire interop with your agent
around the turn of the year — realistically late December 2026, mid-November if
the MLS-library work goes cleanly, mid-February 2027 at the pessimistic end.
Production cutover expected mid-May 2027 (band: mid-March → mid-September
2027).** The band is wide mostly because mdk is moving at ~8 commits/day — a
wire-stable tag we can target would narrow it materially." Assumptions
(one developer at ~0.8 FTE, permission turnaround off the clock, one re-pin
cycle budgeted) and the three risks driving the band are in plan §6.

**Related decision (2026-08-09):** evaluated making `Scramble.Marmot`
protocol-agnostic (Concord / NIP-29, Armada-style) before finishing the
migration — **decided NO**; agnosticism belongs at the app-layer conversation
seam, not in the engine. Two zero-cost rules adopted for the cutover (no Marmot
types in ViewModels; generic Nostr crypto in a non-Marmot namespace). See
`protocol-agnostic-report-2026-08.md`.

## How the reference sources are reached

- Rust MDK: GitHub `marmot-protocol/mdk` (monorepo), tag **`wn-agent-v0.9.10`**
  (the deployed-Whitenoise tag series; plain `v0.9.x` tags stop being the live
  line after 0.9.4), crates `crates/cgka-engine` + `crates/traits` (package
  `cgka-traits`) + `crates/transport-nostr-peeler`. Use `gh api` (authenticated).
  ⚠ Upstream moves fast — ~8 commits/day as of 2026-08; re-check the tag list
  (`gh api repos/marmot-protocol/mdk/tags`) before relying on a pinned reading.
- Spec: GitHub `marmot-protocol/marmot` — surface-organized docs
  (`foundation/`, `protocol-core/`, `app-components/`, `transports/nostr.md`); the
  old MIP-00…05 files are deprecated (`mip-coverage.md` maps them).
- Reference peer: **Amethyst** (`vitorpamplona/amethyst`, `com.vitorpamplona.quartz.marmot`)
  is an independent Kotlin Marmot impl — a same-shape reference; note its
  `quartz/tools/mdk-vector-gen` test-vector pattern (generate vectors from mdk,
  test your impl) — adopt it for `Scramble.Marmot`.
