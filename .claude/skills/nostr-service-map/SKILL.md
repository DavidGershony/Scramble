---
name: nostr-service-map
description: Reference map of Scramble.Core.Services.NostrService — public-API surface grouped by capability, observables, threading model, event-kind handling, signer integration, and gotchas mined from recent fix commits. Use when planning or implementing changes to NostrService.cs / INostrService.cs, or to any code that consumes them (subscriptions, publishing, fetching, NIP-46 signer flows, event-kind additions). Lets the agent plan without reading all 3,664 lines of NostrService.cs.
---

# NostrService — agent reference map

**Last verified:** 2026-06-09 against commit `20f2c97`
**Sources:**
- `src/Scramble.Core/Services/INostrService.cs` (493 lines — public contract + DTOs)
- `src/Scramble.Core/Services/NostrService.cs` (3,664 lines — implementation)

> If the touched file has changed materially since `20f2c97`, treat this map as stale until updated. The PostToolUse hook reminds you of this whenever you edit either file.

---

## 1. Purpose & scope

`NostrService` owns **all Nostr protocol I/O**: relay WebSocket connections, subscription/event routing, signing/publishing, NIP-46 external-signer integration, and protocol-specific helpers (NIP-44 encrypt, NIP-59 gift wrap, NIP-65/17/MIP-02 relay lists, KeyPackages, Welcomes, MLS group messages).

**Not in scope** (lives elsewhere):
- MLS state, KeyPackage private-key persistence → `ManagedMlsService`
- Chat / message orchestration on top of Nostr events → `MessageService`
- Profile metadata caching at the UI layer → `MessageService.GetCachedOrFetchProfileAsync`
- User/group/contact persistence → `StorageService`

The service is a singleton per logged-in user. `Dispose` tears down all relay connections and subjects (NostrService.cs:3629).

---

## 2. Public API map (by capability)

All references are to `NostrService.cs` unless noted as `INostrService.cs`.

### Connection lifecycle

| Method | Line | Notes |
|---|---|---|
| `ConnectAsync(string)` | 353 | Single relay. |
| `ConnectAsync(IEnumerable<string>)` | 358 | Bulk; uses `Task.WhenAll`. |
| `ConnectBotRelaysAsync` | 371 | Bot-only — DM/welcome subs only, **no** group/KP broadcast. |
| `ConnectOutboxRelaysAsync` | 389 | Contacts' outbox relays — same exclusion as bot. |
| `DisconnectAsync` | 1282 | Tears down all. |
| `ReconnectRelayAsync(string)` | 1323 | Force a single relay back online. |
| `DisconnectRelayAsync(string)` | 1330 | Remove a relay. |
| `ConnectedRelayUrls` / `ConfiguredRelayUrls` | 138 / 139 | Snapshot reads of `ConcurrentDictionary` keys. |

Internal helper: `ConnectToRelayAsync(url, retryAttempt)` at line 407 owns retry/backoff. It dedupes against `_relayConnections`, validates URLs (rejects private IPs unless `ProfileConfiguration.AllowLocalRelays`), wires `connection.Messages → ProcessRelayMessageAsync`, and resets `_relayConsecutiveFailures` on success. WS `429` triggers `RateLimitBackoffSeconds = 60` (line 131); initial-connect backoff caps at `MaxInitialConnectBackoffSeconds = 60` (line 132).

### Key management & auth

| Method | Line | Notes |
|---|---|---|
| `GenerateKeyPair()` | 1366 | Returns `(privHex, pubHex, nsec, npub)`. |
| `ImportPrivateKey(string)` | 1400 | Accepts nsec or hex. |
| `SetAuthCredentials(string?)` | 246 | NIP-42 AUTH; call **before** `ConnectAsync` so challenges can be answered. |
| `NpubToHex(string)` | 3219 | Bech32 decode. |
| `SetExternalSigner(IExternalSigner?)` | 163 | Wires NIP-46. Triggers `ProcessPendingGiftWrapsAsync` if signer is connected and buffer is non-empty. |
| `SetNip46ProofOfWorkDifficulty(int)` | 175 | Forwards to signer. |
| `HasExternalSigner` | 244 | Check before publish if no local priv key. |

### Subscriptions

| Method | Line | Notes |
|---|---|---|
| `SubscribeAsync(subId, NostrFilter)` | 1452 | Generic. **TODO** at 1454: not yet integrated with Blockcore client. |
| `UnsubscribeAsync(subId)` | 1459 | |
| `SubscribeToWelcomesAsync(pub, priv?)` | 1469 | Wires kind 1059 gift-wrap routing for Welcomes. |
| `SubscribeToGroupMessagesAsync(groupIds, since?)` | 1501 | Kind 445 by `h`-tag. Tracks `_subscribedGroupIds`. |

The "register active subscriptions on new connection" logic re-fires every active filter on a newly-connected relay — see `RegisterActiveSubscriptionsAsync` called at line 502 from `ConnectToRelayAsync`.

### Publishing

| Method | Line | Kind | Notes |
|---|---|---|---|
| `PublishKeyPackageAsync` | 1562 | 30443 | Optional MDK tags (MIP-00). |
| `PublishWelcomeAsync` | 1607 | 444 → wrapped in 1059 | Requires `keyPackageEventId` (MIP-02). Discovers recipient's kind 10050 DM relays first, falls back to NIP-65 read. Excludes bot-only relays. |
| `PublishGiftWrapAsync` | 1745 | rumor → 13 → 1059 | Generic NIP-59. `targetRelayUrls` lets you scope; null = broadcast. |
| `PublishDeletionAsync` | 1885 | 5 | NIP-09. |
| `PublishCommitAsync` | 2175 | 445 | MIP-03 commit/evolution. Publish **before** Welcome. |
| `PublishGroupMessageAsync` | 2200 | 445 | MIP-03 — `h`-tag + encoding tags. |
| `PublishRawEventJsonAsync` | 2218 | * | Pre-signed bytes; returns extracted event ID. |
| `PublishMetadataAsync` | 3053 | 0 | NIP-01 profile. |
| `PublishRelayListAsync` | 3072 | 10002 | NIP-65 write; content empty. |
| `PublishDmRelayListAsync` | 3094 | 10050 | NIP-17 — `["relay", url]` tags. |
| `PublishKeyPackageRelayListAsync` | 3108 | 10051 | Same tag format as 10050. |

After publish: `WaitForRelayOkAsync(eventId, timeoutMs = 5000)` at line 2292 awaits OK from at least one relay. Returns `(accepted, reason?)`. Internal `PublishOkTracker` (line 40) tracks per-event acceptance — wired via `_pendingOkTrackers`.

### Fetching (one-shot)

| Method | Line | Notes |
|---|---|---|
| `FetchKeyPackagesAsync(pub)` | 2316 | Default limit 5 — inviter path. |
| `FetchKeyPackagesAsync(pub, limit)` | 2319 | Audit path passes 100 to surface stale slots. Prefers target user's kind 10051 relays. |
| `FetchWelcomeEventsAsync(pub, priv?)` | 2496 | Inner kind 444, limit 50 per relay. |
| `FetchNip17DmHistoryAsync(pub, priv?)` | 2583 | Inner kind 14, limit 200. Used on login to restore bot/agent chats. Caller must dedupe by `NostrEventId`. |
| `FetchGroupHistoryAsync(groupIdHex, since, until, limit=50)` | 2618 | Time-windowed kind 445. |
| `FetchUserMetadataAsync(pub)` | 2693 | Kind 0. |
| `FetchRelayListAsync(pub)` | 2859 | NIP-65 kind 10002. |
| `FetchFollowingListAsync(pub)` | 2954 | NIP-02 kind 3 — most recent only (line 2992). |
| `FetchDmRelayListAsync(pub)` | 3122 | Kind 10050. |
| `FetchKeyPackageRelayListAsync(pub)` | 3130 | Kind 10051. |

Phase-1 refactor: every ephemeral fetch routes through internal `QueryRelayAsync` (`917a7ac`).

### Cache-first fetching (relay lists)

| Method | Line | Backed by |
|---|---|---|
| `GetOrFetchRelayListAsync` | 262 | NIP-65 (10002), 30-min TTL (`RelayListCacheTtl` line 122). |
| `GetOrFetchDmRelayListAsync` | 300 | 10050. |
| `GetOrFetchKeyPackageRelayListAsync` | 306 | 10051. |
| Shared helper | 312 | Cache miss falls back to `Fetch*Async` and re-caches via `_storageService`. |

`SetStorageService(IStorageService)` at line 252 wires the persistent cache. Without it, every `GetOrFetch*` is just a `Fetch*`.

### Crypto helpers

| Method | Line | Notes |
|---|---|---|
| `Nip44EncryptAsync` | 3248 | Async — delegates to external signer when present, else local key. |
| `Nip44DecryptAsync` | 3270 | Same routing. |

---

## 3. Observables / streams

All five subjects are `System.Reactive.Subject<T>` (NostrService.cs:27-31), exposed via `AsObservable()`:

| Stream | Type | Emits when | Threading |
|---|---|---|---|
| `ConnectionStatus` | `NostrConnectionStatus` | Connect succeeds/fails, relay disconnects, validation rejects, removal. | Whatever thread `NostrRelayConnection` is on; consumers must `ObserveOn(MainThreadScheduler)` for UI. |
| `Events` | `NostrEventReceived` | Every received Nostr event after dedup (incl. unwrapped kind 14 / 444 from gift wraps). | Thread pool (relay message handler). |
| `WelcomeMessages` | `MarmotWelcomeEvent` | After successful gift-wrap unwrap → kind 444. | Thread pool. |
| `GroupMessages` | `MarmotGroupMessageEvent` | After kind 445 receipt + `h`-tag validation. | Thread pool. |
| `SyncStatus` | `string?` | Sync banner messages ("Syncing N message(s)…"); `null` clears. | Mixed. Consumers in `MainViewModel.cs:495` use `ObserveOn(RxSchedulers.MainThreadScheduler)`. |

None of the subjects complete (`OnCompleted` is never called); `Dispose` disposes the subject machinery. Subjects throw on subscribe-after-dispose.

---

## 4. Threading model

The service is **thread-safe by design** — every shared mutable bit is a `ConcurrentDictionary`/`ConcurrentQueue` (lines 32-37, 102-105, 111-118, 125, 148):

- `_relayConnections`, `_connectedRelays`, `_subscriptions`, `_relayMessageSubscriptions`, `_subscribedGroupIds`, `_recentlyProcessedEventIds`
- `_botOnlyRelays`, `_outboxRelays`
- `_failedGiftWrapEventIds` (gift-wrap circuit breaker, cap 3 — line 112)
- `_relayConsecutiveFailures` (outbox circuit breaker, cap 10 — line 118)
- `_relayEventRates` (per-relay sliding-window rate limit, cap 500/min — line 126)
- `_pendingGiftWraps` (cap 5,000 — line 149)

**ANR / UI-thread rules** (from recent fix commits — `5e67996`, `7d0c2c1`, `93b3c49`):
- `ProcessPendingGiftWrapsAsync` (line 187) explicitly `Task.Run`s itself because it's invoked from `SetExternalSigner` which can run on the UI thread.
- `93b3c49` removed redundant `Task.Run` wrappers from internal async methods — **do not re-add** them speculatively; only wrap when something CPU-bound or signer-blocking might run on the UI thread.
- The drain loop yields with `Task.Delay(50)` every 5 events (line 235) to avoid monopolizing thread-pool threads.

---

## 5. Event-kind handling

Kinds the service knows about — when adding support for a new kind, copy the closest match in this list.

| Kind | Direction | Where handled |
|---|---|---|
| **0** (metadata) | publish | `PublishMetadataAsync` 3053 |
| **0** (metadata) | fetch | `FetchUserMetadataAsync` 2693 |
| **3** (contacts) | fetch | `FetchFollowingListAsync` 2954 (line 2992: pick most recent) |
| **5** (deletion, NIP-09) | publish | `PublishDeletionAsync` 1885 |
| **13** (seal) | internal | `PublishGiftWrapAsync` 1745 (line 1938: `Kind = 13`) |
| **14** (NIP-17 DM rumor) | publish | via `PublishGiftWrapAsync` |
| **14** (NIP-17 DM rumor) | fetch | `FetchNip17DmHistoryAsync` 2583 / drained from `_pendingGiftWraps` |
| **22242** (NIP-42 AUTH) | publish | line 873 |
| **444** (Welcome rumor) | publish | `PublishWelcomeAsync` 1607 (wrapped in 1059) |
| **444** (Welcome rumor) | fetch | `FetchWelcomeEventsAsync` 2496 |
| **445** (group/commit) | publish | `PublishGroupMessageAsync` 2200, `PublishCommitAsync` 2175 |
| **445** (group/commit) | subscribe | `SubscribeToGroupMessagesAsync` 1501; received → `_groupMessages` |
| **1059** (gift wrap) | subscribe | inside `SubscribeToWelcomesAsync` 1469; received → unwrap → `_events` / `_welcomeMessages` |
| **10002** (NIP-65 relay list) | publish | `PublishRelayListAsync` 3072 |
| **10002** | fetch | `FetchRelayListAsync` 2859 |
| **10050** (NIP-17 DM relays) | publish | `PublishDmRelayListAsync` 3094 |
| **10050** | fetch | `FetchDmRelayListAsync` 3122 |
| **10051** (KeyPackage relays) | publish | `PublishKeyPackageRelayListAsync` 3108 |
| **10051** | fetch | `FetchKeyPackageRelayListAsync` 3130 |
| **30443** (KeyPackage) | publish | `PublishKeyPackageAsync` 1562 |
| **30443** | fetch | `FetchKeyPackagesAsync` 2316 / 2319 |

`ProcessRelayMessageAsync` is the central inbound router — see how it's wired from `connection.Messages.Subscribe(...)` at line 457.

---

## 6. Gotchas & invariants

Distilled from `git log -- NostrService.cs` (most recent first) and in-code comments. Each carries a SHA so you can `git show <SHA>` for the full diff.

- **External signer can connect *after* gift wraps arrive (`c6f20e5`, `5b1004f`).** Kind 1059 events that arrive before NIP-46 is ready buffer into `_pendingGiftWraps` (cap 5,000, line 149). `SetExternalSigner` (line 163) drains them via `ProcessPendingGiftWrapsAsync` (line 187). Don't drop the buffer; don't make it synchronous.

- **Permanently undecryptable gift wraps form a circuit-breaker burn (`30fa93c`).** `_failedGiftWrapEventIds` caps retries at 3 (line 111-112). Don't reset this on transient errors — it's specifically for events that will never decrypt.

- **Outbox relays can be unreachable indefinitely (`30fa93c`).** `_relayConsecutiveFailures` caps at 10 (line 117-118); after that, retries pause until next app restart. New retry/backoff logic must respect this counter (reset is at line 499, on successful connection).

- **MLS state import must be transactional (`c6f20e5`, `414cf3f`).** When restoring service state across logins, the importer must wait for the external signer before treating welcomes. This shapes the `SetExternalSigner` flow.

- **Signer disconnect must not block (`5d29b6c`).** Auto-reconnect was previously gated on `IsConnected` — removed because it deadlocked when the signer briefly dropped. Don't re-add `IsConnected` guards around publish-path signer access; check `HasExternalSigner` instead.

- **BIP-340 signature verification is required (`a633a06`).** Schnorr-sig validation is on the inbound path; don't bypass it for "trusted" relays. KeyPackage audit also depends on it.

- **MIP-02: every Welcome must reference its KeyPackage event ID (`INostrService.cs:159`, code at 1607).** The `keyPackageEventId` param is mandatory — the test framework's old `null` was the bug fixed in commit `1f4242b`.

- **MIP-00: 24h last-resort `init_key` retention (`7717f4e`).** KeyPackage rotation must keep the previous `init_key` around for 24h so in-flight Welcomes can still be unwrapped.

- **Per-relay rate limit: 500 events/min (line 126-127).** A burst over this triggers the per-relay backoff. Don't disable for tests — set up a fresh relay (`docker-compose.test.yml`) instead.

- **WS `429` → 60s backoff (line 131).** `ConnectToRelayAsync` catches `WebSocketException` containing `"429"` at line 504 and re-schedules with `RateLimitBackoffSeconds`. A new "retry-after" handler must coexist with this.

- **Bot-only and outbox relays are excluded from group/KP broadcasts (lines 101-105 + `IsBotOnlyRelay` 129).** Any new broadcast helper must check both sets if it's meant to skip them.

- **Gift wraps and replaceable events skip timestamp dedup (line 1052-1053).** Kind 1059 has randomized timestamps by NIP-59 design; kinds 0/3/10002 are historical. The `isGiftWrap` and `isReplaceableEvent` params on the internal dedup helper at line 1233-1234 enforce this — preserve them.

- **Audit fetch limit is intentionally 100 (`2aeec83`, `INostrService.cs:194-200`).** The audit flow surfaces stale slots from before the rotation fix and across multi-device. Inviter flows still use the 5-default overload — don't unify them.

- **Phase 1 refactor — all ephemeral fetches route through `QueryRelayAsync` (`917a7ac`).** New one-shot fetch methods should follow that pattern, not open ad-hoc connections.

---

## 7. How to extend

### Add a new Nostr event kind

1. Decide direction: publish-only, fetch-only, or subscribe.
2. Find the closest existing kind in section 5 and copy its method shape.
3. Add the method to `INostrService.cs` first (gives the agent a planning checklist).
4. For inbound: route from `ProcessRelayMessageAsync` (called at line 461) — emit on `_events` and (if a typed subject is justified) add a new `Subject<T>`.
5. For publishing: pick local-key vs. signer path based on `HasExternalSigner`. Wait for relay OK via `WaitForRelayOkAsync` only if the caller will surface the result.

### Add a new fetcher

- Route through the same internal `QueryRelayAsync` pattern used by `FetchKeyPackagesAsync` etc. — preserves rate-limit and circuit-breaker accounting.
- Prefer the user's purpose-specific relay list (kind 10050/10051) when one is meaningful; fall back to NIP-65 read relays.

### Add a new subscription

- Register the filter via `SubscribeAsync(subId, NostrFilter)`.
- If it needs to persist across reconnects, add it to the active-subscription replay path called at line 502 (`RegisterActiveSubscriptionsAsync`).

### Add a new relay-list kind

- Mirror 10050/10051: publish helper using `["relay", url]` tags, fetch helper, and a `GetOrFetch*Async` cache-first wrapper backed by `_storageService`. Pick a reasonable TTL (current is 30 min, line 122).

---

## 8. Source-of-truth pointer index

When in doubt, read these chunks before reasoning:

- **Subjects + field declarations:** `NostrService.cs:26-149`
- **External signer wiring:** `163-242`
- **Storage / cache:** `252-348`
- **Connection lifecycle:** `353-1300`
  - `ConnectToRelayAsync` (the meat): `407-560`
- **Subscribe paths:** `1452-1560`
- **Publish paths:** `1562-2300`
- **Fetch paths:** `2316-3140`
- **Crypto helpers:** `3219-3290`
- **Dispose:** `3629+`
- **Public DTOs:** `INostrService.cs:334-493`
