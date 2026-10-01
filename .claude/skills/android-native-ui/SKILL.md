---
name: android-native-ui
description: Mobile UI/UX rules for the NATIVE Android head (src/Scramble.Android — Android Views, Fragments, XML layouts). Covers window insets and edge-to-edge under targetSdk 36, the minSdk-24 floor, theming against nine dark themes, touch targets, TalkBack and contrast, RecyclerView adapter and ViewModel-subscription lifecycle, dialogs and scroll containers, strings, and the feedback/empty/error-state conventions this head already uses. Use when adding or changing a fragment, adapter, layout, menu, style or colour in src/Scramble.Android, when reviewing a diff there, or when deciding where a piece of chrome belongs. NOT for the Avalonia heads.
---

# Native Android UI — house rules

**Last verified:** 2026-10-01 against commit `48de274`

**Scope: `src/Scramble.Android` only.** That is the *native* head — Android
Views, Fragments and XML layouts in C#, bound by hand to the shared
`Scramble.Presentation` ReactiveUI ViewModels. It is the only Android head
currently released (`Scramble-native-<version>.apk`; the Avalonia APK is gated
off behind `PUBLISH_AVALONIA_ANDROID='false'` in `publish.yml`).

**Does not apply to** `src/Scramble.UI/Views/**` or
`src/Scramble.Mobile.Android/**`. Those are Avalonia XAML; nothing below
transfers. CLAUDE.md invariant **I1-M** governs that head, and **I1-L** governs
this one — *any* change under `src/Scramble.Android/**` needs a
`Legacy-Android-Change: <reason>` commit trailer until I1-L is retired or
repurposed. Budget for that before you start editing.

Standing audit of this surface: `ai-tasks/mobile-ui-ux-audit-2026-10.md`. If
you are about to fix something, check there first — it is probably already
written up with a line number.

---

## 0. The platform envelope (verify before recommending anything)

| Fact | Where | Consequence |
|---|---|---|
| `net10.0-android` | `Scramble.Android.csproj:4` | — |
| `SupportedOSPlatformVersion` **24.0** (Android 7.0) | `Scramble.Android.csproj:5` | No dynamic colour (API 31+), no `OnBackInvokedCallback`/predictive back (33+), no `SplashScreen` API (31+), no per-app language (33+), no Material You. Anything newer needs an `OperatingSystem.IsAndroidVersionAtLeast(n)` guard — the codebase already does this at `ChatFragment.cs:213` and `SettingsFragment.cs:203`. |
| `targetSdkVersion` **36** (from the SDK default, not pinned) | `obj/*/AndroidManifest.xml:9` | **Edge-to-edge is enforced.** `systemBars.Top` is non-zero. `android:statusBarColor` / `android:navigationBarColor` are ignored. This is the single highest-risk property of this head. |
| Material **1.11.0.3**, `ReactiveUI.AndroidX` 23.2.27 | `Scramble.Android.csproj:19-20` | Material 3 components are available (`Widget.Material3.*`, `materialswitch`), Material 3 *expressive* is not. |
| `supportsRtl="true"` | `Properties/AndroidManifest.xml:7` | Start/end attributes only — see §5. |

Do not propose a modern API without naming the floor and the guard. A
recommendation that silently requires API 31 is a bug report, not a
recommendation.

---

## 1. Window insets — the rule written in blood

`4d53c03` ("every control answered taps a status bar above where it drew") is
the most expensive UI bug in the repo's history. It shipped in v0.7.0, passed
the startup smoke test **twice**, and made the whole app unusable: every tap
landed 8.6mm (142px at 420dpi) above where the control was drawn.

The mechanism, stated so you can recognise it anywhere:

> Padding an Android view moves its **content box**. A framework that renders
> into the content box while mapping `MotionEvent` coordinates against the
> view's own origin will disagree with itself by exactly the padding.

It bit the Avalonia head, not this one — native Android Views move layout and
hit-testing together when you pad them, so **this head is immune to that
specific failure by construction**. What is *not* automatic:

- **Every activity root needs inset handling.** `activity_main.xml:6` and
  `activity_share_target.xml:7` use `android:fitsSystemWindows="true"`.
  `activity_scan_qr.xml` does not — and it draws a `MaterialToolbar` at the top
  of a `FrameLayout`. Any new activity layout gets `fitsSystemWindows="true"` on
  its root, or an explicit `ViewCompat.SetOnApplyWindowInsetsListener`.
- **`fitsSystemWindows` covers system bars, not the IME.** On a targetSdk-35+
  window `adjustResize` is deprecated and the window no longer shrinks for the
  keyboard, so the bottom system-window inset does not grow. There is **no**
  `WindowSoftInputMode`, no `SetOnApplyWindowInsetsListener` and no
  `WindowCompat` call anywhere in this head (grep `Insets|SoftInput|WindowCompat`
  over `src/Scramble.Android` — two hits, both `fitsSystemWindows`). The
  Avalonia head got `WindowSoftInputMode=AdjustResize` in `a6183cc` and the
  native head was never given the equivalent.
  **If you touch the chat composer, handle `WindowInsetsCompat.Type.Ime()`
  yourself.** Pad the *bottom* only (`4d53c03`), in both the inset listener and
  any animation callback — pad in one and not the other and the offset comes
  back for the duration of every keyboard animation.
- **Never pad top or left to clear a system bar from inside a listener.** Use
  `fitsSystemWindows` on the root, or pad a container whose children are
  measured inside it.
- **API-level reproduction matters.** `dcda60e`: the inset defect *cannot occur*
  below API 35, because the window there is already laid out below the status
  bar. A guard or manual check run on an older emulator proves nothing. The CI
  emulator is pinned to API 36 for this reason (`dotnet-android.yml`).

---

## 2. Theming — nine dark themes, and the palette only half-follows them

`Resources/values/styles.xml` defines **nine** themes, all
`Theme.Material3.Dark.NoActionBar`, selected in Settings and persisted by
`Services/ThemeService.cs`. Default is `amoled_purple`
(`ThemeService.cs:40`), applied in `MainActivity.OnCreate` via
`SetTheme(...)` at `MainActivity.cs:41` and re-applied by `Activity.Recreate()`
on change.

There is no `values-night/` and that is **correct**: the app is dark-only, the
theme is a user choice, and a `-night` qualifier would fight it. Do not add one.
If a light theme is ever wanted it is a tenth entry in `styles.xml` +
`ThemeService.AvailableThemes`, not a resource qualifier.

**The rule.** A theme can only repaint what it owns. `styles.xml` overrides
*theme attributes*; `Resources/values/colors.xml` is a single fixed palette with
no per-theme variants. So:

```
?attr/colorSurface          follows the selected theme
@color/surface              does NOT — it is Nostr Purple for ever
```

42 `@color/` references remain in the layouts, 25 of them to palette colours
that should be attributes (`text_muted` ×19, `surface`, `background_dark`,
`bubble_*`). That is why picking AMOLED Black still gives you purple-navy chat
bubbles.

- **In a layout, reference `?attr/…`.** `colorPrimary`, `colorOnPrimary`,
  `colorSurface`, `colorOnSurface`, `colorSurfaceVariant`,
  `colorOnSurfaceVariant`, `colorOutlineVariant`.
- **`@color/` is only for semantics that must not follow the theme** —
  `status_error`, `status_success`, `status_warning`. Nothing else.
- **Raw hex in a layout is always wrong.** Four exist
  (`activity_scan_qr.xml:19,40,68` scrims — defensible over a camera preview;
  `fragment_chat.xml:22` an amber banner — not defensible).
- **New chrome that needs a theme-varying colour needs a new attribute in all
  nine styles**, not a new `@color/`. If that is too much work for the change
  you are making, that is the signal to reuse an existing attribute.
- **Cold-start flash.** `MainActivity`'s manifest theme is `@style/AppTheme`
  (`MainActivity.cs:27`) and the saved theme is applied in `OnCreate`. The
  window background is Nostr Purple `#0F0F23` until then. If you add an
  activity, give it the same treatment and accept the flash, or fix it once for
  everybody by persisting the chosen `windowBackground`.

---

## 3. Contrast — compute it, do not eyeball it

Nine dark themes × hand-picked hex means contrast is not checkable by looking.
Compute WCAG ratios for any new pairing. Thresholds: **4.5:1** normal text,
**3:1** large text (≥18sp, or ≥14sp bold) and non-text UI such as icons.

Measured failures already in the tree (see the audit for the full table):

| Pair | Ratio |
|---|---|
| `@color/text_muted` `#6B6B8D` on `@color/bubble_received` `#2D2D4A` | **2.59:1** |
| `colorOnPrimary` `#FFFFFF` on AmoledPurple `colorPrimary` `#A78BFA` | **2.72:1** |
| white bold on the out-of-sync banner `#FF9800` | **2.16:1** |

The systematic error is **white `colorOnPrimary` over a light accent**. In a
Material 3 dark scheme a light primary takes a *dark* `onPrimary`. Only
`AppTheme.Monochrome` gets this right (`#000000`). When you add or edit a theme,
check `colorOnPrimary` against `colorPrimary` and `colorOnSecondary` against
`colorSecondary` before committing.

A quick checker (no dependencies):

```python
def lin(c):
    c /= 255.0
    return c/12.92 if c <= 0.04045 else ((c+0.055)/1.055)**2.4
def lum(h):
    h = h.lstrip('#')[-6:]
    r, g, b = (int(h[i:i+2], 16) for i in (0, 2, 4))
    return 0.2126*lin(r) + 0.7152*lin(g) + 0.0722*lin(b)
def ratio(a, b):
    la, lb = lum(a), lum(b)
    return (max(la, lb) + 0.05) / (min(la, lb) + 0.05)
```

---

## 4. Touch targets — the minimum is 48dp, not 44dp

Android's minimum is **48×48dp** (Material accessibility guidance; the
Accessibility Scanner flags anything smaller). 44dp is Apple's 44pt, and
`styles.xml`'s `ReactionBarItem` comment cites "the 40dp minimum", which is not
a standard anywhere.

19 interactive controls in this head are under 48dp — the smallest at 32dp
(`skipped_invites_retry`/`_dismiss` in `fragment_chat_list.xml:152,162`,
`member_admin_toggle` in `item_group_member.xml:97`). Full list in the audit.

- **A new tappable control is 48dp minimum in both axes.** For an icon button
  that should *look* 24dp, set `layout_width`/`layout_height` to 48dp and
  `android:padding="12dp"` — the glyph stays small, the target does not.
- An explicit `layout_height` on a `MaterialButton` **overrides** its 48dp
  `minHeight`. Setting `android:layout_height="36dp"` to make a button look
  compact silently breaks the target. Use `Widget.Material3.Button.TextButton`
  with `android:insetTop="0dp" android:insetBottom="0dp"` and let the height be
  `wrap_content`.
- When the visual must stay small inside a dense row, a `TouchDelegate` on the
  parent is the correct escape, not a shrunken view.

---

## 5. Accessibility

19 `contentDescription` attributes exist across 23 layouts. That is the baseline
to improve on, not to match.

- **Every non-text interactive view needs a `contentDescription`.** Every
  `ImageButton`, `ImageView` that is clickable, `FloatingActionButton`, and
  every `<item>` in `Resources/menu/*.xml` (menu titles double as the TalkBack
  label — `menu_chat_list.xml` is fine because its items all have
  `android:title`).
- **Purely decorative views get `android:importantForAccessibility="no"`** —
  the 2dp accent bars, the 10dp status dots, the avatar background behind an
  initial.
- **A list row that reads as a pile of unlabelled TextViews is a defect.** Set
  a single `contentDescription` on the row's root that composes the fields in
  reading order (sender, then content, then time), and mark the children
  `no`. `item_message_received.xml` / `item_message_sent.xml` currently have one
  `contentDescription` each — on the audio play button.
- **Emoji are not labels.** `view_reaction_bar.xml` labels only `reaction_reply`
  and `reaction_copy`; the six emoji glyphs announce as emoji.
- **New messages should be announced.** Nothing calls
  `AnnounceForAccessibility` or sets `accessibilityLiveRegion` anywhere in this
  head. The chat RecyclerView is the obvious place.
- **RTL is clean — keep it that way.** Zero `Left`/`Right`/`gravity="left"`
  attributes in any layout. Use `paddingStart`/`End`, `layout_marginStart`/`End`,
  `gravity="start"/"end"`.
- **Font scaling:** every text size in every layout is `sp` (no `dp` text
  anywhere) — keep it so. But sizes skew small: 12sp is used 47 times, 11sp 19
  times, 10sp twice. Prefer `android:textAppearance="?attr/textAppearanceBodyMedium"`
  (and the `…BodySmall`/`…TitleMedium`/`…LabelLarge` siblings) over a hardcoded
  `textSize` + `textColor` pair — it is the only way the type scale and the
  theme stay in step. No layout currently does this except
  `fragment_chat_list.xml:64`.
- Fixed `layout_height` on a text row clips at large font scales. Prefer
  `wrap_content` + `minHeight`.

---

## 6. Adapters and RecyclerView

All seven adapters in `Adapters/` share one `UpdateItems` shape: replace the
list, call `NotifyDataSetChanged()`. No `DiffUtil`, no stable IDs, anywhere.

- **Prefer `DiffUtil` / `ListAdapter` for any list that updates incrementally** —
  above all `MessageAdapter`, where every arriving message currently rebinds the
  entire visible window. `NotifyDataSetChanged` also cancels item animations and
  resets in-flight row state (a playing voice note's SeekBar, a half-scrolled
  row).
- **Cache view references in the ViewHolder, not per bind.**
  `MessageAdapter.BindMediaViews` runs eight `FindViewById` calls on every bind
  (`MessageAdapter.cs:76-83`), plus more in `BindReactions` and
  `BindReplyQuote`. That is the easiest win in the file.
- **Every branch of a bind must reset every property the other branches set.**
  `MessageAdapter.cs:167` and `:186` call `SetTextColor` for the downloaded and
  error states and nothing resets it, so a recycled holder keeps red text under
  "Loading…". Same for `SetImageBitmap` — the non-image branches only set
  `Visibility = Gone`, so the bitmap stays attached to the recycled row. A bind
  method must be a total function of the item.
- **Never decode a bitmap on the UI thread, and never at full resolution.**
  `MessageAdapter.cs:159-161` calls `BitmapFactory.DecodeByteArray` straight
  from `OnBindViewHolder` with no `inSampleSize`, into a 240dp-wide `ImageView`.
  Decode off-thread with `inJustDecodeBounds` then `inSampleSize`, and cache the
  result keyed by message id.
- **Each ViewHolder owns a `CompositeDisposable` that it disposes at the top of
  `Bind`.** `MessageAdapter`'s holders do this correctly
  (`MessageAdapter.cs:388`, `:414`) — copy it.
- **A nested RecyclerView with `nestedScrollingEnabled="false"` must sit inside
  something that scrolls.** Neither of the two does.
  `pending_invites_recycler` (`fragment_chat_list.xml:122`) is a `wrap_content`
  child of a plain `LinearLayout`, so a long invite list cannot scroll and
  instead squeezes the chat list; `group_members_recycler`
  (`bottom_sheet_group_info.xml:87`) is inside an `AlertDialog` custom panel,
  which does not scroll either (§8).

---

## 7. ViewModel subscriptions and fragment lifecycle

Every fragment holds a `CompositeDisposable _disposables`, disposes it in
`OnDestroyView` and allocates a fresh one. That part is right and consistent —
put every `WhenAnyValue(...).Subscribe(...)` and every `Command.Execute()`
through `.DisposeWith(_disposables)`.

**`CollectionChanged` is not covered by that, and it is the live leak in this
head.** `MainActivity`'s navigation uses `.Replace(...).AddToBackStack(...)`
(`MainActivity.cs:209-250`), so going into a chat destroys
the chat-list fragment's *view* while keeping the *instance*. Popping back
re-runs `OnViewCreated`, which subscribes again. Ten `CollectionChanged +=`
sites exist; **one** unsubscribes.

```csharp
// The pattern. NewChatFragment.cs:138 + :266 is the only place that does this.
private NotifyCollectionChangedEventHandler? _handler;

// OnViewCreated
_handler = (s, e) => Activity?.RunOnUiThread(Refresh);
ViewModel.Items.CollectionChanged += _handler;

// OnDestroyView
if (_handler != null)
{
    ViewModel.Items.CollectionChanged -= _handler;
    _handler = null;
}
```

Also:

- **No static mutable UI callbacks.** `MessageAdapter.OnReplyRequested`
  (`MessageAdapter.cs:265`, assigned at `ChatFragment.cs:246`) and
  `ChatViewModel.FilePickerFunc` (`ChatFragment.cs:60`) are statics assigned
  from a fragment instance and never cleared. The closure outlives the fragment
  and can target a destroyed view. Pass the callback into the adapter's
  constructor, or clear the static in `OnDestroyView`.
- **Register a subscription where its consumer lives.** Bindings created inside
  a dialog builder (`ChatFragment.ShowGroupInfoBottomSheet`,
  `ShowContactInfoBottomSheet`, `ShowInviteMemberDialog`) go into the
  *fragment-lifetime* `_disposables`, so opening a dialog three times leaves
  three live subscriptions and one event produces three Toasts. Give each
  dialog its own `CompositeDisposable` and dispose it on dismiss.
- **Fragments take ViewModels as constructor arguments and therefore have no
  no-arg constructor.** `70cd59c` fixed the resulting rotation crash by
  declaring `ConfigurationChanges` on `MainActivity` so the activity is never
  recreated. That covers the configs it lists —
  `Orientation | ScreenSize | ScreenLayout | KeyboardHidden`
  (`MainActivity.cs:28`) — and nothing else. **Any config change not in that
  list, and any system-initiated process-death restore, still hands the
  `FragmentManager` a fragment it cannot instantiate.** Before adding a
  fragment, either give it a public no-arg constructor plus argument-bundle
  rehydration, or know that you are relying on that list. Do not quietly widen
  the list as a fix; it is already load-bearing and undocumented at the call
  site.

---

## 8. Dialogs, sheets and scroll containers

`40a7520` recorded the failure mode, measured: *"A control below the fold
reports bounds clipped to the display, so 'bottom <= screenHeight' calls it
visible, taps the edge and hits nothing. At 320x640 the identity card overflows
and Continue is off-screen; at 1080x2424 it fits and the fault cannot be seen at
all."* CI drives the emulator at 320x640/160dpi for exactly this reason.

- **Any dialog or sheet whose content can grow needs a scroll container.** Four
  in this head have none: `bottom_sheet_group_info.xml`,
  `bottom_sheet_contact_info.xml`, `dialog_my_profile.xml`,
  `dialog_invite_member.xml`. `AlertDialog`'s custom-view panel does not scroll
  — only its `message` text does.
- **`bottom_sheet_*` layouts are shown in `MaterialAlertDialogBuilder`**
  (`ChatFragment.cs:642`, `:783`), not a `BottomSheetDialog`. The filenames
  record an intent the code never got to. If you touch either, move it to
  `BottomSheetDialog` — it scrolls, it drags, and the layout is already written
  for it.
- **Check new screens at 320x640.** It is the only size at which the fold bugs
  are visible, and it is what CI uses.
- Dialogs inherit `AppTheme` from the activity; `MaterialAlertDialogBuilder`
  picks up `?attr/` values. `ScanQrActivity` is the exception — it declares
  `Theme.Material3.DayNight.NoActionBar` (`ScanQrActivity.cs:37`), so it ignores
  the user's theme and flips to light on a light-mode device. Don't copy that.

---

## 9. Strings, formatting and locale

There is **no `Resources/values/strings.xml`**, and no `dimens.xml`. 117
hardcoded `android:text="…"` literals sit in the layouts, plus dozens more
built with C# interpolation in the fragments. Nothing is translatable.

A full extraction is a big, mechanical, I1-L-trailered change and is not
something to do as a side effect. The rules that apply to work you are already
doing:

- **A literal that looks like data must have an `android:id`, or come from
  code.** `fragment_settings.xml:699` is `android:text="Version 0.1.0"` with no
  id — so nothing can rewrite it, and the About card ships a wrong version
  number beside the correct one (`SettingsFragment.cs:103`). This is the whole
  argument for the rule in one line.
- **Pluralise through `getQuantityString`, not a ternary.**
  `ChatListFragment.cs:288-290` and `ChatFragment.cs:727` pick singular/plural
  in C#.
- **Never format a time or date with a fixed pattern.**
  `MessageAdapter.cs:394` and `:421` use `ToString("HH:mm")`, which ignores the
  device's 12/24-hour setting; `ChatListAdapter.FormatRelativeTime` hardcodes
  `"MMM d"` and English suffixes. Use
  `global::Android.Text.Format.DateFormat.GetTimeFormat(context)` and
  `DateUtils.GetRelativeTimeSpanString`.
- **New strings go in `strings.xml`** — creating the file is cheaper than
  adding the 118th literal, and it gives the extraction somewhere to land.
- **Prefer Material icons over `@android:drawable/*`.** 18 references to
  framework drawables exist (`ic_menu_edit` ×6, `ic_menu_close_clear_cancel` ×3,
  …). They are visually inconsistent across OEM skins, unthemed, and several are
  deprecated. Vector drawables in `Resources/drawable/` are the house pattern
  (six already there).

---

## 10. Feedback, loading, empty and error states

Current conventions, so you match rather than invent:

| Need | This head's convention | Where |
|---|---|---|
| Transient status | `Snackbar.Make(view, msg, LengthLong)` bound to `ChatListViewModel.StatusMessage` | `ChatListFragment.cs:310` |
| Everything else transient | `Toast.MakeText` — 16 call sites | `SettingsFragment`, `LoginFragment`, `ChatFragment`, `MessageAdapter`, `ScanQrActivity`, `ShareTargetActivity` |
| Inline error | a `TextView` with `@color/status_error`, `Visibility` bound to the error property | `fragment_login.xml:67`, `dialog_invite_member.xml` |
| Busy | a `ProgressBar` whose `Visibility` tracks `IsLoading…` (8 layouts) | `fragment_chat_list.xml:182` |
| Busy on a button | disable it and swap its label | `ChatListFragment.cs:277`, `ChatFragment.cs:828` |
| Empty list | a centred `LinearLayout` toggled against `items.Count` | `fragment_chat_list.xml:190` |

- **Prefer `Snackbar` to `Toast` for anything the user may need to act on.**
  Toasts are rate-limited and suppressed in the background on API 30+, cannot
  carry an action, and sit outside the app's theme. The Snackbar path already
  exists.
- **An empty state must not be a full-bleed overlay.** `empty_state` in
  `fragment_chat_list.xml:190` is `match_parent`×`match_parent` and a sibling of
  the content column, so "No chats yet" draws over the tab strip and over the
  pending-invites section that is visible at the same time. Put the empty view
  *inside* the slot the list occupies.
- **An empty state and a spinner must be mutually exclusive.** The two code
  paths in `ChatListFragment` disagree: `:340` guards on `!IsLoading`, `:237`
  does not.
- **Empty copy must match the tab it is shown for.** The same "No chats yet"
  string serves Chats, Agents and Archived.
- **Three states minimum for anything that loads: busy, empty, failed.** Two of
  three is the common omission here.

---

## 11. Navigation and back

- `MainActivity` owns navigation with four `Navigate*` methods, each a
  `Replace` + `AddToBackStack` on `Resource.Id.fragment_container`
  (`MainActivity.cs:209-250`). Add a screen by adding a method there; do not
  start a second navigation mechanism.
- **Back is explicit, and that was a fix.** `995b6ea`: back inside a chat took
  up to three presses because of IME state and stacked entries.
  `ChatFragment` installs an `OnBackPressedCallback` that pops immediately
  (`ChatFragment.cs:65-66`, `:106-122`). A new screen with its own IME or
  dialog state should do the same rather than hoping the default is right.
  Predictive back needs API 33 — see §0.
- **Do not commit two fragment transactions for one state.**
  `MainActivity.OnCreate` does: the `WhenAnyValue(IsLoggedIn)` subscription
  (`:107-131`) emits its current value immediately and shows a fragment, then
  `:153-156` shows a freshly constructed one again.
- **`OnResume` is not a free place to put work.** `MainActivity.OnResume`
  (`:380-432`) reconnects every relay and writes two `StatusMessage` values —
  so two Snackbars — on *every* resume, including returning from the QR scanner
  or the share sheet. Gate resume-time work on actually having been backgrounded.

---

## 12. Where chrome belongs

Platform-specific services live in `Services/` (`AndroidClipboardService`,
`AndroidAudioService`, `AndroidNotificationService`, `ThemeService`, …) and are
injected into the shared ViewModels from `MainActivity.OnCreate`. A fragment
should bind, not compute.

Two things in particular do **not** belong in a fragment:

- **Business logic.** If the Avalonia head needs the same behaviour, it belongs
  on a `Scramble.Presentation` ViewModel. CLAUDE.md's standing concern about
  this head is *feature drift between the two Android heads*, and a rule
  implemented in a fragment is a rule the other head does not get.
- **Engine types.** `Scramble.Marmot` types stop at the service layer; see
  CLAUDE.md's Dark Matter cutover rules. Fragments see `Chat`, `Message`,
  `Member`, `Role`.

---

## 13. Verification — what actually checks this head

`dotnet-android.yml` runs two jobs. The `android` job compiles **both** heads.
The `smoke` job boots an emulator and runs three scripts — `android-smoke.ps1`,
`android-tap-alignment.ps1`, `android-message-flow.ps1` — and **all three
resolve their APK under `src/Scramble.Mobile.Android/bin`**
(`android-smoke.ps1:92`). The head that ships has only a compile gate; nothing
has ever started it in CI.

So for this head:

- A green build means it compiles. `4e8f53d` is the standing reminder that it
  can compile and die on the first screen (ReactiveUI 23's explicit builder,
  `MainActivity.cs:91-93`).
- **Run it yourself.** From PowerShell — `AndroidSdkDirectory` does not survive
  Git Bash:

```powershell
$env:JAVA_HOME='C:\work\jdk'
dotnet build src\Scramble.Android\Scramble.Android.csproj `
  -p:AndroidSdkDirectory='C:\work\android-sdk' -p:JavaSdkDirectory='C:\work\jdk'
```

  Then boot an emulator at **API 36** (below 35 the inset class of bug cannot
  occur — `dcda60e`) with `-memory 6144` (2048 gets the app killed by the
  lowmemorykiller, which looks like a crash with no exception), and point
  `scripts/android-smoke.ps1 -Apk …` at
  `src/Scramble.Android/bin/Release/net10.0-android/app.scramble.chat-Signed.apk`.
- Check at **320x640/160dpi** as well as a phone-sized screen. `40a7520` found
  three faults, each invisible at one of the two.
- `./scripts/check-drift.ps1` before pushing, and remember the
  `Legacy-Android-Change:` trailer.

---

## 14. Review checklist

For any diff under `src/Scramble.Android/**`:

1. `Legacy-Android-Change:` trailer present (I1-L).
2. New activity root has `fitsSystemWindows="true"` or an inset listener; no
   top/left padding applied from a listener.
3. Touching the composer? IME insets handled, bottom only, in listener **and**
   animation callback.
4. Colours are `?attr/…`; `@color/` only for `status_*`; no raw hex.
5. New `colorPrimary`/`colorOnPrimary` pairing contrast-checked ≥ 4.5:1 (text)
   / 3:1 (icons), in **all nine** themes it appears in.
6. Every new tappable ≥ 48×48dp; no explicit `layout_height` under 48dp on a
   `MaterialButton`.
7. `contentDescription` on every new non-text control; decoration marked
   `importantForAccessibility="no"`.
8. Text uses `sp` and, preferably, `?attr/textAppearance*`.
9. Every `CollectionChanged +=` has a matching `-=` in `OnDestroyView`; every
   `Subscribe` is `.DisposeWith(...)`; dialog bindings use a dialog-scoped
   disposable.
10. Bind methods reset every property any other branch sets; no bitmap decode on
    the UI thread.
11. Growable dialog content sits in a scroll container; checked at 320x640.
12. Busy / empty / failed states all present and mutually exclusive.
13. New user-visible string in `strings.xml`; no fixed date/time pattern; no
    ternary pluralisation.
14. Built and started on an API 36 emulator, because CI will not do it for you.
