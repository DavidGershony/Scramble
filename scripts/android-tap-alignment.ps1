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

.PARAMETER MaxOffsetPx
    How far the two centroids may sit apart. Chosen from measurement, not taste:
    on the same emulator and the same screen, the fixed build reports 38px and
    the v0.7.0 build 180px. 90 sits with better than a 2x margin either side.

    The two sets are not expected to coincide exactly even when healthy -- a
    redraw covers slightly different ground than the set of nodes that changed --
    which is why the healthy figure is 38 and not 0.
#>
[CmdletBinding()]
param(
    [string]$PackageId = 'app.scramble.chat',
    [int]$MaxOffsetPx = 90,
    [string]$ArtifactDir = '.'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Get-Adb {
    foreach ($c in @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME, "$env:LOCALAPPDATA\Android\Sdk", 'C:\work\android-sdk')) {
        if ($c -and (Test-Path (Join-Path $c 'platform-tools/adb.exe'))) { return (Join-Path $c 'platform-tools/adb.exe') }
    }
    $p = Get-Command adb -ErrorAction SilentlyContinue
    if ($p) { return $p.Source }
    throw 'adb not found.'
}
$adb = Get-Adb

function Get-Screenshot([string]$path) {
    & $adb exec-out screencap -p > $path
    if (-not (Test-Path $path) -or (Get-Item $path).Length -eq 0) { throw "screencap produced nothing at $path" }
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
Write-Host "[tap] tapping ($cx,$cy)"
& $adb shell input tap $cx $cy | Out-Null
Start-Sleep -Seconds 3
Get-Screenshot $after
$nodesAfter = Get-Nodes (Get-UiTree 'ui-after')

# --- hit-test space: which nodes appeared or vanished, and where ------------
$beforeKeys = @{}; foreach ($n in $nodesBefore) { $beforeKeys[$n.Key] = $true }
$afterKeys  = @{}; foreach ($n in $nodesAfter)  { $afterKeys[$n.Key]  = $true }
$changedNodes = @()
$changedNodes += $nodesAfter  | Where-Object { -not $beforeKeys.ContainsKey($_.Key) }
$changedNodes += $nodesBefore | Where-Object { -not $afterKeys.ContainsKey($_.Key) }

if ($changedNodes.Count -eq 0) {
    Write-Host '[tap] FAILED: the tap changed nothing in the visual tree at all.' -ForegroundColor Red
    Write-Host '      The control did not react, so alignment could not be measured.' -ForegroundColor Red
    exit 1
}
$nodeCentroid = ($changedNodes | ForEach-Object { ($_.T + $_.B) / 2 } | Measure-Object -Average).Average

# --- render space: which pixels changed, and where --------------------------
$bmpA = [System.Drawing.Bitmap]::FromFile((Resolve-Path $before))
$bmpB = [System.Drawing.Bitmap]::FromFile((Resolve-Path $after))
try {
    if ($bmpA.Width -ne $bmpB.Width -or $bmpA.Height -ne $bmpB.Height) { throw 'screenshot sizes differ' }

    # Skip the status-bar band: its clock ticks on its own and is not the app.
    $skipTop = [int]($bmpA.Height * 0.08)
    $sumY = 0.0; $count = 0
    for ($y = $skipTop; $y -lt $bmpA.Height; $y += 2) {
        for ($x = 0; $x -lt $bmpA.Width; $x += 2) {
            $pa = $bmpA.GetPixel($x, $y); $pb = $bmpB.GetPixel($x, $y)
            if ([Math]::Abs($pa.R-$pb.R) + [Math]::Abs($pa.G-$pb.G) + [Math]::Abs($pa.B-$pb.B) -gt 24) {
                $sumY += $y; $count++
            }
        }
    }
} finally { $bmpA.Dispose(); $bmpB.Dispose() }

if ($count -eq 0) {
    Write-Host '[tap] FAILED: the visual tree changed but not one pixel did.' -ForegroundColor Red
    Write-Host '      The app reacted somewhere that is not on screen at all.' -ForegroundColor Red
    exit 1
}
$pixelCentroid = $sumY / $count

$offset = [Math]::Abs($pixelCentroid - $nodeCentroid)
Write-Host ("[tap] hit-test centroid y={0:N0}   render centroid y={1:N0}   offset={2:N0}px" -f $nodeCentroid, $pixelCentroid, $offset)

if ($offset -gt $MaxOffsetPx) {
    Write-Host ''
    Write-Host ("[tap] FAILED: the app draws {0:N0}px away from where it is touched." -f $offset) -ForegroundColor Red
    Write-Host '      The tap reached the control -- the visual tree changed -- but the' -ForegroundColor Red
    Write-Host '      redraw landed somewhere else, so a user has to press that far off' -ForegroundColor Red
    Write-Host '      the target to hit anything. This is the v0.7.0 defect: a top or' -ForegroundColor Red
    Write-Host '      left padding on the Avalonia view moves rendering without moving' -ForegroundColor Red
    Write-Host '      hit-testing. See MainActivity.ImeInsetListener.' -ForegroundColor Red
    Write-Host "      Screenshots: $before  $after" -ForegroundColor Red
    exit 1
}

Write-Host '[tap] OK   the app draws where it is touched.' -ForegroundColor Green
