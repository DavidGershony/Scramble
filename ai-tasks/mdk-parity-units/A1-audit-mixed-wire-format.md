# A1 — AUDIT: MIXED_CIPHERTEXT wire-format support in dotnet-mls

**Type:** AUDIT (read + written report, **no code changes**) · **Size:** M ·
**Depends-on:** none · Unblocks: SelfRemove, RequiredCapabilities-LCD, GCE-upgrade

## Why

Rust MDK uses `MIXED_CIPHERTEXT` wire-format policy: outgoing handshakes stay
ciphertext (`PrivateMessage`), but the group **accepts incoming `PublicMessage`
handshakes** — required to receive `PublicMessage` SelfRemove proposals from
departing members. Before we implement SelfRemove we must know exactly what our
MLS layer already supports. **This unit only reads and reports.**

## What to read

- `lib/dotnet-mls/src/DotnetMls/Group/MlsGroup.cs` (esp. `Commit`,
  `ProcessCommit(PrivateMessage)`, `ProcessCommit(PublicMessage)`,
  `ProcessMessage`/proposal handling, `ProcessCommitCore`, and any
  `WireFormat` usage)
- `lib/dotnet-mls/src/DotnetMls/**` for message framing
  (`MessageFraming`, `PublicMessage`, `PrivateMessage`, `WireFormat` types)
- `lib/marmot-cs/src/MarmotCs.Core/Mdk.cs` around the comment
  "PublicMessage is only expected for SelfRemove proposals" and its
  message-processing path

## Questions to answer (be precise; quote file:line)

1. **Outgoing:** Are commits/proposals emitted as `PrivateMessage` today?
   (Confirm `Commit(...)` returns `PrivateMessage`.)
2. **Incoming commits:** Is there a working `ProcessCommit(PublicMessage)` path,
   and does `ProcessCommitCore` handle both wire formats? Any code that would
   reject a `PublicMessage`?
3. **Incoming proposals:** Can the layer receive and validate a **bare
   `PublicMessage` proposal** (not a commit) — specifically a SelfRemove
   proposal (proposal type `0x000a`)? Or is proposal processing wired only for
   `PrivateMessage` / only inside commits?
4. **Wire-format policy:** Is there any notion of a per-group wire-format policy
   (PURE vs MIXED)? If not (likely), note that dotnet-mls has no explicit policy
   gate — which means the real question is purely "does the parse/validate path
   accept PublicMessage handshakes."
5. **SelfRemove proposal type:** Is proposal type `0x000a` (SelfRemove) known
   anywhere in dotnet-mls (enum/const/match)? Or is it entirely absent?
6. **marmot-cs side:** What does the `Mdk.cs` PublicMessage/SelfRemove path at
   the referenced comment actually do today — process, ignore, or error?

## Deliverable (the report)

A markdown report with, for each question: the answer, the exact `file:line`
evidence, and a one-line "gap or OK." End with a **"Gap summary"** section
listing precisely what is missing to (a) receive a `PublicMessage` SelfRemove
proposal and (b) emit one — separated by layer (dotnet-mls vs marmot-cs). Do not
propose or write code; just the factual map.

## Scope guards

- **No code changes.** Reading and reporting only.

## Report back

Post the full report. It becomes the input for the SelfRemove implementation
units. Do not commit anything.
