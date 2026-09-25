# Scramble.Android — the native Android head

`src/Scramble.Android` is the **native** Android head: Android Views and
Fragments, written in C#, driving the same `Scramble.Presentation` ReactiveUI
ViewModels and the same `Scramble.Core` services as every other head. It is not
a separate app — only a separate view layer.

**It ships, and since 2026-09-25 it is the only Android head that does.** Built on
every PR (`dotnet-android.yml`) and released by `publish.yml`. The Avalonia Android
APK is gated off behind `PUBLISH_AVALONIA_ANDROID` in that workflow.

## Two Android heads, one app id

| Head | APK asset | UI | Released? |
|---|---|---|---|
| `src/Scramble.Mobile.Android` | `Scramble-<version>.apk` | Avalonia, shared with desktop | **no**, gated off since 2026-09-25 |
| `src/Scramble.Android` | `Scramble-native-<version>.apk` | native Android Views | yes |

The Avalonia head is still built on every PR and still receives shared view changes.
Only its release is paused.

Both use ApplicationId `app.scramble.chat` and are signed with the same release
key, so **installing one replaces the other in place and the profile database
survives the switch**. They cannot be installed side by side.

That shared identity is why pausing the Avalonia release strands nobody: the native
APK installs straight over an existing Avalonia install and keeps the account, chats
and history. While the gate is off there is only one APK per release, so an Obtainium
filter on the asset name has nothing to choose between — anyone previously tracking
`Scramble-<version>.apk` will stop seeing updates and needs to track
`Scramble-native-<version>.apk` instead.

## Why it came back

It was abandoned in the 2026-05-11 Avalonia-on-Android pivot and frozen on
2026-07-14. When it was re-examined on 2026-09-23 it was **three compile errors**
away from building against the Dark Matter engine, and the UI layer had barely
moved in the meantime — `Scramble.UI` had taken 2 commits, `Scramble.Presentation`
3, and the only two `feat:` commits in that window were the engine flip itself.

What it needed:

- `MainActivity.cs` — `ManagedMlsService` → `DarkMatterMlsServiceFactory.Create`,
  the same seam every other head uses
- `SettingsFragment.cs` — `MarmotCsVersion` went with the marmot-cs engine
- `ChatFragment.cs` — `global::Android.Widget.Button`, a namespace collision
  between `Android.Widget` and this project's own `Scramble.Android` root
- `MainActivity.cs` — an explicit `RxAppBuilder.CreateReactiveUIBuilder()
  .WithAndroidX().BuildApp()`. ReactiveUI 23.x replaced assembly-scanning
  auto-registration with an explicit builder while this head was frozen, so
  without it the app compiled and then died on launch with a
  `TypeInitializationException` the moment a ViewModel called `WhenAnyValue`.

That last one is the reason a compile gate is not enough on its own: the head
built cleanly and crashed on the first screen. `scripts/android-smoke.ps1` is
what catches that.

## What it does not have yet

Fragments exist for Login, ChatList, Chat, NewChat, AddBot and Settings. The
multi-device work added to the Avalonia head after the freeze — device linking,
the account switcher, the profile dialog — has no native equivalent.
