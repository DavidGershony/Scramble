> ⚠️ **SUPERSEDED (2026-07).** This targeted the mdk **0.8.0** line. Whitenoise is
> now committed to the **Dark Matter** rewrite (mdk 0.9.x), which is Scramble's
> real target. **Do not plan from this file.** See
> **`00-START-HERE-dark-matter.md`** and **`dark-matter-migration-scoping-2026-07.md`**.
> Kept for history.

# MDK Parity Plan — bring Scramble up to the current Rust MDK (2026-07)

## Why this document exists

Whitenoise runs the **Rust MDK** (`marmot-protocol/mdk`). Scramble runs a
from-scratch C# reimplementation split across two libraries:

- `lib/dotnet-mls` — the MLS (RFC 9420) state machine.
- `lib/marmot-cs` — the Marmot/Nostr protocol layer (MIP-00…05) on top of it.

For Scramble to interoperate with Whitenoise, these two must match the **Rust
MDK's concrete behavior** — not just the Marmot spec prose. The spec describes
*what*; the MDK decides *how*, and interop lives in the *how* (MLS wire-format
policy, validation, dedup, capability negotiation). An earlier spec-driven
analysis (`marmot-protocol-compliance*.md`) caught the spec-level items but
under-weighted the implementation-level ones. **This plan diffs against the MDK
implementation directly.**

> Scope note: this is a **plan only**. No code changes are described as done
> here except where already committed on branch `feat/marmot-batch1-protocol-v3`.
> Each numbered Session below is sized to be completed in one focused working
> session, because large sessions have historically gone wrong on this repo.

## What version are we targeting, and what does WN actually run?

- **Latest MDK:** `v0.9.4` (2026-07-10). The 0.9.x line moved MDK into a
  monorepo (`crates/cgka-engine`, `marmot-app`, `transport-*`, `agent-*`); the
  **0.9.1→0.9.4 releases are agent/app/infra and carry no Nostr/MLS wire
  changes.** The internal `mdk-core → cgka-engine` rename does not change wire
  bytes.
- **What our WN test container runs:** `whitenoise-rs@master` pins
  `mdk-core = 0.8.0` at rev `e8cd584`. That rev's changelog "Unreleased" section
  already contains the early-0.9.0 protocol work (extension v3, rumor-ID
  verification, KeyPackage d-tag options, admin fixes).
- **Therefore the wire-relevant parity work = mdk 0.7.0 → 0.8.0 + early 0.9.0
  `mdk-core` changes.** That is the authoritative change set this plan is built
  from (source: `crates/mdk-core/CHANGELOG.md` @ `e8cd584`).

### Test oracle status

- Current docker (`docker-compose.test.yml` → `tests/whitenoise-docker`) builds
  WN from `whitenoise-rs@master` = **mdk 0.8.0-rev**. Good enough to validate
  most items below.
- A true **0.9.4** oracle would require building WN from the mdk monorepo
  (`crates/cli` / `wn-agent`), which has a different CLI — our
  `wnd_test.rs` + `WhitenoiseDockerClient` harness would need adapting. Treat
  that as its own setup task (Session O) before relying on 0.9.x-only behavior.
- **Known harness caveat:** `WhitenoiseGroupInteropTests` fetches *all* kind:445
  events and tries to decrypt each (including already-consumed and
  wrong-epoch events), so it fails with `Generation already consumed` /
  `AEAD failed` **on `master` too** (verified against base `b158675`). It is a
  harness artifact, not a protocol gap. Fixing it (Session N) is prerequisite to
  trusting interop signal.

---

## Spec reorganization (2026) + newly-surfaced gaps — READ THIS

The Marmot spec **deprecated the flat MIP-00…05 documents** and reorganized
around implementer surfaces: `foundation/`, `protocol-core/`, `app-components/`,
`transports/`, `features/`. `mip-coverage.md` maps old MIP → new docs. The MIPs
"remain useful history" but the surface docs are now authoritative.

**Impact on our code naming:** none required for interop. Renaming our
`MarmotCs.Protocol.MipXX` folders/namespaces to the new surfaces is a *cosmetic*
refactor with zero wire impact — parked as a post-parity rename unit
(`mdk-parity-units/Z1-rename-mip-to-surface.md`), NOT a prerequisite.

**Impact on references:** cite the new surface docs going forward:

| Our topic | New authoritative doc |
|---|---|
| Wire format / envelopes | `foundation/wire-envelopes.md`, `foundation/canonical-encoding.md` |
| KeyPackages / identity | `foundation/key-packages.md`, `foundation/identity.md` |
| SelfRemove / leaving | `protocol-core/member-departure.md` |
| Commit race / branch selection | `protocol-core/convergence.md`, `retained-history.md` |
| Admin rules | `app-components/admin-policy-v1.md` |
| Nostr kinds/tags | `transports/nostr.md` |

**Two NEW gaps the MIP-based plan missed (both need verification against the
target MDK, then likely implementation):**

- **account-identity-proof — MLS LeafNode extension `0xf2f1`.**
  Binds the Nostr account key in the MLS `BasicCredential` to the leaf's MLS
  signature public key via a 64-byte BIP-340 Schnorr signature; MDK ≥0.9.x
  **MUST-rejects** KeyPackages/leaves without a valid proof.
  **AUDIT RESULT (A4, high confidence): NOT enforced by the MDK version
  Whitenoise runs today.** WN pins `mdk-core 0.8.0` (rev `e8cd584`), which has no
  `0xf2f1` code at all — confirmed by mdk source across refs, the whitenoise-rs
  Cargo pin, and a live decoded WN KeyPackage on the relay (`client=MDK/0.8.0`,
  extensions `[0x000a, 0xf2ee]`, **no `0xf2f1`**). So this is a **near-future
  item, not a current blocker** — do NOT implement now (emitting proofs buys
  nothing; validating inbound would *break* interop with 0.8.0 WN).
  **Trigger to implement:** when `whitenoise-rs` bumps its `mdk` pin to ≥0.9.x
  (the "Dark Matter" rewrite line). **When it lands, target v2, not v1:** the
  shipped extension is `marmot.account-identity-proof.v2` (version byte `2`,
  domain `.v2`); the Schnorr sig is over a canonical unpublished `kind:450` Nostr
  event (content `""`, created_at `0`, tags `d/extension/version/ciphersuite/
  signature_scheme/mls_signature_key`), **not** a raw field concatenation. Payload
  body is fixed-width big-endian. See **Session 12**.
- **`convergence-policy-v1` — mandated branch-convergence constants + witness
  quorum.** `protocol-core/convergence.md` defines a full model
  (`max_rewind_commits=5`, `app_payload_past_epoch_limit=5`,
  `settlement_quiescence_ms=1000`, `witness_quorum_senders_per_epoch=2`,
  `witness_quorum_epochs`, `max_witness_override_depth`). Our `CommitRaceResolver`
  implements only the created_at/lex-id tiebreaker — a subset. Session 3
  (ciphertext dedup) is one small piece; **full convergence parity is a larger,
  separate effort** — see **Session 13**.

---

## Already delivered (verify against oracle, don't re-do)

These landed on `feat/marmot-batch1-protocol-v3` and map directly to MDK changes:

| MDK change | PR | Where in our code | Status |
|---|---|---|---|
| MIP-03 kind:445 = base64(nonce‖ChaCha20-Poly1305), exporter `("marmot","group-event",32)` | #208 (0.7.0) | `GroupEventEncryption.cs` | ✅ |
| MIP-04 HKDF expand-only (exporter as PRK) | #217 (0.7.1) | `HkdfProvider.ExpandSha256` usage | ✅ |
| KeyPackage kind:443 → kind:30443 addressable + d-tag | #233 | `ManagedMlsService`, `NostrService` | ✅ |
| Encrypted content min length 12 → 28 | #230 | `GroupEventEncryption.Decrypt` (our C.3.a) | ✅ |
| Extension v3 + `disappearing_message_secs` (codec) | #253/#258 | `NostrGroupData*.cs` (our C.1.a) | ✅ codec only |
| Reject missing/non-base64 encoding on inbound | — | `MessageService` handlers (our C.1.d) | ✅ |

Also fixed (Scramble-internal, spec-aligned, not an MDK-parity item):
MIP-03 ephemeral key is now fresh-random per event; MIP-00 KeyPackage selection
is deterministic; `AcceptWelcome` requires valid 0xF2EE.

---

## Work sessions

Each session lists: **MDK ref · layer · current state · what to change · how ·
tests · size · depends-on.** Sizes: **S** ≈ half a session, **M** ≈ one
session, **L** ≈ needs splitting further at start.

### Session 1 — Extension forward-compatibility (accept future versions)  · S · CRITICAL

- **MDK ref:** #88 — "Accept `NostrGroupDataExtension` payloads from future
  versions with unknown trailing fields." Previously any v(N+1) extension on the
  wire was rejected, which *"would have bricked every group operation the moment
  any peer authored a newer-version extension."*
- **Layer:** marmot-cs (`NostrGroupDataCodec`).
- **Current state:** **Likely already OK.** Our decoder throws only on
  `version == 0`; for `version >= 3` it reads known fields and ignores trailing
  bytes, so a v4 payload is accepted. **Needs a confirming test**, not
  necessarily a code change.
- **How:** Add a decode test: hand-craft a "v4" payload = valid v3 bytes + extra
  trailing field; assert it decodes to the v3 fields without throwing. If it
  *does* throw, remove any upper-version guard.
- **Tests:** `NostrGroupDataCodec_FutureVersion_IgnoresTrailingFields`.
- **Depends-on:** none. **Do this first** — it's the cheapest insurance against a
  network-wide brick.

### Session 2 — Verify unsigned rumor IDs on receive  · S

- **MDK ref:** #287 — "Verified unsigned application-message rumor IDs before
  accepting or sending them, preventing caller-supplied IDs from being trusted
  when they do not match the canonical event hash."
- **Layer:** marmot-cs / Scramble (`ManagedMlsService.DecryptMessageAsync`).
- **Current state:** We compute the canonical id on **send**
  (`ComputeRumorEventId`) and verify the inner **sender** on receive
  (`EnsureInnerRumorSenderMatches`), but we do **not** recompute and verify the
  rumor **id** on receive.
- **How:** In the decrypt path, after parsing the inner rumor JSON, recompute
  `ComputeRumorEventId(pubkey, created_at, kind, tags, content)` and compare to
  the rumor's `id`; drop the message on mismatch (mirror the sender-verify guard,
  which already lives right there).
- **Tests:** unit tests on an extracted helper (match / mismatch), same pattern
  as `Mip03InnerSenderVerificationTests`.
- **Depends-on:** none.

### Session 3 — Ciphertext replay dedup in commit race resolution  · S/M

- **MDK ref:** #246 — "Epoch snapshots now store a SHA-256 hash of the outer
  event content; re-wrapped events carrying identical ciphertext are rejected
  before the MIP-03 timestamp/ID comparison, preventing replay-triggered
  rollbacks."
- **Layer:** marmot-cs (`CommitRaceResolver` / `Mdk.ProcessMessageAsync`).
- **Current state:** `CommitRaceResolver.ResolveWinner` does the MIP-03
  created_at/lex-id tiebreaker but has **no ciphertext-hash dedup**. A replayed
  re-wrapped commit could trigger a spurious rollback.
- **How:** When processing a commit, compute SHA-256 of the outer 445 content;
  track seen hashes per epoch (alongside the existing pending/snapshot state);
  reject a commit whose ciphertext hash was already seen *before* running the
  tiebreaker.
- **Tests:** `CommitRaceResolver` unit test — same ciphertext replayed twice is
  rejected the second time; distinct ciphertexts still race normally.
- **Depends-on:** none.

### Session 4 — MIXED_CIPHERTEXT wire-format audit  · M · FOUNDATION

- **MDK ref:** #236/#264 — "New groups use `MIXED_CIPHERTEXT` wire format policy
  (outgoing: ciphertext, incoming: mixed)… required to accept `PublicMessage`
  SelfRemove proposals." `self_update` converges local policy to MIXED.
- **Layer:** dotnet-mls (`MlsGroup`, message framing).
- **Current state:** **Partial.** dotnet-mls already has
  `ProcessCommit(PublicMessage)` and `Mdk.cs:790` notes PublicMessage is
  expected for SelfRemove. Outgoing commits are `PrivateMessage` (correct). What
  is unverified: whether inbound **PublicMessage *proposals*** (not just commits)
  are accepted/validated, and whether there is any policy gate that would reject
  them.
- **How:** Audit `MlsGroup.ProcessMessage` / proposal handling for PublicMessage
  proposals. Confirm: (a) inbound PublicMessage proposals parse and validate;
  (b) inbound PublicMessage commits process; (c) outbound stays PrivateMessage.
  Write down the exact gap. **This session is an audit + a written gap list**,
  not necessarily a code change — it scopes Session 5.
- **Tests:** characterization tests for PublicMessage proposal processing.
- **Depends-on:** none. Precedes Session 5.

### Session 5 — SelfRemove send-side (voluntary leave)  · L (split at start)

- **MDK ref:** #236 — "`leave_group` now sends a SelfRemove proposal (type
  `0x000a`) as `PublicMessage`… auto-committed by any member… falls back to
  Remove for legacy groups. Non-admin members can create SelfRemove-only
  Commits."
- **Layer:** dotnet-mls (SelfRemove proposal type + PublicMessage emit) +
  marmot-cs (leave flow) + Scramble (`MessageService.LeaveGroupAsync`).
- **Current state:** `LeaveGroupAsync` is **local-only** — it cleans up local
  state and never notifies the group, so other members still see the leaver as
  present. Receive-side PublicMessage/SelfRemove awareness partly exists
  (`Mdk.cs:790`).
- **How (split into ≥2 sessions):**
  1. dotnet-mls: represent + emit a SelfRemove proposal as PublicMessage; allow
     any member to commit a SelfRemove-only commit.
  2. marmot-cs + Scramble: `LeaveGroupAsync` publishes the SelfRemove (with
     legacy `Remove` fallback when the group's RequiredCapabilities lacks
     SelfRemove), following the staged-publish-then-merge discipline already used
     for AddMember.
- **Tests:** unit (proposal encoding/type 0x000a as PublicMessage) + integration
  (leaver is removed from remaining members' view) + interop against WN.
- **Depends-on:** Session 4 (wire-format audit), Session 6 (RequiredCapabilities,
  for the fallback decision).

### Session 6 — RequiredCapabilities as LCD of invitee capabilities  · M

- **MDK ref:** #261 — "`create_group` computes `RequiredCapabilities` as a
  least-common-denominator intersection of `SUPPORTED_PROPOSALS` with every
  invitee's advertised proposals… any legacy invitee strips `SelfRemove`…
  empty-invitee groups stay permissive." Plus #263 typed
  `InviteeMissingRequiredProposal`.
- **Layer:** marmot-cs / dotnet-mls (group creation + add_members admission).
- **Current state:** Unverified — likely hardcodes required capabilities (or
  omits `self_remove`, tracked earlier as "Fix D"). Mixed-version invites (a
  legacy peer joining) may fail admission.
- **How:** On create/add, compute required proposals = intersection of supported
  with each invitee's advertised `mls_proposals`. Emit that into the group's
  RequiredCapabilities. Surface a typed error when an invitee's KeyPackage can't
  satisfy an existing group's requirements.
- **Tests:** all-modern invitees ⇒ `[SelfRemove]`; any legacy invitee ⇒ strips
  SelfRemove; empty invitees ⇒ `[]`.
- **Depends-on:** none, but pairs naturally with Session 5.

### Session 7 — Admin validation hardening  · M

- **MDK ref:** #223 (prune non-member admins), #225 (atomically strip admin on
  member removal), #256 (reject receiver-side commits that leave zero admins),
  #236 (SelfRemove/commit rejected if it would leave zero admins), #288 (admin
  auto-commit of legacy Remove(self)).
- **Layer:** marmot-cs (admin-list validation on send + receive) + dotnet-mls
  (commit validation).
- **Current state:** Partial/unknown. `MessageService` extracts admin pubkeys but
  earlier audit found no gating on inbound non-self-update commits; admin
  depletion checks not implemented.
- **How:** On processing commits/GCE updates that mutate `admin_pubkeys`: prune
  non-members, reject if zero active admins remain, strip admin atomically when a
  member is removed. Gate admin-only operations.
- **Tests:** depletion rejection; prune-non-member; atomic strip on removal.
- **Depends-on:** overlaps Session 6 (both touch group-data/admin state).

### Session 8 — GroupContextExtensions upgrade path  · M

- **MDK ref:** #266 — public `group_member_capabilities`,
  `group_capability_upgrade_status`, `upgrade_group_capabilities`, plus
  `group_required_proposals` (#265). Lets an admin upgrade a mixed group to
  require SelfRemove once all members are modern.
- **Layer:** marmot-cs (new query + admin-commit APIs) + Scramble (optional UX).
- **Current state:** Missing.
- **How:** Implement the capability inspection accessors, then an admin-only GCE
  commit that adds proposal types to RequiredCapabilities (all-or-nothing, with
  TOCTOU re-validation). Converge local wire-format to MIXED alongside.
- **Tests:** upgrade readiness reporting (AlreadyRequired/Available/Blocked);
  admin-only enforcement.
- **Depends-on:** Sessions 4, 6.

### Session 9 — Disappearing messages end-to-end  · L (this is the old "Batch 2")

- **MDK ref:** #253/#258 (v3 propagation through create/update/welcome), #248
  (`create_message` accepts allow-listed outer tags incl. NIP-40 `expiration`),
  #306 (validation + NIP-40).
- **Layer:** marmot-cs (propagate `disappearing_message_secs` through create /
  update / welcome; auto-apply NIP-40 on outer 445; strip caller expiration when
  disabled) + Scramble (Chat model field, storage, local deletion sweeper, UI)
  + **both UI targets** (`Scramble.Mobile.Android` and `Scramble.UI`, per the
  project parity rule).
- **Current state:** Codec done (Session-0 work); **no propagation, no NIP-40, no
  UI, no deletion timer.**
- **How (split into ≥3 sessions):** (a) marmot-cs propagation + NIP-40 outer tag;
  (b) Scramble persistence + local deletion sweeper; (c) UI in both heads.
- **Tests:** codec propagation; outer 445 carries NIP-40 when enabled and not
  when disabled; local expiry deletes.
- **Depends-on:** Session 1 (forward-compat) is nice-to-have first.

### Session 10 — MIP-04 media parity (thumbhash + audio metadata)  · M

- **MDK ref:** #244 (ThumbHash preview alongside BlurHash; parse both), #300
  (optional audio `duration_ms` + `waveform` in IMETA).
- **Layer:** Scramble (`MessageService` media build/parse) — `Scramble.Native`
  already has `fast_thumbhash` bindings.
- **Current state:** Unverified; likely blurhash-only, no audio metadata.
- **How:** Emit `thumbhash` on outbound media; parse both `blurhash` and
  `thumbhash` on inbound; add optional audio metadata to IMETA.
- **Tests:** media round-trip carries thumbhash; inbound parses both.
- **Depends-on:** none. Independent; can slot anytime.

### Session 11 — MIP-04 crypto audit  · S

- **MDK ref:** #217 (HKDF expand-only), #222 (legacy fallback deadlines), #208
  (exporter label).
- **Layer:** marmot-cs (`Mip04MediaCrypto` — not audited line-by-line).
- **Current state:** Foundation matches (label, expand-only); AAD byte layout,
  v1 rejection, SHA-256 integrity unverified.
- **How:** Read `Mip04MediaCrypto` end-to-end; confirm AAD =
  `"mip04-v2"‖0x00‖file_hash‖0x00‖mime‖0x00‖filename`, v1 rejection, and
  `SHA256(decrypted)==x`. Add missing checks + tests.
- **Depends-on:** none.

### Session 12 — Account identity proof (`0xf2f1`)  · L (audit first) · POSSIBLY #1 BLOCKER

- **Spec ref:** `foundation/account-identity-proof-v1.md` — new breaking MLS
  LeafNode extension (`0xf2f1`, `marmot.account-identity-proof.v1`). Binds
  `account_identity[32]` (Nostr pubkey) to the leaf's MLS signature public key
  with a BIP-340 Schnorr signature over the defined payload. Clients **MUST
  reject** leaves/KeyPackages without a valid proof.
- **Layer:** dotnet-mls (LeafNode extension type + validation) + marmot-cs
  (build the proof when creating KeyPackages/leaves; verify on inbound
  KeyPackages/welcomes/commits).
- **Current state:** **Absent** — not in our KeyPackage builder, not validated
  anywhere.
- **How (audit first, then split):**
  1. **Verify enforcement:** does the target MDK version (0.8.0-rev / 0.9.4)
     actually build + require `0xf2f1` today? Check the mdk source / a live WN
     KeyPackage on the relay. If not yet enforced, this drops in priority; if
     enforced, it is the top blocker.
  2. If enforced: implement the extension payload (fixed-width big-endian, **not**
     QUIC-varint), Schnorr signing with the Nostr account key, attach to our
     leaves/KeyPackages, and reject inbound leaves/KPs lacking a valid proof.
- **Tests:** payload round-trip; signature verify/reject; inbound KP without proof
  rejected; interop (WN accepts our KP, we accept WN's).
- **Depends-on:** verification step gates everything. **Do the audit early.**

### Session 13 — Convergence policy v1 (branch selection + witness quorum)  · L (split)

- **Spec ref:** `protocol-core/convergence.md`, `protocol-core/retained-history.md`
  — mandated constants and a witness-quorum branch-scoring model, well beyond the
  created_at/lex-id tiebreaker.
- **Layer:** marmot-cs (`CommitRaceResolver` + epoch/branch state) + storage
  (retained history / rewind).
- **Current state:** Partial — we have the tiebreaker and (after Session 3) the
  ciphertext dedup, but not the rewind window, witness quorum, or settlement
  quiescence.
- **How:** Audit `convergence.md` + `retained-history.md` in full, map each
  constant/rule to our commit-processing + snapshot code, then implement in
  slices (rewind window → witness quorum → quiescence). **Its own multi-session
  effort — do after the wire-format + identity-proof blockers.**
- **Depends-on:** Session 3, Session 4.

---

## Explicitly out of scope (record, don't build)

- **MIP-05 push notifications** (#235, #238, #254 — kind:446/447/448/449, token
  wire format). Scramble has no push story. If push is ever on the roadmap it is
  its own multi-session effort with the 1084-byte token format.
- **MDK monorepo agent/app crates** (`agent-*`, `marmot-app`, `transport-quic-*`).
  Not part of the Nostr/MLS wire surface Scramble must match.

## Supporting sessions (test infrastructure)

### Session N — Fix the interop harness  · M
Make `WhitenoiseGroupInteropTests` decrypt only the target event, in arrival
order, so the suite reflects real behavior instead of failing on
already-consumed/wrong-epoch events. Prerequisite to trusting interop signal for
every session above.

### Session O — Stand up a 0.9.x oracle (optional)  · M/L
Adapt `tests/whitenoise-docker` to build WN from the mdk monorepo
(`crates/cli` / `wn-agent`) so we test against true 0.9.x. Needs
`WhitenoiseDockerClient` + `wnd_test.rs` adaptation to the new CLI. Only needed
to validate 0.9.x-only behavior; most sessions validate fine against the current
0.8.0-rev oracle.

---

## Suggested order (dependency-aware)

0. **Session 12 audit step** (does the target MDK enforce `0xf2f1` identity
   proof?) — do this FIRST. If yes, it jumps to the front as the #1 blocker.
1. **Session 1** (forward-compat — cheapest brick-insurance)
2. **Session N** (fix harness — so later interop checks mean something)
3. **Session 2** (rumor-id verify) · **Session 3** (ciphertext dedup) — small, independent
4. **Session 4** (wire-format audit) → **Session 6** (RequiredCapabilities LCD) → **Session 5** (SelfRemove)
5. **Session 7** (admin validation) · **Session 8** (GCE upgrade) — build on 4/6
6. **Session 10/11** (encrypted-media / MIP-04) — independent, slot anytime
7. **Session 13** (full convergence policy) — after wire-format + identity-proof settle
8. **Session 9** (disappearing messages) — large feature, do when the wire layer is settled
9. **Session O** (0.9.x oracle) — when 0.9.x-only validation is needed

## Prior Batch-3 hygiene (separate from MDK parity)

These are Scramble-internal correctness items from the earlier spec analysis, not
MDK-parity gaps. Lower priority; fold in opportunistically:
- 24h post-join self-update scheduler (persisted-timestamp approach chosen).
- Catch-up on outstanding commits before self-update.
- init_key deletion timing with 24h grace after rotation.

## Appendix — authoritative sources

- `crates/mdk-core/CHANGELOG.md` @ rev `e8cd584` (what WN pins) — 0.7.0 → 0.8.0
  + early-0.9.0 "Unreleased". Primary source for every row above.
- MDK GitHub releases `v0.8.0`, `v0.9.0` … `v0.9.4`.
- whitenoise-rs `Cargo.toml` — pins `mdk-core 0.8.0` rev `e8cd584`.
- Our branch `feat/marmot-batch1-protocol-v3` — commits `7be4f20`, `a55e527`
  (submodule) and `34c297f`, `63d5c38`, `8e21e4e` (parent) for the already-done
  rows.
