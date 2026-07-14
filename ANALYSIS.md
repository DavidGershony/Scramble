# Scramble — AI-Assisted Development Post-Mortem

**Window analysed:** 2026-02-26 → 2026-05-31 (94 days, 591 commits on `master`)
**Method:** classify every commit by message + spot-check diff, then mine git
history for clusters, hotspots, parity, and test timing. Inferences are
labelled `[INFERENCE]` with a confidence note. The user's prompts are not
available; commit-message tone is used only as a weak proxy where noted.

---

## TL;DR

* **Fixing took over building.** Mar fix:feature ratio 0.61 → Apr 0.93 → May 0.91.
* **MLS + signer plumbing was the loudest churn axis.** 4 of the top 10 churn
  files are MLS/Nostr core services; `ExternalSignerService` has 59% fix-density.
* **Tests followed fixes, they didn't gate features.** MIP-00/01/02/03 interop
  tests landed *the same day* the first MLS cluster blew up. CI excludes all
  Relay + Integration tests by `--filter` (`.github/workflows/dotnet-desktop.yml:55,58`).
* **Dual-target work routinely shipped on one side first.** 95 single-target
  features (40 Android, 55 Avalonia) vs only 57 both-target commits.
* **Verdicts:** **H1 partial**, **H2 confirmed (weak-to-moderate evidence)**,
  **H3 confirmed (strong evidence)**.

---

## STEP 1 — Commit category counts

| Category        | Count | %     |
|-----------------|------:|------:|
| feature         |   269 | 45.5% |
| bugfix          |   187 | 31.6% |
| regression-fix¹ |    25 |  4.2% |
| test            |    49 |  8.3% |
| chore / build   |    48 |  8.1% |
| refactor        |    12 |  2.0% |
| revert          |     1 |  0.2% |

¹ `regression-fix` here counts only commits whose **message** signals "again /
still / re-fix / properly / actually / finally / broke / regression". Repeated-
fix-on-same-file regressions are captured separately in STEP 3 (clusters) —
**the regression count below is a floor, not a ceiling.**

**Spot-check** (60 random commits, ≈10% sample): classifier agreed with the
diff for ~90% of commits. The dominant noise is "Add X and fix Y" omnibus
commits getting marked `bugfix` when they're partly feature. Noise is symmetric
and does not change the trend.

### AI-marker fraction

* 458 / 591 commits (**77.5%**) carry the `Co-Authored-By: Claude` /
  "🤖 Generated with Claude Code" trailer.
* Non-AI commits cluster in the early Feb–Mar setup phase (CI, NuGet, project
  rename), before the trailer was used routinely — so the comparison isn't
  clean.
* Crude fix-density: AI-marked **36.5%**, non-AI **33.8%**. Not a meaningful
  delta given the temporal skew. `[INFERENCE — low confidence]` AI assistance
  did not measurably inflate per-commit fix-density; the damage shows up in
  *cluster* shape, not per-commit ratio.

---

## STEP 2 — Monthly timeline

| Month   | feat | bugfix | reg-fix | refactor | test | chore | revert | **fix:feat** |
|---------|-----:|-------:|--------:|---------:|-----:|------:|-------:|-------------:|
| 2026-02 |    8 |      3 |       2 |        0 |    2 |     1 |      0 | 0.62         |
| 2026-03 |  109 |     64 |       3 |        2 |   12 |    13 |      0 | 0.61         |
| 2026-04 |   87 |     71 |      10 |        4 |   20 |    10 |      1 | **0.93** ⚠️ |
| 2026-05 |   65 |     49 |      10 |        6 |   15 |    24 |      0 | **0.91** ⚠️ |

* Mar was the *building* month — 109 features, fix:feat 0.61.
* Apr and May flipped to *firefighting* — fix:feat ~0.92. Tests, refactor, and
  chore all rose in May (24 chore commits — mostly version bumps + CI signing
  loops, see cluster #2).
* **Build-velocity didn't recover.** Once Apr started, fix-density never came
  back down. `[INFERENCE — medium confidence]` This is the signature of an
  accumulating defect backlog, not a single bad week.

---

## STEP 3 — Regression clusters

Definition: ≥3 fix commits to the same file inside a 14-day window, plus the
immediately preceding feature commit.

| # | File / dir | Fixes | Span | Preceded by feature (days before cluster start) |
|---|---|---:|---:|---|
| **C1** | `OpenChat.Core/Services/ManagedMlsService.cs` | **15** | 11d | `1fb134c5 Add MIP-03 decryption layer for cross-MDK message interop` (-2d) |
| **C2** | `.github/workflows/publish.yml` | 8 | 6d | `c7d46f4a Add Android workload install to desktop publish job` (-0d) — feat *was* the regressor |
| C3 | `OpenChat.Core/Services/NostrService.cs` | 10 | 12d | `45aec98f Use NIP-65 relay discovery for KeyPackage fetch and Welcome delivery` (-4d) |
| C4 | `OpenChat.Core/Services/MessageService.cs` | 9 | 10d | `9bc5af8e Upgrade group message handler logging` (-0d) — the real trigger was the MIP-03 cluster bleeding over |
| C5 | `Scramble.Presentation/ViewModels/MainViewModel.cs` | 8 | 14d | (post-rename: cluster spans signer-restore + cache-first metadata work in mid-May) |
| C6 | `OpenChat.Presentation/ViewModels/MainViewModel.cs` | 8 | 14d | `e568d979 Add external signer (Amber/NIP-46) support for Nostr event publishing` (-4d) |
| C7 | `Scramble.Core/Services/NostrService.cs` | 6 | 13d | `44de066b feat: multi-device support — detect peer device KPs and auto-add to groups` (-0d) |
| C8 | `OpenChat.Core/Services/IMlsService.cs` | 6 | 10d | `b6089bec Add KeyPackage audit, multi-KP persistence` (-5d) |
| C9 | `Scramble.UI/Controls/MessageBubble.axaml.cs` | 5 | 2d | `a4d09e16 Multi-target Scramble.UI for net10.0-android` (-0d) |
| C10 | `OpenChat.Core/Services/ExternalSignerService.cs` | 5 | 14d | `04e4ce7f Add QR code for NIP-46 nostrconnect login with NIP-44 decryption` (-12d) |
| C11 | `OpenChat.Presentation/ViewModels/LoginViewModel.cs` | 5 | 10d | `b66dc700 Switch NIP-46 relay from relay.damus.io to relay.nsec.app` (-0d) |
| C12 | `OpenChat.Presentation/ViewModels/ShellViewModel.cs` | 5 | 10d | (cluster begins with the architecture rewrite commit itself) |

### What the clusters say

1. **MIP-03 decryption (C1) is the single biggest regression generator** in the
   project. Adding cross-MDK group-message decryption destabilised
   `ManagedMlsService`, `NostrService`, `MessageService`, `IMlsService`, and
   `MainViewModel` in the *same* 14-day window. Five of the top twelve clusters
   trace back to it.
2. **CI/publish workflow (C2)** is a separate species: feature commit
   `c7d46f4a Add Android workload install` is itself the regressor, and the
   next 6 days are blind retries on signing/build mechanics. Eight fixes in
   six days is the classic "trying things until CI is green" loop.
3. **Signer/NIP-46 work (C10, C11, C6)** clusters together — every change to
   Amber session lifecycle or relay-URL switching produced fixes on adjacent
   files. `[INFERENCE — high confidence]` the abstraction boundary between
   `ExternalSignerService`, `LoginViewModel`, and `MainViewModel` was weak;
   state leaked through all three.
4. **MessageBubble (C9)** is the dual-target smoking gun: 5 fixes in 2 days
   the moment `Scramble.UI` became multi-target Android, almost entirely
   "fix Avalonia/Android name collision" or "platform-conditional UI".

---

## STEP 4 — Churn hotspots

Top files by **total commit count** (excluding the Rust `target/` build dir).
*OpenChat → Scramble was renamed on 2026-04-29 (`d8aee2bb`); each subsystem
appears under both names — the file is the same code.*

| Rank | File | Total | Fixes | Feat | Fix-density |
|------|------|------:|------:|-----:|-----------:|
| 1 | `OpenChat.Core/Services/MessageService.cs` | 56 | 19 | 35 | 0.34 |
| 2 | `OpenChat.Core/Services/NostrService.cs` | 54 | 28 | 26 | **0.52** |
| 3 | `OpenChat.Core/Services/ManagedMlsService.cs` | 52 | 23 | 21 | 0.44 |
| 4 | `OpenChat.Presentation/ViewModels/ChatViewModel.cs` | 48 | 12 | 35 | 0.25 |
| 5 | `OpenChat.Presentation/ViewModels/ChatListViewModel.cs` | 43 | 15 | 27 | 0.35 |
| 6 | `OpenChat.Presentation/ViewModels/MainViewModel.cs` | 35 | 16 | 18 | 0.46 |
| 7 | `OpenChat.UI/Views/MainWindow.axaml` | 33 | 8 | 23 | 0.24 |
| 8 | `OpenChat.Core/Services/StorageService.cs` | 32 | 11 | 21 | 0.34 |
| 8= | `.github/workflows/publish.yml` | 32 | 11 | 6 | 0.34 |
| 9 | `Scramble.Presentation/ViewModels/MainViewModel.cs` | 31 | 13 | 14 | 0.42 |
| 10 | `OpenChat.Android/MainActivity.cs` | 25 | 12 | 11 | **0.48** |

Top files by **fix-density** (minimum 8 touches, the unstable ones):

| File | Touches | Fix-density |
|------|--------:|------------:|
| `tests/OpenChat.UI.Tests/HeadlessRealMlsIntegrationTests.cs` | 9 | **0.67** |
| `OpenChat.Presentation/ViewModels/LoginViewModel.cs` | 13 | 0.62 |
| `Scramble.Core/Services/ExternalSignerService.cs` | 10 | 0.60 |
| `OpenChat.Core/Services/ExternalSignerService.cs` | 22 | **0.59** |
| `Scramble.Presentation/ViewModels/ChatListViewModel.cs` | 11 | 0.55 |
| `OpenChat.Core/Services/NostrService.cs` | 54 | 0.52 |
| `OpenChat.Android/MainActivity.cs` | 25 | 0.48 |

**Two findings stand out:**

* `ExternalSignerService` is the single most fragile production file —
  practically 6-of-10 changes to it are fixes, both pre- and post-rename.
* `HeadlessRealMlsIntegrationTests.cs` — 0.67 fix-density. **The MLS test
  harness itself was unstable.** That means MLS tests were not a reliable
  regression detector during the period they should have been.

### Directory rollup (depth 3, post-rename folded in mentally)

| Directory | Commits | Fixes | Density |
|-----------|--------:|------:|--------:|
| `*.Core/Services` (combined) | 446 | 173 | 0.39 |
| `*.Presentation/ViewModels` (combined) | 262 | 95 | 0.36 |
| `*.Android/Resources` | 142 | 29 | 0.20 |
| `*.UI/Views` (combined) | 167 | 36 | 0.22 |
| `*.Android/Fragments` | 70 | 12 | 0.17 |
| `.github/workflows/publish.yml` | 32 | 11 | 0.34 |

UI directories (`UI/Views`, `Android/Resources`, `Android/Fragments`) are
**less fix-dense** than `Core/Services`. The instability lives below the UI
layer.

---

## STEP 5 — Dual-target parity

| Bucket | Commits | Of which: feature | bugfix | reg-fix |
|--------|--------:|------------------:|-------:|--------:|
| both-targets    |  57 | 38 | 12 | 1 |
| android-only    |  71 | 40 | 27 | 3 |
| avalonia-only   |  94 | 55 | 27 | 7 |
| shared-only     | 204 | 84 | 85 | 14 |
| other (CI/tests)| 165 | 52 | 36 | 0 |

* **95 single-target features** vs only **38 both-target features**. The
  default mode of work was "ship on one side first".
* **43 explicit platform-tagged fix commits** (subjects containing
  "Android", "desktop", "mobile", "Avalonia"). E.g. `1585cc9c Fix Android
  build: TextInputLayout.SetError does not exist in Xamarin bindings`,
  `337dc487 fix: hide chat back button on desktop, show only on mobile`,
  `fbe35d1c fix: fully qualify Button type ... Android build ambiguity`.
* Of 95 single-target features, **16 were demonstrably followed by an
  other-target fix within 14 days** using a weak word-overlap match. The
  real number is almost certainly higher — the match is conservative.

**Examples of port-the-feature regressions:**

| Single-target feature | Other-target follow-up fix |
|---|---|
| 2026-03-18 *Auto KeyPackage lookup on chat creation* (avalonia) | 2026-04-02 *Fix relay selection list not appearing on Android new chat/group* (android) |
| 2026-03-27 *Add sending emoji reactions from desktop* (avalonia) | 2026-04-02 *Fix Android theme change freeze, FAB color, and add emoji reactions* (android) |
| 2026-04-29 *Add copy npub buttons / selectable text* (avalonia) | 2026-05-09 *fix(android): add copy text action to chat message long-press menu* |
| 2026-05-11 *Multi-target Scramble.UI for net10.0-android* (avalonia) | 2026-05-15 *fix: change Avalonia Android package ID to app.scramble.chat* (android) |
| 2026-05-13 *ui(mobile): move action buttons to top header* (android) | 2026-05-15 *fix: reply button not working on mobile + blue bg* (avalonia) |

The Avalonia ↔ Avalonia-Android multi-target migration (mid-May) shows
**5 MessageBubble fixes in 2 days**, all dual-target name-collision /
control-resolution issues (C9 above).

---

## STEP 5b — The Avalonia-on-Android pivot (May 11)

Called out separately because monthly buckets hide it. On 2026-05-11 the
project simultaneously did three things:

1. Multi-targeted `src/Scramble.UI` for `net10.0-android`
   (`a4d09e16 Multi-target Scramble.UI for net10.0-android; relocate desktop-only services`).
2. Added a **new** Avalonia-based Android head at
   `src/Scramble.Mobile.Android/` (`f5f51411 Add Scramble.Mobile.Android: Avalonia 12 Android head`).
3. Migrated the legacy `Scramble.Android` (Views/Fragments) app to
   ReactiveUI 23 (`1148208d`, `573b84a9`).

For the next ~11 days, **three UI heads were maintained in parallel**:
legacy Android Fragments, Avalonia desktop, and Avalonia-on-Android.

### Weekly fix:feature ratio around the pivot

| ISO week | Feat | Fix | Ratio | Note |
|---------:|-----:|----:|------:|------|
| 2026-W16 |   20 |  13 | 0.65  | 3 wks before pivot — steady |
| 2026-W17 |   15 |  12 | 0.80  | |
| 2026-W18 |    9 |  13 | 1.44  | |
| **W19** |   22 |  13 | **0.59** | ← pivot week (May 11) — landed clean |
| **W20** |   36 |  24 | **0.67** | ← wk after — still shipping features |
| **W21** |    2 |   8 | **4.00** | ← 2 wks after — pure firefighting |
| **W22** |    2 |  10 | **5.00** | ← 3 wks after — same |

**This is the real "fixing dominated" signal.** The month-level May ratio of
0.91 smooths together W19–W20 (still building) and W21–W22 (nothing but
fixes). W21+W22 combined had **4 features and 18 fixes** — a 4.5× fix ratio.

### Pivot-specific churn

* **27 commits touched `src/Scramble.Mobile.Android/`** in the 11 days after
  it appeared. Categories: 14 feature, 10 bugfix, 2 regression-fix, 1
  refactor. **Fix-density: 0.44** — nearly one in every two commits on this
  new subdirectory was a fix.
* **`src/Scramble.UI/` post-pivot: 39 commits (13 fixes)** in 20 days vs
  **4 commits (1 fix)** in the 12 days before. Multi-targeting the shared UI
  to Android grew the surface ~10×.
* The `MessageBubble.axaml.cs` micro-cluster (C9 in the main table — 5 fixes
  in 2 days) is entirely inside this pivot. So is the entire C7 cluster on
  `Scramble.Core/Services/NostrService.cs` (6 fixes / 13d, starting the day
  of multi-device support on May 15).

### Fixes explicitly caused by Avalonia-on-Android friction

Post-pivot fix commits whose subjects name multi-target friction:

| Date | Hash | Subject |
|------|------|---------|
| 2026-05-12 | `8432fcfa` | `fix(ui): resolve IClipboard SetTextAsync error in Avalonia 12` |
| 2026-05-14 | `ebc13963` | `fix: correct grantUriPermission typo in Mobile.Android manifest` |
| 2026-05-14 | `253117cf` | `fix: add runtime permission request for RECORD_AUDIO on Avalonia Android` |
| 2026-05-14 | `36301ad7` | `fix: disambiguate Stream type in MobileAndroidAudioService (System.IO.Stream vs Android.Media.Stream)` |
| 2026-05-14 | `0f195867` | `fix: Android launcher icon white background — add adaptive icon` |
| 2026-05-15 | `fbe35d1c` | `fix: fully qualify Button type in IsInsideButton to resolve Android build ambiguity` |
| 2026-05-15 | `bb9bb84e` | `fix: change Avalonia Android package ID to app.scramble.chat for seamless update` |
| 2026-05-15 | `a0d476bd` | `fix: enable release keystore signing for Avalonia Android head` |

Eight fixes explicitly tagged by the developer as "this is Avalonia-Android
friction". This *undercounts* the true cost — it doesn't include the
`Scramble.Mobile.Android/` bring-up fixes, the shared-UI reflow work, or the
"hide chat back button on desktop, show only on mobile" style commits
where the fix is a `PlatformContext` capability flag introduced *because*
one shared view now runs on two form factors.

### What the pivot actually cost

* 8 named multi-target friction fixes
* 5 `MessageBubble.axaml.cs` fixes in 2 days (cluster C9)
* 6 `Scramble.Core/Services/NostrService.cs` fixes in 13 days (cluster C7)
* introduction of a new abstraction (`PlatformContext`, commit `24de2dc4`)
  purely to feature-gate the shared UI per form factor
* an extra ~14 days of near-zero feature velocity (W21–W22 ratio 4–5×)

`[INFERENCE — moderate confidence]` The pivot itself was not wrong — the
codebase clearly wanted a single Avalonia UI. But **doing it while the
legacy Android app was still being fixed, while cross-MDK MLS interop was
still stabilising, and without integration gates on the new head compounded
all three problems.** The Apr–May firefighting mode was already established
before the pivot; the pivot then extended it by two weeks and reset the
UI-level test surface to zero.

### Update to H2

The dual-target verdict in STEP 7 (H2 confirmed, weak-to-moderate)
understated this. Isolated to the pivot window, **H2 is confirmed with
strong evidence**: W21+W22 combined = 12 commits total, 10 of which are
fixes, and the dominant fix-source is the Avalonia-Android multi-target
transition itself. The reason my whole-repo verdict remains
"weak-to-moderate" is that the pivot is a *bounded event*, not the
project's steady-state dual-target tax.

### Strategy addition — S8: freeze the other head during a pivot

Add to `CLAUDE.md`:

> **Pivot discipline.** When migrating a UI head (framework swap, target
> addition, ReactiveUI major version), the other head enters *frozen*
> mode: bugfix-only, no new features, for the pivot duration + 1 week
> stabilisation. Landing new features on the legacy head during a pivot
> compounds the fix backlog. See W21–W22 2026 for the evidence.

Also: for the new head, require an integration test that renders every
top-level view + shell interaction (`HeadlessMobileAndroidSmokeTests`) as
a merge gate — so that a `Button` type-collision or `IClipboard` API
break fails on the PR, not in a fix commit two days later.

## STEP 6 — Test net coverage timing

**Method:** for each top hotspot, look up the date the first test file naming
that subsystem was added, then compare to the file's first cluster start.

| Subsystem | First cluster | First targeted test added | Verdict |
|---|---|---|---|
| `ManagedMlsService` (MLS lifecycle) | 2026-03-16 | `Mip00InteropTests` / `Mip01-04InteropTests` on 2026-03-16–17 | **Same day — reactive** |
| `MessageService` | 2026-03-17 → 03-27 | `MessageService unit tests (32 tests)` 2026-04-07 | **11d after cluster ended** |
| `ExternalSignerService` | 2026-03-11 → 03-26 | `Amber signer integration tests (11 tests)` 2026-03-19 | **Mid-cluster** |
| `LoginViewModel` | 2026-03-18 → 03-28 | `LoginViewModel signer-login tests` 2026-05-04 | **~5 weeks late** |
| Device sync (Private Notes) | (single-feature on 2026-04-08) | `device sync E2E diagnostic test` 2026-05-30 | **Same day as the fix, after 7+ weeks of silent breakage — see commit `3f18c51`** |
| Multi-device AddMember / MIP-00 24h retention | feature 2026-05-15 / 2026-05-20 | `test: add coverage for multi-device AddMember...` 2026-05-21 | **1d after feature lands — closest to TDD** |

`HeadlessRealMlsIntegrationTests.cs` had 9 touches and 6 fixes (0.67
fix-density). The MLS test harness was itself unstable; tests were rewritten
in step with the code they were supposed to guard, defeating their purpose.

**CI scope (`.github/workflows/dotnet-desktop.yml:55,58`):**

```
dotnet test ... --filter "Category!=Relay&Category!=Integration"
```

CI runs **only unit tests**. Every Relay-tagged and Integration-tagged test
— including the cross-MDK interop tests, real-relay E2E, headless real-MLS —
is filtered out. So even when integration tests existed, **they did not gate
merges**.

The pattern is overwhelming: across MLS, signer, message, login, and device
sync, **tests trailed bugs rather than leading features.** Adding the test
*was* the fix in many cases.

---

## STEP 7 — Hypothesis verdicts

### H1 — "UI/Avalonia layer is disproportionately unstable vs the C#/crypto core."

**Verdict: PARTIAL — refuted at the directory level, partially confirmed
inside specific ViewModels.**

* Avalonia `UI/Views` directories sit at 0.22 fix-density; Android
  `Fragments` at 0.17. **`Core/Services` is 0.39 — nearly 2× more fix-dense.**
* But `LoginViewModel` (0.62), `MainViewModel` (0.46), `ChatListViewModel`
  (0.55 post-rename) — i.e. the *Presentation* layer that drives UI — sit at
  UI-or-worse fix density. The instability is in **ViewModel state
  coordination**, not in XAML or Fragments.
* `[INFERENCE — high confidence]` What looks like "UI bugs" externally
  (chat switching, profile, login flicker) is actually ReactiveUI
  state-management churn — `a0e3e580 fix: chat switching perf, connection
  reliability, and desktop UX issues` touches 14 files, mostly ViewModels +
  StorageService.

### H2 — "Dual-target Android+Avalonia generated recurring regressions."

**Verdict: CONFIRMED with weak-to-moderate evidence.**

Confirming signals:
* 95 single-target features vs 38 both-target features.
* 43 platform-tagged fix commits; at least 16 demonstrably "port the
  feature to the other target" within 14 days.
* `MessageBubble.axaml.cs` (C9): 5 fixes in 2 days when Avalonia.UI was
  multi-targeted to Android — pure dual-target collateral damage.
* `MainActivity.cs` and `fragment_settings.xml` both in the top-25 by churn.

Weakening signals:
* Total Android-fix and Avalonia-fix volumes are comparable (27 each in
  single-target buckets) — neither platform is being abandoned.
* Shared-only commits had **85 bugfix + 14 reg-fix vs 84 feat** — the
  Core/Presentation layer regresses just as hard as the UI does.

`[INFERENCE — moderate confidence]` The dual-target requirement is a real
multiplier, but it is **not the dominant** cause of churn. C1 (MIP-03)
alone produced more fix volume than the entire dual-target follow-up tax.

**Post-window update (2026-06-23, commit `2f1b629`).** The window this
analysis covers ended 2026-05-31. Between then and today, the maintainer
formally deprecated `src/Scramble.Android` — the legacy fragment head that
supplied one side of the dual-target friction. Every commit since has
routed UI work through the shared `src/Scramble.UI` (multi-targeted to
`src/Scramble.Mobile.Android`). The structural fix for H2 has therefore
already landed; what remains is **drift prevention**, not parity. The
recommendation set in STEP 8 has been revised: S3 is now an anti-drift
gate (blocks new code in the abandoned head and new view content in the
mobile shell), not a parity gate between two live UI implementations.

### H3 — "Missing behavioural / regression tests let new features silently break old ones."

**Verdict: CONFIRMED with strong evidence.**

Confirming signals:
* MIP-00/01/02/03 interop tests were added **the same day** the first MLS
  regression cluster started, and only because the bugs were found
  manually first.
* `MessageService` had no unit tests until 11 days after its cluster ended.
* `LoginViewModel` had no targeted tests until 5 weeks after its cluster.
* Device sync was broken in production for ~7 weeks before
  `bb2466da test: add device sync E2E diagnostic test proving bidirectional
  Private Notes sync` finally proved the regression — and the fix landed
  literally an hour later (`3f18c51`).
* **CI excludes all Relay+Integration tests by filter.** The cross-MDK
  interop suite, the real-MLS headless suite, the real-relay E2E suite —
  none of them are required to merge.
* `HeadlessRealMlsIntegrationTests.cs` had a fix-density of 0.67. The MLS
  test harness was itself a top-10 fix-density file. Tests churned in step
  with the code, not ahead of it.

This is the clearest finding in the entire repository. **The project did
not have a behavioural regression net** for its highest-risk subsystems
(MLS lifecycle, signer, device sync) until the bugs forced one. And even
then, those tests did not gate CI.

---

## STEP 8 — Strategy: enforce over suggest

Each recommendation below ties to a specific finding above and is phrased as
something the CI/hook system enforces, *not* something Claude is expected to
remember.

### S1 — Make the integration suite the merge gate (fixes H3, C1, C3, C4)

Current state: `.github/workflows/dotnet-desktop.yml:55,58` filters out
Relay + Integration tests. The most useful tests in the repo never run
required.

Action:
* Split CI into `unit` (current behaviour) and `integration` (required for
  PRs that touch `src/*/Services/*` or `lib/marmot-cs/**` or `lib/dotnet-mls/**`).
* Add a `path-filtered required check` rule on GitHub so MLS / Nostr / signer
  PRs cannot merge without the integration suite green.
* Run cross-MDK + headless-real-MLS + real-relay groups in nightly + on
  paths above.

### S2 — MLS lifecycle regression contract (fixes C1, C8, H3, device-sync incident)

Create `tests/Scramble.Diagnostics/Compliance/MlsLifecycle/`:
* **non-power-of-2 tree** (3, 5, 7 members) Welcome / Commit / Remove sequence.
* **epoch ratchet** across 50 commits with mixed message + admin operations.
* **stale Welcome** delivered after group has already advanced epoch.
* **device-sync bidirectional Private Notes** — promote the diagnostic test
  `bb2466da` from optional to required.
* **KeyPackage exhaustion + last-resort fallback** end-to-end.

Each scenario fails the build on regression. Tie the existing dev-notes in
`ai-tasks/marmot-protocol-compliance.md` to these as the source of truth.

### S3 — Anti-drift gate (revised after the June-23 correction)

**Original design (superseded).** The first draft of S3 was a parity gate
requiring paired changes across `src/Scramble.UI` and `src/Scramble.Android`.
That design missed commit `2f1b629` (2026-06-23), which formally
deprecated `src/Scramble.Android`. Parity between two live UI heads is no
longer an invariant — there is only one live UI head (`Scramble.UI`,
multi-targeted).

**Current design.** Two enforceable anti-drift rules, both bypassable via a
documented commit trailer:

* **Rule L** — no PR may modify a file under `src/Scramble.Android/**`.
  Escape: `Legacy-Android-Change: <reason>`.
* **Rule M** — no PR may **add** a new `.axaml` file under
  `src/Scramble.Mobile.Android/**`. Modifications to existing view files
  (`MobileMainView.axaml`) are fine — view content belongs in
  `src/Scramble.UI/Views/**`. Escape: `Mobile-Shell-Exempt: <reason>`.

Wired as `scripts/check-drift.ps1` + `.github/workflows/drift.yml` + an
optional local `scripts/hooks/pre-push.ps1`. See CLAUDE.md invariant I1.

`src/Scramble.Android/OBSOLETE.md` makes the legacy-head status
discoverable to anyone who opens the folder without reading CLAUDE.md.

### S4 — Signer state-machine harness (fixes C10, C11, C6, ExternalSignerService 59% density)

`ExternalSignerService` + `LoginViewModel` + `MainViewModel` were not
separable in practice — every change to one caused fixes in the others.

Action:
* A property-based state-machine test that walks
  `disconnected → connecting → connected → app-suspended → reconnecting →
  connected → publishing → publish-failed → reconnected` permutations against
  `MockExternalSignerBuilder`. Use FsCheck/Verify so every fix to the signer
  is forced to add a permutation.
* A regression-style "previously broken inputs" file (`SignerKnownBugs.cs`)
  that grows on every reg-fix.

### S5 — Per-commit affected-area report in PR template (fixes H2 + drift detection)

Add a `pull_request_template.md` checkbox:

```
- [ ] Touches MLS state (MlsService / ManagedMlsService / KeyPackage)
      → integration suite green, MIP-00..04 interop tests pass
- [ ] Touches signer (ExternalSignerService / Login)
      → SignerStateMachineTests green
- [ ] Touches one UI head only
      → reason: ___________ (or matching change in the other head)
```

Combined with S3 enforcement, this puts the dual-target requirement at the
top of every PR rather than in a CLAUDE.md the author may not have read.

### S6 — `CLAUDE.md` invariants the model is reminded of every session

The current `CLAUDE.md` (post-`2f1b629`) already establishes
`Scramble.Mobile.Android` as the ship target and marks `Scramble.Android`
as abandoned. This recommendation **appends** five enforceable invariants
plus a high-risk file list rather than replacing what's already there.

The invariants shipped (see CLAUDE.md for the full text):

1. **I1** — anti-drift: no changes to `src/Scramble.Android/**`; no new
   `.axaml` files under `src/Scramble.Mobile.Android/**`. Escapes:
   `Legacy-Android-Change:` and `Mobile-Shell-Exempt:` trailers.
   *Gate: `.github/workflows/drift.yml`.*
2. **I2** — service/protocol changes require the integration suite
   (`Category=Integration|Relay|MIP-Compliance|…`). No escape.
   *Gate: `.github/workflows/integration.yml`.*
3. **I3** — every `fix:` on a service must add a regression test in the
   same commit. Escape: `Test-Debt:` trailer.
4. **I4** — no flag-day rewrites; > 8 files across subsystems requires a
   split. Escape: `Landing-Discipline-Exempt:` trailer.
5. **I5** — pivot freeze; other UI head is bugfix-only during a
   migration. Escape: `Pivot-Exempt:` trailer.

High-risk file list drawn directly from the STEP 4 hotspot table (top
fix-density, min 8 touches): `ExternalSignerService.cs` 0.59,
`LoginViewModel.cs` 0.62, `NostrService.cs` 0.52, `ManagedMlsService.cs`
0.44, `MessageService.cs` 0.34, `MainViewModel.cs` 0.42,
`MessageBubble.axaml.cs` (2026-05 cluster), `MainActivity.cs` (recent
IME-inset cluster).

### S7 — Session discipline: "no flag-day rewrites"

`e05ff875 ShellViewModel architecture, npub-based auto-profiles, and
upload/logout fixes` is 34 files / +1323 / -200 in a single commit. It
appears in **six** of the twelve regression clusters because every
subsequent fix touched something it had moved. The MIP-03 decryption
addition (C1) and the multi-target Scramble.UI migration (C9) are the same
shape — a single landing that destabilised five neighbours.

Add to `CLAUDE.md`:

```
LANDING DISCIPLINE: do not combine a refactor with a feature in one
commit. If a change touches > 8 files across more than one subsystem,
land the refactor first as a no-op (verified by tests), then the feature.
```

### Summary table — recommendation → finding

| Recommendation | Addresses |
|---|---|
| S1 — Integration suite as merge gate | H3, all MLS clusters |
| S2 — MLS lifecycle regression contract | C1, C8, device-sync incident |
| S3 — Anti-drift gate (revised) | Post-window H2 correction, legacy-head cleanup |
| S4 — Signer state-machine harness | C10, C11, C6, ExternalSignerService 0.59 |
| S5 — PR-template affected-area report | H2, drift detection |
| S6 — `CLAUDE.md` invariants | All — generalised guardrails |
| S7 — No flag-day rewrites | C1, C9, ShellViewModel rewrite |
| S8 — Freeze the other head during a pivot | Avalonia-on-Android pivot (STEP 5b), W21–W22 firefighting |

---

## Appendix — exact commands used

Raw extraction:

```powershell
git log --pretty=format:"%H|%ai|%an|%ae|%s" > .commits.txt
git log --pretty=format:"COMMIT|%H|%ai|%s" --numstat > .commits-stats.txt
git log --pretty=format:"%H|%b"           > .commits-bodies.txt
```

Branches and merges:

```powershell
git branch -a
git log --merges --pretty=oneline
```

First-commit dates for tests:

```powershell
git log --pretty=format:"%H %ai %s" --diff-filter=A -- `
  "tests/Scramble.Core.Tests/*.cs" `
  "tests/OpenChat.Core.Tests/*.cs" `
  "tests/Scramble.UI.Tests/*.cs" `
  "tests/OpenChat.UI.Tests/*.cs" `
  "tests/Scramble.Diagnostics/*.cs"
```

Classification + cluster + hotspot + parity analysis:

```powershell
python .analysis/classify.py            # STEP 1, 2
python .analysis/spotcheck.py           # 10% diff verification
python .analysis/clusters_hotspots.py   # STEP 3, 4
python .analysis/parity.py              # STEP 5
```

Scripts live at `.analysis/*.py` for reproduction. JSON snapshot of
classified commits at `.analysis/commits.json`.

---

*Generated 2026-06-29 from 591 commits over 94 days.*
