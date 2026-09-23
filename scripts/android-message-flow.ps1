<#
.SYNOPSIS
    Drives the shipped Android head through create-identity, open-chat and
    send-message, and asserts the message comes back rendered.

.DESCRIPTION
    I5's freeze-exit condition asks for a scripted create-group / send-message
    pass, and until this existed nothing had ever exercised a protocol path on a
    device. android-smoke.ps1 proves the app starts; android-tap-alignment.ps1
    proves it reacts where it draws. Neither touches the engine.

    What this covers, end to end on the device:

      * a keypair generated on-device and stored through the Keystore-backed
        ISecureStorage
      * DarkMatterMlsServiceFactory.Create -- which FAILS CLOSED when that
        storage is missing, so reaching a chat list at all proves the real
        thing was wired, not a fallback
      * an MLS group (the self-chat) with a message encrypted, persisted and
        rendered back

    **Everything is located by role, never by coordinate.** Avalonia exposes its
    visual tree to uiautomator, so the tab strip is "three Buttons sharing a top
    edge", the chat row is "the first ListBoxItem", and the send button is "the
    Button to the right of the message TextBox". Coordinates are read from those
    nodes at the moment of use.

    That last point is not fastidiousness. Typing raises the keyboard, which
    moves the send button: bounds captured before typing put the tap into the
    keyboard, and the message sits unsent in the box while every "did it work"
    check looks fine. Re-read after every state change.

.PARAMETER Message
    Text to send. Defaults to a timestamped string so a rerun cannot pass on a
    message left by the previous one.
#>
[CmdletBinding()]
param(
    [string]$PackageId = 'app.scramble.chat',
    [string]$Message = ('smoke' + (Get-Date -Format 'HHmmssfff')),
    [int]$StepTimeoutSeconds = 45,
    [string]$ArtifactDir = '.'
)
$ErrorActionPreference = 'Stop'

function Get-Adb {
    $onPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $roots = @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME, "$env:LOCALAPPDATA\Android\Sdk", 'C:\work\android-sdk') |
        Where-Object { $_ }
    foreach ($root in $roots) {
        foreach ($exe in @('platform-tools/adb', 'platform-tools/adb.exe')) {
            try { $c = Join-Path $root $exe } catch { continue }
            if (Test-Path $c) { return $c }
        }
    }
    throw 'adb not found. Put it on PATH or set ANDROID_SDK_ROOT.'
}
$adb = Get-Adb

$dumpIndex = 0
function Get-Tree {
    $script:dumpIndex++
    $name = 'flow-{0:d2}' -f $script:dumpIndex
    & $adb shell uiautomator dump /sdcard/$name.xml 2>&1 | Out-Null
    $local = Join-Path $ArtifactDir "$name.xml"
    & $adb pull /sdcard/$name.xml $local 2>&1 | Out-Null
    # An empty result rather than a throw: uiautomator legitimately has nothing
    # to dump while a screen is still coming up ("null root node returned"), and
    # every caller is inside a Wait-For that will simply ask again. Throwing here
    # turned "not ready yet" into a hard failure on the very first poll.
    if (-not (Test-Path $local)) { return '' }
    return (Get-Content $local -Raw)
}

function Get-Nodes([string]$xml, [string]$class) {
    $pattern = '<node[^>]*class="' + [regex]::Escape($class) + '"[^>]*bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"'
    [regex]::Matches($xml, $pattern) | ForEach-Object {
        [pscustomobject]@{
            L = [int]$_.Groups[1].Value; T = [int]$_.Groups[2].Value
            R = [int]$_.Groups[3].Value; B = [int]$_.Groups[4].Value
            CX = [int](([int]$_.Groups[1].Value + [int]$_.Groups[3].Value) / 2)
            CY = [int](([int]$_.Groups[2].Value + [int]$_.Groups[4].Value) / 2)
        }
    }
}

# The three login tabs, identified as Buttons sharing a top edge.
function Get-TabStrip($buttons) {
    $g = $buttons | Group-Object T | Where-Object { $_.Count -ge 3 } | Select-Object -First 1
    if ($g) { return ($g.Group | Sort-Object L) }
    return $null
}

# The widest Button that is not one of the tabs: "Generate New Identity" on one
# screen, "Continue with this identity" on the next.
function Get-PrimaryButton([string]$xml) {
    $buttons = @(Get-Nodes $xml 'Button')
    if ($buttons.Count -eq 0) { return $null }
    $strip = Get-TabStrip $buttons
    $stripKeys = @{}
    if ($strip) { foreach ($b in $strip) { $stripKeys["$($b.L),$($b.T)"] = $true } }
    $buttons | Where-Object { -not $stripKeys.ContainsKey("$($_.L),$($_.T)") } |
        Sort-Object { $_.R - $_.L } -Descending | Select-Object -First 1
}

function Wait-For([scriptblock]$condition, [string]$what) {
    $deadline = (Get-Date).AddSeconds($StepTimeoutSeconds)
    do {
        $xml = Get-Tree
        $r = & $condition $xml
        if ($r) { return $r }
        Start-Sleep -Milliseconds 1500
    } while ((Get-Date) -lt $deadline)
    & $adb exec-out screencap -p > (Join-Path $ArtifactDir 'flow-failure.png')
    throw "timed out after ${StepTimeoutSeconds}s waiting for $what (see flow-failure.png)"
}

<#
.SYNOPSIS
    The display size in effect, honouring any override.
#>
function Get-ScreenSize {
    $out = (& $adb shell wm size 2>$null | Out-String)
    $m = [regex]::Match($out, 'Override size:\s*(\d+)x(\d+)')
    if (-not $m.Success) { $m = [regex]::Match($out, 'Physical size:\s*(\d+)x(\d+)') }
    if (-not $m.Success) { throw "could not read the display size from: $out" }
    return [pscustomobject]@{ W = [int]$m.Groups[1].Value; H = [int]$m.Groups[2].Value }
}
$screen = Get-ScreenSize
Write-Host "[flow] screen $($screen.W)x$($screen.H)"

<#
.SYNOPSIS
    Scrolls the current view up by roughly half a screen.
.DESCRIPTION
    Needed because a control can be present in the visual tree and still be off
    the bottom of the display: uiautomator reports its layout bounds, not its
    visibility. On a 320x640 screen the generated-identity card overflows and
    "Continue with this identity" sits below the fold, so tapping its reported
    centre lands on the very edge of the screen and does nothing. On a
    1080x2424 screen the same card fits and the fault is invisible -- which is
    why this was found by running at CI's resolution rather than mine.
#>
function Scroll-Down {
    $x = [int]($screen.W / 2)
    & $adb shell input swipe $x ([int]($screen.H * 0.72)) $x ([int]($screen.H * 0.28)) 400 | Out-Null
    Start-Sleep -Milliseconds 1200
}

<#
.SYNOPSIS
    Waits for a control, scrolling when it is present but out of view.
#>
function Wait-ForVisible([scriptblock]$condition, [string]$what) {
    $deadline = (Get-Date).AddSeconds($StepTimeoutSeconds)
    do {
        $xml = Get-Tree
        $node = & $condition $xml
        if ($node) {
            # Strictly inside, with a margin. A control that runs off the bottom
            # has its bounds CLIPPED to the display, so it reports B exactly
            # equal to the screen height -- and a naive "B <= H" then calls it
            # visible, taps the very edge, and hits nothing. That is precisely
            # how this failed at 320x640.
            if ($node.B -le ($screen.H - 8)) { return $node }
            Scroll-Down          # present in the tree, but below the fold
            continue
        }
        Start-Sleep -Milliseconds 1500
    } while ((Get-Date) -lt $deadline)
    & $adb exec-out screencap -p > (Join-Path $ArtifactDir 'flow-failure.png')
    throw "timed out after ${StepTimeoutSeconds}s waiting for $what to be visible (see flow-failure.png)"
}

function Tap($node, [string]$what) {
    Write-Host "[flow] tap $what at ($($node.CX),$($node.CY))"
    & $adb shell input tap $node.CX $node.CY | Out-Null
}


<#
.SYNOPSIS
    Find a control, tap it, and confirm the tap actually did something --
    retrying the tap if it did not.
.DESCRIPTION
    Injected taps get lost. It happened twice while building this: once on an
    API 34 emulator where no tap ever registered, and once here, where the
    "New Key" tab tap vanished and the run then waited 45s for a panel that was
    never going to appear, reporting the panel as missing rather than the tap as
    lost.

    Tapping and waiting for the NEXT state, with a retry, makes a dropped event
    a non-event. A control that genuinely does nothing still fails, after being
    asked three times, and the message says which tap it was.
#>
function Step([scriptblock]$find, [scriptblock]$confirm, [string]$what, [int]$attempts = 3) {
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        $node = Wait-ForVisible $find $what
        Tap $node $what
        $deadline = (Get-Date).AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 1200
            $result = & $confirm (Get-Tree)
            if ($result) { return $result }
        } while ((Get-Date) -lt $deadline)
        Write-Host "[flow] '$what' had no effect (attempt $attempt) -- tapping again" -ForegroundColor Yellow
    }
    & $adb exec-out screencap -p > (Join-Path $ArtifactDir 'flow-failure.png')
    throw "'$what' never took effect after $attempts attempts (see flow-failure.png)"
}

Write-Host "[flow] message for this run: $Message"

& $adb shell am force-stop $PackageId | Out-Null
& $adb shell pm clear $PackageId 2>&1 | Out-Null      # a known state, every run
& $adb logcat -c 2>&1 | Out-Null
$launcher = (& $adb shell "cmd package resolve-activity --brief -c android.intent.category.LAUNCHER $PackageId" 2>$null |
             Out-String).Trim() -split "`r?`n" | Where-Object { $_ -match "^$([regex]::Escape($PackageId))/" } | Select-Object -First 1
if (-not $launcher) { throw "could not resolve a LAUNCHER activity for $PackageId" }
& $adb shell am start -n $launcher | Out-Null

# ── 1. a new identity, generated on the device ──────────────────────────────
# Each step names what it taps and what proves the tap landed. The generate
# panel is identified by having no TextBox (the Import panel it replaces has
# one); the generated identity by having two (npub and nsec, read-only).
$null = Step `
    { param($xml) $strip = Get-TabStrip @(Get-Nodes $xml 'Button'); if ($strip) { $strip[1] } else { $null } } `
    { param($xml) @(Get-Nodes $xml 'Button').Count -ge 3 -and @(Get-Nodes $xml 'TextBox').Count -eq 0 } `
    'the New Key tab'

$null = Step `
    { param($xml) if (@(Get-Nodes $xml 'TextBox').Count -eq 0) { Get-PrimaryButton $xml } else { $null } } `
    { param($xml) @(Get-Nodes $xml 'TextBox').Count -ge 2 } `
    'Generate New Identity'

$row = Step `
    { param($xml) if (@(Get-Nodes $xml 'TextBox').Count -ge 2) { Get-PrimaryButton $xml } else { $null } } `
    { param($xml) @(Get-Nodes $xml 'ListBoxItem') | Select-Object -First 1 } `
    'Continue with this identity'

# ── 2. the chat list means the engine came up ───────────────────────────────
Write-Host '[flow] OK   reached the chat list -- identity stored and the MLS service constructed'

$null = Step `
    { param($xml) @(Get-Nodes $xml 'ListBoxItem') | Select-Object -First 1 } `
    { param($xml) $xml -match 'content-desc="Type a message' } `
    'the first chat'

# ── 3. send a message ───────────────────────────────────────────────────────
$box = Wait-ForVisible { param($xml) @(Get-Nodes $xml 'TextBox') | Select-Object -First 1 } 'the message box'
Tap $box 'the message box'
Start-Sleep -Seconds 2
& $adb shell input text $Message | Out-Null
Start-Sleep -Seconds 1

# Re-read AFTER typing: the keyboard has moved the send button. Bounds captured
# before this point put the tap into the keyboard, and the message then sits
# unsent in the box while nothing reports an error.
$null = Step `
    { param($xml)
      $b = @(Get-Nodes $xml 'TextBox') | Select-Object -First 1
      if (-not $b) { return $null }
      @(Get-Nodes $xml 'Button') | Where-Object { $_.L -ge $b.R -and $_.T -lt $b.B -and $_.B -gt $b.T } | Select-Object -First 1 } `
    { param($xml) $xml -match [regex]::Escape($Message) } `
    'send'

# ── 4. it has to come back rendered ─────────────────────────────────────────
& $adb exec-out screencap -p > (Join-Path $ArtifactDir 'flow-sent.png')

$logcat = (& $adb logcat -d 2>$null | Out-String)
$refusals = $logcat -split "`r?`n" | Select-String -Pattern 'MlsIngestRefusedException|PublishUnconfirmedException|InvalidOperationException'
if ($refusals) {
    Write-Host '[flow] FAILED: the message rendered, but the engine logged a refusal:' -ForegroundColor Red
    foreach ($r in ($refusals | Select-Object -First 8)) { Write-Host "    $r" -ForegroundColor Red }
    exit 1
}

Write-Host ''
Write-Host '[flow] OK   identity created, chat opened, message sent and rendered.' -ForegroundColor Green
