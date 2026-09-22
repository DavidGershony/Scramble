# A2 — AUDIT: MIP-04 media crypto (AAD, v1 rejection, SHA-256 integrity)

**Type:** AUDIT (read + written report, **no code changes**) · **Size:** S ·
**Depends-on:** none

## Why

MIP-04 encrypted media has an exact wire contract. We need to confirm our
implementation matches the Rust MDK / spec byte-for-byte before trusting media
interop. **This unit only reads and reports.**

## What to read

- `lib/marmot-cs/src/MarmotCs.Protocol/Crypto/Mip04MediaCrypto.cs` (end to end)
- Its callers in `src/Scramble.Core/Services/` (media encrypt/decrypt paths)
- Existing tests: `tests/Scramble.Core.Tests/Mip04MediaCryptoTests.cs`

## Questions to answer (be precise; quote file:line)

1. **Version:** Is `mip04-v2` the current version, and is `mip04-v1`
   **rejected** on decrypt? (Quote the check, or note its absence.)
2. **Key derivation:** Is the file key derived as
   `HKDF-Expand(exporter_secret, context, 32)` with exporter label
   `("marmot","encrypted-media")` and expand-only semantics (exporter used
   directly as the PRK)? Confirm the exact context bytes.
3. **AAD byte layout:** Is the AEAD AAD exactly
   `"mip04-v2" || 0x00 || file_hash || 0x00 || mime || 0x00 || filename`?
   Quote how the AAD is assembled.
4. **Nonce:** Random 12-byte nonce per encryption, stored in the `n` imeta field?
5. **Integrity:** After decrypt, is `SHA256(decrypted_content)` compared to the
   `x` field value, and does a mismatch reject the content?
6. **Edge cases:** invalid base64 / too-short / AEAD-failure — do they drop
   without exposing plaintext?

## Deliverable (the report)

For each question: answer + `file:line` evidence + "OK" or "GAP: <what's
missing>". End with a **"Gaps to fix"** list ordered by severity. If everything
is already correct, say so explicitly (that is a valid, valuable outcome).

## Scope guards

- **No code changes.** Reading and reporting only.

## Report back

Post the full report. Do not commit.
