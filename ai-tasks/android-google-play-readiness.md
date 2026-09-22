# Android Google Play Store Readiness

## Status: Not Ready — 3 Blockers, 5 Recommended Fixes

## Analysis Date: 2026-04-28

---

## Blockers (must fix before submission)

### 1. No App Icons
**Severity: Critical — instant rejection**

The `mipmap-hdpi/`, `mipmap-xhdpi/`, `mipmap-xxhdpi/` directories are empty. No launcher icon exists in any density.

**Required:**
- `ic_launcher.png` in mdpi (48x48), hdpi (72x72), xhdpi (96x96), xxhdpi (144x144), xxxhdpi (192x192)
- Adaptive icon support (Android 8.0+): `ic_launcher_foreground.xml` + `ic_launcher_background.xml` in `mipmap-anydpi-v26/`
- Round icon variant: `ic_launcher_round.png` in all densities

**How to fix:**
- Design a launcher icon (or use Android Studio's Image Asset Studio)
- Generate all density variants
- Place in `src/OpenChat.Android/Resources/mipmap-*/`
- Update `AndroidManifest.xml` if icon references differ from default

### 2. Version Code Hardcoded to 1
**Severity: Critical — blocks updates**

`src/OpenChat.Android/OpenChat.Android.csproj` has `ApplicationVersion` set to `1`. Google Play requires a strictly increasing version code for each APK/AAB upload. If published with `1`, the next update must be `2`, etc.

**Current config:**
```xml
<ApplicationVersion>1</ApplicationVersion>
<ApplicationDisplayVersion>0.5.0</ApplicationDisplayVersion>
```

**How to fix:**
- Auto-increment version code in CI/CD (e.g., use build number from GitHub Actions)
- Or set manually before each release: `<ApplicationVersion>$(BUILD_NUMBER)</ApplicationVersion>`
- Display version should follow semver: `1.0.0` for first Play Store release

### 3. No Data Extraction Rules (Android 12+)
**Severity: High — Play Console warning**

Apps targeting SDK 31+ should declare `android:dataExtractionRules` in the manifest. This controls what data is eligible for cloud backup and device-to-device transfer.

For a privacy-focused encrypted messaging app, this should be restrictive — MLS keys and encrypted messages should NOT be backed up (they're device-specific and won't work on a new device).

**How to fix:**
- Create `src/OpenChat.Android/Resources/xml/data_extraction_rules.xml`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<data-extraction-rules>
    <cloud-backup>
        <exclude domain="database" path="." />
        <exclude domain="sharedpref" path="." />
        <exclude domain="file" path="OpenChat/" />
    </cloud-backup>
    <device-transfer>
        <exclude domain="database" path="." />
        <exclude domain="file" path="OpenChat/" />
    </device-transfer>
</data-extraction-rules>
```
- Add to manifest: `android:dataExtractionRules="@xml/data_extraction_rules"`

---

## Recommended Fixes (strongly advised before launch)

### 4. No Crash Reporting
**Severity: High**

The app has file-based logging (Serilog) and a built-in log viewer, but no remote crash reporting. If the app crashes in production, there's no way to know unless the user manually exports logs.

**Options:**
- Firebase Crashlytics (free, Google's standard)
- Sentry (.NET SDK available, privacy-friendly — can self-host)
- App Center (Microsoft, being retired — avoid)

**Recommendation:** Sentry — it has a .NET SDK, can be self-hosted for privacy, and doesn't require Google Play Services. Firebase Crashlytics requires Google Play Services which some privacy-conscious users may not have.

**Effort:** 1-2 days

### 5. No Deep Links for Nostr URIs
**Severity: Medium**

The app handles `bunker://` URLs via manual paste in the login screen but doesn't register as a handler for these URI schemes. Users can't tap a `bunker://` link in another app and have OpenChat open.

**Missing intent-filters for:**
- `bunker://` — NIP-46 signer connection (Amber interop)
- `nostrconnect://` — alternative NIP-46 scheme
- `nostr:npub1...` — user profile links (future)
- `nostr:note1...` — note/event links (future)

**How to fix:**
Add to `AndroidManifest.xml` inside the `<activity>` tag:
```xml
<intent-filter>
    <action android:name="android.intent.action.VIEW" />
    <category android:name="android.intent.category.DEFAULT" />
    <category android:name="android.intent.category.BROWSABLE" />
    <data android:scheme="bunker" />
</intent-filter>
<intent-filter>
    <action android:name="android.intent.action.VIEW" />
    <category android:name="android.intent.category.DEFAULT" />
    <category android:name="android.intent.category.BROWSABLE" />
    <data android:scheme="nostrconnect" />
</intent-filter>
```

Then handle the intent data in `MainActivity.OnNewIntent()`.

**Effort:** 1-2 days

### 6. No Splash Screen
**Severity: Medium**

Android 12+ has a mandatory system splash screen (SplashScreen API). Without explicit configuration, the system uses a default white screen with the app icon (which doesn't exist — see blocker #1). This creates a poor first impression.

**How to fix:**
- Add AndroidX SplashScreen dependency
- Configure splash theme in `styles.xml` with `windowSplashScreenBackground` and `windowSplashScreenAnimatedIcon`
- Apply splash theme to the activity in manifest

**Effort:** 0.5-1 day (after icons exist)

### 7. Use Modern Photo Picker (Android 13+)
**Severity: Low**

The app uses `ActivityResultContracts.GetContent()` for media selection. Android 13+ introduced `PickVisualMedia()` which provides a better UX and doesn't require `READ_MEDIA_*` permissions.

**How to fix:**
- Use `ActivityResultContracts.PickVisualMedia()` on Android 13+
- Fall back to `GetContent()` on older versions
- Remove `READ_MEDIA_IMAGES` permission if only using the new picker

**Effort:** 0.5 day

### 8. Predictive Back Gesture (Android 13+)
**Severity: Low**

Android 13+ supports predictive back animations that show a preview of where the back gesture will navigate. Not implementing this won't block submission but the app won't feel native.

**How to fix:**
- Add `android:enableOnBackInvokedCallback="true"` to manifest
- Use `OnBackPressedDispatcher` instead of `OnBackPressed()` override
- Test back navigation in all fragments

**Effort:** 1 day

---

## What's Already Production-Ready

> **Re-checked 2026-09-22 against `src/Scramble.Mobile.Android`, the head that
> actually ships.** Five claims in this section described
> `src/Scramble.Android` — the head abandoned by the 2026-05-11 Avalonia pivot —
> and were false of the shipped app. They are corrected below rather than
> deleted, because which head a claim was true of is the useful part.

### Permissions & Security
- Permissions declared in the shipped manifest: INTERNET, RECORD_AUDIO,
  FOREGROUND_SERVICE, FOREGROUND_SERVICE_DATA_SYNC, POST_NOTIFICATIONS,
  WAKE_LOCK. **No CAMERA and no storage permissions** — consistent with there
  being no QR scanner or file picker on this head yet, so their absence is a
  missing feature and not a missing declaration.
- POST_NOTIFICATIONS permission for Android 13+
- `allowBackup="false"` — **was not set on this head until 2026-09-22.**
  Android defaults it to true, so the OS was backing the app's private data
  directory, and therefore the profile SQLite file with its MLS ratchet state
  and leaf private keys, to the user's cloud account. The abandoned head set it;
  this one was created without it. Now set.
  - **Still open:** on API 31+ `allowBackup="false"` stops cloud backup but not
    device-to-device transfer. Closing that needs an
    `android:dataExtractionRules` resource, which wants a real device to verify.
- No hardcoded secrets, API keys, or test credentials anywhere in the codebase
- Keys stored in Android Keystore (AES-GCM encryption), via
  `MobileAndroidSecureStorage`
- The MLS store is encrypted at rest by `SecureMarmotStorageProvider`, the
  decorator the Dark Matter cutover replaced `EncryptedSqliteStorageProvider`
  with — that class was deleted with the legacy engine in P11 step 5. Which
  columns are protected, which are deliberately in the clear and why, are
  recorded on that class and enforced by a column census in
  `SecureMarmotStorageProviderTests`.

### Build & Signing
- Signing config uses environment variables (not hardcoded passwords), and
  `AndroidKeyStore=true` is set — without it the SDK ignores the signing
  properties and silently falls back to debug signing
- **No release keystore is in the repo.** This section previously named
  `openchat-release.keystore`; no such file is tracked. Producing and storing
  one is outstanding release work, not a done item.
- Application id: `app.scramble.chat` (was recorded here as `com.openchat.app`,
  which predates the rename)
- Target framework: `net10.0-android` (was recorded as `net9.0-android`)
- Min SDK: API 24 (Android 7.0) — covers ~97% of devices

### Background Service
- `RelayForegroundService` properly implemented with persistent notification
- Foreground service type declared: `ForegroundServiceType.TypeDataSync`
- Notification channel created with appropriate importance level
- Wake lock for connection persistence
- Can be toggled on/off by user in Settings

### Privacy Posture (strong for Play Store data safety form)
- No analytics or telemetry
- No device identifiers collected
- No location data
- No third-party tracking SDKs
- All messages end-to-end encrypted (MLS protocol)
- User controls relay selection (data routing)
- External signer support (private keys can stay off-device entirely)

---

## Play Store Admin Tasks (non-code)

### Data Safety Form
Google Play requires a data safety declaration. For OpenChat:

| Data Type | Collected | Shared | Purpose |
|-----------|-----------|--------|---------|
| User ID (public key) | Yes | Yes (relays) | Core functionality |
| Messages | Yes | Yes (relays, encrypted) | Core functionality |
| Media files | Optional | Optional (Blossom server) | User-initiated sharing |
| Contacts (following list) | Optional | Optional (relays) | Social features |
| App logs | Yes (local only) | No | Diagnostics |

Encryption: In transit (WSS/TLS) + at rest (MLS + SQLite encryption)
Data deletion: User can delete all data by clearing app data

### Store Listing Assets Required
- Feature graphic: 1024x500 px
- Phone screenshots: minimum 2, recommended 4-8 (1080x1920 or similar)
- Tablet screenshots: recommended if supporting tablets
- Short description: max 80 characters
- Full description: max 4000 characters
- Privacy policy URL: **required** (must be hosted publicly)

### Content Rating
Complete the IARC questionnaire in Play Console:
- App category: Communication / Messaging
- User-generated content: Yes (messages, but E2EE so not moderatable)
- Expected rating: likely Teen (13+) or Everyone (depending on questionnaire answers)
- Note: E2EE messaging without content moderation may trigger additional review

### Privacy Policy
**Required for Play Store.** Must cover:
- What data is collected (public keys, encrypted messages, optional media)
- How data is transmitted (to user-selected Nostr relays over WSS)
- Encryption practices (MLS E2EE, at-rest encryption)
- User rights (full control over data, can delete by clearing app)
- Third-party services (Nostr relays, Blossom media servers — user-configured)
- Contact information

---

## Recommended Implementation Order

1. **App icons** (blocker #1) — must do first, everything else depends on having icons
2. **Data extraction rules** (blocker #3) — 30 minutes, XML file + manifest attribute
3. **Version code auto-increment** (blocker #2) — CI/CD change
4. **Splash screen** (fix #6) — depends on icons
5. **Deep links** (fix #5) — improves Amber/signer interop
6. **Crash reporting** (fix #4) — important for production monitoring
7. **Modern photo picker** (fix #7) — low effort polish
8. **Predictive back** (fix #8) — lowest priority polish
