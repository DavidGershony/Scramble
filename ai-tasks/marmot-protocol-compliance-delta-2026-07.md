> ⚠️ **SUPERSEDED (2026-07).** Spec-based delta against the pre-Dark-Matter
> protocol. Target is now the **Dark Matter** rewrite. **Do not plan from this
> file.** See `00-START-HERE-dark-matter.md`. Kept for history.

# Marmot Protocol Compliance — Delta Report (2026-07)

## Purpose

This is a **delta** on top of the earlier compliance plan
[`marmot-protocol-compliance.md`](marmot-protocol-compliance.md). The earlier
document was written against an older spec snapshot and a `Scramble` /
`marmot-cs` state that has since moved substantially.

This report answers three questions:

1. **What has the Marmot protocol spec changed** since the earlier plan was
   written?
2. **What has `marmot-cs` (and Scramble) already implemented** from that
   plan?
3. **What is still missing** — where does the work need to happen (marmot-cs
   vs. `dotnet-mls` vs. `Scramble.Core`), what is the blast radius, and
   what tests move with it?

## Baseline

| Layer | Ref (2026-07-02) | Baseline for delta |
|---|---|---|
| Marmot spec | `github.com/marmot-protocol/marmot@21a67b2` (`master`) | Earlier plan tracked `master` @ ~`24bab35` (2 major features later) |
| `marmot-cs` submodule | `84b106f` on `DavidGershony/marmot-cs.git` | Earlier plan tracked pre-staging-API tip |
| Scramble | `master` @ `b158675` | Earlier plan tracked `fc6b3c5` |
| Prior audit | 76 MUST/SHOULD rows (26 OK / 9 Violated / 19 Missing / 3 N/A / 19 Unknown) | See existing plan for the full row-level table |

---

## Section A — Spec-level deltas since the earlier plan

Sourced from `tmp-marmot-spec/marmot/CHANGELOG.md`'s **Unreleased** section
plus commits between the earlier baseline and `21a67b2`. Each row lists what
the spec now says that it did not (or said differently) before, and which
MIP is affected.

### A.1  **MIP-01 v3 — disappearing messages**  *(breaking)*

Extension format bumped **v2 → v3**. New field:

```
opaque disappearing_message_secs<0..8>
```

- Empty (0 bytes) = disabled (`None` — messages persist forever)
- Exactly 8 bytes (big-endian `uint64`) = enabled
- Value `0` MUST be rejected as invalid
- Added in version 3

**MIP-03 obligations that come with it:**

- Kind:445 senders MUST auto-apply a NIP-40 `expiration` tag on the outer
  event, computed from the inner rumor's `created_at`, when the group has a
  nonzero duration.
- Implementations MUST strip any caller-supplied expiration tag when the
  group does not enable disappearing messages.

*Earlier plan status:* declared out of scope (row in MIP-01 table).
*Now:* breaking, per CHANGELOG.

### A.2  **MIP-00/02 — `content` field encoding tightened to base64**  *(breaking)*

KeyPackage (kind:30443/443) and Welcome (kind:444) events MUST use base64
for the `content` field. The `encoding` tag MUST be `["encoding", "base64"]`
and implementations **MUST reject** events with missing or non-base64
`encoding` tags. Hex is retired.

*Earlier plan status:* OK on publish, Unknown on receive.
*Now:* receive-side reject is normative.

### A.3  **MIP-00 — kind:30443 addressable KeyPackage migration**  *(breaking, cutover already past)*

`kind:30443` is now the canonical KeyPackage format. Migration guidance
lives at `docs/migrations/2026-05-01-kind-30443-cutover.md` (cutover
2026-05-01 UTC, already elapsed).

*Earlier plan status:* Phase 0.5 "verify legacy 443 acceptance window".
*Now:* cutover past — legacy 443 is only relevant to historic-inbox reads.

### A.4  **MIP-05 — token / notification format changes**  *(breaking)*

- `EncryptedToken` expanded 280 → **1084 bytes**
- `TokenPlaintext` 220 → **1024 bytes**
- `token_length` validation replaced with a **universal 1–1021** range
- Batch recommendation dropped from 100 → **25 tokens** per `kind:446` event

*Earlier plan status:* MIP-05 not in scope; kind 10051 flagged Unknown.
*Now:* MIP-05 has concrete wire-format numbers and is easier to implement,
but Scramble has no push story yet.

### A.5  **MIP-04 — thumbhash imeta tags**  *(non-breaking, recommended for new impls)*

MIP-04 now documents optional `thumbhash` `imeta` tags alongside
`blurhash`. Receivers MUST parse both. New implementations SHOULD emit
`thumbhash`; `blurhash` remains only for backward compatibility.

### A.6  **MIP-01 — HKDF-SHA256 pin + canonical input encoding**  *(spec fix)*

Explicitly specifies **HKDF-SHA256** for all image encryption key and upload
keypair derivations (v1 and v2). Canonicalises HKDF inputs:

- `salt` = empty octet string (zero bytes, length 0, **not** a null/None
  value or RFC 5869's HashLen-zeros default)
- `info` labels = UTF-8 bytes with no terminator or length prefix

Previously ambiguous — some clients used `salt=None` which produced
different PRKs across libraries.

### A.7  **MIP-03 — non-admin members may commit SelfRemove proposals**  *(new capability)*

*(spec commit `137da0b`)* Non-admin members can now create dedicated
SelfRemove-only Commits. Prior spec required admin-committed. Also opens
door to member-driven leave flow without waiting for an admin.

### A.8  **MIP-01 / MIP-03 — admin MUST self-demote before SelfRemove**  *(clarification)*

*(spec commit `de9453c`)* Admins MUST relinquish admin status (via a GCE
proposal that mutates `admin_pubkeys`) **before** sending a SelfRemove.
Prior spec was silent on ordering.

### A.9  **MIP-03 — minimum encrypted content length fix**  *(spec fix)*

*(spec commit `0a827b7`)* The floor for kind:445 encrypted content length
was corrected. `marmot-cs` currently rejects `< 28 bytes`
(`GroupEventEncryption.cs:96-97`). **Must verify** the new spec threshold
matches (12-byte nonce + 16-byte tag = 28-byte minimum for
ChaCha20-Poly1305 with empty plaintext; the fix likely reconciles the
description, not the number, but worth a byte-count re-read).

### A.10  **MIP-02 — Welcome contents clarification** *(clarification, no code change expected)*

*(spec commit `23a1295`)* Text tightened; no wire-format change.

### A.11  **MIP-02 — post-join self-update sequencing clarification** *(clarification)*

*(spec commit `0e37af9`)* Recommends explicit ordering: catch up on
outstanding commits first, then self-update, then send app messages.
Reinforces existing MUST/SHOULD.

### A.12  **Threat model — T.3.6 disappearing-message non-compliance** *(new threat)*

Threat entry added for members whose clients ignore local deletion timers.
Reinforces A.1's MUSTs.

---

## Section B — What has already landed since the earlier plan

### B.1  In `marmot-cs` (submodule tip `84b106f`)

Verified from git log and `MessageService.cs` consumption:

| Feature | Earlier plan phase | marmot-cs commit(s) | Status |
|---|---|---|---|
| Staged commit API (`StageAddMembers` / `StageRemoveMembers` / `StageSelfUpdate` / `StageGroupDataUpdate`) | Phase 1 | `67aff10`, `84b106f` | **Landed** |
| `ClearPendingCommit(byte[] groupId)` at MDK level, `HasPendingCommit` | Phase 1 | `84b106f` (staging cluster) | **Landed** |
| MIP-03 tiebreaker (`ProcessIncomingCommitAsync`, `CommitRaceResolver`) | Phase 2 | `0310e4e`, `fcc16bc` | **Landed** |
| MIP-03 nonce uniqueness tracking, `DuplicateNonceException` | Phase 3 | `645efb6` | **Landed** |
| MIP-03 constants regression tests | Phase 7 | `bcb6d9a` | **Landed** |
| Kind:443 → kind:30443 migration | Phase 6 (KP rotation) | `cf3d89d` | **Landed** |
| KeyPackage slot ID (d-tag) helper + rotation contract | Phase 6 | `ce113ae` | **Landed** |
| `HkdfProvider` routing (Linux OpenSSL 3.x fix; also aligns with A.6) | correctness | `e102577` | **Landed** — likely satisfies A.6 |
| `ExportSecret` custom overload | Phase 1 dep | `6aeac51` | **Landed** |
| MlsMessage envelope wrapping for cross-impl interop | correctness | `60cc6a0` | **Landed** |
| Idempotent group/message/welcome storage (`INSERT OR REPLACE`) | correctness | `ad8f94d` | **Landed** |
| SQLite table prefix support | infra | `de9420c` | **Landed** |

### B.2  In Scramble

Verified by grepping `src/Scramble.Core`:

| Feature | Earlier plan phase | Scramble file(s) | Status |
|---|---|---|---|
| `PublishUnconfirmedException` thrown for kind 445 (commit), 1059 (welcome gift-wrap), 30443 (KP) | Phase 3 | `NostrService.cs:1601, 1734, 2193, 2212`; `PublishUnconfirmedException.cs` | **Landed** |
| `IMlsService.StageAddMemberAsync` + `MergePendingCommit` + `ClearPendingCommit` + `HasPendingCommit` | Phase 4 | `IMlsService.cs:43`; `ManagedMlsService.cs:978, 1006, 1035`; `MlsService.cs:376` | **Landed** |
| `AddMemberAsync` uses staged flow with catch-by-pending-commit differentiation | Phase 4 | `MessageService.cs:816`, `1259-1333`, `1401`, `1489-1547` | **Landed** — the central bug's fix is deployed |
| Kind:30443 addressable KeyPackage on publish and rotation | Phase 6 (KP rotation) | `KeyPackage.cs:66,72,109`; `ManagedMlsService.cs:495` (comment cites MIP-02 step 5); `NostrService.cs:2411,2422` | **Landed** |
| KeyPackage slot d-tag stable across rotation | Phase 6 | `ManagedMlsService.cs:64,309` | **Landed** |

**Net effect:** Phases 1, 2, 3, and 4 of the earlier plan — the highest-risk
work — are effectively done in both layers. Phase 6 (KP rotation) is
partially done: rotation flow exists; init_key deletion timing and 24h
self-update scheduler still need to be verified.

---

## Section C — What is still missing (updated gap list)

### C.1  Highest-impact new spec obligations

| # | Requirement | Layer | Delta | Test |
|---|---|---|---|---|
| C.1.a | MIP-01 v3 — bump `NostrGroupData.Version` default to 3; add `DisappearingMessageSecs` field with strict `0-or-8-byte` TLS encoding; reject `0` value | **marmot-cs primary** (`NostrGroupData.cs`, `NostrGroupDataCodec.cs`) — Scramble surface changes secondary | Breaking wire change. Groups created under v2 need a migration path (reads must accept both v2 and v3; writes emit v3 once nostr network has largely rolled) | Codec round-trip + reject-`0` unit tests |
| C.1.b | MIP-03 auto-apply NIP-40 `expiration` on kind:445 outer event when group has nonzero duration | Scramble (`ManagedMlsService.EncryptMessageAsync` callers in `MessageService.cs`) + marmot-cs helper | Modest — needs group-context read at send time | Integration test: enable disappearing, publish, assert outer event has NIP-40 tag |
| C.1.c | MIP-03 strip caller-supplied `expiration` when group has no disappearing set | Scramble | Modest — one filter in the outer-event builder | Integration test with adversarial caller |
| C.1.d | MIP-00 base64 encoding tag: reject inbound events missing or non-base64 `encoding` tag | Scramble (`NostrService.FetchKeyPackagesAsync`, welcome gift-wrap unwrap path) + marmot-cs codec | Small — a validator at the codec boundary | Fixture-driven negative tests |
| C.1.e | MIP-04 thumbhash imeta support: emit `thumbhash` alongside `blurhash` on outbound media; parse both on inbound | Scramble (`MessageService.cs:322-396`) — Scramble.Native already has `fast_thumbhash` bindings | Small on outbound (compute at encrypt time); parsing already tolerant | Media round-trip test |
| C.1.f | MIP-01 HKDF-SHA256 with empty-octet-string salt (not `None`) — verify | marmot-cs (`ImageEncryption.cs`, `ExporterSecretKeyDerivation.cs`, `HkdfProvider`) | **Likely already OK** — `HkdfProvider.ExpandSha256(secret, label, 32)` shape suggests empty salt, but a byte-level test against a spec vector is required | Cross-impl HKDF test vector |

### C.2  Retained gaps from the earlier plan that are still open

None of these were closed by B.1/B.2. Sorted by expected effort:

| # | Requirement | Layer | Status |
|---|---|---|---|
| C.2.a | MIP-02 24h post-join self-update scheduler | Scramble (`ManagedMlsService.UpdateKeysAsync` exists; needs a scheduled trigger) | **Open** |
| C.2.b | MIP-02 catch-up on outstanding commits before self-update | Scramble | **Open** |
| C.2.c | MIP-00 last_resort init_key deletion after replacement published | Scramble | **Partially open** — replacement is now published (rotation exists); needs the "delete old init_key after replacement" step, with the 24h grace window if the KP was consumed |
| C.2.d | MIP-03 inner-sender vs `pubkey` verification on receive (`ManagedMlsService.cs:623` extracts sender but does not compare against rumor `pubkey`) | Scramble | **Open — spoofing hazard** |
| C.2.e | MIP-00 KeyPackage selection policy: prefer non-last_resort, prefer highest `created_at`, lex-smallest id tiebreaker | Scramble (`MessageService.cs:683` naive `FirstOrDefault`) | **Open** |
| C.2.f | MIP-01 `required_capabilities` includes `self_remove` (0x000a) | dotnet-mls / marmot-cs group creation path | **Open** (tracked as "Fix D") — required before any SelfRemove implementation lands |
| C.2.g | MIP-03 SelfRemove full flow (proposal + accept-by-any-member commit + validation) | marmot-cs primary + Scramble caller | **Open** — now with two new obligations from A.7 (non-admin path) and A.8 (admin-relinquish-before-SelfRemove) |
| C.2.h | MIP-01 GCE admin authorization gate on inbound commits | dotnet-mls / marmot-cs | **Open** |
| C.2.i | MIP-04 v1 rejection + AAD byte layout verification | marmot-cs (`Mip04MediaCrypto.cs`) — file exists; layout unaudited | **Open** — Phase 0.5 read from earlier plan |

### C.3  Verification-only items (not necessarily new code)

| # | Requirement | Layer | Action |
|---|---|---|---|
| C.3.a | MIP-03 minimum encrypted content length matches new spec text | marmot-cs (`GroupEventEncryption.cs:96-97`) | Read spec commit `0a827b7`, confirm 28 remains the correct floor and adjust error message wording if the spec now labels it differently |
| C.3.b | MIP-01 HKDF-SHA256 conformance (A.6) | marmot-cs | Add HKDF-SHA256 test vector against a Rust MDK vector |
| C.3.c | MIP-02 accept-Welcome flow validates 0xF2EE extension before storing group | marmot-cs (`Mdk.AcceptWelcomeAsync`) | Read + one negative test |
| C.3.d | MIP-02 catch-up-then-self-update sequencing (A.11 clarification) | Scramble | Already an open item (C.2.b); no new code beyond that |

---

## Section D — Impact analysis by layer

### D.1  marmot-cs — most of the work lands here

Rationale: the protocol spec's wire-format and codec-level changes are all
in `MarmotCs.Protocol` / `MarmotCs.Core`. Scramble mostly consumes them.

- **Breaking codec changes:** C.1.a (extension v3), possibly C.1.d
  (encoding tag validation)
- **Additive codec:** C.1.f verification, C.3.a check, C.3.b HKDF vectors
- **New MLS logic:** C.2.f `self_remove` in required_capabilities, C.2.g
  SelfRemove flow, C.2.h GCE admin gate
- **Ripple to `dotnet-mls`:** C.2.f needs a `required_capabilities`-writing
  path if it doesn't already exist; C.2.h needs an inbound-commit hook the
  MDK can gate on

**Blast radius:** any consumer of `MarmotCs.Protocol.Mip01.NostrGroupData`
sees a new required field (`DisappearingMessageSecs`) — Scramble, tests,
Whitenoise-interop, and any future consumer. But this is a controlled
codec-level change with a clear compatibility window.

### D.2  Scramble — consumption + user-facing wiring

- **Wiring for A.1 disappearing messages:**
  - `Chat` model needs a `DisappearingMessageSecs` field (persisted).
  - Settings UI (Android + Desktop) needs to expose it (Scramble's UI parity
    rule applies).
  - `MessageService.SendMessageAsync` reads chat-level setting, computes
    NIP-40 tag, injects on outer event.
  - Inbound: local deletion timer per rumor `created_at + secs`.
  - Storage: message expiration column + a background sweeper.
- **Adversarial-safety fix C.2.d** is a small comparison, high value.
- **Selection policy C.2.e** is a sort-then-pick refactor in
  `MessageService.cs:683`.
- **24h scheduler C.2.a** — needs a durable schedule that survives process
  restarts (persistence-hook pattern already used by Scramble for pending
  ops).

### D.3  dotnet-mls — minimal but load-bearing

- Confirm `RequiredCapabilities` allows registering `self_remove` (0x000a)
  as a required proposal type; if not, add.
- Confirm `ProcessCommitCore` exposes enough context for marmot-cs to gate
  on admin identity for non-self-update / non-SelfRemove commits.

---

## Section E — Test coverage impact

The earlier plan's test discipline still applies (`Compliance/MipXX/`
folder, `[Trait("MIP", "MIP-XX")]`, failing-test-before-patch). Delta:

### E.1  New failing tests we now need

Roughly **~15 new tests** on top of the earlier plan's ~25, mostly
concentrated in the disappearing-messages feature:

- `Compliance/Mip01/GroupDataExtensionV3Tests` (codec round-trip; reject
  `0`; boundary between v2 and v3 on read)
- `Compliance/Mip03/DisappearingMessagesTests` (auto-apply NIP-40 on outer;
  strip caller-supplied when disabled; inbound expiration timer)
- `Compliance/Mip00/EncodingTagValidationTests` (reject missing/hex
  encoding tag)
- `Compliance/Mip04/ThumbhashTests` (emit + parse both hash types)
- `Compliance/Mip01/HkdfSha256VectorsTests` (spec vectors — cross-impl)
- `Compliance/Mip00/KeyPackageSelectionTests` (non-last_resort preferred,
  highest `created_at`, id tiebreaker)
- `Compliance/Mip03/InnerSenderVerificationTests` (spoofed inner rumor
  rejected)
- `Compliance/Mip02/SelfUpdate24hSchedulerTests` (schedule survives
  process restart)
- `Compliance/Mip02/CatchUpBeforeSelfUpdateTests` (ordering)

### E.2  Existing tests to invert or delete

- `RelayHarness/PublishFailureTests`: earlier plan called for inverting
  these in Phase 4. That work has landed (C.1.b in B.2). **Verify** the
  tests were actually inverted — if not, invert them now.

### E.3  Whitenoise-diagnostic ripple

The Whitenoise diagnostics suite currently exercises v2 group data. Once
C.1.a lands in marmot-cs, the diagnostics that create groups must be
updated to expect v3. Reader-side tolerance (v2-or-v3) is what keeps this
survivable during the migration window.

---

## Section F — Recommended plan

### F.1  Scope decision

The earlier plan was written as a single ~16-21 day batch. That batch is
now largely complete (Phases 1-4 landed). What remains is not one batch —
it's three logically-separable feature streams, each testable
independently.

I recommend splitting the remaining work into three shippable batches
rather than one monolithic PR, so we can:

1. Ship the new spec breaking-change (v3 extension) on a schedule the
   Whitenoise / other clients can coordinate with.
2. Ship user-visible features (disappearing messages) on a UX cadence.
3. Ship the remaining compliance hygiene (self-update scheduler, selection
   policy, inner-sender verification, etc.) as a low-risk quality-of-life
   sweep.

### F.2  Batch 1 — Protocol v3 + wire hygiene *(~3-5 days, mostly marmot-cs)*

- **C.1.a** — `NostrGroupData` v3: add `DisappearingMessageSecs`, keep v2
  read compatibility for a rollout window
- **C.1.d** — inbound encoding-tag validation for kind:30443/443/444
- **C.1.f / C.3.b** — HKDF-SHA256 conformance test vector
- **C.3.a** — verify MIP-03 minimum content length text vs. spec
- **C.3.c** — verify Welcome accept flow validates 0xF2EE

Bump `marmot-cs` version; Scramble picks up the new package. No user-facing
UI in this batch. Diagnostics run against a hybrid v2/v3 relay set.

### F.3  Batch 2 — Disappearing messages end-to-end *(~5-7 days, Scramble-heavy)*

- **C.1.b** — auto-apply NIP-40 expiration on outbound kind:445
- **C.1.c** — strip caller-supplied expiration when disabled
- Chat-level persistence of `DisappearingMessageSecs`
- Settings UI in both `Scramble.Mobile.Android` and `Scramble.UI`
  (per project parity rule)
- Inbound local-deletion timer + storage sweeper
- **C.1.e** thumbhash emission is a small parallel task; may or may not
  ride along

### F.4  Batch 3 — Compliance hygiene sweep *(~4-6 days, mixed layers)*

- **C.2.d** inner-sender verification (small, spoofing-safety win)
- **C.2.e** KeyPackage selection policy (naive `FirstOrDefault` → spec
  policy)
- **C.2.a** 24h self-update scheduler (durable)
- **C.2.b** catch-up-before-self-update ordering
- **C.2.c** init_key deletion timing with 24h grace
- **C.2.i** MIP-04 v1 rejection + AAD byte layout audit

### F.5  Batch 4 — SelfRemove *(deferred — depends on `self_remove` in required_capabilities)*

- **C.2.f** in dotnet-mls / marmot-cs: `required_capabilities` includes
  `0x000a`
- **C.2.g** SelfRemove flow, with A.7 non-admin support and A.8 admin
  relinquish ordering
- **C.2.h** GCE admin authorization gate on inbound commits

Ships only after Batch 1 (v3 extension carries new group-creation ceremony
anyway).

### F.6  Why batches instead of one batch

- **Coordination cost:** Batch 1 is coordinated cross-implementation
  (Whitenoise et al.). Batch 2 is our internal UX. Batch 3 is internal
  hardening. Different reviewer pools, different rollout windows.
- **Test signal clarity:** each batch's tests fail before and pass after
  its own patches — easier to attribute breakage during rollout.
- **Rollback surface:** if Batch 2's UX breaks, we don't have to unwind
  wire-format changes.

### F.7  Not-recommended

Do **not** bundle SelfRemove into Batch 3. SelfRemove has cross-cutting
implications (admin ceremony, non-admin commit path, dotnet-mls capability
work) that will bloat any batch it lands in. Keeping it in its own batch
also gives it a proper before/after test window.

---

## Section G — Open questions before starting

1. **v2/v3 migration window** — how long do we accept v2-formatted
   `NostrGroupData` on read? Marmot spec is silent; propose "read forever,
   write v3 only after Batch 1 ships." Confirm with Whitenoise team.
2. **MIP-05 (push)** — Batch scope excludes MIP-05. Confirm push is not on
   the near-term Scramble roadmap; if it is, MIP-05 needs its own batch
   with the new 1084-byte token format.
3. **Verification of pre-existing landings** — `MessageService.cs`
   references show staged-commit consumption is present. Before starting
   Batch 3, run
   `tests/Scramble.Diagnostics/RelayHarness/PublishFailureTests` and
   confirm they've already been inverted per Phase 4. If not, they still
   need to be.

---

## Appendix — File pointers used

Spec (fresh clone in `lib/marmot-cs/tmp-marmot-spec/marmot/`, HEAD
`21a67b2`):

- `CHANGELOG.md` §Unreleased — the authoritative delta source for Section A
- `01.md:97` — `disappearing_message_secs` field definition
- Commits mentioned inline: `21a67b2`, `387fe67`, `5eb04de`, `e24b606`,
  `0a827b7`, `de9453c`, `b992ca8`, `137da0b`, `23a1295`, `5f367aa`,
  `0e37af9`, `64bf159`, `24bab35`

marmot-cs (submodule tip `84b106f`):

- `src/MarmotCs.Protocol/Mip01/NostrGroupDataExtension.cs:14` — `Version =
  2` default (v3 bump target)
- `src/MarmotCs.Protocol/Crypto/ImageEncryption.cs:112,124` — HKDF-SHA256
  usage
- `src/MarmotCs.Protocol/Crypto/ExporterSecretKeyDerivation.cs:36-51` —
  HKDF-SHA256 usage
- Recent commits enumerated in B.1

Scramble (`b158675`):

- `src/Scramble.Core/Services/MessageService.cs:816, 1259-1333, 1401,
  1489-1547` — staged AddMember flow
- `src/Scramble.Core/Services/NostrService.cs:1601, 1734, 2193, 2212` —
  `PublishUnconfirmedException` sites
- `src/Scramble.Core/Services/ManagedMlsService.cs:64, 309, 495` — kind:30443
  rotation
- `src/Scramble.Core/Services/PublishUnconfirmedException.cs` — exception
  definition
- `src/Scramble.Core/Services/IMlsService.cs:34, 43, 198` — staged API +
  slot ID

Housekeeping: `lib/marmot-cs/tmp-marmot-spec/` was created for this
analysis (fresh spec clone). Delete after reading, or keep as an
implementation reference. It is not tracked by git.
