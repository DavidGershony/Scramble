#!/usr/bin/env pwsh
# PostToolUse hook: when the agent edits NostrService.cs or INostrService.cs,
# emit a reminder to stderr (which Claude Code surfaces back into the session)
# telling the agent to refresh the nostr-service-map skill before finishing.
#
# Wired in .claude/settings.local.json:
#   "PostToolUse" matcher "Edit|Write|MultiEdit" runs this script for every edit;
#   the script returns instantly for paths it doesn't care about.

$ErrorActionPreference = 'Stop'

# stdin is a JSON object with the hook payload. Tool input differs per tool but
# all of Edit/Write/MultiEdit carry a file_path under tool_input.
$raw = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

try {
    $payload = $raw | ConvertFrom-Json -ErrorAction Stop
} catch {
    # Bad JSON — be silent, never block a real edit because we couldn't parse.
    exit 0
}

$filePath = $payload.tool_input.file_path
if ([string]::IsNullOrWhiteSpace($filePath)) { exit 0 }

# Match either NostrService.cs or INostrService.cs anywhere in the path.
# Normalize separators so this works on Windows and *nix.
$normalized = $filePath -replace '\\', '/'
$isNostrFile =
    $normalized -match '/Scramble\.Core/Services/(I?NostrService\.cs)$'

if (-not $isNostrFile) { exit 0 }

# Don't remind when the agent is editing the skill itself.
if ($normalized -match '/nostr-service-map/SKILL\.md$') { exit 0 }

# Best-effort current SHA for the refresh instruction.
$sha = try { (& git rev-parse --short HEAD 2>$null).Trim() } catch { '<run: git rev-parse --short HEAD>' }
if ([string]::IsNullOrWhiteSpace($sha)) { $sha = '<run: git rev-parse --short HEAD>' }

$msg = @"
[nostr-service-map] You just edited a NostrService source file.

Before finishing this task you MUST:
  1. Review .claude/skills/nostr-service-map/SKILL.md and update any sections
     affected by your change (API map line numbers, observables, threading,
     event-kind table, gotchas — whichever applies).
  2. Bump the "Last verified" header to:  $(Get-Date -Format 'yyyy-MM-dd') against commit $sha
  3. If your change introduces a new gotcha that isn't obvious from the type
     signature, add a bullet to section 6 with a one-line "why".

Skipping this leaves the map stale and misleads future agents.
"@

# Writing to stderr surfaces the message back to Claude as a system-reminder.
[Console]::Error.WriteLine($msg)
exit 0
