> ⚠️ **SUPERSEDED (2026-07).** These units targeted the mdk **0.8.0** line. The
> target is now the **Dark Matter** rewrite (mdk 0.9.x). **Do not hand these to an
> implementer.** See `../00-START-HERE-dark-matter.md` and
> `../dark-matter-migration-scoping-2026-07.md`. Kept for history (a few findings,
> e.g. A4's account-identity-proof v2 details, were carried into the scoping doc).

# MDK Parity — work-order units

Each file in this directory is a **self-contained work order** for one small unit
of the MDK parity effort (see `../mdk-parity-plan-2026-07.md` for the big
picture). Each unit is written to be handed to an implementer model (e.g. Sonnet)
**one at a time**, executed literally, and verified before moving on.

## How to use

1. Pick the next `READY` unit from the index below (respect `Depends-on`).
2. Paste that unit file's contents to the implementer as the task.
3. When it reports back, check its "Acceptance criteria" and the verify commands.
4. `AUDIT` units produce a written report, not code — bring that report back to
   the orchestrator (the stronger model) to author the follow-up implementation
   units.

## Rules every unit assumes (the implementer MUST follow)

These are repeated tersely in each unit, but they apply globally:

- **Test-first.** Write the test(s) named in the unit, watch them fail (or
  confirm-pass for verification units), then implement, then watch them pass.
- **Stay in scope.** Touch only the files the unit lists. Do **not** refactor
  unrelated code, rename things, fix unrelated warnings, or "improve" nearby
  code. If something outside scope looks wrong, note it in the report — do not
  change it.
- **Do not commit** unless the unit says to. The orchestrator handles commits.
- **Do not** change public wire behavior beyond what the unit specifies. In
  particular do not change `NostrGroupData` default version (stays 2), and do not
  alter existing encryption/exporter logic.
- **Report back** exactly what the unit's "Report back" section asks.

### Repo gotchas (important, easy to get wrong)

- **Two solutions.** `marmot-cs` and `dotnet-mls` are git submodules under `lib/`.
  Scramble references them by **ProjectReference**, so source edits are picked up
  without any version bump.
- **Building/testing marmot-cs standalone** requires the local dotnet-mls flag:
  `dotnet test <proj> -c Debug -p:UseLocalDotnetMls=true`. Without it, restore
  tries to pull `DotnetMls` from GitHub Packages and fails.
- **Scramble.Core is multi-targeted.** Build/test the desktop target with
  `-f net10.0` (the `net10.0-android` target needs the Android SDK and will error
  in a plain shell — that is expected, ignore it).
- **Internals are visible to tests** for both `Scramble.Core` (→
  `Scramble.Core.Tests`, `Scramble.Diagnostics`) and `MarmotCs.Core` (→
  `MarmotCs.Core.Tests`). You can test `internal` members directly.
- **Do not touch** `tmp-*` directories or `lib/marmot-cs/tmp-marmot-spec/`.

## Index

Status: `READY` = safe to hand off · `AUDIT` = read+report only, no code ·
`BLOCKED` = await orchestrator decomposition after its audit lands.

| Unit | Title | Type | Size | Depends-on |
|---|---|---|---|---|
| **A4** | **Audit: is `account-identity-proof` (`0xf2f1`) enforced by target MDK?** | AUDIT | M | — |
| U1 | Extension forward-compatibility (accept future versions) | READY | S | — |
| U2 | Verify unsigned rumor IDs on receive | READY | S | — |
| U3 | Ciphertext replay dedup in commit-race resolution | READY | M | — |
| U4 | Encrypted-media thumbhash emit + parse | READY | S | — |
| U7 | Fix the Whitenoise interop harness | READY | M | — |
| A1 | Audit: MIXED_CIPHERTEXT wire-format support in dotnet-mls | AUDIT | M | — |
| A2 | Audit: encrypted-media crypto (AAD, v1 reject, SHA-256) | AUDIT | S | — |
| A3 | Audit: admin-list validation current state | AUDIT | S | — |
| — | Account identity proof `0xf2f1` (Session 12) | BLOCKED | after A4 | A4 |
| — | SelfRemove send-side | BLOCKED | after A1 | A1 |
| — | RequiredCapabilities LCD / mixed-version invites | BLOCKED | after A1 | A1 |
| — | GroupContextExtensions upgrade path | BLOCKED | after A1 | A1 |
| — | Full convergence policy v1 (Session 13) | BLOCKED | after A1 | A1 |
| — | Disappearing messages end-to-end | BLOCKED | after U1 | U1 |
| Z1 | (PARKED) Rename `MipXX` code → new spec surfaces | REFACTOR | M | all parity done |

**Suggested order:** **A4 first** (it may be the #1 interop blocker) → U1 → U7 →
(U2, U3, U4, A1, A2, A3 in any order) → bring the audit reports back to the
orchestrator to unblock the identity-proof / SelfRemove / capabilities /
convergence / disappearing-messages units. **Z1 stays parked** until parity lands.

## Note on spec reorganization

The Marmot spec deprecated the flat MIP-00…05 docs for surface-organized docs
(`foundation/`, `protocol-core/`, `app-components/`, `transports/`, `features/`).
Our code still uses `MipXX` naming — that is fine and unchanged (Z1 handles the
optional rename later). Cite the **new surface docs** as authoritative; see the
mapping table in `../mdk-parity-plan-2026-07.md`.
