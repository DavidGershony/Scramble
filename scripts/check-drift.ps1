#!/usr/bin/env pwsh
# S3 (revised): anti-drift gate. Two rules, both bypassable via commit trailer.
#
# Rule L — legacy-Android drift protection.
#   `src/Scramble.Android/**` is the obsolete Views/Fragments head (see
#   `src/Scramble.Android/OBSOLETE.md`). It isn't shipped and isn't compiled
#   by CI. Modifications there almost always indicate a contributor working
#   on the wrong head. Fails a PR that touches any file under that path.
#   Escape trailer: `Legacy-Android-Change: <reason>` (deletion of the folder,
#   final cleanup work, etc.)
#
# Rule M — Mobile-Android shell purity protection.
#   `src/Scramble.Mobile.Android/**` is a platform bootstrapping shell only:
#   activity, lifecycle, permissions, native services, IME insets. UI content
#   lives in `src/Scramble.UI/Views/**` and is multi-targeted onto the mobile
#   head automatically. Adding a *new* `.axaml` view file under
#   `src/Scramble.Mobile.Android/**` is drift — the view content belongs in
#   the shared UI. Modifications to existing view files (MobileMainView.axaml
#   etc.) are fine.
#   Escape trailer: `Mobile-Shell-Exempt: <reason>` (genuinely platform-
#   specific chrome that cannot be authored in the shared UI).
#
# Rationale: ANALYSIS.md STEP 5b (Avalonia-on-Android pivot). The pivot's
# whole point was to collapse two UI implementations into one. Nothing but
# review currently prevents future work from re-introducing per-head
# implementations — this gate makes both directions of drift explicit.
#
# Usage:
#   ./scripts/check-drift.ps1                     # compare HEAD to origin/master
#   ./scripts/check-drift.ps1 -BaseRef HEAD~5     # compare HEAD to HEAD~5
#   ./scripts/check-drift.ps1 -ListPaths          # print the rules
#
# Exit codes:
#   0 no violation (or exempted, or no relevant files touched)
#   1 rule L violation (legacy Android touched)
#   2 rule M violation (new .axaml under Mobile.Android)
#   3 both rules violated

[CmdletBinding()]
param(
    [string]$BaseRef = 'origin/master',
    [string]$HeadRef = 'HEAD',
    [switch]$ListPaths,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$LegacyAndroidPathRe = '^src/Scramble\.Android/'
$MobileAndroidViewRe = '^src/Scramble\.Mobile\.Android/.*\.axaml$'

if ($ListPaths) {
    Write-Host "Rule L (legacy-Android drift, escape=`Legacy-Android-Change:`):"
    Write-Host "  matches: $LegacyAndroidPathRe"
    Write-Host "Rule M (new .axaml in Mobile.Android, escape=`Mobile-Shell-Exempt:`):"
    Write-Host "  matches added files: $MobileAndroidViewRe"
    exit 0
}

# Range resolution — three-dot merge-base, fall back to HEAD~1..HEAD if the
# base ref isn't reachable (shallow clone, first PR on a fresh branch).
$mergeBase = git merge-base $BaseRef $HeadRef 2>$null
if ($LASTEXITCODE -eq 0 -and $mergeBase) {
    $range = "$BaseRef...$HeadRef"
} else {
    Write-Warning "Could not resolve $BaseRef; falling back to HEAD~1..HEAD"
    $range = "HEAD~1..HEAD"
}

$allChanged = @(git diff --name-only $range) | ForEach-Object { $_ -replace '\\','/' }
$addedFiles = @(git diff --diff-filter=A --name-only $range) | ForEach-Object { $_ -replace '\\','/' }

$legacyHits = @($allChanged | Where-Object { $_ -imatch $LegacyAndroidPathRe })
$mobileNewViewHits = @($addedFiles | Where-Object { $_ -imatch $MobileAndroidViewRe })

if ($legacyHits.Count -eq 0 -and $mobileNewViewHits.Count -eq 0) {
    if (-not $Quiet) { Write-Host "check-drift: no anti-drift rules triggered in $range (OK)" }
    exit 0
}

# Collect commit-message trailers in the range.
$commitBodies = git log --format='%B%n---SEP---' "$BaseRef..$HeadRef" 2>$null
if ($LASTEXITCODE -ne 0) {
    $commitBodies = git log --format='%B%n---SEP---' HEAD~1..HEAD
}
$legacyExempt = $commitBodies | Select-String -Pattern '(?im)^\s*Legacy-Android-Change:\s*(\S.*)$' -List
$mobileExempt = $commitBodies | Select-String -Pattern '(?im)^\s*Mobile-Shell-Exempt:\s*(\S.*)$' -List

$exit = 0

if ($legacyHits.Count -gt 0) {
    if ($legacyExempt) {
        if (-not $Quiet) {
            $reason = $legacyExempt[0].Matches[0].Groups[1].Value.Trim()
            Write-Host "check-drift: Rule L touched $($legacyHits.Count) file(s) but exempted by 'Legacy-Android-Change: $reason' (OK)"
        }
    } else {
        Write-Host ""
        Write-Host "check-drift: RULE L VIOLATION — legacy Android touched" -ForegroundColor Red
        Write-Host ""
        Write-Host "  src/Scramble.Android/** is obsolete (see src/Scramble.Android/OBSOLETE.md)." -ForegroundColor Yellow
        Write-Host "  You changed:" -ForegroundColor Yellow
        $legacyHits | ForEach-Object { Write-Host "    $_" }
        Write-Host ""
        Write-Host "  The current Android target is src/Scramble.Mobile.Android (Avalonia head)." -ForegroundColor Yellow
        Write-Host "  UI content lives in src/Scramble.UI/Views/** and multi-targets onto mobile automatically." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  If this change is intentional (removal, final cleanup), add to any commit:" -ForegroundColor Cyan
        Write-Host "    Legacy-Android-Change: <one-line reason>" -ForegroundColor Cyan
        Write-Host ""
        $exit = 1
    }
}

if ($mobileNewViewHits.Count -gt 0) {
    if ($mobileExempt) {
        if (-not $Quiet) {
            $reason = $mobileExempt[0].Matches[0].Groups[1].Value.Trim()
            Write-Host "check-drift: Rule M added $($mobileNewViewHits.Count) new mobile view(s) but exempted by 'Mobile-Shell-Exempt: $reason' (OK)"
        }
    } else {
        Write-Host ""
        Write-Host "check-drift: RULE M VIOLATION — new .axaml under Mobile.Android" -ForegroundColor Red
        Write-Host ""
        Write-Host "  You added:" -ForegroundColor Yellow
        $mobileNewViewHits | ForEach-Object { Write-Host "    $_" }
        Write-Host ""
        Write-Host "  Mobile.Android is a platform bootstrapping shell — activity, permissions," -ForegroundColor Yellow
        Write-Host "  IME insets, native services. UI content belongs in src/Scramble.UI/Views/**" -ForegroundColor Yellow
        Write-Host "  and multi-targets onto mobile automatically." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  If this view is genuinely platform-specific chrome that cannot be authored" -ForegroundColor Cyan
        Write-Host "  in the shared UI, add to any commit:" -ForegroundColor Cyan
        Write-Host "    Mobile-Shell-Exempt: <one-line reason>" -ForegroundColor Cyan
        Write-Host ""
        $exit = if ($exit -eq 1) { 3 } else { 2 }
    }
}

exit $exit
