# Implement MIP-00 last_resort KeyPackage lifecycle

## Status: Partially Complete

### What was implemented (2026-05-20)
- **24 h init_key retention** (MIP-00 §"Deletion Timing"): `StoredKeyPackageMaterial` now carries a `ConsumedAt` timestamp. When `ProcessWelcomeAsync` matches a last-resort KP the material is kept but stamped instead of silently retained forever. `PruneExpiredConsumedKeyPackages()` zeroizes material once 24 h have elapsed; called on state restore, before generating a new KP, and before processing Welcomes.
- **Service state v5**: serialization bumped to v5 to persist `ConsumedAt` per KP; v4 state is migrated on load (ConsumedAt = null for all KPs).

### Still needed

## Problem

Our KeyPackages include the `last_resort` extension (`0x000a`) in the `mls_extensions` tag as required by MIP-00, but `ProcessWelcomeAsync` discards the init key private material after a single use. This violates the `last_resort` semantics — the init key should be retained so multiple senders can use the same KP to invite us.

This caused the stuck-invite bug: sender A and sender B both fetch the same KP from relays, sender A's Welcome is accepted and consumes the KP, sender B's Welcome fails permanently.

## What MIP-00 requires

- All Marmot KeyPackages are `last_resort` (0x000a MUST be in mls_extensions)
- The init key MUST be retained after processing a Welcome (not discarded)
- After a successful Welcome, the client SHOULD rotate: publish a new KP, then delete old key material only after the new KP is confirmed on relays (or after a 24-hour grace window)
- Clients MUST NOT rotate if Welcome processing fails

## What White Noise does

- Publishes ONE KeyPackage per account
- Background maintenance every 10 minutes: auto-rotate if expired (>30 days) or consumed
- After Welcome consumes a KP, waits 30s "quiet period" before deleting key material (handles burst of multiple senders using same KP)
- Tracks lifecycle in DB: `published_key_packages(account_pubkey, key_package_hash_ref, event_id, consumed_at, key_material_deleted, created_at)`

## Changes needed

### 1. Don't discard init key on Welcome processing
- `ManagedMlsService.ProcessWelcomeAsync` currently removes the matched KP from `_storedKeyPackages` after use
- Instead: mark it as consumed but retain the init/HPKE private keys
- Allow subsequent Welcomes to use the same KP

### 2. Track KP lifecycle in DB
- New table: `published_key_packages(id, account_pubkey, key_package_hash_ref, event_id, consumed_at, key_material_deleted, created_at)`
- Record when a KP is published, consumed, and when key material is deleted

### 3. Background KP rotation
- After a Welcome is successfully processed, schedule rotation
- Publish a new KP to relays
- Delete old KP key material only after new KP confirmed on relays
- Timer-based: check every N minutes if rotation is needed

### 4. Migrate to kind 30443 (addressable events)
- MIP-00 now uses kind 30443 with a `d` tag for slot-based rotation
- Publishing a new KP with the same `d` tag replaces the old one — no NIP-09 deletion needed
- During migration: dual-publish kind 443 + kind 30443, prefer 30443 when fetching
- Planned cutover: 2026-05-01

## Files to modify
- `src/OpenChat.Core/Services/ManagedMlsService.cs` — ProcessWelcomeAsync, key retention
- `src/OpenChat.Core/Services/IStorageService.cs` / `StorageService.cs` — new KP lifecycle table
- `src/OpenChat.Core/Services/MessageService.cs` — background rotation trigger
- `src/OpenChat.Core/Services/NostrService.cs` — kind 30443 publishing, `d` tag support
