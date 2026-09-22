<#
.SYNOPSIS
    Starts the shipped Android head on a connected device or emulator and
    checks that it is still running with its own UI on screen.

.DESCRIPTION
    I5's freeze lifts on "an equivalent smoke test green in CI", and until
    2026-09-22 nothing had ever *started* this head -- CI built an APK and
    stopped there. Building is not the property in question. Avalonia-on-Android
    fails at startup, not at compile time.

    What this asserts, and why each one is here rather than the obvious check:

      1. The process is alive after a settle period. Not at t+0: the app starts,
         draws, and can be killed a second later.
      2. The resumed activity is ours. A dead app leaves the launcher resumed,
         and a launcher on screen looks identical to a pass if you only ask
         "did the command succeed".
      3. No FATAL EXCEPTION in logcat.
      4. No lowmemorykiller kill of our package. This one is not theoretical:
         on the default Pixel_9 AVD (hw.ramSize = 2048) the app reached a
         visible window and was then killed with "min watermark is breached and
         swap is low". Without this check that reads as a crash-free run with a
         dead process, and the cause is invisible.

    What is NOT used as the assertion, having been tried:

      * `am start -W` exit status. It printed "Status: ok" for a launch whose
        process was already dead, and "Status: timeout" for the run that
        actually worked -- a Debug Mono build on a software renderer does not
        report a first frame inside am's window. It is useful output and
        worthless as a gate.
      * The presence of `monodroid-assembly: open_from_bundles: failed to load
        bundled assembly ...` in logcat. That is normal for a Debug/FastDev
        build, which loads assemblies from the filesystem. It looks alarming
        and means nothing.

.PARAMETER Apk
    Path to the APK. Defaults to the Debug output of the mobile head.

.PARAMETER PackageId
    Application id. Must match <ApplicationId> in Scramble.Mobile.Android.csproj.

.PARAMETER SettleSeconds
    How long the app must stay up. 30s is enough to catch an LMK kill, which
    arrived at ~12s on the undersized AVD.

.PARAMETER ScreenshotPath
    Where to write a PNG of the final state. Written on failure too -- a
    screenshot of the wrong screen is the fastest way to see what happened.
#>
[CmdletBinding()]
param(
    [string]$Apk,
    [string]$PackageId = 'app.scramble.chat',
    [int]$SettleSeconds = 30,
    [string]$ScreenshotPath = 'android-smoke.png'
)
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot/..").Path

function Get-Adb {
    foreach ($candidate in @(
        $env:ANDROID_SDK_ROOT, $env:ANDROID_HOME, "$env:LOCALAPPDATA\Android\Sdk", 'C:\work\android-sdk'
    )) {
        if (-not $candidate) { continue }
        foreach ($exe in @('platform-tools/adb.exe', 'platform-tools/adb')) {
            $p = Join-Path $candidate $exe
            if (Test-Path $p) { return $p }
        }
    }
    $onPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "adb not found. Set ANDROID_SDK_ROOT or put adb on PATH."
}

$adb = Get-Adb
Write-Host "[smoke] adb: $adb"

if (-not $Apk) {
    $Apk = Get-ChildItem -Recurse -Filter "$PackageId-Signed.apk" `
        (Join-Path $repoRoot 'src/Scramble.Mobile.Android/bin') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Apk -or -not (Test-Path $Apk)) {
    throw "No APK found. Build the head first, or pass -Apk."
}
Write-Host "[smoke] apk: $Apk"

# A device must already be attached. Booting one is the caller's job: locally
# that is `emulator -avd <name>`, in CI it is the emulator action's to do.
& $adb wait-for-device | Out-Null
$booted = (& $adb shell getprop sys.boot_completed 2>$null | Out-String).Trim()
if ($booted -ne '1') {
    throw "A device is attached but has not finished booting (sys.boot_completed='$booted')."
}

# Guest RAM, reported rather than enforced. The failure it explains is silent:
# the app starts, draws, and is killed by the lowmemorykiller a few seconds
# later, which no exception and no non-zero exit code will tell you about.
$memLine = (& $adb shell cat /proc/meminfo 2>$null | Select-String '^MemTotal').ToString()
if ($memLine -match '(\d+)\s*kB') {
    $totalMb = [int]([int]$Matches[1] / 1024)
    Write-Host "[smoke] guest RAM: ${totalMb} MB"
    if ($totalMb -lt 3500) {
        Write-Warning ("Guest RAM is ${totalMb} MB. The app was killed by the lowmemorykiller " +
                       "at 2048 MB on 2026-09-22 after reaching a visible window. " +
                       "Boot the emulator with -memory 6144 if this run fails at step 4.")
    }
}

Write-Host "[smoke] installing ..."
$install = (& $adb install -r -g "$Apk" 2>&1 | Out-String)
if ($install -notmatch 'Success') { throw "install failed:`n$install" }

& $adb shell am force-stop $PackageId | Out-Null
& $adb logcat -c | Out-Null

# The activity is generated by the Android SDK from the [Activity] attribute,
# so its class name is a hash and must be discovered, not hardcoded.
$launcher = (& $adb shell "cmd package resolve-activity --brief -c android.intent.category.LAUNCHER $PackageId" 2>$null |
             Out-String).Trim() -split "`r?`n" | Where-Object { $_ -match "^$([regex]::Escape($PackageId))/" } | Select-Object -First 1
if (-not $launcher) { throw "Could not resolve a LAUNCHER activity for $PackageId." }
Write-Host "[smoke] launching $launcher"

# Output kept for the log; deliberately not used as the pass condition.
$startOutput = (& $adb shell am start -W -n $launcher 2>&1 | Out-String).Trim()
Write-Host "[smoke] am start said: $(($startOutput -split "`r?`n" | Where-Object { $_ -match '^Status:' }) -join ' ')"

Write-Host "[smoke] settling for ${SettleSeconds}s ..."
Start-Sleep -Seconds $SettleSeconds

$failures = @()

# 1. still alive
$pidText = (& $adb shell pidof $PackageId 2>$null | Out-String).Trim()
if ($pidText) { Write-Host "[smoke] OK   process alive (pid $pidText)" }
else { $failures += "the process is not running after ${SettleSeconds}s" }

# 2. our activity is the one on screen
$resumed = (& $adb shell dumpsys activity activities 2>$null | Select-String 'ResumedActivity' | Out-String)
if ($resumed -match [regex]::Escape($PackageId)) { Write-Host "[smoke] OK   our activity is resumed" }
else {
    $who = if ($resumed -match '\su0\s(\S+)') { $Matches[1] } else { '<none>' }
    $failures += "our activity is not resumed (on screen: $who)"
}

$logcat = (& $adb logcat -d 2>$null | Out-String)

# 3. no managed crash
if ($logcat -match 'FATAL EXCEPTION') {
    $frames = ($logcat -split "`r?`n" | Select-String -Pattern 'AndroidRuntime' | Select-Object -First 12) -join "`n"
    $failures += "a FATAL EXCEPTION was logged:`n$frames"
} else { Write-Host "[smoke] OK   no FATAL EXCEPTION" }

# 4. not killed for memory -- the one that looks like nothing at all
if ($logcat -match "lowmemorykiller.*Kill '$([regex]::Escape($PackageId))'") {
    $line = ($logcat -split "`r?`n" | Select-String 'lowmemorykiller' | Select-Object -First 1)
    $failures += ("the lowmemorykiller killed the app -- this is a too-small guest, not an app bug. " +
                  "Boot the emulator with more RAM (-memory 6144).`n  $line")
} else { Write-Host "[smoke] OK   not killed by the lowmemorykiller" }

try {
    & $adb exec-out screencap -p > $ScreenshotPath
    Write-Host "[smoke] screenshot: $ScreenshotPath"
} catch { Write-Warning "could not capture a screenshot: $_" }

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "[smoke] FAILED:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}

Write-Host ''
Write-Host "[smoke] the Android head starts, stays up, and owns the screen." -ForegroundColor Green
