<!--
Scramble PR template. The checkboxes below tie to enforceable gates
(CLAUDE.md invariants I1–I5). If a box is off, CI will block the merge or
the reviewer will send it back.

Remove sections that don't apply. Do not delete the section headings — they
are how the invariant is traceable back to ANALYSIS.md.
-->

## Summary

<!-- One or two lines. What changes for the user, or the reason for the refactor. -->

## Affected areas (check every box that fits)

- [ ] **MLS lifecycle** — `src/Scramble.Core/Services/{MlsService,ManagedMlsService,MessageService}.cs`
- [ ] **Nostr transport** — `src/Scramble.Core/Services/NostrService.cs`, relay wiring, subscription lifecycle
- [ ] **Signer state** — `ExternalSignerService.cs`, `LoginViewModel.cs`, `MainViewModel.cs`
- [ ] **Storage schema / migration** — `StorageService.cs` DDL, `MlsStates`, `PendingInvites`
- [ ] **Shared UI (Avalonia)** — `src/Scramble.UI/**` (multi-targeted onto both desktop and mobile)
- [ ] **Mobile.Android platform shell** — `src/Scramble.Mobile.Android/**` (activity, permissions, IME insets, native services)
- [ ] **CI / build** — `.github/workflows/**`, `docker-compose.test.yml`

## Anti-drift (I1)

<!-- Only relevant if you touched Scramble.Android or Scramble.Mobile.Android. -->

- [ ] No files under `src/Scramble.Android/**` were modified in this PR, OR
- [ ] A commit in this PR carries a `Legacy-Android-Change: <reason>` trailer.
      Reason: _<one line — why the change legitimately touches the abandoned head>_

- [ ] No new `.axaml` view files were added under `src/Scramble.Mobile.Android/**` (existing files may be modified), OR
- [ ] A commit in this PR carries a `Mobile-Shell-Exempt: <reason>` trailer.
      Reason: _<one line — why this view legitimately lives in the mobile shell and not in `src/Scramble.UI/Views/**`>_

## Test coverage (I2, I3)

- [ ] Touches `src/Scramble.Core/Services/**`? Then an integration test in
      `tests/Scramble.Diagnostics/` or `tests/Scramble.Core.Tests/` was
      added/updated in this PR. The `integration` CI job must be green.
- [ ] This is a `fix:` commit on a service? Then a regression test that
      **fails without the fix** is included. If not, this commit carries a
      `Test-Debt: <reason>` trailer and I've filed a follow-up.
- [ ] Signer-related change? At least one row in
      `tests/Scramble.UI.Tests/SignerStateMachineTests.cs` or
      `SignerKnownBugsTests.cs` was added/updated.

## Landing discipline (I4)

- [ ] Diff is ≤ 8 files, OR spans only a single subsystem, OR carries a
      `Landing-Discipline-Exempt: <reason>` trailer explaining why a bundled
      landing is safer than a split (e.g., refactor + feature in one atomic
      commit because separating them would leave a broken build).

## Pivot freeze (I5)

- [ ] Not applicable — the project is not currently in a UI-head pivot, OR
- [ ] This PR is a bugfix (no new features), OR
- [ ] This PR carries a `Pivot-Exempt: <reason>` trailer.

## Manual test plan

<!-- What did you verify locally? For UI changes, at least one screen on
     desktop and one on Mobile.Android since the same view code runs on both. -->

- [ ] Desktop (Avalonia): _description_
- [ ] Mobile.Android (Avalonia head): _description or n/a_

## Rationale traceback

<!-- Optional — link to the ANALYSIS.md invariant your change addresses. -->

Relevant invariants: <!-- e.g. I2 (integration coverage) -->
Cluster / hotspot: <!-- e.g. C1 (MIP-03 decryption cluster), or "new subsystem" -->
