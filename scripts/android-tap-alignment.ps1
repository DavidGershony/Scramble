<#
.SYNOPSIS
    Measures whether the Android head draws where it is touched, and fails if the
    two disagree by more than a few pixels.

.DESCRIPTION
    v0.7.0 shipped with every control responding to taps about 8.6mm above where
    it was drawn, and the startup smoke test passed it twice. Starting was never
    the property in question: the app rendered correctly and was untouchable.

    **Two tempting checks that do not work**, both tried:

    1. "Tap the centre of a control's uiautomator bounds and see if it reacts."
       Passes on the broken build. Measured on v0.7.0: uiautomator placed the
       login TextBox at y-centre 1345 and a tap there opened the keyboard, while
       the field was drawn at y-centre 1486. Reported bounds follow hit-testing,
       which is the half that was never wrong, so this check agrees with the bug.

    2. "Tap a tab and diff the pixels inside that tab's rectangle." Fails on a
       healthy build: the login tab strip draws no selection indicator, so
       switching tabs changes nothing inside the tab itself. The visible change
       is entirely in the panel below.

    **What this does.** It tags both coordinate spaces from the same action and
    compares them:

      * tap a control (any control -- the tap only has to make the UI change)
      * ask uiautomator what changed  -> nodes that appeared or vanished, in
                                         HIT-TEST space
      * diff the two screenshots      -> pixels that changed, in RENDER space
      * compare the vertical centroids of those two sets

    On a healthy build the same event produces both, so the centroids land on
    top of each other. If a padding moves rendering without moving hit-testing,
    they separate by exactly that padding -- and the failure message reports the
    offset in pixels, which is the quantity a person can act on.

    No reference screenshot, no hardcoded coordinates, no assumption about which
    control gives visible feedback.

.PARAMETER MaxOffsetDp
    How far the two centroids may sit apart, in density-independent pixels.

    **In dp because the defect scales with density and a pixel threshold does
    not.** The offset this catches is one status-bar height, so it shrinks on a
    coarser screen. Measured on the same broken APK:

      420dpi (1080x2424)   healthy 38px / 14dp     broken 180px / 69dp
      160dpi (320x640)     healthy  6px /  6dp     broken  58px / 58dp

    A 90px threshold -- chosen on the 420dpi device and perfectly sound there --
    passes the broken build at 160dpi, which is the resolution CI runs. The test
    would have been decorative on the only machine that runs it automatically.

    30dp separates healthy from broken by better than 2x at both densities.

    The healthy figure is not 0 because a redraw covers slightly different
    ground than the set of nodes that changed.
#>
[CmdletBinding()]
param(
    [string]$PackageId = 'app.scramble.chat',
    [int]$MaxOffsetDp = 30,
    [int]$ReactionTimeoutSeconds = 30,
    [string]$ArtifactDir = '.'
)
$ErrorActionPreference = 'Stop'

function Get-Adb {
    # PATH first: that is how CI has it, and it avoids guessing at roots entirely.
    $onPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # Then the usual SDK roots. Both binary names, because the executable is
    # `adb` on Linux and `adb.exe` on Windows -- checking only one sends the loop
    # past a perfectly good SDK.
    #
    # Join-Path is wrapped because it THROWS on a foreign absolute path: on Linux,
    # Join-Path 'C:\work\android-sdk' ... raises "Cannot find drive. A drive with
    # the name 'C' does not exist", which under ErrorActionPreference=Stop kills
    # the script before it prints anything. That is exactly how this failed on the
    # Ubuntu runner.
    $roots = @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME, "$env:LOCALAPPDATA\Android\Sdk", 'C:\work\android-sdk') |
        Where-Object { $_ }
    foreach ($root in $roots) {
        foreach ($exe in @('platform-tools/adb', 'platform-tools/adb.exe')) {
            try { $candidate = Join-Path $root $exe } catch { continue }
            if (Test-Path $candidate) { return $candidate }
        }
    }
    throw 'adb not found. Put it on PATH or set ANDROID_SDK_ROOT.'
}
$adb = Get-Adb

<#
.SYNOPSIS
    Dots per inch actually in effect, honouring any override.
#>
function Get-DeviceDensity {
    $out = (& $adb shell wm density 2>$null | Out-String)
    $override = [regex]::Match($out, 'Override density:\s*(\d+)')
    if ($override.Success) { return [int]$override.Groups[1].Value }
    $physical = [regex]::Match($out, 'Physical density:\s*(\d+)')
    if ($physical.Success) { return [int]$physical.Groups[1].Value }
    throw "could not read the display density from: $out"
}

# A PNG for humans to look at when this fails.
function Get-Screenshot([string]$path) {
    & $adb exec-out screencap -p > $path
    if (-not (Test-Path $path) -or (Get-Item $path).Length -eq 0) { throw "screencap produced nothing at $path" }
}

<#
.SYNOPSIS
    The raw framebuffer, as width/height plus RGBA bytes.
.DESCRIPTION
    `screencap` without -p emits a small header then w*h*4 bytes, which needs no
    image library to read. That matters: the first version of this script used
    System.Drawing, which is Windows-only, so it died on the Linux CI runner
    before printing a single line -- the smoke test passed, the step failed, and
    the log had nothing in it. Parsing bytes works everywhere pwsh does.

    The header is 12 bytes (width, height, format) on older Android and 16 on
    newer (a colour-space field was added), so it is derived from the length
    rather than assumed.
#>
function Get-Framebuffer([string]$path) {
    & $adb exec-out screencap > $path
    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $path))
    if ($bytes.Length -lt 32) { throw "raw screencap produced $($bytes.Length) bytes" }

    $w = [BitConverter]::ToUInt32($bytes, 0)
    $h = [BitConverter]::ToUInt32($bytes, 4)
    if ($w -le 0 -or $h -le 0 -or $w -gt 20000 -or $h -gt 20000) { throw "implausible framebuffer size ${w}x${h}" }

    $header = $bytes.Length - ($w * $h * 4)
    if ($header -ne 12 -and $header -ne 16) {
        throw "cannot account for the screencap header: ${w}x${h} leaves $header byte(s)"
    }
    return [pscustomobject]@{ W = [int]$w; H = [int]$h; Offset = [int]$header; Bytes = $bytes }
}

# Pulled rather than read through `adb shell cat`, whose CRLF translation
# corrupts the XML on a Windows host.
function Get-UiTree([string]$name) {
    & $adb shell uiautomator dump /sdcard/$name.xml 2>&1 | Out-Null
    $local = Join-Path $ArtifactDir "$name.xml"
    & $adb pull /sdcard/$name.xml $local 2>&1 | Out-Null
    if (-not (Test-Path $local)) { throw 'could not pull the uiautomator dump' }
    return (Get-Content $local -Raw)
}

# A node's identity for change detection: what it is, what it says, where it is.
function Get-Nodes([string]$xml) {
    [regex]::Matches($xml, '<node[^>]*?class="(?<c>[^"]*)"[^>]*?(?:content-desc="(?<d>[^"]*)")?[^>]*?bounds="\[(?<l>\d+),(?<t>\d+)\]\[(?<r>\d+),(?<b>\d+)\]"') |
        ForEach-Object {
            [pscustomobject]@{
                Key = "$($_.Groups['c'].Value)|$($_.Groups['d'].Value)|$($_.Groups['l'].Value),$($_.Groups['t'].Value),$($_.Groups['r'].Value),$($_.Groups['b'].Value)"
                T   = [int]$_.Groups['t'].Value
                B   = [int]$_.Groups['b'].Value
            }
        }
}

Write-Host '[tap] reading the visual tree ...'
$xmlBefore = Get-UiTree 'ui-before'
$nodesBefore = Get-Nodes $xmlBefore

# Any control will do -- the tap only has to make the UI change. The login tab
# strip is three Buttons sharing a top edge; the middle one always changes
# something from a fresh start, without raising a keyboard.
$buttons = [regex]::Matches($xmlBefore, '<node[^>]*class="Button"[^>]*bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"') |
    ForEach-Object {
        [pscustomobject]@{ L=[int]$_.Groups[1].Value; T=[int]$_.Groups[2].Value; R=[int]$_.Groups[3].Value; B=[int]$_.Groups[4].Value }
    }
$strip = $buttons | Group-Object T | Where-Object { $_.Count -ge 3 } | Select-Object -First 1
if (-not $strip) { throw "expected the login tab strip (three Buttons on one row); found $($buttons.Count) Button node(s). Is the app on the login screen?" }
$target = ($strip.Group | Sort-Object L)[1]
$cx = [int](($target.L + $target.R) / 2)
$cy = [int](($target.T + $target.B) / 2)

$before = Join-Path $ArtifactDir 'tap-before.png'
$after  = Join-Path $ArtifactDir 'tap-after.png'
Get-Screenshot $before
$fbA = Get-Framebuffer (Join-Path $ArtifactDir 'fb-before.raw')
Write-Host "[tap] tapping ($cx,$cy)"
& $adb shell input tap $cx $cy | Out-Null

# Wait for the app to react rather than assuming it has.
#
# This was a flat 3-second sleep, which passed on a desktop-class emulator and
# failed on CI's: the run came back with the before and after dumps -- and the
# before and after screenshots -- byte for byte identical, because a Release
# Mono build on a loaded runner had simply not repainted yet. A fixed sleep
# turns "slower than I guessed" into "the control did not react", which is a
# different and much more alarming claim.
#
# Polling the visual tree instead makes the check as fast as the device allows
# and as patient as it needs to be. Only a genuinely unresponsive control now
# reaches the timeout.
$beforeKeysEarly = @{}; foreach ($n in $nodesBefore) { $beforeKeysEarly[$n.Key] = $true }
#
# The tap is also sent a second time half way through, because the CI failure
# this replaced showed the before and after states byte-identical -- not one
# pixel moved -- on an app that had been idle for thirty seconds. That is as
# consistent with a dropped input event as with a slow repaint, and there was no
# evidence to choose between them. A single retry covers the first without
# weakening anything: a control that genuinely never reacts still fails, it just
# gets asked twice.
$deadline = (Get-Date).AddSeconds($ReactionTimeoutSeconds)
$retryAt = (Get-Date).AddSeconds($ReactionTimeoutSeconds / 2)
$retried = $false
$nodesAfter = $null
do {
    Start-Sleep -Milliseconds 1500
    $candidate = Get-Nodes (Get-UiTree 'ui-after')
    $changed = @($candidate | Where-Object { -not $beforeKeysEarly.ContainsKey($_.Key) }).Count
    if ($changed -gt 0) { $nodesAfter = $candidate; break }
    if (-not $retried -and (Get-Date) -gt $retryAt) {
        Write-Host '[tap] no reaction yet -- sending the tap once more'
        & $adb shell input tap $cx $cy | Out-Null
        $retried = $true
    }
} while ((Get-Date) -lt $deadline)

if ($null -eq $nodesAfter) {
    $nodesAfter = Get-Nodes (Get-UiTree 'ui-after')
    Write-Host "[tap] nothing changed within ${ReactionTimeoutSeconds}s" -ForegroundColor Yellow
}

Get-Screenshot $after
$fbB = Get-Framebuffer (Join-Path $ArtifactDir 'fb-after.raw')

# --- hit-test space: which nodes appeared or vanished, and where ------------
$beforeKeys = @{}; foreach ($n in $nodesBefore) { $beforeKeys[$n.Key] = $true }
$afterKeys  = @{}; foreach ($n in $nodesAfter)  { $afterKeys[$n.Key]  = $true }
$changedNodes = @()
$changedNodes += $nodesAfter  | Where-Object { -not $beforeKeys.ContainsKey($_.Key) }
$changedNodes += $nodesBefore | Where-Object { -not $afterKeys.ContainsKey($_.Key) }

if ($changedNodes.Count -eq 0) {
    Write-Host "[tap] FAILED: the tap changed nothing in ${ReactionTimeoutSeconds}s." -ForegroundColor Red
    Write-Host '      The control did not react at all, so alignment could not be measured.' -ForegroundColor Red
    Write-Host '      Either the app is wedged, or the tap landed on something inert --' -ForegroundColor Red
    Write-Host "      it was aimed at [$($target.L),$($target.T)][$($target.R),$($target.B)]." -ForegroundColor Red
    Write-Host "      Compare $before against the ui-before.xml bounds to see which." -ForegroundColor Red
    exit 1
}
$nodeCentroid = ($changedNodes | ForEach-Object { ($_.T + $_.B) / 2 } | Measure-Object -Average).Average

# --- render space: which pixels changed, and where --------------------------
if ($fbA.W -ne $fbB.W -or $fbA.H -ne $fbB.H) { throw 'framebuffer sizes differ between the two captures' }

# Skip the status-bar band: its clock ticks on its own and is not the app.
$skipTop = [int]($fbA.H * 0.08)
$sumY = 0.0; $count = 0
$bytesA = $fbA.Bytes; $bytesB = $fbB.Bytes
$offA = $fbA.Offset; $offB = $fbB.Offset
$stride = $fbA.W * 4

for ($y = $skipTop; $y -lt $fbA.H; $y += 2) {
    $rowA = $offA + ($y * $stride)
    $rowB = $offB + ($y * $stride)
    for ($x = 0; $x -lt $fbA.W; $x += 2) {
        $i = $x * 4
        $d = [Math]::Abs($bytesA[$rowA + $i]     - $bytesB[$rowB + $i]) +
             [Math]::Abs($bytesA[$rowA + $i + 1] - $bytesB[$rowB + $i + 1]) +
             [Math]::Abs($bytesA[$rowA + $i + 2] - $bytesB[$rowB + $i + 2])
        if ($d -gt 24) { $sumY += $y; $count++ }
    }
}

if ($count -eq 0) {
    Write-Host '[tap] FAILED: the visual tree changed but not one pixel did.' -ForegroundColor Red
    Write-Host '      The app reacted somewhere that is not on screen at all.' -ForegroundColor Red
    exit 1
}
$pixelCentroid = $sumY / $count

$offset = [Math]::Abs($pixelCentroid - $nodeCentroid)
$dpi = Get-DeviceDensity
$offsetDp = $offset * 160.0 / $dpi
Write-Host ("[tap] hit-test centroid y={0:N0}   render centroid y={1:N0}   offset={2:N0}px = {3:N0}dp at {4}dpi" -f $nodeCentroid, $pixelCentroid, $offset, $offsetDp, $dpi)

if ($offsetDp -gt $MaxOffsetDp) {
    Write-Host ''
    Write-Host ("[tap] FAILED: the app draws {0:N0}px ({1:N0}dp) away from where it is touched." -f $offset, $offsetDp) -ForegroundColor Red
    Write-Host '      The tap reached the control -- the visual tree changed -- but the' -ForegroundColor Red
    Write-Host '      redraw landed somewhere else, so a user has to press that far off' -ForegroundColor Red
    Write-Host '      the target to hit anything. This is the v0.7.0 defect: a top or' -ForegroundColor Red
    Write-Host '      left padding on the Avalonia view moves rendering without moving' -ForegroundColor Red
    Write-Host '      hit-testing. See MainActivity.ImeInsetListener.' -ForegroundColor Red
    Write-Host "      Screenshots: $before  $after" -ForegroundColor Red
    exit 1
}

Write-Host '[tap] OK   the app draws where it is touched.' -ForegroundColor Green
