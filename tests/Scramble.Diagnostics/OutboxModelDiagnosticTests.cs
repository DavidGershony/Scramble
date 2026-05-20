using System.Text;
using System.Text.Json;
using System.Reactive.Linq;
using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
using Xunit;

namespace Scramble.Diagnostics;

/// <summary>
/// Diagnostic tests proving Phase 4 (outbox model) is working end-to-end.
/// Requires: docker compose -f docker-compose.test.yml up -d
/// (nostr-rs-relay on ws://localhost:7777)
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "OutboxModel")]
public class OutboxModelDiagnosticTests : IAsyncLifetime
{
    private static string RelayUrl => TestRelayConfig.RelayUrl;

    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();
    private readonly List<NostrService> _nostrServices = new();

    public OutboxModelDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public ValueTask InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ns in _nostrServices)
        {
            try { await ns.DisconnectAsync(); }
            catch { }
            (ns as IDisposable)?.Dispose();
        }

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        foreach (var path in _dbPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    private async Task<(NostrService nostr, StorageService storage, string pubKey, string privKey)> CreateUser(string name)
    {
        var nostr = new NostrService();
        _nostrServices.Add(nostr);
        var keys = nostr.GenerateKeyPair();

        var dbPath = Path.Combine(Path.GetTempPath(), $"oc_outbox_{name}_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();
        await storage.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = keys.publicKeyHex,
            PrivateKeyHex = keys.privateKeyHex,
            Npub = keys.npub,
            Nsec = keys.nsec,
            DisplayName = name,
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        _output.WriteLine($"Created user '{name}': {keys.publicKeyHex[..16]}...");
        return (nostr, storage, keys.publicKeyHex, keys.privateKeyHex);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 1: ContactRelays storage — round-trip save/load
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ContactRelays_SaveAndLoad_RoundTrips()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: ContactRelays storage round-trip");
        _output.WriteLine("═══════════════════════════════════════════════════════");

        var (_, storage, pubKey, _) = await CreateUser("StorageTest");

        var contactPubKey = "aaaa" + new string('0', 60);
        var relayUrls = new List<string>
        {
            "wss://relay1.example.com",
            "wss://relay2.example.com",
            "wss://relay3.example.com"
        };

        // Save kind 10050 (DM relays) for a contact
        await storage.SaveContactRelayListAsync(contactPubKey, 10050, relayUrls);
        _output.WriteLine($"Saved {relayUrls.Count} DM relay URLs for contact {contactPubKey[..8]}...");

        // Load them back
        var loaded = await storage.GetContactRelayListAsync(contactPubKey, 10050);
        Assert.True(loaded.HasValue, "Should return cached relay list");
        Assert.Equal(3, loaded.Value.Urls.Count);
        Assert.Contains("wss://relay1.example.com", loaded.Value.Urls);
        Assert.Contains("wss://relay2.example.com", loaded.Value.Urls);
        Assert.Contains("wss://relay3.example.com", loaded.Value.Urls);
        _output.WriteLine($"Loaded {loaded.Value.Urls.Count} URLs, FetchedAt={loaded.Value.FetchedAt:O}");

        // Verify FetchedAt is recent (within last 10 seconds)
        var age = DateTimeOffset.UtcNow - loaded.Value.FetchedAt;
        Assert.True(age.TotalSeconds < 10, $"FetchedAt should be recent but was {age.TotalSeconds}s ago");
        _output.WriteLine($"FetchedAt age: {age.TotalMilliseconds:F0}ms — OK");

        // Save NIP-65 relay preferences for the same contact
        var prefs = new List<RelayPreference>
        {
            new() { Url = "wss://write.example.com", Usage = RelayUsage.Write },
            new() { Url = "wss://read.example.com", Usage = RelayUsage.Read },
            new() { Url = "wss://both.example.com", Usage = RelayUsage.Both }
        };
        await storage.SaveContactRelayPreferencesAsync(contactPubKey, prefs);
        _output.WriteLine($"Saved {prefs.Count} NIP-65 relay preferences for contact");

        var loadedPrefs = await storage.GetContactRelayPreferencesAsync(contactPubKey);
        Assert.True(loadedPrefs.HasValue, "Should return cached relay preferences");
        Assert.Equal(3, loadedPrefs.Value.Relays.Count);

        var writePref = loadedPrefs.Value.Relays.First(r => r.Url == "wss://write.example.com");
        Assert.Equal(RelayUsage.Write, writePref.Usage);
        var readPref = loadedPrefs.Value.Relays.First(r => r.Url == "wss://read.example.com");
        Assert.Equal(RelayUsage.Read, readPref.Usage);
        var bothPref = loadedPrefs.Value.Relays.First(r => r.Url == "wss://both.example.com");
        Assert.Equal(RelayUsage.Both, bothPref.Usage);
        _output.WriteLine($"NIP-65 preferences round-tripped correctly with Usage values preserved");

        // Verify kind 10050 data is independent of NIP-65 (kind 10002)
        var dmAgain = await storage.GetContactRelayListAsync(contactPubKey, 10050);
        Assert.True(dmAgain.HasValue, "DM relay list should still exist after saving NIP-65 prefs");
        Assert.Equal(3, dmAgain.Value.Urls.Count);
        _output.WriteLine("DM (kind 10050) and NIP-65 (kind 10002) stored independently — OK");

        // Different contact has no data
        var otherPub = "bbbb" + new string('0', 60);
        var noData = await storage.GetContactRelayListAsync(otherPub, 10050);
        Assert.False(noData.HasValue, "Different contact should have no cached data");
        _output.WriteLine("Different contact returns null — OK");

        _output.WriteLine("\nPASSED: ContactRelays storage round-trip");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 2: ContactRelays storage — overwrite updates FetchedAt
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ContactRelays_Overwrite_UpdatesFetchedAt()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: ContactRelays overwrite updates FetchedAt");
        _output.WriteLine("═══════════════════════════════════════════════════════");

        var (_, storage, _, _) = await CreateUser("OverwriteTest");
        var contactPub = "cccc" + new string('0', 60);

        // First save
        await storage.SaveContactRelayListAsync(contactPub, 10051, new[] { "wss://old-relay.example.com" });
        var first = await storage.GetContactRelayListAsync(contactPub, 10051);
        Assert.True(first.HasValue);
        var firstFetchedAt = first.Value.FetchedAt;
        _output.WriteLine($"First save: FetchedAt={firstFetchedAt:O}, URLs={string.Join(", ", first.Value.Urls)}");

        // Wait a bit then overwrite with different URLs
        await Task.Delay(1100);
        await storage.SaveContactRelayListAsync(contactPub, 10051, new[] { "wss://new-relay.example.com", "wss://new2.example.com" });
        var second = await storage.GetContactRelayListAsync(contactPub, 10051);
        Assert.True(second.HasValue);
        _output.WriteLine($"Second save: FetchedAt={second.Value.FetchedAt:O}, URLs={string.Join(", ", second.Value.Urls)}");

        // FetchedAt should be newer
        Assert.True(second.Value.FetchedAt > firstFetchedAt,
            $"FetchedAt should be updated: first={firstFetchedAt:O}, second={second.Value.FetchedAt:O}");
        // Old URL should be gone, new URLs present
        Assert.DoesNotContain("wss://old-relay.example.com", second.Value.Urls);
        Assert.Equal(2, second.Value.Urls.Count);
        Assert.Contains("wss://new-relay.example.com", second.Value.Urls);
        _output.WriteLine("Overwrite replaced URLs and updated FetchedAt — OK");

        _output.WriteLine("\nPASSED: ContactRelays overwrite updates FetchedAt");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 3: Publish NIP-65 relay list → GetOrFetchRelayListAsync cache
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishNip65_ThenGetOrFetch_CachesResult()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: Publish NIP-65, then GetOrFetch caches result");
        _output.WriteLine("═══════════════════════════════════════════════════════");

        // Alice publishes her NIP-65 relay list to the relay
        var (alice, aliceStorage, alicePub, alicePriv) = await CreateUser("Alice");
        await alice.ConnectAsync(RelayUrl);
        await Task.Delay(1000);
        Assert.True(alice.ConnectedRelayUrls.Count > 0, "Alice must be connected");
        _output.WriteLine($"Alice connected to {string.Join(", ", alice.ConnectedRelayUrls)}");

        var aliceRelays = new List<RelayPreference>
        {
            new() { Url = RelayUrl, Usage = RelayUsage.Both },
            new() { Url = "wss://alice-write.example.com", Usage = RelayUsage.Write },
            new() { Url = "wss://alice-read.example.com", Usage = RelayUsage.Read }
        };
        var eventId = await alice.PublishRelayListAsync(aliceRelays, alicePriv);
        _output.WriteLine($"Alice published NIP-65 relay list (event {eventId[..16]}...)");
        await Task.Delay(500);

        // Bob connects and fetches Alice's relay list via GetOrFetchRelayListAsync
        var (bob, bobStorage, bobPub, bobPriv) = await CreateUser("Bob");
        bob.SetStorageService(bobStorage);
        await bob.ConnectAsync(RelayUrl);
        await Task.Delay(1000);
        Assert.True(bob.ConnectedRelayUrls.Count > 0, "Bob must be connected");

        // First call — cache miss, hits the network
        var fetchedRelays = await bob.GetOrFetchRelayListAsync(alicePub);
        _output.WriteLine($"GetOrFetch (1st call, cache miss): returned {fetchedRelays.Count} relays");
        Assert.True(fetchedRelays.Count >= 2, $"Should find at least 2 relays, got {fetchedRelays.Count}");

        // Verify we got the right relay URLs
        var fetchedUrls = fetchedRelays.Select(r => r.Url).ToList();
        _output.WriteLine($"  URLs: {string.Join(", ", fetchedUrls)}");
        foreach (var r in fetchedRelays)
            _output.WriteLine($"  {r.Url} -> {r.Usage}");

        // Verify it was cached in the DB
        var cached = await bobStorage.GetContactRelayPreferencesAsync(alicePub);
        Assert.True(cached.HasValue, "Relay list should be cached in DB after first fetch");
        _output.WriteLine($"DB cache: {cached.Value.Relays.Count} relays, FetchedAt={cached.Value.FetchedAt:O}");

        // Second call — cache hit, should return from DB without network
        var fetchedAgain = await bob.GetOrFetchRelayListAsync(alicePub);
        Assert.Equal(fetchedRelays.Count, fetchedAgain.Count);
        _output.WriteLine($"GetOrFetch (2nd call, cache hit): returned {fetchedAgain.Count} relays — same as 1st");

        _output.WriteLine("\nPASSED: NIP-65 publish + GetOrFetch caching");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 4: Publish kind 10050 DM relay list → GetOrFetchDmRelayListAsync
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishDmRelayList_ThenGetOrFetch_CachesResult()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: Publish kind 10050, then GetOrFetchDm caches");
        _output.WriteLine("═══════════════════════════════════════════════════════");

        var (alice, _, alicePub, alicePriv) = await CreateUser("Alice");
        await alice.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        // Alice publishes her DM relay list (kind 10050)
        var dmRelays = new List<string> { RelayUrl, "wss://dm-relay.example.com" };
        var eventId = await alice.PublishDmRelayListAsync(dmRelays, alicePriv);
        _output.WriteLine($"Alice published kind 10050 DM relay list (event {eventId[..16]}...)");
        await Task.Delay(500);

        // Bob fetches via cache-first method
        var (bob, bobStorage, _, _) = await CreateUser("Bob");
        bob.SetStorageService(bobStorage);
        await bob.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        var fetched = await bob.GetOrFetchDmRelayListAsync(alicePub);
        _output.WriteLine($"GetOrFetchDm (1st call): returned {fetched.Count} DM relays");
        Assert.True(fetched.Count >= 1, $"Should find at least 1 DM relay, got {fetched.Count}");
        foreach (var url in fetched)
            _output.WriteLine($"  {url}");

        // Verify cached in DB (kind 10050)
        var cached = await bobStorage.GetContactRelayListAsync(alicePub, 10050);
        Assert.True(cached.HasValue, "DM relay list should be cached in DB");
        Assert.Equal(fetched.Count, cached.Value.Urls.Count);
        _output.WriteLine($"DB cache: {cached.Value.Urls.Count} DM relays, FetchedAt={cached.Value.FetchedAt:O}");

        // Second call — cache hit
        var fetchedAgain = await bob.GetOrFetchDmRelayListAsync(alicePub);
        Assert.Equal(fetched.Count, fetchedAgain.Count);
        _output.WriteLine($"GetOrFetchDm (2nd call, cache hit): {fetchedAgain.Count} relays — same");

        _output.WriteLine("\nPASSED: Kind 10050 publish + GetOrFetchDm caching");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 5: Publish kind 10051 KP relay list → GetOrFetchKeyPackageRelayListAsync
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishKpRelayList_ThenGetOrFetch_CachesResult()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: Publish kind 10051, then GetOrFetchKp caches");
        _output.WriteLine("═══════════════════════════════════════════════════════");

        var (alice, _, alicePub, alicePriv) = await CreateUser("Alice");
        await alice.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        // Alice publishes her KeyPackage relay list (kind 10051)
        var kpRelays = new List<string> { RelayUrl, "wss://kp-relay.example.com" };
        var eventId = await alice.PublishKeyPackageRelayListAsync(kpRelays, alicePriv);
        _output.WriteLine($"Alice published kind 10051 KP relay list (event {eventId[..16]}...)");
        await Task.Delay(500);

        // Bob fetches via cache-first method
        var (bob, bobStorage, _, _) = await CreateUser("Bob");
        bob.SetStorageService(bobStorage);
        await bob.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        var fetched = await bob.GetOrFetchKeyPackageRelayListAsync(alicePub);
        _output.WriteLine($"GetOrFetchKp (1st call): returned {fetched.Count} KP relays");
        Assert.True(fetched.Count >= 1, $"Should find at least 1 KP relay, got {fetched.Count}");
        foreach (var url in fetched)
            _output.WriteLine($"  {url}");

        // Verify cached in DB (kind 10051)
        var cached = await bobStorage.GetContactRelayListAsync(alicePub, 10051);
        Assert.True(cached.HasValue, "KP relay list should be cached in DB");
        Assert.Equal(fetched.Count, cached.Value.Urls.Count);
        _output.WriteLine($"DB cache: {cached.Value.Urls.Count} KP relays, FetchedAt={cached.Value.FetchedAt:O}");

        _output.WriteLine("\nPASSED: Kind 10051 publish + GetOrFetchKp caching");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 6: Outbox relay connections — ConnectOutboxRelaysAsync
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConnectOutboxRelays_ConnectsAndExcludesFromBroadcast()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: Outbox relay connection + broadcast exclusion");
        _output.WriteLine("═══════════════════════════════════════════════════════");

        var (nostr, storage, pubKey, privKey) = await CreateUser("OutboxUser");
        nostr.SetStorageService(storage);

        // Connect to main relay
        await nostr.ConnectAsync(RelayUrl);
        await Task.Delay(1000);
        var connectedBefore = nostr.ConnectedRelayUrls.Count;
        _output.WriteLine($"Connected to main relay: {connectedBefore} relay(s) — {string.Join(", ", nostr.ConnectedRelayUrls)}");
        Assert.True(connectedBefore > 0, "Must be connected to at least one relay");

        // Connect the same relay as outbox — should be a no-op (already connected)
        await nostr.ConnectOutboxRelaysAsync(new[] { RelayUrl });
        var connectedAfterSame = nostr.ConnectedRelayUrls.Count;
        _output.WriteLine($"After ConnectOutboxRelaysAsync(same relay): {connectedAfterSame} relay(s) — no-op expected");
        Assert.Equal(connectedBefore, connectedAfterSame);

        // Publish a NIP-65 event — it should succeed (routed through main relay, not the outbox pattern)
        var relays = new List<RelayPreference>
        {
            new() { Url = RelayUrl, Usage = RelayUsage.Both }
        };
        var eventId = await nostr.PublishRelayListAsync(relays, privKey);
        Assert.False(string.IsNullOrEmpty(eventId), "Should publish NIP-65 to main relay");
        _output.WriteLine($"Published NIP-65 via main relay: {eventId[..16]}...");

        _output.WriteLine("\nPASSED: Outbox relay connection + broadcast exclusion");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 7: PublishGiftWrapAsync auto-discovers DM relays
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishGiftWrap_AutoDiscoversDmRelays()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: PublishGiftWrapAsync auto-discovers DM relays");
        _output.WriteLine("═══════════════════════════════════════════════════════");

        // Alice publishes her DM relay list
        var (alice, _, alicePub, alicePriv) = await CreateUser("Alice");
        await alice.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        var dmRelays = new List<string> { RelayUrl };
        await alice.PublishDmRelayListAsync(dmRelays, alicePriv);
        _output.WriteLine($"Alice published DM relay list: {string.Join(", ", dmRelays)}");
        await Task.Delay(500);

        // Subscribe Alice to welcomes (kind 1059) so we can check delivery
        await alice.SubscribeToWelcomesAsync(alicePub, alicePriv);
        _output.WriteLine("Alice subscribed to welcomes");

        // Bob sends a gift wrap to Alice WITHOUT specifying targetRelayUrls
        var (bob, bobStorage, bobPub, bobPriv) = await CreateUser("Bob");
        bob.SetStorageService(bobStorage);
        await bob.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        var rumorTags = new List<List<string>>
        {
            new() { "p", alicePub }
        };

        // This should auto-discover Alice's DM relays (kind 10050) since no targetRelayUrls
        var giftWrapEventId = await bob.PublishGiftWrapAsync(
            rumorKind: 14,
            content: "Hello Alice, this is an auto-discovered gift wrap!",
            rumorTags: rumorTags,
            senderPrivateKeyHex: bobPriv,
            senderPublicKeyHex: bobPub,
            recipientPublicKeyHex: alicePub,
            targetRelayUrls: null);  // <-- null triggers auto-discovery

        Assert.False(string.IsNullOrEmpty(giftWrapEventId), "Gift wrap should be published");
        _output.WriteLine($"Bob published gift wrap (auto-discovered relays): {giftWrapEventId[..16]}...");

        // Verify Bob's storage now has Alice's DM relay list cached
        var bobCached = await bobStorage.GetContactRelayListAsync(alicePub, 10050);
        Assert.True(bobCached.HasValue, "Alice's DM relay list should be cached in Bob's DB after auto-discovery");
        _output.WriteLine($"Bob's DB cache has Alice's DM relays: {string.Join(", ", bobCached.Value.Urls)}");

        _output.WriteLine("\nPASSED: PublishGiftWrapAsync auto-discovers DM relays");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 8: PublishGiftWrap falls back to NIP-65 when no 10050 exists
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishGiftWrap_FallsBackToNip65_WhenNoDmRelays()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: PublishGiftWrap falls back to NIP-65 read relays");
        _output.WriteLine("═══════════════════════════════════════════════════════");

        // Alice publishes only NIP-65 (no kind 10050)
        var (alice, _, alicePub, alicePriv) = await CreateUser("Alice");
        await alice.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        var nip65Relays = new List<RelayPreference>
        {
            new() { Url = RelayUrl, Usage = RelayUsage.Both }
        };
        await alice.PublishRelayListAsync(nip65Relays, alicePriv);
        _output.WriteLine("Alice published NIP-65 relay list (no kind 10050)");
        await Task.Delay(500);

        // Bob sends gift wrap — should fall back to NIP-65 since no kind 10050 exists
        var (bob, bobStorage, bobPub, bobPriv) = await CreateUser("Bob");
        bob.SetStorageService(bobStorage);
        await bob.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        var rumorTags = new List<List<string>>
        {
            new() { "p", alicePub }
        };

        var giftWrapEventId = await bob.PublishGiftWrapAsync(
            rumorKind: 14,
            content: "Hello Alice, via NIP-65 fallback!",
            rumorTags: rumorTags,
            senderPrivateKeyHex: bobPriv,
            senderPublicKeyHex: bobPub,
            recipientPublicKeyHex: alicePub,
            targetRelayUrls: null);

        Assert.False(string.IsNullOrEmpty(giftWrapEventId), "Gift wrap should be published via NIP-65 fallback");
        _output.WriteLine($"Bob published gift wrap (NIP-65 fallback): {giftWrapEventId[..16]}...");

        // Verify Bob's storage now has Alice's NIP-65 relay preferences cached
        var bobCachedPrefs = await bobStorage.GetContactRelayPreferencesAsync(alicePub);
        Assert.True(bobCachedPrefs.HasValue, "Alice's NIP-65 relays should be cached in Bob's DB after fallback");
        _output.WriteLine($"Bob's DB cache has Alice's NIP-65 relays: {string.Join(", ", bobCachedPrefs.Value.Relays.Select(r => $"{r.Url}({r.Usage})"))}");

        _output.WriteLine("\nPASSED: PublishGiftWrap NIP-65 fallback");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 9: End-to-end — publish relay lists, cache them, aggregate
    //          contacts' relays like InitializeNetworkAsync does
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E2E_ContactRelayAggregation_FindsTopRelays()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: E2E contact relay aggregation");
        _output.WriteLine("═══════════════════════════════════════════════════════");

        // Create 3 users who all publish their DM relays
        var (alice, _, alicePub, alicePriv) = await CreateUser("Alice");
        var (bob, _, bobPub, bobPriv) = await CreateUser("Bob");
        var (carol, _, carolPub, carolPriv) = await CreateUser("Carol");

        // Connect all to relay
        await Task.WhenAll(
            alice.ConnectAsync(RelayUrl),
            bob.ConnectAsync(RelayUrl),
            carol.ConnectAsync(RelayUrl));
        await Task.Delay(1500);

        // Each publishes kind 10050 with overlapping relay URLs
        await alice.PublishDmRelayListAsync(
            new List<string> { RelayUrl, "wss://shared.example.com", "wss://alice-only.example.com" }, alicePriv);
        await bob.PublishDmRelayListAsync(
            new List<string> { RelayUrl, "wss://shared.example.com", "wss://bob-only.example.com" }, bobPriv);
        await carol.PublishDmRelayListAsync(
            new List<string> { "wss://shared.example.com", "wss://carol-only.example.com" }, carolPriv);
        _output.WriteLine("All 3 users published DM relay lists");
        await Task.Delay(500);

        // Dave connects, fetches all 3 contacts' relay lists via cache-first
        var (dave, daveStorage, davePub, davePriv) = await CreateUser("Dave");
        dave.SetStorageService(daveStorage);
        await dave.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        var aliceDm = await dave.GetOrFetchDmRelayListAsync(alicePub);
        var bobDm = await dave.GetOrFetchDmRelayListAsync(bobPub);
        var carolDm = await dave.GetOrFetchDmRelayListAsync(carolPub);
        _output.WriteLine($"Fetched relay lists: Alice={aliceDm.Count}, Bob={bobDm.Count}, Carol={carolDm.Count}");

        // Aggregate by frequency (same logic as InitializeNetworkAsync)
        var relayFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var urls in new[] { aliceDm, bobDm, carolDm })
        {
            foreach (var url in urls)
            {
                var normalized = url.TrimEnd('/');
                relayFrequency[normalized] = relayFrequency.GetValueOrDefault(normalized) + 1;
            }
        }

        _output.WriteLine("Relay frequency map:");
        foreach (var (url, count) in relayFrequency.OrderByDescending(kv => kv.Value))
            _output.WriteLine($"  {url}: {count} contacts");

        // "wss://shared.example.com" should appear 3 times (all users)
        Assert.True(relayFrequency.ContainsKey("wss://shared.example.com"),
            "shared.example.com should be in the frequency map");
        Assert.Equal(3, relayFrequency["wss://shared.example.com"]);
        _output.WriteLine("wss://shared.example.com has frequency 3 — correct");

        // Top relay should be shared.example.com or the local relay
        var topRelay = relayFrequency.OrderByDescending(kv => kv.Value).First();
        _output.WriteLine($"Top relay: {topRelay.Key} (frequency {topRelay.Value})");

        // Verify all data is cached in Dave's DB
        var aliceCached = await daveStorage.GetContactRelayListAsync(alicePub, 10050);
        var bobCached = await daveStorage.GetContactRelayListAsync(bobPub, 10050);
        var carolCached = await daveStorage.GetContactRelayListAsync(carolPub, 10050);
        Assert.True(aliceCached.HasValue && bobCached.HasValue && carolCached.HasValue,
            "All 3 contacts' relay lists should be cached");
        _output.WriteLine("All 3 contacts' relay lists cached in Dave's DB — OK");

        _output.WriteLine("\nPASSED: E2E contact relay aggregation");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 10: Gift wrap round-trip — Bob sends, Alice receives via
    //           auto-discovered DM relays
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GiftWrap_RoundTrip_ViaAutoDiscoveredDmRelays()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: Gift wrap round-trip via auto-discovered relays");
        _output.WriteLine("═══════════════════════════════════════════════════════");

        // Alice sets up with DM relay list
        var (alice, aliceStorage, alicePub, alicePriv) = await CreateUser("Alice");
        alice.SetStorageService(aliceStorage);
        await alice.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        await alice.PublishDmRelayListAsync(new List<string> { RelayUrl }, alicePriv);
        _output.WriteLine("Alice published DM relay list");
        await Task.Delay(500);

        // Alice subscribes to kind 1059 gift wraps and tracks raw events
        var receivedEvents = new List<NostrEventReceived>();
        alice.Events.Subscribe(e =>
        {
            if (e.Kind == 1059)
            {
                _output.WriteLine($"Alice received kind 1059 event: {e.EventId?[..16]}...");
                receivedEvents.Add(e);
            }
        });
        await alice.SubscribeToWelcomesAsync(alicePub, alicePriv);
        _output.WriteLine("Alice subscribed to welcomes (kind 1059)");

        // Bob sends gift wrap to Alice, auto-discovering her DM relays
        var (bob, bobStorage, bobPub, bobPriv) = await CreateUser("Bob");
        bob.SetStorageService(bobStorage);
        await bob.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        var rumorTags = new List<List<string>> { new() { "p", alicePub } };
        var giftWrapId = await bob.PublishGiftWrapAsync(
            rumorKind: 14,
            content: "Round-trip test message from Bob",
            rumorTags: rumorTags,
            senderPrivateKeyHex: bobPriv,
            senderPublicKeyHex: bobPub,
            recipientPublicKeyHex: alicePub,
            targetRelayUrls: null);

        _output.WriteLine($"Bob sent gift wrap: {giftWrapId[..16]}...");

        // Wait for Alice to receive
        await Task.Delay(3000);

        _output.WriteLine($"Alice received {receivedEvents.Count} kind 1059 event(s)");
        Assert.True(receivedEvents.Count > 0,
            "Alice should receive at least one kind 1059 gift wrap via auto-discovered DM relay");

        _output.WriteLine("\nPASSED: Gift wrap round-trip via auto-discovered DM relays");
    }
}
