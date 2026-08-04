# Dark Matter migration — scoping & architecture decision (2026-07)

**Status:** decision-support. No code. Purpose: size the migration and choose
between (A) hand-rolling Dark Matter in C# and (B) FFI-binding the Rust MDK, so we
can give Whitenoise a defensible date.

**Confidence key:** 🟢 verified this session · 🟡 informed inference · 🔴 needs a
deep-dive before it can carry a timeline commitment.

## 1. Context (why this supersedes the earlier plan)

Whitenoise — **the Marmot reference implementation** — is fully committed to the
"Dark Matter" rewrite (mdk 0.9.x, the `cgka-engine` monorepo) and has asked us for
a migration date. Dark Matter is therefore the target; the 0.7/0.8-era protocol is
legacy. The incremental parity plan (`mdk-parity-plan-2026-07.md`) and its units
targeted the 0.8.0 *deltas* and are **largely superseded** — this is a *migration*,
not a delta-chase. 🟢

## 2. What Dark Matter actually is 🟢

From `crates/cgka-engine` (mdk `v0.9.4`): **"OpenMLS-backed… OpenMLS owns MLS
validation and key schedule state. Marmot owns the application-facing state
machine, convergence policy, feature negotiation, and transport wrapping."**

**The RFC 9420 MLS core is unchanged.** Dark Matter is a rewrite of the Marmot
*layer above* MLS. `cgka-engine` module map (from its `lib.rs`):

- `engine` / `engine_metrics` — the `Engine<S>` state machine + reorg telemetry
- `identity` + **`account_identity_proof`** — signer/credential + the `0xf2f1`
  binding (new, MUST-reject on ≥0.9.x)
- **`feature_registry`** — runtime feature negotiation (replaces static constants)
- **`wire_format`** — now `PURE_PLAINTEXT_WIRE_FORMAT_POLICY` (0.7 was PURE_CIPHERTEXT,
  0.8 was MIXED_CIPHERTEXT — **third wire-format policy in three releases**)
- `provider` — OpenMLS provider (crypto + storage adapter)
- `group_lifecycle` — `create_group`, `join_welcome`
- `message_processor` — inbound `ingest` / outbound `send`
- **`distributed_convergence` / `canonicalization` / `convergence`** — the executable
  branch-selection policy (witness quorum, rewind window, settlement quiescence)
- `openmls_projection` — bytes-first OpenMLS↔model bridge
- **`epoch_manager`** — explicit epoch state machine
  (`Stable/PendingPublish/Merging/Recovering/Unrecoverable`)
- **`fork_recovery`** — same-epoch commit rollback/replay
- `publish` — publish-confirm / publish-failed lifecycle
- `capability_manager` / `capabilities` / `upgrade` — capability policy

## 3. Layer mapping → Scramble 🟡

| Dark Matter (`cgka-engine`) | Scramble layer | Impact |
|---|---|---|
| OpenMLS core (MLS validation, key schedule) | `dotnet-mls` | **Mostly survives** — RFC 9420 unchanged |
| Engine state machine, epoch_manager, publish lifecycle | `marmot-cs` (`Mdk.cs`) | **Rewrite** — we have a simpler staged-commit flow |
| convergence / canonicalization / fork_recovery | `marmot-cs` (`CommitRaceResolver`) | **Rewrite** — ours is a created_at/lex-id tiebreaker only |
| account_identity_proof (`0xf2f1` v2) | `dotnet-mls` (leaf ext) + `marmot-cs` | **New** — absent today |
| feature_registry / capability upgrade | `marmot-cs` | **New** — static today |
| wire_format = PURE_PLAINTEXT | `dotnet-mls` framing | **Change** — re-target policy |
| MIP-00…03 event codecs | `marmot-cs` (`MipXX`) | **Mostly survives** — same Nostr wire lineage |

**Headline:** `dotnet-mls`'s MLS investment is **not** thrown away. The rewrite is
concentrated in the `marmot-cs` layer we already own, plus a leaf-extension add in
`dotnet-mls`.

## 4. What survives — evidence-based (deep-dive agent vs `cgka-engine@v0.9.4` + `transports/nostr.md`) 🟢

The earlier optimistic "half of marmot-cs survives" was **too generous.** Verified
findings:

**Genuinely reusable (crypto/framing primitives):**
- `dotnet-mls` **crypto** (ciphersuite 0x0001, HPKE, Ed25519, HKDF) + **TLS codec**
  (`TlsReader/Writer`, QUIC varint) — SURVIVES.
- `GroupEventEncryption` (kind:445 ChaCha20-Poly1305, `MLS-Exporter("marmot",
  "group-event",32)`, empty AAD, base64, 28-byte floor) — **exact match**, SURVIVES.
- `Nip44Encryption` / NIP-59 `GiftWrap` — external standards, SURVIVE.
- kind:445 / 444 / 30443 event builders — **PORTABLE** but wrong today: the spec now
  says senders **MUST NOT** emit an `encoding` tag; we emit it on all three.

**Corrections to earlier claims (these do NOT survive):**
- **`NostrGroupData` `0xf2ee` extension → REWRITE.** Dark Matter **abandoned** the
  `0xf2ee` GroupContext extension. `nostr_group_id`+relays moved to a new
  **app-component `0x8004` (`NostrRoutingV1`)** in OpenMLS's `app_data_dictionary`;
  name/admins/image/retention split into **separate** app-components. **⚠ Our
  committed C.1.a (ext-v3 `disappearing_message_secs` on `0xf2ee`) targets an
  abandoned format** — disappearing-messages now lives in a `message-retention`
  app-component. C.3.c (0xF2EE-on-accept) and C.1.d (encoding-tag *require*) are
  likewise on the old model.
- **KeyPackages → PORTABLE-heavy + NEW.** Must add the **`0xf2f1`
  account-identity-proof** leaf extension (a Schnorr-signed kind:450 proof) — which
  Dark Matter **rejects KeyPackages/commits without** — plus a required
  `app_components` tag, and drop `encoding`. Absent from our code entirely.
- **`CommitRaceResolver` → REWRITE.** Dark Matter **forbids** using relay
  `created_at`/event-id/arrival-order to choose group state — the exact premise of
  our resolver. Replaced by the convergence/canonicalization model.
- **`dotnet-mls` → stays as-is (generic RFC 9420 MLS, NOT Marmot code).**
  Decision: the library is not modified for Marmot reasons — the Marmot-specific
  bits (`0xf2f1` leaf ext, app-components, identity proof) are built in the new
  `Scramble.Marmot` layer *above* it. The only open question is whether
  `dotnet-mls`'s **existing generic** MLS mechanisms suffice for that layer to
  build on: (a) carrying/reading **arbitrary custom leaf + GroupContext
  extensions** (opaque bytes), (b) the **SelfRemove** proposal type, (c)
  **PublicMessage** framing for handshakes (already present:
  `ProcessCommit(PublicMessage)` exists), (d) a retained past-epochs window. Any
  gap is a **generic RFC-9420 capability** question, decided on its own merits —
  not Marmot modification of `dotnet-mls`. 🔴 needs a scoped generic-capability
  check (see risk #4).

**Whole new subsystems (no analog in our code):** app-components model,
account-identity-proof, feature/capability registry, and — the hard center —
**distributed convergence / canonicalization** (backed upstream by a conformance
simulator).

## 5. Option A — hand-roll Dark Matter in C#

Evolve `marmot-cs` to the `cgka-engine` model; keep `dotnet-mls`; pure-managed stack.

- **Effort:** **L–XL.** New: engine epoch state machine, full convergence policy
  (witness quorum / rewind / quiescence), fork recovery, feature registry,
  account-identity-proof v2, wire-format re-target. 🔴 (needs the deep-dive to size)
- **Pros:** no native-binding/build complexity; `dotnet-mls` investment preserved;
  full control; Amethyst is a working same-shape reference (own Kotlin stack) to
  mine, incl. their mdk-vector-gen test pattern.
- **Cons:** **perpetual parity chase** against a fast-moving pre-1.0 reference
  (3 wire-format policies in 3 releases is the warning sign); every future WN
  change is our reimplementation work; high risk of subtle convergence divergence.

## 6. Option B — FFI-bind the Rust MDK

Retire hand-rolled `marmot-cs` (and possibly much of `dotnet-mls`); call `mdk`
through a native shim, reusing the `Scramble.Native` Rust precedent.

- **Feasibility:** mdk ships **`marmot-uniffi` = UniFFI (Kotlin/Swift only)**.
  **No C# target.** 🟢 So the path is a **C-ABI wrapper crate around mdk + P/Invoke**
  (or the community `uniffi-bindgen-cs`, unmaintained → risky). 🔴 Shim effort
  unquantified.
- **Effort:** **L** up-front (shim + cross-platform native builds for Android +
  Windows/Linux/macOS desktop), then **near-zero parity maintenance**.
- **Pros:** **kills the parity chase** — wire-compat with WN guaranteed by
  construction; auto-inherits every Dark Matter change; the reference team does the
  protocol work.
- **Cons:** binding a **pre-1.0, fast-changing API** (breakage at the FFI seam);
  own the native build/packaging for every Scramble platform; larger threading/
  memory-safety surface; `dotnet-mls` largely retired (sunk investment); Android +
  desktop native distribution complexity.

## 7. Comparison & lean 🟡

| Axis | A: hand-roll C# | B: FFI Rust mdk |
|---|---|---|
| Up-front effort | L–XL | L |
| Ongoing maintenance | **High (forever)** | **Low** |
| Wire-compat certainty | Medium (our reimpl) | **High (same code)** |
| Native-build burden | None new | High |
| `dotnet-mls` reuse | Full | Little |
| Precedent | Amethyst (Kotlin) | Scramble.Native (Rust) |

**My lean: Option B (FFI), _if_ the C-ABI shim proves tractable.** The decisive
factor is the parity-chase: this session is direct evidence that hand-rolling a
moving reference is a treadmill (0.7→0.8→0.9 rewrite + 3 wire-format policies in
months). Binding the reference implementation converts "reimplement forever" into
"rebuild a shim occasionally." The catch is real (no C# uniffi, pre-1.0 seam), so
this lean is **contingent on a spike** proving the shim + native-build story.

**When A wins instead:** if the FFI spike shows the C# binding/native-build cost is
prohibitive across Android + desktop, or if staying pure-managed is a hard product
constraint — then hand-roll, and lean hard on Amethyst as the reference + adopt
their mdk-vector-gen tests.

## 8. Must-confirm before giving WN a date 🔴

1. **FFI spike:** stand up a minimal C-ABI wrapper around one `mdk` call
   (e.g. create KeyPackage) and P/Invoke it from C# on Android **and** desktop.
   This single result decides A vs B and sizes B.
2. **Convergence semantics:** read `convergence.md` + `cgka-engine/src/convergence*`
   end-to-end to size Option A's hardest module.
3. **account-identity-proof v2:** exact signing construction (kind:450 canonical
   event) — needed either way (A implements it; B must feed the Nostr key to mdk).
4. **WN's own timeline:** when does *deployed* WN flip its pin to Dark Matter? That
   sets our hard deadline and whether a transition window needs any dual-running.
5. **Storage:** Dark Matter uses SQLCipher-backed `storage-sqlite`; check fit with
   Scramble's existing encrypted SQLite storage.

## 8b. Option A′ — new Dark Matter engine project (RECOMMENDED direction)

Chosen steer: stay pure-managed C#, do **not** abandon the C# investment, and —
because marmot-cs **never got external adoption** — do not preserve it as a
library. Instead of mutating `Mdk.cs` in place, build the Dark Matter engine as a
**new Scramble-internal project** that salvages the proven codecs.

- **New project** (e.g. `Scramble.Marmot`) — the Dark Matter engine, architected
  fresh around the `cgka-engine` model (epoch state machine, convergence, fork
  recovery, feature registry, identity-proof, publish lifecycle).
- **Reuse `MarmotCs.Protocol`** (MIP-00…03 codecs, `NostrGroupData`,
  `GroupEventEncryption`, NIP-44/59) — port the files in or keep that one project
  as a reference. This is the ~half of marmot-cs that survives. 🟡
- **Keep `dotnet-mls`** (OpenMLS-equivalent core survives). 🟢
- **Retire `MarmotCs.Core` / `Mdk.cs`** — effectively a rewrite; no adoption lost. 🟡

Survives/rewrite split (module-level read, **needs a line-by-line confirm**):

| marmot-cs piece | Fate |
|---|---|
| `MarmotCs.Protocol` (codecs, encryption, NIP-44/59, NostrGroupData) | **Survives** → reuse |
| `MarmotCs.Core` `Mdk.cs` (orchestration) | **Rewrite** (~30% scaffolding reusable) |
| `CommitRaceResolver` | **Rewrite** (subsumed by convergence model) |
| `dotnet-mls` | **Survives** |

**Trade-off accepted:** this keeps the perpetual parity-chase (we re-implement WN's
changes). Justified by full control + staying managed. **FFI (Option B) remains the
fallback** if the chase proves unsustainable.

## 9. Recommended next step (for the chosen Option A′ direction)

The FFI spike is deprioritized (FFI is now the fallback, not the plan). To give WN
a defensible date, do this instead:

1. **Confirm the survives/rewrite split** — a focused, line-level diff of
   `MarmotCs.Core/Mdk.cs` (+ `CommitRaceResolver`) against `cgka-engine`'s
   `engine` / `message_processor` / `group_lifecycle` / `convergence` /
   `epoch_manager` modules. Output: exactly what scaffolding is reusable, and the
   new engine's module list. Sizes the rewrite.
2. **Deep-dive `convergence.md` + `cgka-engine/src/convergence*`** — the hardest,
   least-understood module; the main timeline risk.
3. **Pin down account-identity-proof v2** — the kind:450 canonical-event signing
   construction (needed early; it's MUST-reject on the target).
4. **Confirm WN's own deployment date** — when deployed WN flips to Dark Matter is
   our hard deadline.

With (1)–(3) mapped, draft the new `Scramble.Marmot` project's module plan and a
phased build order, then a **date with a confidence band**.

**Holding answer for WN:** "Committed to Dark Matter. Building it as a fresh engine
in our stack, reusing our proven codecs. Sizing the convergence/engine rewrite now;
date in ~[N] weeks."

## 10. Reusability matrix + proposed `Scramble.Marmot` layout (deep-dive, evidence-based)

**Reuse (SURVIVES / PORTABLE):** `dotnet-mls` crypto + TLS codec (survives);
`GroupEventEncryption`, `Nip44`, NIP-59 `GiftWrap` (survive); kind:445/444/30443
event builders (portable — strip the `encoding` tag, enforce spec tag cardinality,
add NIP-40 expiration + `app_components` tag); `KeyPackageSlotId` (survives);
storage provider + Sqlite plumbing (portable — needs new tables); `dotnet-mls`
Types/Tree/KeySchedule/`MlsGroup` (portable — pending the feature audit).

**Rewrite / New:** `NostrGroupData 0xf2ee` codec (rewrite → app-components);
`CommitRaceResolver` (rewrite → convergence); `Mdk.cs` orchestrator (rewrite →
engine + epoch state machine + publish-before-apply); account-identity-proof
`0xf2f1` (new); app-components subsystem incl. `NostrRoutingV1 0x8004` (new);
convergence/canonicalization/fork-recovery (new); feature/capability registry +
auto-committer (new).

**Proposed project layout:**
- `Scramble.Marmot.Mls` ← `dotnet-mls` (after feature audit: AppDataDictionary,
  `0xf2f1` leaf ext, SelfRemove, PublicMessage framing).
- `Scramble.Marmot.Wire.Nostr` ← ported Nostr codecs (tags fixed).
- `Scramble.Marmot.Identity.AccountProof` — NEW (`0xf2f1` + kind:450 Schnorr proof).
- `Scramble.Marmot.AppComponents` — NEW (`0x8004` routing, admin-policy, profile,
  retention, media, id-lists).
- `Scramble.Marmot.Engine` — REWRITE (engine, epoch manager, message processor,
  publish-before-apply, capabilities, auto-committer).
- `Scramble.Marmot.Convergence` — NEW (convergence/canonicalization/fork-recovery).
  **The hard center.**
- `Scramble.Marmot.Storage` — ported + extended (typed raw/peeled records,
  processed-message idempotency, pending-commit durability, routing-state history).
- `Scramble.Marmot.Transport.Nostr` — glue to Scramble's `NostrService`
  (subscriptions per `nostr.md`; publish-ack → engine `ConfirmPublished`).

## 11. Top rewrite risks (timeline drivers)

1. **Distributed convergence / canonicalization** — prime suspect. Witness-quorum +
   rewind-horizon + tip-priority branch selection with a conformance simulator
   behind it. No starting point in our code (`CommitRaceResolver` is a *discarded*
   approach). Hardest to get provably correct.
2. **account-identity-proof `0xf2f1`** — every KeyPackage + commit leaf must carry a
   byte-exact Schnorr proof; a single canonical-id mismatch = 100% rejection by real
   peers.
3. **Publish-before-apply rewrite** — inverts control flow across the whole
   orchestrator + transport (every send path becomes two-phase, gated on relay ack).
4. **`dotnet-mls` generic-capability sufficiency** 🔴 — `dotnet-mls` stays as-is
   (generic MLS). Verify its **existing** generic support is enough for the Marmot
   layer to build on: arbitrary custom leaf/GroupContext extensions (opaque),
   SelfRemove proposal type, PublicMessage framing (present), retained past-epochs.
   Any gap is a **generic RFC-9420** enhancement decided separately — not Marmot
   work in the library. If a needed generic mechanism is genuinely absent, that
   informs the estimate but does not change the "dotnet-mls is not Marmot code"
   principle.
5. **App-component breadth + routing rotation** — group id/relays/name/admin/
   retention/media are now separate signed components with admin-gated updates and
   retained-window multi-address fetch; required for basic delivery across relay
   changes.

**Immediate must-do:** the **`dotnet-mls` generic-capability check** (risk #4) — it
gates how much the `Scramble.Marmot` layer can build on the existing MLS core vs.
needing generic-MLS extensions. Largest swing in the estimate. **→ DONE, see §12.**

## 12. `dotnet-mls` generic-capability check — RESULTS (2026-07-21) 🟢

Read-only line-level audit of `lib/dotnet-mls` (submodule @ `c45b972`) against the
four generic mechanisms the `Scramble.Marmot` layer must build on. **No files were
modified.** Verdict per capability:

| # | Capability | Status | Evidence | Gap / action |
|---|---|---|---|---|
| a | Opaque custom **leaf + GroupContext extensions** | ✅ **PRESENT** | `Extension` is an open `ushort` type + opaque `byte[]`, lossless round-trip (`Types/Extension.cs:47-58`); carried on both `LeafNode.Extensions` (`Types/LeafNode.cs:47`) and `GroupContext.Extensions` (`Types/GroupContext.cs:40`). No closed allowlist. | **None.** RFC-standard caveat only: a GC extension must be advertised in every non-blank leaf's `Capabilities.Extensions` or commit-apply rejects it (`Group/MlsGroup.cs:1946`). `Scramble.Marmot` adds `0xf2f1`/etc. to leaf capabilities before use. |
| b | **SelfRemove** proposal (type `0x0008`) | ❌ **ABSENT** | `ProposalType` is a **closed enum**; decode **throws `TlsDecodingException` on any unknown type** (`Types/ProposalType.cs:29-43`, `Types/Proposal.cs:28-42`). Not pass-through extensible (unlike extensions). | Generic RFC-9420 add, **MEDIUM** (~5 touch points: enum value, proposal class, 2 decode switches, commit-apply with the security check that sender == removed leaf). |
| c | **PublicMessage** framing (for PURE_PLAINTEXT handshakes) | ⚠️ **PARTIAL** | **Consume commit: YES** — `ProcessCommit(PublicMessage)` verifies membership_tag + applies (`Group/MlsGroup.cs:604`). **Produce commit: NO** — `Commit()` returns `PrivateMessage` only (`:285`, `:513`). **Produce proposal: NO** — propose* return raw `Proposal` objects, unframed (`:238-274`). **Consume proposal: UNVERIFIED** — `CacheProposal(PublicMessage)` does no sig/membership check, assumes pre-verified (`:645`). Low-level `MessageFraming.CreatePublicMessage()` can frame any content type (`Message/MessageFraming.cs:164`). | The doc's "already present" was **half right**. Need: expose PublicMessage **produce** paths for commit+proposal, and a **verify** step on proposal consume. + 🔴 flagged (verify vs spec, not from memory): `PublicMessage` writes/reads membership_tag for **all** member senders regardless of content type (`Types/PublicMessage.cs:43`, `:56`); RFC 9420 §6.2 reportedly says membership_tag MUST NOT be present for Proposal content — confirm against the spec before relying on PURE_PLAINTEXT proposal wire-compat with WN. |
| d | **Retained past-epochs** window | ❌ **ABSENT** | On commit-apply, `_keySchedule` + `_secretTree` are overwritten with no history (`Group/MlsGroup.cs:1092-1099`); decrypt hard-assumes current epoch, `msg.Epoch` used only in AAD, never as a key lookup (`:1635`, `Message/MessageFraming.cs:410-436`). A PrivateMessage from epoch N cannot be decrypted after advancing to N+1. | Generic RFC-9420 add, **MEDIUM-LARGE** (~100-200 lines: epoch→`(KeyScheduleEpoch, SecretTree)` window + pruning + epoch-keyed decrypt). Needed for out-of-order delivery under convergence. |

**Bottom line:** the capability that would have been *worst* to lack — (a) opaque
custom extensions — is **fully generic and present**, so the `0xf2f1` identity-proof
leaf ext and app-component GroupContext data can be carried without library changes.
The other three are **generic RFC-9420 gaps, not Marmot-specific**: SelfRemove
(MEDIUM), PublicMessage produce/verify + membership_tag spec check (MEDIUM), retained
past-epochs (MEDIUM-LARGE). Combined they're a bounded, ~2–3 well-scoped generic-MLS
work items — this does **not** blow up the estimate, but each requires new work in
`dotnet-mls`.

**⚠ Process constraint (user, 2026-07-21):** `dotnet-mls` is **not to be modified
without explicit permission**, and the new engine must be built **without touching
`marmot-cs`** (standalone `Scramble.Marmot`, clean cutover). So (b)/(c)/(d) become
**permission-gated generic-MLS proposals**, surfaced individually — not edits made
inline during the Marmot build.

**⚠ Architectural boundary (user, 2026-07-21): no Marmot protocol leaks into
`dotnet-mls`.** The library knows only *generic MLS mechanisms*; `Scramble.Marmot`
knows *what the opaque bytes mean*. Concretely:
- **Stays in `Scramble.Marmot` only:** the extension-type IDs and their semantics
  (`0xf2f1` identity-proof, `0x8004` NostrRoutingV1, `0xf2ee` legacy, app-component
  schemas), the identity-proof signing construction, convergence/canonicalization
  policy, feature registry, all Nostr wire concerns. `dotnet-mls` carries these as
  **opaque bytes it never parses** (capability (a) makes this free).
- **If added to `dotnet-mls` (the b/c/d gaps):** expressed as **standard RFC-9420 /
  mls-extensions** features — SelfRemove proposal type, PublicMessage produce/verify,
  retained-past-epochs secret window — with **no Marmot constants, no `0xf2..` IDs,
  no Nostr coupling**. If a change can't be expressed generically, it doesn't belong
  in `dotnet-mls`; it belongs above it in `Scramble.Marmot`.
