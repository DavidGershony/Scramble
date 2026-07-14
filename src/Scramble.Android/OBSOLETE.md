# OBSOLETE — do not modify this directory

`src/Scramble.Android` is the **legacy** native Android head of Scramble,
built on Android Views and Fragments. It is no longer shipped:

- The publish workflow builds only `src/Scramble.Mobile.Android`.
- `Scramble.Desktop.slnf` excludes this project.
- The desktop CI workflow does not compile it.
- No release artifact contains its output.

## What to use instead

The current Android target is `src/Scramble.Mobile.Android` (Avalonia head),
which reuses the UI code in `src/Scramble.UI/Views/**` via multi-targeting.
When you touch UI, you edit **one place** and both platforms pick it up.

Platform-specific Android plumbing (activity lifecycle, permissions,
foreground services, IME insets, native shims) lives in
`src/Scramble.Mobile.Android/`.

## Enforcement

`.github/workflows/drift.yml` fails any PR that modifies files under
`src/Scramble.Android/**`. If you have a genuine reason to touch this
directory (final cleanup, folder removal, etc.), add a
`Legacy-Android-Change: <reason>` trailer to any commit in the PR.

See `CLAUDE.md` invariant I1 and `ANALYSIS.md` STEP 5b for the historical
rationale (the 2026-05-11 Avalonia-on-Android pivot that made this head
redundant, and the W21–W22 firefighting weeks that followed).
