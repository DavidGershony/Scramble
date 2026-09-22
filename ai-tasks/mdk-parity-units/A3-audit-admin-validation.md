# A3 — AUDIT: admin-list validation current state

**Type:** AUDIT (read + written report, **no code changes**) · **Size:** S ·
**Depends-on:** none · Unblocks: admin-validation implementation units

## Why

Rust MDK 0.8.0 tightened admin handling: prune non-member admins (#223), strip
admin atomically on member removal (#225), reject receiver-side commits that
would leave zero active admins (#256), and reject SelfRemove that would deplete
admins (#236). Before implementing these we need a map of what Scramble/marmot-cs
does today. **This unit only reads and reports.**

## What to read

- `src/Scramble.Core/Services/MessageService.cs` — everywhere `admin` /
  `admin_pubkeys` / `AdminPubkeys` appears (search both). Note how admins are
  extracted from the `0xF2EE` extension and whether anything is validated.
- `lib/marmot-cs/src/MarmotCs.Core/Mdk.cs` — commit/GCE processing; any admin
  authorization gate on inbound commits.
- `lib/marmot-cs/src/MarmotCs.Protocol/Mip01/NostrGroupDataExtension.cs` —
  `AdminPubkeys` shape.
- `lib/dotnet-mls/src/DotnetMls/Group/MlsGroup.cs` — `ProcessCommitCore` and
  whether it validates committer authorization at all.

## Questions to answer (be precise; quote file:line)

1. When a Commit that modifies group membership or `admin_pubkeys` arrives, is
   the **committer verified to be an admin**? Where, or is it absent?
2. Is there **any** admin-depletion check (reject an operation that would leave
   zero active admins)? On send? On receive?
3. When a member is removed, is their key **stripped from `admin_pubkeys`** in
   the same operation, or can a removed member linger in the admin list?
4. Are **non-member entries pruned** from `admin_pubkeys` before validation, or
   would a stale/non-member admin key break "active admin" counting?
5. How is "active admin" even computed today (cross-reference `admin_pubkeys`
   against actual current members)? Does that helper exist?

## Deliverable (the report)

Per question: answer + `file:line` + "present / partial / missing". End with a
**"What must be built"** list, split by layer (Scramble vs marmot-cs vs
dotnet-mls), so the orchestrator can write targeted implementation units.

## Scope guards

- **No code changes.** Reading and reporting only.

## Report back

Post the full report. Do not commit.
