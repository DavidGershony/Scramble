# A4 — AUDIT: is `account-identity-proof-v1` (`0xf2f1`) enforced by the target MDK?

**Type:** AUDIT (read + report, **no code changes**) · **Size:** M ·
**Depends-on:** none · **Do EARLY** — this may be the #1 interop blocker.

## Why

The reorganized Marmot spec added `foundation/account-identity-proof-v1.md`: a new
**breaking** MLS LeafNode extension `0xf2f1` (`marmot.account-identity-proof.v1`)
that binds the Nostr account key to the leaf's MLS signature key with a BIP-340
Schnorr signature. Spec says clients **MUST reject** leaves/KeyPackages without a
valid proof. It is absent from our code entirely. Before we build anything, we
must learn **whether the MDK version Whitenoise actually runs enforces it today** —
that decides whether this is the top priority or a future item.

## What to check (report each with evidence)

1. **Does the target MDK build/require it?**
   - Search the mdk source for the extension. Use the GitHub CLI:
     ```bash
     gh api "repos/marmot-protocol/mdk/contents/crates?ref=v0.9.4" --jq '.[].name'
     gh search code --repo marmot-protocol/mdk "0xf2f1" 2>/dev/null || \
       gh api "repos/marmot-protocol/mdk/git/trees/v0.9.4?recursive=1" \
         --jq '.tree[].path' | grep -iE "identity.?proof|f2f1|account.?identity"
     ```
   - Also check the rev Whitenoise pins (`e8cd584`, mdk 0.8.0) — does *that* build
     require it, or is it 0.9.x-only? (The 0.8.0 `mdk-core/CHANGELOG.md` did not
     mention it.)
2. **Does a live WN KeyPackage carry `0xf2f1`?**
   - With the WN docker up (`docker compose -f docker-compose.test.yml up -d`),
     have WN publish a KeyPackage, fetch the kind:30443 event from the relay
     (`ws://localhost:7777`), base64-decode `content`, and inspect the MLS
     KeyPackage's LeafNode extensions for type `0xf2f1`. Report present/absent.
3. **Our current state:** confirm Scramble/marmot-cs does NOT build or validate
   `0xf2f1` today (grep `src/`, `lib/marmot-cs`, `lib/dotnet-mls` for
   `f2f1` / `identity.proof` / `account.identity`).
4. **Payload shape:** from the spec doc, record the exact byte layout (version,
   ciphersuite, signature scheme, `account_identity[32]`, sig-pubkey-len +
   pubkey, `schnorr_signature[64]`) and the note that it uses **fixed-width
   big-endian**, NOT the QUIC-varint canonical encoding. Record exactly what the
   64-byte Schnorr signature is computed over (quote the spec).

## Deliverable (the report)

A markdown report answering 1–4 with evidence, ending with a **verdict**:
- "ENFORCED by target MDK" → this becomes the top implementation priority; list
  what dotnet-mls + marmot-cs must add (build proof on our KPs/leaves; verify
  inbound; reject invalid).
- "NOT yet enforced / spec-ahead-of-impl" → record as a near-future item, no
  immediate code.

## Scope guards

- **No code changes.** Reading, querying, and reporting only.

## Report back

Post the full report + verdict. It gates whether we implement Session 12 now. Do
not commit.
