# Scramble — Project Context

## Platform targets

This project has **two UI targets** that must both be kept in sync when making UI changes:

| Project | Platform | UI framework | Key entry point |
|---------|----------|--------------|-----------------|
| `src/Scramble.Mobile.Android` | Android (mobile) | Avalonia XAML (Avalonia Android head) | `src/Scramble.Mobile.Android/Views/MobileMainView.axaml` |
| `src/Scramble.UI` + `src/Scramble.Desktop` | Windows / Linux / macOS (desktop) | Avalonia XAML | `src/Scramble.UI/Views/` |

Shared logic lives in:
- `src/Scramble.Core` — services, models, MLS/Nostr
- `src/Scramble.Presentation` — ReactiveUI ViewModels (used by both targets)

**Any feature that touches the UI must be implemented in both `Scramble.Mobile.Android` and `Scramble.UI`.**

### Abandoned: `src/Scramble.Android`

`src/Scramble.Android` is a legacy native Android head (fragment-based, Android Views).
It is **not** shipped: the publish workflow builds only `Scramble.Mobile.Android`, the
desktop test suite excludes it, and the desktop CI workflow does not compile it. **Do
not port new features into it.** Do not treat it as the "Android target" the parity
rule above refers to.
