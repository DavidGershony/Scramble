# Account-identity-proof v2 — byte-exact construction (step 4, 2026-08-09)

**Status: DONE.** This pins down the kind:450 proof construction the Dark Matter
migration's step 4 required (START-HERE step 4; scoping doc risk #2 — MUST-reject
on target, "a single canonical-id mismatch = 100% rejection").

**Confidence key:** 🟢 verified against source this session · 🟡 inference ·
🔴 needs verification before code lands.

**Sources (exact versions, all read in full this session):**
- Spec (`marmot-protocol/marmot` @ HEAD, 2026-08-09):
  `app-components/account-identity-proof-v2.md` (status **adopted**),
  `foundation/authorization-proofs.md` (adopted),
  `foundation/account-identity-proof-v1.md` (superseded),
  `foundation/identity.md`, `foundation/canonical-encoding.md` §"Nostr-shaped values".
- Code: `crates/cgka-engine/src/account_identity_proof.rs` @ **v0.9.4** (417
  lines) and @ **wn-agent-v0.9.10** (889 lines), plus `key_package.rs` @
  wn-agent-v0.9.10 (profile plumbing).

---

## 0. ⚠ STRATEGIC FINDING FIRST — our v0.9.4 pin is stale on exactly this feature

There are **three** proof constructions in history, and the format **hard-broke
after our pinned tag**:

| Construction | Carrier | Where it lives | Status |
|---|---|---|---|
| **v1** | LeafNode custom ext `0xf2f1`, version byte `1` | spec `foundation/account-identity-proof-v1.md` | Superseded. Binary-preimage SHA-256 signing (NOT a Nostr event). Never in mdk 0.9.x. **Do not implement.** 🟢 |
| **"Legacy" event-shaped v2** | LeafNode custom ext `0xf2f1`, version byte `2` | **mdk v0.9.4 code only** — it has **no spec document** (the spec jumps v1 → 0x8009) | What our pinned tag implements and emits. At mdk HEAD it survives as the accept-only `ProtocolProfile::Legacy`. 🟢 |
| **Current v2** | LeafNode `app_data_dictionary` **component `0x8009`** (104 bytes) | spec `app-components/account-identity-proof-v2.md` (adopted); mdk from **wn-agent-v0.9.5** onward | The real target. Spec: "MUST NOT accept \[0xf2f1\] as a substitute for component id 0x8009" — no mixed fallback. 🟢 |

The mdk tag series after v0.9.4 is `wn-agent-v0.9.5` … `wn-agent-v0.9.10`
(the deployed-Whitenoise line). `0x8009` is present from wn-agent-v0.9.5 🟢
(grep-verified). mdk HEAD classifies every group as exactly one
`ProtocolProfile` (Legacy = group `RequiredCapabilities` contains `0xf2f1`;
Current = group `app_components` requires `0x8009`; both or neither → reject —
`account_identity_proof_HEAD.rs:470-521`).

**Consequences for Scramble:**
1. **Implement the Current (`0x8009`) construction as primary.** New groups
   Scramble creates must be Current-profile — Legacy is a dead end the spec
   explicitly forbids carrying forward.
2. **Legacy (0xf2f1-v2) is accept-only, and only if we must join groups created
   by v0.9.4-era peers.** Decide at step 5 whether to support it at all; if WN's
   deployed fleet is ≥ wn-agent-v0.9.5, skip it entirely (one less construction).
   🔴 confirm WN's deployed version when asking for their date.
3. **The v0.9.4 pin should be re-pinned to the latest `wn-agent-v0.9.x` tag at
   step 5.** This is the first concrete proof that 0.9.4-frozen details are
   already drifting; assume other subsystems drifted too (a step-5 task:
   diff v0.9.4 → wn-agent-v0.9.10 changelog for the modules we've already
   analyzed).

---

## 1. The Current construction (`0x8009`) — implement this

### 1.1 Carrier bytes (what goes in the LeafNode)

Exactly one entry under component id `0x8009` in the LeafNode's
`app_data_dictionary`, data = **exactly 104 bytes** 🟢 (spec §Component data;
HEAD `account_identity_proof.rs:275-284`):

```text
struct {
  opaque signer_pubkey[32];   // raw x-only secp256k1 account pubkey
  uint64 created_at;          // unsigned BIG-ENDIAN unix seconds
  opaque signature[64];       // BIP-340 Schnorr signature
} MarmotAuthorizationProof;   // fixed-width, no prefixes, no version field
```

Plus: the LeafNode's `app_components` support list MUST advertise `0x8009`
(validated — HEAD `:351-358`), and the GroupContext's required-components list
MUST require it. The component is leaf-only: it MUST NOT appear in a
GroupContext dictionary (HEAD rejects, `:489-497`), and MUST NOT be
created/updated via `AppDataUpdate`.

### 1.2 The signing event (local-only, never published)

Reconstructed identically by producer and verifier 🟢 (spec §Signing event; HEAD
`:137-168` — they agree field-for-field):

```text
kind       = 450
pubkey     = lowercase-hex(signer_pubkey)                  // 64 hex chars
created_at = proof.created_at                              // integer, 1..=2^53-1
content    = "Authorize this MLS leaf key for my Marmot account"
tags       = [                                             // EXACTLY these, EXACTLY this order, no others
  ["d",                "marmot.account-identity-proof.v2"],
  ["component",        "0x8009"],
  ["ciphersuite",      "0x0001"],                          // 0x + exactly 4 LOWERCASE hex digits
  ["signature_scheme", "0x0807"],                          // same encoding (Ed25519 under cs 0x0001)
  ["mls_signature_key", <lowercase hex of the leaf signature_key bytes, no 0x, no TLS length prefix>]
]
```

- **Event id** = SHA-256 of the NIP-01 canonical serialization
  `[0, pubkey, created_at, kind, tags, content]` — UTF-8 JSON, no insignificant
  whitespace, NIP-01 escaping only (`"` `\` `\n` `\r` `\t` `\b` `\f`; everything
  else verbatim), `created_at`/`kind` as JSON integers 🟢
  (`canonical-encoding.md:175-183`).
- **Signature** = BIP-340 Schnorr over the 32-byte event id, under the account
  key. Stored signature is the 64-byte raw form.
- `created_at`: producer sets local current unix seconds; valid range
  `1..=9007199254740991` (2^53−1); **no receiver-side age/freshness rule** —
  never compare against wall clock (spec §Validating; HEAD `:457-467` has an
  explicit "do not add age rejection" comment). Zero is invalid (that's what
  distinguishes it from the Legacy event at the range level).
- The event is a **signing template**: MUST NOT be published to relays.

### 1.3 Official test vector (unit-test this byte-for-byte) 🟢

From spec §Signing test vector — BIP-340 secret key `3`, all-zero aux
randomness:

```text
signer_pubkey     = f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9
created_at        = 1700000000
ciphersuite       = 0x0001,  signature_scheme = 0x0807
mls_signature_key = 000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f

canonical serialization:
[0,"f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9",1700000000,450,[["d","marmot.account-identity-proof.v2"],["component","0x8009"],["ciphersuite","0x0001"],["signature_scheme","0x0807"],["mls_signature_key","000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"]],"Authorize this MLS leaf key for my Marmot account"]

event_id  = b7e9a15dd85990fb0f49c33db3cc9875f73986207b038404ceb6b7fec4e0af6b
signature = c5315d3c85b9d4907cb03395a2a97b3ba2eab393f8e45b13a5d5233acedac60a51d2a295e1b1b5ee372d18a49bdb8041a7dba9dedce722c7c6f712f78bbdfb5d
104-byte component data:
f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9000000006553f100c5315d3c85b9d4907cb03395a2a97b3ba2eab393f8e45b13a5d5233acedac60a51d2a295e1b1b5ee372d18a49bdb8041a7dba9dedce722c7c6f712f78bbdfb5d
```

Note: BIP-340 signing uses deterministic-with-aux; to reproduce the vector's
exact signature bytes the test must sign with all-zero aux randomness (or just
*verify* the given signature, which is aux-independent — do both: verify the
vector, and verify our own fresh signatures round-trip).

### 1.4 MUST-reject validation checklist (verifier) 🟢

From spec §Validation + HEAD `:317-455`, in order:

1. Credential is a `BasicCredential` with a 32-byte identity that is a valid
   x-only secp256k1 point.
2. Leaf carries **exactly one** of {legacy ext `0xf2f1`, component `0x8009`} —
   both present → reject ("mixes"); neither → reject.
3. (Current) `0x8009` advertised in the leaf's `app_components` support list.
4. Component data exactly 104 bytes, no trailing bytes.
5. `signer_pubkey` == `BasicCredential.identity` (byte-exact) and a valid
   x-only key.
6. `created_at` in `1..=2^53−1`. No wall-clock comparison.
7. `ciphersuite`/`signature_scheme` used in reconstruction == the validated MLS
   context (KeyPackage's own ciphersuite when validating a KeyPackage — mdk#747;
   the group's when validating a member leaf).
8. `mls_signature_key` == the leaf's `signature_key` bytes (exact, unprefixed).
9. Recompute NIP-01 event id from the reconstructed template; BIP-340-verify
   `signature` over it under `signer_pubkey`.
10. Group-level: GroupContext must require `0x8009` (Current) XOR `0xf2f1`
    (Legacy); mixed or absent → group invalid. All leaves must match the
    group's profile (`ensure_profile`, HEAD `:523-534`).

**Validation seams** (must run at, per HEAD + survives/rewrite diff §2.1):
KeyPackage parse; every leaf of a joined Welcome's ratchet tree; staged-commit
Add proposals (each added leaf), Update proposals (leaf must keep its prior
account identity), and the commit's update-path leaf (must match the
committer); session-open hydration (cached via the validated-tree marker).

### 1.5 Replacement rules 🟢

- A proof is reusable only while **all** signed inputs are byte-identical; new
  leaf key / ciphersuite / scheme / account ⇒ new proof (new `created_at`).
- A member may replace its component only via MLS-authenticated replacement of
  its own LeafNode, keeping the same account identity. Account change =
  remove + re-add, never a self-update.
- `0x8009` MUST NOT be removed from a non-blank member leaf.

---

## 2. The Legacy event-shaped v2 (`0xf2f1`, version byte 2) — accept-only, maybe never

Only needed if Scramble must join v0.9.4-era groups (see §0.2). Differences
from Current 🟢 (v0.9.4 `account_identity_proof.rs:67-131`; unchanged at HEAD
as the Legacy arm):

- **Carrier:** LeafNode custom extension `0xf2f1`, payload
  `u8 version=2 || u16 ciphersuite BE || u16 scheme BE || opaque identity[32] ||
  u16 keylen BE || opaque key[keylen] || opaque sig[64]`, no trailing bytes.
- **Event:** kind 450, `content = ""`, `created_at = 0`, tags in order:
  `["d","marmot.account-identity-proof.v2"]`,
  `["extension","0xf2f1"]` (lowercase 0x-hex4),
  `["version","2"]`,
  `["ciphersuite","1"]` (**DECIMAL** — not 0x-hex! v0.9.4 `:83-90`),
  `["signature_scheme","2055"]` (**DECIMAL** for 0x0807),
  `["mls_signature_key",<hex>]`.
- Same NIP-01 id + BIP-340 signature over it.
- Group marker: `0xf2f1` in MLS `RequiredCapabilities` extension types (not
  app_components).
- No spec document exists for this construction — mdk code is the only
  normative source. No official test vector; generate one from mdk if we
  implement it (Amethyst-style `mdk-vector-gen`).

The decimal-vs-hex tag encoding and content/created_at differences mean the two
constructions can never verify against each other — the break is total, by
design (authorization-proofs.md §Versioning).

---

## 3. Signer integration — very good news for Scramble/Amber 🟢

`authorization-proofs.md:5-8` states the design goal outright: the proof is a
**normal signed Nostr event** precisely to "accommodate external signers that
sign Nostr events but do not expose arbitrary BIP-340 signing." That is
Scramble's NIP-46/Amber situation exactly:

- The `AccountIdentityProofSigner` seam (required by the engine builder — see
  survives/rewrite diff §2.1 EngineBuilder row) maps to:
  - **local-key signer:** sign the event id directly;
  - **Amber/NIP-46 signer:** hand the kind:450 unsigned event (the code even
    provides `proof_event_json()` for exactly this) to the external signer via
    the existing `ExternalSignerService` flow, get a signed event back.
- **Producer-side MUST (spec §Producing):** when an external signer returns a
  signed event, verify byte-exactly that `pubkey`/`created_at`/`kind`/`tags`/
  `content` equal the request, the id recomputes, and the signature verifies —
  reject substitutions, extra tags, or stale responses. mdk's
  `signature_from_signed_event` (`HEAD:183-198`) is the reference shape.
- Signer UIs will show kind 450 + the content string; the human-readable
  content exists for that consent screen.
- ⚠ Timing: proof signing happens at **KeyPackage creation** and at **leaf
  replacement** — both can occur when Amber is not immediately reachable. The
  Scramble flow must treat proof-signing as an async, user-visible step (same
  class as existing Amber signing prompts), not an inline sync call. 🟡 design
  note for the engine's C# signer seam: make `SignAccountIdentityProofAsync`
  properly async (the Rust trait is sync; C# should not copy that).

---

## 4. What this needs from `dotnet-mls` — nothing new 🟢

- The `0x8009` component rides the LeafNode `app_data_dictionary` — for
  **carriage**, that dictionary is itself opaque-extension bytes (capability (a)
  of scoping §12: opaque leaf extensions PRESENT). Encoding/decoding the
  dictionary is `Scramble.Marmot.AppComponents` work, not library work.
- Validation needs read access to every member leaf's extensions + signature
  key (Welcome tree walk, staged-commit proposals). 🔴 one small check before
  engine coding: confirm dotnet-mls exposes per-leaf `LeafNode.Extensions` and
  `SignatureKey` for (i) all ratchet-tree leaves post-Welcome and (ii) the
  leaves inside a staged commit's Add/Update proposals. Expected present
  (KeyPackage/LeafNode are public types per the §12 audit) but verify —
  read-only accessor additions would be trivially generic if missing.
- NIP-01 canonical event id + BIP-340 sign/verify: Scramble already owns both
  (every published Nostr event id; schnorr via existing crypto). Zero new
  primitives.

## 5. Size estimate — revise DOWN

Scoping §risk-2 carried this as **M** with "byte-exact minefield" risk. With the
construction now pinned and an official test vector available:

- **Current construction (build + validate + vectors): S.** One event template,
  one 104-byte codec, one BIP-340 verify, wired into validation seams that the
  engine (sized L) already accounts for.
- **Legacy accept-path (if kept): +S.** Second template + TLV codec, no vector
  (generate via mdk).
- The risk was never the code size — it was *discovering* the construction
  precisely. That's done; the residual risk is the two 🔴s: dotnet-mls per-leaf
  accessor check, and WN's deployed version (drives Legacy-support yes/no and
  the re-pin).

## 6. Actions fed into step 5

1. Re-pin the reference from `v0.9.4` → latest `wn-agent-v0.9.x`; add a
   drift-diff task (v0.9.4 → wn-agent-v0.9.10) over the already-analyzed
   modules before finalizing the build order.
2. Ask WN, alongside the date question: which mdk tag is deployed, and do any
   production groups still require `0xf2f1` (Legacy)? Their answer decides
   whether Scramble implements §2 at all.
3. `Scramble.Marmot.Identity.AccountProof` build item: Current construction +
   spec vector test first; `IAccountIdentityProofSigner` async seam bridging
   local-key and Amber/NIP-46 signing.
4. dotnet-mls read-accessor check (§4 🔴) — read-only, no permission needed to
   *check*; any gap becomes a permission-gated generic proposal.
