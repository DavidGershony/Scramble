# Native Android head — UI/UX audit, October 2026

**Status: findings only.** No UI code was changed. Audited at commit `48de274`
(`chore(android): 0.7.9`), against `.claude/skills/android-native-ui/SKILL.md`,
which was written from the same read.

**Scope: `src/Scramble.Android` — the native head**, the only Android head
currently released (`Scramble-native-<version>.apk`; the Avalonia APK is gated
off behind `PUBLISH_AVALONIA_ANDROID='false'`). 6 fragments, 23 layouts, 7
adapters, 2 activities, 1 activity-helper. The Avalonia heads
(`src/Scramble.UI/Views`, `src/Scramble.Mobile.Android`) are deliberately out of
scope.

**Why this surface and not the other.** The three device checks in
`dotnet-android.yml` — `android-smoke.ps1`, `android-tap-alignment.ps1`,
`android-message-flow.ps1` — all resolve their APK under
`src/Scramble.Mobile.Android/bin` (`scripts/android-smoke.ps1:92`). The head
that ships has a **compile gate and nothing else**. `4e8f53d` is the standing
proof that is not enough: it compiled cleanly and died on the first screen.
Everything below therefore had to be read rather than observed.

---

## How to read the priorities

- **P0 — broken for users today.** A static read is sufficient to be confident,
  or the mechanism is arithmetic.
- **P1 — degrades with use, or is unreachable on some devices.** Real, but the
  magnitude depends on a device I did not have.
- **P2 — diverges from Material / Android convention.** Visible, fixable, not
  breaking anything.
- **P3 — nit.**

Each finding says what I verified and what I did not. **Nothing here was
observed on a device or emulator.** See §"What I could not check" at the end for
the list of claims that need a device to confirm, and the one claim I believe
is wrong-ish but could not settle.

---

## P0-1 — The shipped head does no window-inset work at all, and the keyboard fix never came to it

`src/Scramble.Android` contains **zero** inset handling. Grep
`Insets|SoftInput|WindowCompat|EdgeToEdge|WindowInsets` across every `.cs` and
`.xml` in it and you get two hits, both `android:fitsSystemWindows="true"` —
`activity_main.xml:6` and `activity_share_target.xml:7`.

- `MainActivity`'s `[Activity]` attribute (`MainActivity.cs:27-28`) sets
  `Label`, `MainLauncher`, `LaunchMode`, `Theme` and `ConfigurationChanges`.
  **No `WindowSoftInputMode`.**
- `a6183cc` ("Android back button, keyboard resize, signer app-switch…") added
  `WindowSoftInputMode=AdjustResize` so the keyboard would push the chat input
  up. Its diff touches `src/Scramble.Mobile.Android/MainActivity.cs`. The native
  head got only the `AndroidLauncher.cs` half of that commit. **The keyboard fix
  has never existed in the head that ships.**
- `targetSdkVersion` is **36** (`obj/*/AndroidManifest.xml:9` — it comes from
  the SDK default; nothing in the csproj pins it). On a targetSdk-35+ window
  Android enforces edge-to-edge, `adjustResize` is deprecated, and the window no
  longer shrinks for the IME — so even the setting that is missing would not be
  the whole answer. The composer in `fragment_chat.xml:126-236` is the last child
  of a vertical `LinearLayout` with no inset consumer above it.

The behaviour therefore falls entirely to the system default
(`adjustUnspecified`, which lets the platform choose pan or resize) on a window
whose insets nobody consumes. That is not a configuration anyone chose, and no
test in this repo exercises it.

`4d53c03` is why this matters more than it looks. That commit is the only
inset-shaped bug the project has had, it cost a shipped release, and its root
cause was *not* a code change:

> Why now, with no change to this file: the window is edge-to-edge — Android
> enforces that for apps targeting SDK 35+ — so systemBars.Top is non-zero where
> it used to be 0 and the padding was a no-op. The bug arrived with a platform
> rule, not with a commit, which is why it is not in any diff.

The native head is immune to that *specific* failure — padding a native View
moves layout and hit-testing together — but it is exposed to the same platform
rule with none of the handling.

**Recommendation.** One `ViewCompat.SetOnApplyWindowInsetsListener` on
`fragment_chat.xml`'s root, consuming `Type.Ime() | Type.SystemBars()` and
applying the result as **bottom padding only** (top/left padding from a listener
is what caused `4d53c03`), plus the matching
`WindowInsetsAnimationCompat.Callback` — `4d53c03` found that patching the
listener without the animation callback brings the offset back for the duration
of every keyboard animation. Add `WindowSoftInputMode = AdjustResize` on
`MainActivity` for the API 24–34 devices the floor still supports.

**Not verified on a device.** What is verified: the absence of every relevant
API call, the asymmetry with `a6183cc`, and targetSdk 36.

---

## P0-2 — `activity_scan_qr.xml` draws its toolbar under the status bar

`activity_scan_qr.xml` is the one activity layout with **no**
`fitsSystemWindows`, and `ScanQrActivity.cs` adds no inset listener. Its root is
a `FrameLayout` (`:2-6`) with:

- a `MaterialToolbar` at `layout_height="?attr/actionBarSize"` at the top
  (`:15-24`) carrying the title and the only back affordance, and
- a bottom bar with the hint text and the "Enter manually" escape hatch
  (`:33-58`), `layout_gravity="bottom"`.

Under enforced edge-to-edge the toolbar occupies the status-bar strip and the
bottom bar occupies the navigation-bar strip. On a gesture-nav device the
"Enter manually" button sits in the home-gesture zone.

This is the clearest case in the audit because the comparison is internal: the
other two activity layouts set `fitsSystemWindows="true"` and this one does not.

**Fix:** `android:fitsSystemWindows="true"` cannot go on this root (it would
inset the camera `PreviewView` too). Keep the preview full-bleed and apply the
system-bar insets as padding to the toolbar and the bottom bar individually.

---

## P0-3 — The group member list is unreachable in a group that does not fit on screen

`bottom_sheet_group_info.xml` is a `LinearLayout` at
`layout_height="wrap_content"` (`:2-7`). Its last child is

```xml
<androidx.recyclerview.widget.RecyclerView
    android:id="@+id/group_members_recycler"
    android:layout_height="wrap_content"
    android:nestedScrollingEnabled="false" />     <!-- :87-92 -->
```

and the file contains no scroll container. `ChatFragment.ShowGroupInfoBottomSheet`
shows it through `MaterialAlertDialogBuilder(...).SetView(dialogView)`
(`ChatFragment.cs:783-788`) — **not** a `BottomSheetDialog`, despite the
filename. `AlertDialog`'s custom-view panel is not a scroll container; only its
`message` text is.

So: unbounded `wrap_content` list, nested scrolling disabled, no scrolling
ancestor. Members past the first screenful are clipped with no way to reach
them — and `ai-tasks/buffered-replay-not-draining-2026-09-30.md` documents a
real user in a **49-member** group.

Three other dialogs have the same shape and no scroll container, and one of them
has 150 lines of content:

| Layout | Lines | Shown by |
|---|---|---|
| `bottom_sheet_group_info.xml` | 93 | `ChatFragment.cs:783` |
| `bottom_sheet_contact_info.xml` | 119 | `ChatFragment.cs:642` |
| `dialog_my_profile.xml` | 150 | `ChatListFragment` |
| `dialog_invite_member.xml` | 72 | `ChatFragment.cs:853` |

This exact failure mode has already been caught once in this project, measured,
in `40a7520`:

> A control below the fold reports bounds **clipped to the display**, so
> "bottom <= screenHeight" calls it visible, taps the edge and hits nothing. At
> 320x640 the identity card overflows and Continue is off-screen; at 1080x2424
> it fits and the fault cannot be seen at all.

That was the Avalonia login card. The same thing is sitting in four native
dialogs, and it is invisible on a modern phone — which is presumably why nobody
has hit it.

**Fix.** Move both `bottom_sheet_*` layouts to `BottomSheetDialog`, which is
what their filenames say and what they are shaped for: it scrolls, it drags, and
it handles a long member list natively. Wrap `dialog_my_profile.xml` and
`dialog_invite_member.xml` in a `NestedScrollView`.

**Confidence.** High on the mechanism, which is three independent properties of
the XML plus a documented property of `AlertDialog`. Not confirmed on a device.

---

## P0-4 — Full-resolution bitmap decode on the UI thread, repeated for every arriving message

`MessageAdapter.cs:159-161`, inside `BindMediaViews`, called straight from
`OnBindViewHolder`:

```csharp
var bitmap = BitmapFactory.DecodeByteArray(
    item.DecryptedMediaBytes, 0, item.DecryptedMediaBytes.Length);
mediaImage.SetImageBitmap(bitmap);
```

No `BitmapFactory.Options`, no `inJustDecodeBounds`, no `inSampleSize`, no
cache, no background thread. The target is `media_image`, declared
`layout_width="240dp"`, `maxHeight="320dp"`
(`item_message_received.xml:87-94`). A 12-megapixel phone photo decodes to
roughly **48 MB** of `ARGB_8888` to fill a 240dp box.

It is not once per image, either. `MessageAdapter.UpdateItems` is
`_items = items; NotifyDataSetChanged();` (`:25-29`), and
`ChatFragment.cs:289-299` calls it on **every** `Messages.CollectionChanged`.
So every arriving message re-decodes every visible image bubble from scratch.

The default heap on a mid-range device is 128–256 MB. Two or three photos in the
visible window is an `OutOfMemoryError`; one is a visible stall on the main
thread.

Interaction with P1-1 below makes it worse than linear: each leaked
`CollectionChanged` handler triggers another full `NotifyDataSetChanged`, so the
decode count multiplies by the number of times the user has opened a chat this
session.

**Fix.** Decode on a background thread with `inJustDecodeBounds` then
`inSampleSize` sized to the view; cache by message id; and move the list to
`DiffUtil` so an arriving message rebinds one row rather than all of them.

---

## P0-5 — Settings → About ships a hardcoded, wrong version number

`fragment_settings.xml:696-700`:

```xml
<TextView
    android:layout_width="wrap_content"
    android:layout_height="wrap_content"
    android:text="Version 0.1.0"
    ... />
```

**No `android:id`.** Nothing can rewrite it. The app is at
`ApplicationDisplayVersion` **0.7.9** (`Scramble.Android.csproj:10`), and
`SettingsFragment.cs:103` already writes the real value into a *different*
TextView:

```csharp
libraryVersionsText.Text = $"Version {ViewModel.AppVersion}  |  MLS Engine: {ViewModel.DotnetMlsVersion}";
```

So the About card displays the correct version and the string "Version 0.1.0"
adjacent to each other. Thirty seconds to fix, and it is the cleanest possible
argument for the `strings.xml` discipline in §P2-5: a literal that looks like
*data* must have an id or come from code.

**Fix.** Delete the TextView, or give it an id and set it from
`ViewModel.AppVersion`.

---

## P1-1 — Nine leaked `CollectionChanged` subscriptions; the list rebuild cost grows with every chat you open

Every fragment disposes its `CompositeDisposable` in `OnDestroyView` and
allocates a fresh one. `CollectionChanged` is not a disposable, and it is not
cleaned up.

Ten subscription sites; **one** unsubscribes:

| Site | Collection | Unsubscribed? |
|---|---|---|
| `ChatListFragment.cs:241` | `Chats` | no |
| `ChatListFragment.cs:242` | `AgentChats` | no |
| `ChatListFragment.cs:243` | `ArchivedChats` | no |
| `ChatListFragment.cs:254` | `PendingInvites` | no |
| `ChatFragment.cs:289` | `Messages` | no |
| `AddBotFragment.cs:151` | `BotNip65Relays` | no |
| `AddBotFragment.cs:157` | `BotAvailableRelays` | no |
| `NewChatFragment.cs:97` | `Following` | no |
| `SettingsFragment.cs:377` | `Relays` | no |
| `NewChatFragment.cs:138` | `NewChatParticipants` | **yes** — `:266` |

The growth is not theoretical, because the navigation style guarantees it.
`MainActivity.NavigateToChat` uses `.Replace(...).AddToBackStack(...)`
(`MainActivity.cs:209-217`): the chat-list fragment's **view** is destroyed, the
**instance** survives in the `FragmentManager`, and popping back re-runs
`OnViewCreated`, which subscribes four more times. Each surviving closure
captured the previous `emptyState` / `_adapter` / `recyclerView` locals, so each
one also pins a dead view hierarchy.

After opening 20 chats in a session, one arriving chat-list change runs **21**
`_adapter.UpdateItems(...)` → 21 `NotifyDataSetChanged()`. Combine with P0-4 and
each of those re-decodes the visible bitmaps.

`NewChatFragment` already shows the correct pattern — handler in a field,
`-=` in `OnDestroyView` (`:138`, `:262-268`). Nine sites need the same five
lines.

**This is the finding I would fix first**, because it is mechanical, it is the
cause of "the app gets slower the longer I use it", and the fix is already
written down in the same folder.

---

## P1-2 — Dialog bindings live for the fragment's lifetime, so a Toast fires once per time you opened the sheet

`ChatFragment.ShowGroupInfoBottomSheet`, `ShowContactInfoBottomSheet` and
`ShowInviteMemberDialog` create their bindings against the **fragment-lifetime**
`_disposables`, not a dialog-scoped one. Example — `ChatFragment.cs:754-761`:

```csharp
ViewModel.WhenAnyValue(x => x.AdminActionStatus)
    .ObserveOn(RxSchedulers.MainThreadScheduler)
    .Subscribe(status =>
    {
        if (!string.IsNullOrEmpty(status) && Activity != null)
            Toast.MakeText(Activity, status, ToastLength.Short)?.Show();
    })
    .DisposeWith(_disposables);
```

Open the group-info sheet three times without leaving the chat and one
promote/demote shows **three** Toasts. The contact-info sheet has seven such
bindings (`:654-708`) that keep writing into the dismissed dialog's TextViews;
those are harmless but keep the views alive.

**Fix.** A `CompositeDisposable` per dialog, disposed from `DismissEvent`.
`MainActivity.ShowAccountSwitcherDialog` already uses `DismissEvent` for
exactly this kind of cleanup (`MainActivity.cs:205`).

---

## P1-3 — The empty state is a full-bleed overlay that draws over the pending-invites section

`fragment_chat_list.xml:190-205` declares `empty_state` as a
`match_parent` × `match_parent` `LinearLayout`, a **direct child of the
`CoordinatorLayout`** and a sibling of the content column that holds the tab
strip, the pending-invites section, the skipped-invites notice and the chat list.
It is declared last, so it draws on top.

`ChatListFragment.cs:237` shows it whenever `items.Count == 0`, and
`:267` independently shows the pending-invites section whenever
`PendingInviteCount > 0`. Those are not mutually exclusive — in fact the
**first-run case** is precisely both: no chats yet, one group invite waiting.
The user sees "No chats yet" centred over the Accept/Decline row they are
supposed to press.

The overlay has no background colour, so the invite row shows through behind the
text rather than being hidden. It is also not clickable, so touches should fall
through to the controls underneath (see "could not check").

Two more problems in the same six lines:

- **Spinner and empty state both show during the initial load.** `:340` guards
  with `&& !ViewModel.IsLoading`; `:237` — the path that actually runs on every
  `CollectionChanged` — does not. Two code paths in one file, disagreeing.
- **The copy is wrong for two of the three tabs.** `RefreshChatList`
  (`:227-239`) selects from `ArchivedChats` / `AgentChats` / `Chats` and shows
  the same "No chats yet" for all three.

**Fix.** Move `empty_state` inside the slot the chat-list `RecyclerView`
occupies (or make it a sibling *of the RecyclerView* within the content column),
gate it on `!IsLoading`, and pick the string from the active tab.

---

## P1-4 — Contrast: `text_muted` and white-on-light-accent fail WCAG across the themes

Nine hand-picked dark palettes make this uncheckable by eye, so I computed it.
Ratios are WCAG 2.1 relative luminance; thresholds are **4.5:1** for normal
text, **3:1** for ≥18sp / ≥14sp-bold text and for non-text UI such as icons.

### `@color/text_muted` `#6B6B8D` — 19 uses, the worst pairing in the app

| Over | Ratio | Where it lands |
|---|---|---|
| `@color/bubble_received` `#2D2D4A` | **2.59:1** | message timestamp, reply-quote text, media status, audio duration (`item_message_received.xml:65,76,127,152`) |
| `@color/bubble_sent` `#1A1A2E` | **3.34:1** | same fields on sent bubbles (`item_message_sent.xml`) |
| `@color/surface` `#1E1E3F` | 3.13:1 | reply preview bar (`fragment_chat.xml:111`) |
| AMOLED surface `#0A0A0A` | 3.88:1 | chat-list timestamp (`item_chat.xml:64`), About version line |

These are 11sp and 12sp texts, so the 3:1 large-text allowance does not apply.
Every message timestamp in the app is at 2.59:1.

### `colorOnPrimary` is `#FFFFFF` over a light accent in 7 of 9 themes

| Theme | `colorPrimary` | onPrimary | Ratio |
|---|---|---|---|
| `AppTheme.GoldenAxe` | `#FF8C00` | `#FFFFFF` | **2.33:1** |
| `AppTheme.ForestGreen` | `#10B981` | `#FFFFFF` | **2.54:1** |
| `AppTheme.AmoledPurple` *(the default)* | `#A78BFA` | `#FFFFFF` | **2.72:1** |
| `AppTheme.BloodOrange` | `#F97316` | `#FFFFFF` | **2.80:1** |
| `AppTheme.CyberTeal` (`onSecondary`/`secondary`) | `#22D3EE` | `#FFFFFF` | **1.81:1** |
| `AppTheme.Monochrome` | `#A0A0A0` | `#000000` | 8.03:1 ✅ |

This is one systematic mistake, not six: in a Material 3 **dark** scheme a light
primary takes a **dark** `onPrimary`. Only `Monochrome` got it right. It paints
the send FAB's icon (`fragment_chat.xml:233`, `app:tint="?attr/colorOnPrimary"`),
every filled `MaterialButton`, and the Log Out label. At 2.33–2.80:1 these fail
even the 3:1 non-text threshold.

### Three more

- **Out-of-sync banner, `fragment_chat.xml:22-34`:** white bold 13sp on a
  hardcoded `#FF9800` → **2.16:1**. This is a warning the user is specifically
  meant to read. Black text on amber is the conventional pairing and clears 4.5.
- **Log Out, `fragment_settings.xml:708-717`:** `?attr/colorOnPrimary` (white
  in 8 of 9 themes) on `@color/status_error` `#EF4444` → **3.76:1**.
- **Sender name in a received bubble:** `?attr/colorPrimary` on the *fixed*
  `@color/bubble_received` → 3.13:1 on the default palette's `#8B5CF6`, 3.60:1
  on MidnightBlue. This pairing is uncontrolled by construction: the text colour
  follows the theme and the background does not. It is a symptom of P2-1.

The checker used is in the skill (§3) so these numbers can be reproduced and
new pairings checked before they land.

---

## P1-5 — 19 interactive controls below the 48dp minimum

Android's minimum touch target is **48×48dp**. Enumerated from the layouts:

| Size | Control | File:line |
|---|---|---|
| 32dp h | `skipped_invites_retry` | `fragment_chat_list.xml:150` |
| 32dp h | `skipped_invites_dismiss` | `fragment_chat_list.xml:160` |
| 32dp h | `member_admin_toggle` | `item_group_member.xml:94` |
| 36dp | `cancel_reply_button` | `fragment_chat.xml:117` |
| 36dp | `cancel_recording_button` | `fragment_chat.xml:143` |
| 36dp | `audio_play_button` | `item_message_received.xml:106`, `item_message_sent.xml:95` |
| 36dp | `relay_remove_button` | `item_relay.xml:31` |
| 36dp | `member_copy_npub` | `item_group_member.xml:108` |
| 36dp | `copy_npub_button`, `copy_nsec_button` | `fragment_login.xml:155,188` |
| 36dp h | `invite_accept_button`, `invite_decline_button` | `item_pending_invite.xml:85,75` |
| 36dp h | `rescan_invites_button` | `fragment_chat_list.xml:109` |
| 36dp h | `copy_group_link_button` | `dialog_invite_member.xml:45` |
| 36dp h | `log_refresh_button` | `dialog_log_viewer.xml:27` |
| 40dp | `attach_button`, `record_button` | `fragment_chat.xml:185,216` |
| 40dp | `contact_copy_npub_button` | `bottom_sheet_contact_info.xml:70` |
| 40dp | `my_copy_npub_button`, `my_copy_nsec_button` | `dialog_my_profile.xml:82,131` |
| 44dp | every item in the reaction bar | `styles.xml` `ReactionBarItem` |

Two notes that matter more than the list:

1. **`invite_accept_button` and `invite_decline_button` are 36dp tall and
   adjacent** (`item_pending_invite.xml:75-93`). Accept and Decline on a group
   invite are not symmetric outcomes — a declined invite is unreachable
   afterwards, which is exactly what the Retry affordance at
   `ChatListFragment.cs:294-299` exists to work around. Undersized, adjacent,
   asymmetric-consequence buttons are a bad combination.
2. **`styles.xml`'s `ReactionBarItem` justifies 44dp against the wrong
   standard.** Its comment reads *"44dp is above the 40dp minimum"*. There is no
   40dp minimum on Android; 44 is Apple's 44pt. The reasoning in that comment —
   eight glyphs in a row on a 411dp phone — is sound and 48dp would still fit
   (8 × 48 = 384dp plus padding is tight but survivable), but the premise should
   be corrected either way so it is not copied.

**Fix pattern** (does not change how anything looks): set the view to 48dp and
push the glyph in with `android:padding`. For `MaterialButton`, note that an
explicit `layout_height` **overrides** its built-in 48dp `minHeight` — use
`wrap_content` plus `android:insetTop="0dp" android:insetBottom="0dp"` instead of
`layout_height="32dp"`.

---

## P1-6 — ViewHolder recycling leaks visual state

`MessageAdapter.BindMediaViews` is an if/else-if chain over nine states
(`:85-221`). Two branches mutate properties that no other branch resets:

- **Text colour.** `:167` sets `status_success` for a downloaded file, `:186`
  sets `status_error` for a media error. Nothing resets it. A recycled holder
  that previously showed an error renders "Loading…" (`:176`), "Your IP will be
  visible to the host" (`:203`) or "[Encrypted media]" (`:216`) in **red**.
- **Bitmap.** `:161` sets the image; the other eight branches only set
  `mediaImage.Visibility = Gone`. The bitmap stays attached to the recycled row,
  so the holder keeps a full decoded image alive for a text message.

A bind method must be a total function of its item. The ViewHolders already get
the *subscription* half of this right —
`_disposables.Dispose(); _disposables = new CompositeDisposable();` at the top
of both `Bind` methods (`:388`, `:414`) — so the shape to copy is in the file.

**Also, cheaply:** `BindMediaViews` runs **eight** `FindViewById` calls per bind
(`:76-83`), plus two in `BindReactions` (`:236`) and three in `BindReplyQuote`
(`:357-364`). With `NotifyDataSetChanged` on every message that is ~13 view-tree
traversals × every visible row × every message. Cache them in the ViewHolder.

---

## P1-7 — Fragment restore after process death is still the crash `70cd59c` fixed for rotation

Every fragment takes a ViewModel as a constructor argument and none declares a
no-arg constructor:

```
AddBotFragment(MainViewModel)                              AddBotFragment.cs:23
ChatFragment(MainViewModel)                                ChatFragment.cs:44
ChatListFragment(MainViewModel, ShellViewModel?)           ChatListFragment.cs:33
LoginFragment(ShellViewModel)                              LoginFragment.cs:24
NewChatFragment(MainViewModel)                             NewChatFragment.cs:34
SettingsFragment(MainViewModel)                            SettingsFragment.cs:37
```

`70cd59c` ("Fix Android crash on orientation change") diagnosed this exactly:

> Fragments are constructed with ViewModel args; on rotation Android recreates
> the Activity and the FragmentManager tries to restore them via a no-arg
> constructor that doesn't exist. Handle orientation / screen-size config changes
> in-place to avoid the recreate cycle.

The chosen fix was `ConfigurationChanges` on the activity. It covers what it
lists and nothing else — `MainActivity.cs:28`:

```
ConfigChanges.Orientation | ConfigChanges.ScreenSize |
ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden
```

Absent: `UiMode`, `FontScale`, `Locale`, `LayoutDirection`, `Density`,
`SmallestScreenSize`. Also absent from the protection entirely: a
system-initiated **process-death restore**, which `ConfigurationChanges` does
nothing about — the user backgrounds the app, Android reclaims it, the user
returns from Recents, and the `FragmentManager` restores saved fragment state by
instantiating a class that cannot be instantiated. That is routine on a
memory-constrained phone and is what Developer Options' "Don't keep activities"
simulates.

`ShareTargetActivity.cs:20` declares a narrower set still
(`Orientation | ScreenSize`).

This is a latent crash, not a present one, and I could not trigger it. But it is
*the same crash* that already happened once, with a fix that covers one of its
triggers. CLAUDE.md names "background survival" as a known cluster around this
head.

**Fix (the cheap half).** Give each fragment a public no-arg constructor plus a
`static NewInstance(...)` that stashes the identifying keys in `Arguments`, and
re-resolve the ViewModel from the `ShellViewModel` in `OnCreate`. The
ViewModels already survive — `MainActivity` keeps `_shellViewModel` in a
`static` field for exactly this reason (`MainActivity.cs:31-33`).
**Do not** widen `ConfigurationChanges` as the fix; the list is already
load-bearing and undocumented at the call site.

---

## P2-1 — The theme picker repaints about half the app

Nine themes, selected in Settings, applied by `SetTheme` + `Activity.Recreate()`
(`MainActivity.cs:41`, `SettingsFragment.cs:577-600`). `styles.xml` overrides
**theme attributes**; `Resources/values/colors.xml` is a single fixed palette
with no per-theme variants. So anything a layout references as `@color/…` is
frozen at the Nostr Purple palette for all nine.

42 `@color/` references remain in the layouts. 25 of them are palette colours:

```
19  @color/text_muted           #6B6B8D   purple-grey
 1  @color/surface              #1E1E3F   the composer background, fragment_chat.xml:130
 1  @color/background_dark      #0F0F23
 1  @color/bubble_sent          #1A1A2E   item_message_sent.xml
 1  @color/bubble_received      #2D2D4A   item_message_received.xml:15
 1  @color/bubble_sent_text     #E0E0FF
 1  @color/bubble_received_text #E0E0FF   item_message_received.xml:136
```

The remaining 17 are `status_error`/`status_success`/`status_warning` (correct —
semantics that must not follow the theme) plus a handful of `secondary` /
`on_primary` / `surface_secondary` that should be attributes.

The user-visible result: pick **AMOLED Black** — whose whole point is a true
black, blue-accented scheme — and the chat screen, which is the screen you spend
all your time on, keeps purple-navy bubbles, a purple-tinted composer, and
`#E0E0FF` message text. The theme that looks most changed in the picker changes
the least where it matters.

This also *causes* the uncontrolled contrast pairing at the end of P1-4: the
sender name follows the theme and the bubble it sits on does not, so nobody
chose that ratio.

There is no `values-night/` and **that is correct, not a defect.** The app is
dark-only by design (all nine parent on `Theme.Material3.Dark.NoActionBar`), the
theme is an explicit user choice persisted in SharedPreferences
(`ThemeService.cs`), and a `-night` qualifier would fight it. A light theme, if
ever wanted, is a tenth entry in `styles.xml` + `ThemeService.AvailableThemes` —
not a resource qualifier. Do not add `values-night/`.

**Fix.** Replace the 25 palette references with `?attr/` equivalents. `surface`,
`background_dark`, `text_muted` and the four `bubble_*` map onto
`colorSurface` / `android:colorBackground` / `colorOnSurfaceVariant` /
`colorSurfaceVariant` / `colorSurfaceContainerHigh` respectively, with
per-theme values added to all nine styles where no existing attribute fits.
Doing this and P1-4's `colorOnPrimary` audit together is the right order — once
the bubbles follow the theme, the pairings become checkable.

---

## P2-2 — `ScanQrActivity` ignores the user's theme and flips to light

`ScanQrActivity.cs:37`: `Theme = "@style/Theme.Material3.DayNight.NoActionBar"`.
Every other activity uses `@style/AppTheme`. So this one screen — and any dialog
raised from it — uses the Material baseline palette, and on a device in light
mode it renders light inside an otherwise dark-only app.

**Fix:** `Theme = "@style/AppTheme"`, and keep the explicit
`@android:color/white` on the controls that sit over the camera preview (which
is what it needs them for).

---

## P2-3 — Toast is the default feedback channel; Snackbar exists and is used once

16 `Toast.MakeText` call sites against 1 `Snackbar`
(`ChatListFragment.cs:310`). Several Toasts carry errors:

```
SettingsFragment.cs:150   $"Reconnect failed: {ex.Message}"
SettingsFragment.cs:722   $"Failed to export: {ex.Message}"
SettingsFragment.cs:780   "Media permissions required for MIP-04"
LoginFragment.cs:131      "No app found to handle nostrconnect:// links"
```

On API 30+ Toasts are rate-limited, suppressed when the app is not in the
foreground, cannot carry an action, and render outside the app's theme — so an
error the user might want to retry or report arrives in the one channel that
cannot offer either. "Copied!" is a fine Toast; a failed relay reconnect is not.

**Fix.** Route anything actionable through `Snackbar`, with an action where one
exists (Retry on reconnect, Settings on a permission denial). The pattern is
already in `ChatListFragment`.

---

## P2-4 — `OnResume` reconnects every relay and pops two Snackbars, every time

`MainActivity.OnResume` (`:380-432`) unconditionally sets
`StatusMessage = "Reconnecting to relays..."`, fires `ReconnectCommand`, and
then sets `"Connected to N/M relays"`. `ChatListFragment.cs:305-313` turns every
non-empty `StatusMessage` into a `Snackbar.LengthLong`.

`OnResume` runs on every return to the activity — including coming back from
`ScanQrActivity`, the system file picker (`ChatFragment.cs:534`), the share
sheet, a permission dialog, and Amber. Each of those costs two Snackbars and a
full relay reconnect. The comment explains the intent ("Android suspends network
connections when app is backgrounded"), which is right for a real background
transition and wrong for a same-task activity result.

**Fix.** Track whether the process was actually backgrounded (`OnStop` → flag,
or a `ProcessLifecycleOwner` observer) and only reconnect on a true
foreground transition. Suppress the "Reconnecting…" Snackbar when the reconnect
completes under a second.

---

## P2-5 — Nothing is translatable; 117 literals and no `strings.xml`

No `Resources/values/strings.xml` exists. 117 hardcoded `android:text="…"`
values sit in the layouts, plus dozens more built in C# — including full
sentences:

```
fragment_chat.xml:31          "This device is out of sync — messages may be missing."
fragment_settings.xml:349     "Risk: Medium — Enabling media loading exposes your IP ..."
fragment_settings.xml:691     "Scramble is an open-source chat application built with ..."
MessageAdapter.cs:102         "Media loading disabled\nEnable in Settings > Privacy"
MessageAdapter.cs:203         "Your IP will be visible to the host"
ChatListFragment.cs:289       "1 group invite received — encryption key not available ..."
```

Related, and independently worth fixing because it affects every user regardless
of language:

- **Pluralisation is done with ternaries**, not `getQuantityString`:
  `ChatListFragment.cs:288-290` and `:344-346`, and `ChatFragment.cs:727`
  (`$"{ViewModel.ParticipantCount} participants"` — "1 participants").
- **Time formatting ignores the device's 12/24-hour setting.**
  `MessageAdapter.cs:394` and `:421` use `item.Timestamp.ToLocalTime().ToString("HH:mm")`.
  A user whose phone shows 12-hour time sees 24-hour timestamps on every
  message. `DateFormat.GetTimeFormat(context)` is the one-line fix and it is
  pure win. `ChatListAdapter.FormatRelativeTime` (`:132-141`) hardcodes `"MMM d"`
  and English suffixes; `DateUtils.GetRelativeTimeSpanString` handles both.

A full extraction is a large mechanical diff that needs an I1-L trailer and
should not ride along on a feature. The two sub-items above are small and
independent. The rule going forward is in the skill: new strings go in
`strings.xml`, and a literal that looks like data needs an id or must come from
code — see P0-5 for what happens otherwise.

---

## P2-6 — Accessibility: 19 `contentDescription` attributes across 23 layouts

Distribution: `fragment_chat` 5, `fragment_login` 4, `dialog_my_profile` 2,
`view_reaction_bar` 2, and one each in `bottom_sheet_contact_info`,
`fragment_chat_list`, `item_group_member`, `item_message_received`,
`item_message_sent`, `item_relay`. Everything else has none.

Concretely, for a TalkBack user:

- **A message row announces as a pile of unlabelled text.** Each of
  `item_message_received.xml` / `item_message_sent.xml` has exactly one
  `contentDescription` — on the audio play button. The row has no composed
  description, so TalkBack reads sender, reply-sender, reply-content,
  media-status, content, reactions and timestamp as seven separate unlabelled
  nodes in layout order.
- **Reactions announce as raw emoji.** `reactions_display`
  (`item_message_received.xml:140`) has no description, and in
  `view_reaction_bar.xml` only `reaction_reply` and `reaction_copy` are labelled
  — the six emoji glyphs (`:184-212`) are not.
- **Nothing announces an arriving message.** No `AnnounceForAccessibility` and
  no `accessibilityLiveRegion` anywhere in the head.
- **`android:importantForAccessibility` is never used**, so decorative views
  (the 2dp accent bars at `fragment_chat.xml:86-90` and
  `item_message_received.xml:47-50`, the 10dp recording dot at
  `fragment_chat.xml:151-155`, the avatar background behind an initial) are not
  excluded from the tree.
- **Menu items are fine** — `menu_chat.xml` and `menu_chat_list.xml` all carry
  `android:title`, which doubles as the label.

Two things the head gets **right** and should keep:

- **Font scaling.** Every text size in every layout is `sp`; there is not one
  `dp` text size. Sizes do skew small (12sp × 47, 11sp × 19, 10sp × 2), and no
  layout uses `?attr/textAppearance*` except `fragment_chat_list.xml:64` — so
  the type scale is hand-rolled and does not follow the theme — but the scaling
  itself works.
- **RTL.** `supportsRtl="true"` in the manifest and **zero**
  `Left`/`Right`/`paddingLeft`/`gravity="left"` attributes in any of the 23
  layouts. That is unusual and worth protecting.

---

## P3 — Nits

- **`MainActivity.OnCreate` commits two fragment transactions for one state.**
  The `WhenAnyValue(x => x.IsLoggedIn)` subscription at `:107-131` emits the
  current value on subscribe and shows a fragment; `:153-156` then constructs
  and shows another one. Two `Replace` commits and one discarded fragment every
  cold start.
- **80dp of dead space at the end of the chat list.**
  `fragment_chat_list.xml:178` sets `paddingBottom="80dp"` on
  `chat_list_recycler`, but that layout has no FAB — "New chat" is a toolbar
  action (`menu_chat_list.xml`). If the padding is for the gesture-nav bar it
  should come from the navigation-bar inset, not a magic number.
- **Badges are squares.** `invite_badge` (`fragment_chat_list.xml:97-105`) is a
  24dp `TextView` with `android:background="?attr/colorPrimary"` and no shape
  drawable; `chat_unread_badge` (`item_chat.xml:86-97`) the same. Material
  badges are circles/pills. `avatar_background.xml` already exists as the
  pattern to copy.
- **`chat_unread_badge` hardcodes `@android:color/white`** instead of
  `?attr/colorOnPrimary` — which, given P1-4, is currently the same wrong
  colour, but for a different reason.
- **18 `@android:drawable/*` framework icons** (`ic_menu_edit` ×6,
  `ic_menu_close_clear_cancel` ×3, `ic_media_play` ×2, `ic_menu_send`,
  `ic_menu_search`, `ic_menu_preferences`, `ic_menu_add`, `ic_input_add`,
  `ic_btn_speak_now`, `ic_popup_sync`). These are unthemed, inconsistent across
  OEM skins and several are long deprecated. Six local vector drawables already
  exist as the house pattern.
- **`fragment_settings.xml:57` design-time default says "Nostr Purple
  (Default)"** while `ThemeService.cs:40`'s default is `amoled_purple`. Bound at
  runtime (`SettingsFragment.cs:324`) so only visible in the editor, but
  misleading.
- **Cold-start colour flash.** `MainActivity`'s manifest theme is
  `@style/AppTheme` (`:27`) and the saved theme is applied in `OnCreate` (`:41`),
  so the window background is Nostr Purple `#0F0F23` for the first frames on 8
  of 9 themes.
- **No `imeOptions` on the message input.** `fragment_chat.xml:207-212` is
  `textMultiLine`, `maxLines="4"`, no `imeOptions` — fine as a choice, but worth
  being a choice.
- **Permission flow is one tap longer than it needs to be in one place.**
  `ChatFragment.OnRequestPermissionsResult` (`:599-621`) auto-continues for both
  request codes, which is right. `SettingsFragment.cs:780` does not — a denial
  shows a Toast with no route to app settings.
- **Long-press hit area overshoots the bubble.**
  `MessageAdapter.cs:256` sets the long-click listener on `ItemView`, whose root
  `FrameLayout` has `paddingEnd="64dp"` (`item_message_received.xml:7`), so
  long-pressing the empty gutter beside a received message opens that message's
  reaction bar.

---

## Priority order for actually doing this

1. **P0-5** — delete one wrong TextView. Minutes.
2. **P1-1** — nine `-=` lines. The pattern is already in `NewChatFragment`.
   Biggest ratio of user-visible improvement to risk in the list.
3. **P0-1** + **P0-2** — one inset listener for the composer, two for the QR
   activity. These need an API 36 emulator to confirm, which is the real cost.
4. **P0-4** — downsampled off-thread decode + a cache. Pairs naturally with
   moving `MessageAdapter` to `DiffUtil`, which also fixes P1-6's cost half.
5. **P1-3** + **P1-2** — small, local, each confined to one method.
6. **P1-4** + **P2-1** together — fix `colorOnPrimary` in the seven themes and
   move the 25 palette references to attributes in the same pass. Doing either
   alone leaves the contrast unverifiable.
7. **P0-3** — `BottomSheetDialog` migration for the two `bottom_sheet_*`
   layouts, `NestedScrollView` for the other two.
8. **P1-5** — mechanical, 19 controls, no visual change if done with padding.
9. **P1-7** — the restore crash. Real but latent; needs the most design thought
   of anything here.
10. **P2-x** and the nits.

Note **I4** (no flag-day rewrites): several of these span Fragments, Adapters
and Views. P1-1 alone touches five fragments — that is one subsystem and under
the 8-file line, but P2-1 plus P1-4 together will exceed it. Land the palette
refactor as a no-op first, then the contrast fixes.

---

## What I could not check

**Nothing in this audit was observed running.** No device, no emulator. The
findings are a static read of 9,654 lines of C# and XML plus the git history.
Specifically:

- **Every P0-1 / P0-2 claim about what the keyboard and system bars actually do
  is inference.** What is verified: the complete absence of inset-handling APIs
  in this head, the absence of `WindowSoftInputMode`, `targetSdkVersion` 36, and
  the fact that `a6183cc` gave the Avalonia head a fix this head never got. What
  is *not* verified: whether the composer ends up behind the keyboard, whether
  the system picks pan or resize here, and how much of the scan-QR toolbar is
  actually occluded. All three need an API 36 device. This is the single most
  valuable hour of follow-up in the document.
- **P0-3 (clipped member list)** rests on `AlertDialog`'s custom-view panel not
  being a scroll container. I am confident of that, and of the three XML
  properties, but I did not watch a 49-member group fail to scroll.
- **P0-4's magnitude** depends on real message payload sizes and the device
  heap. The code path — synchronous, unsampled, uncached, on the UI thread — is
  certain; "OOM on a mid-range phone" is a prediction.
- **P1-3's touch behaviour: I think I am wrong about the worst case and say so.**
  A `match_parent` `LinearLayout` with no click listener returns `false` from
  `onTouchEvent`, so a `ViewGroup` should keep dispatching down the z-order and
  the Accept/Decline buttons underneath should still be reachable. So the likely
  symptom is "No chats yet" drawn *over* a working invite row — ugly and
  confusing rather than blocking. I have left it at P1 on that reading. If
  touches do get swallowed it is a P0; a two-minute check on a device settles it.
- **P1-7 was not triggered.** Confirming it means enabling "Don't keep
  activities" and backgrounding the app, or changing the system font size while
  it is open. I did not do either.
- **P1-4's contrast numbers are computed, not measured on a panel.** WCAG 2.1
  relative luminance; the function is in the skill. They do not account for
  Material's own elevation overlays, which lighten a surface slightly and would
  move the bubble-background figures by a small amount in the *worse* direction
  for `text_muted`.
- **The three fragments I read least closely** are `AddBotFragment` (203 lines),
  `LoginFragment` (268) and `NewChatFragment` (314). I checked their
  subscription lifecycle, their layouts' touch targets and colours, and their
  `CollectionChanged` sites, but did not walk their flows end to end.
  `SettingsFragment` (807 lines) and its 720-line layout I surveyed rather than
  read line by line — the UnifiedPush/ntfy registration sub-flow
  (`fragment_settings.xml:424-630`) in particular is complex enough to deserve
  its own pass.
- **No performance measurement.** No systrace, no `dumpsys gfxinfo`, no frame
  timings. The adapter findings are "this is O(all visible rows) per message
  when it could be O(1)", not "this drops N frames".
- **`ai-tasks/android-google-play-readiness.md` (2026-04-28) was not
  re-validated.** It is stale in places — its "no app icons" and "version code
  hardcoded to 1" blockers are both resolved — and it does not overlap with
  anything here: it never mentions strings, accessibility, contrast, touch
  targets or theming. Its still-open items (crash reporting, deep links, splash
  screen, modern photo picker, predictive back) are complementary to this audit,
  not duplicated by it. Note that predictive back needs API 33 and the floor is
  24.
