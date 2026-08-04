# Z1 — (PARKED) Rename `MipXX` code structure to the new spec surfaces

**Type:** REFACTOR (mechanical, no behavior change) · **Size:** M ·
**Status:** PARKED — do **not** start until the interop parity work has landed.
**Depends-on:** all in-flight parity units complete (this rename would collide
with units that reference `MarmotCs.Protocol.MipXX` paths).

## Why parked

The Marmot spec deprecated the flat MIP-00…05 docs and reorganized around
surfaces (`foundation/`, `protocol-core/`, …). Aligning our code's `MipXX`
namespaces/folders is **cosmetic** — zero wire/interop impact — so it earns no
priority over the actual interop work, and doing it first would break every
open parity unit. Run it only as a cleanup once parity is done.

## Goal (when unparked)

Rename our internal `MipXX` organization to the new surface names, with **no
behavior change** and **no wire change**.

## Scope (when unparked)

- `lib/marmot-cs/src/MarmotCs.Protocol/` folders + namespaces:
  `Mip00 → foundation/keypackages`, `Mip01 → foundation/groupdata`,
  `Mip02 → protocolcore/joining`, `Mip03 → protocolcore/groupmessaging`, etc.
  (agree the exact mapping with the orchestrator first — see
  `../mdk-parity-plan-2026-07.md` reference table).
- Every `using MarmotCs.Protocol.MipXX;` in `src/Scramble.Core/**` and all tests.
- Optionally: `MIP-XX` comments and `[Trait("MIP", "MIP-XX")]` test traits →
  surface names (lower priority; can be a second pass).

## Hard rules (when unparked)

- **Zero behavior change.** Namespace/folder/identifier renames only. No logic,
  no wire, no default-version changes.
- Do it in one mechanical sweep per rename target; build + run the FULL suite
  after each: marmot-cs (`-p:UseLocalDotnetMls=true`), `Scramble.Core` (net10.0),
  diagnostics. Every test that passed before MUST pass after.
- Do not combine with any functional change.

## Acceptance criteria (when unparked)

- All suites green, identical counts to pre-rename.
- No `MarmotCs.Protocol.MipXX` identifiers remain (or a documented, agreed
  subset intentionally kept).

## Report back

The mapping used, the rename diff summary, and before/after test counts.
