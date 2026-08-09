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

See `src/Scramble.Android/OBSOLETE.md` for the full deprecation notice.

---

## INVARIANTS — break and CI will fail

Every rule below is enforced by a real check on the repo. Where a rule allows
an escape, the escape must be recorded (commit trailer or documented category)
so that exemptions are auditable via `git log --grep`.

### I1 — Anti-drift on the abandoned and mobile-shell paths

Two sub-rules, both enforced by `.github/workflows/drift.yml` running
`scripts/check-drift.ps1`:

**I1-L (Legacy Android).** No PR may modify a file under
`src/Scramble.Android/**`.
- **Escape:** `Legacy-Android-Change: <reason>` trailer on any commit in the
  range (for the rare intentional touch — folder removal, final cleanup).

**I1-M (Mobile shell purity).** No PR may **add** a new `.axaml` file under
`src/Scramble.Mobile.Android/**`. Modifications to existing view files
(e.g. `MobileMainView.axaml`) are fine — view content belongs in
`src/Scramble.UI/Views/**` and is multi-targeted onto the mobile head.
- **Escape:** `Mobile-Shell-Exempt: <reason>` trailer (genuinely
  platform-specific chrome that cannot be authored in the shared UI).

**Why:** ANALYSIS.md STEP 5b — the 2026-05-11 Avalonia-on-Android pivot
consolidated two UI implementations into one. Drift back into legacy
(observable in the git log via periodic maintainer reverts) or view content
leaking into the mobile shell would re-fragment the codebase.

### I2 — Service / protocol changes require integration coverage

Any change under these paths triggers the required integration suite:

- `src/Scramble.Core/Services/**`
- `src/Scramble.Presentation/**`
- `lib/marmot-cs/**`, `lib/dotnet-mls/**`
- `tests/Scramble.Diagnostics/**`
- `tests/Scramble.Core.Tests/**`

- **Gate:** `.github/workflows/integration.yml` runs the whitelist union of
  `Category=Integration|Relay|MIP-Compliance|ProtocolCompliance|FullE2E|
  EpochSync|DeviceSync|OutboxModel|Notifications|RelayHarness|
  ExporterSecret|Native` on Ubuntu with `docker-compose.test.yml` up.
- **Escape hatch:** none. If a new subsystem needs a new category, add it
  to both `integration.yml` and `docs/ci-setup.md`.
- **Why:** ANALYSIS.md STEP 6 — pre-existing `dotnet-desktop.yml` explicitly
  filtered `Category!=Relay&Category!=Integration`, so the entire
  `Scramble.Diagnostics` project (MIP-00..04 interop, cross-MDK, DeviceSync,
  Outbox) was never a merge gate. Device sync was silently broken for
  ~7 weeks.

### I3 — Every `fix:` commit on a service must add a regression test

Any `fix:` / `fix(...)` commit whose diff touches a file under
`src/Scramble.Core/Services/**` **must** add or modify at least one test
under `tests/Scramble.Core.Tests/`, `tests/Scramble.UI.Tests/`, or
`tests/Scramble.Diagnostics/` in the same commit.

- **Gate:** enforced by review + PR template checklist. No script gate yet —
  proposed if violations recur.
- **Escape hatch:** `Test-Debt: <reason>` commit trailer. Every such
  trailer is a follow-up ticket.
- **Why:** `MessageService.cs` had 19 fix commits before its first unit
  test landed 11 days after the fix cluster ended. Tests must lead fixes,
  not trail them.

### I4 — No flag-day rewrites

A single commit **must not** combine a refactor with a feature/fix and
touch more than 8 files across more than one subsystem (Services,
ViewModels, Views, Mobile.Android shell). If a change is that big, land the
refactor first as a no-op (verified by the full integration suite), then
the feature.

- **Gate:** review, plus a warning in `scripts/check-drift.ps1` when a
  future extension detects a diff shape exceeding this threshold.
- **Escape hatch:** `Landing-Discipline-Exempt: <reason>` trailer.
- **Why:** commit `e05ff875` (34 files, +1323/-200) appears in **six** of
  the twelve regression clusters in ANALYSIS.md. Every neighbour it
  touched had to be re-fixed later.

### I5 — Pivot freeze

When migrating a UI head (framework swap, target addition, ReactiveUI
major-version bump), the **other** UI head enters *bugfix-only* mode
until the pivot's new head has an equivalent smoke test green in CI, plus
one week of stabilisation.

- **Gate:** review + an explicit banner in the pivot's tracking issue.
- **Escape hatch:** `Pivot-Exempt: <reason>` trailer on any feature
  landing during the freeze.
- **Why:** the 2026-05-11 Avalonia-on-Android pivot produced W21+W22 with
  a **4.5×** fix:feature ratio — the worst two-week stretch in the repo.
  8 fixes were named "Avalonia-Android friction"; the legacy head kept
  receiving features throughout, compounding the backlog.

---

## HIGH-RISK FILES — treat as load-bearing

The files below have the highest historical fix-density (ANALYSIS.md
STEP 4). Changes here should be small, tested, and reviewed with more care
than average.

| File | Fix-density | Why it's fragile |
|---|---|---|
| `src/Scramble.Core/Services/ExternalSignerService.cs` | **0.59** | Signer lifecycle state leaks into `LoginViewModel` and `MainViewModel` — every change reverberates. |
| `src/Scramble.Core/Services/ManagedMlsService.cs` | 0.44 | Origin of the MIP-03 decryption cluster (15 fixes / 11d). Cross-MDK interop is a minefield. |
| `src/Scramble.Core/Services/NostrService.cs` | 0.52 | Gift-wrap buffering, h-tag routing, epoch-based subscription. Racy. |
| `src/Scramble.Core/Services/MessageService.cs` | 0.34 | Rumor/reaction/reply id tracking. First unit tests landed *after* the cluster. |
| `src/Scramble.Presentation/ViewModels/LoginViewModel.cs` | 0.62 | Signer connect/reconnect handshake state. |
| `src/Scramble.Presentation/ViewModels/MainViewModel.cs` | 0.42 | Signer-restore + cache-first metadata coordination. |
| `src/Scramble.UI/Controls/MessageBubble.axaml.cs` | (2026-05 cluster) | Multi-target Avalonia/Android name collisions live here. |
| `src/Scramble.Mobile.Android/MainActivity.cs` | (IME-inset cluster) | Recent fix cluster around keyboard resize and background survival. |

---

## Session discipline

- **Small commits.** If your diff is > 8 files, split the refactor from
  the feature. See I4.
- **Test-first on services.** If you're touching a file listed under
  "High-risk files", write or update a `Category=Integration` /
  `Category=MIP-Compliance` test in the same commit — CI will require it
  anyway (I2), so writing it first shortens the debug loop.
- **UI code goes in `src/Scramble.UI`.** The mobile head picks it up via
  multi-targeting. Only genuinely platform-specific chrome (activity,
  permissions, insets, native services) belongs in
  `src/Scramble.Mobile.Android/`. See I1-M.
- **Don't defer regression tests.** If you're writing a `fix:`, the test
  that proves the fix is part of the fix, not a follow-up. See I3.

### Dark Matter cutover rules (decided 2026-08-09)

Provenance: `ai-tasks/protocol-agnostic-report-2026-08.md` — the engine stays
Marmot-only; protocol agnosticism (Concord / NIP-29, Armada-style) lives at the
app-layer conversation seam, deferred until after the migration ships.

- **No `Scramble.Marmot` types in `Scramble.Presentation`.** ViewModels bind
  only to protocol-neutral models (`Chat`, `Message`, `Member`, `Role`, …)
  surfaced by `Scramble.Core` services; chat records carry a protocol
  discriminator. Engine types (`SendIntent`, `IngestOutcome`, `GroupEvent`,
  epoch/commit state) stop at the service layer.
- **Generic Nostr crypto is not Marmot-namespaced.** When porting codecs into
  the new engine, `Nip44Encryption`, `GiftWrap`, and other generic Nostr
  primitives go in a shared namespace (e.g. `Scramble.Marmot.Wire.Nostr` →
  keep the generic pieces under a `…Nostr.Crypto`-style namespace with no
  Marmot semantics), so a future non-Marmot provider can reuse them without
  referencing the engine.
- **Do not build** `IConversationProvider`, Concord, or NIP-29 code during the
  migration. Interface extraction happens after a second concrete provider
  exists.

## Reproducing CI locally

```powershell
# Unit tests (fast)
dotnet test Scramble.Desktop.slnf --filter "Category!=Relay&Category!=Integration" -p:DesktopOnly=true

# Integration tests (needs Docker)
docker compose -f docker-compose.test.yml up -d nostr-relay
dotnet test tests/Scramble.Diagnostics/ --filter "Category=Integration|Category=MIP-Compliance|Category=ProtocolCompliance|Category=FullE2E|Category=EpochSync|Category=DeviceSync|Category=OutboxModel|Category=Notifications|Category=RelayHarness|Category=ExporterSecret"

# Drift check
./scripts/check-drift.ps1
```

## Documentation index

- `ANALYSIS.md` — full post-mortem of Feb–May 2026 development;
  provenance for every invariant above.
- `docs/ci-setup.md` — branch-protection configuration for the required
  status checks.
- `AGENTS.md` — agent-specific notes (unchanged).
- `src/Scramble.Android/OBSOLETE.md` — legacy-head deprecation notice.
- `ai-tasks/` — per-feature planning docs. Completed ones under
  `ai-tasks/completed/`.
