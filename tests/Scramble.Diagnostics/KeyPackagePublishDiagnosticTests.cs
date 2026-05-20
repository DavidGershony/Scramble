using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Scramble.Core.Configuration;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
using Xunit;
namespace Scramble.Diagnostics;

/// <summary>
/// Diagnostic test that publishes a kind 30443 KeyPackage event to real relays
/// and verifies they accept and return it. Uses a dedicated test-only nsec
/// to avoid polluting relays with random keys.
/// </summary>
[Trait("Category", "Integration")]
public class KeyPackagePublishDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    // Dedicated test-only key — NOT a real user. Generated solely for this test.
    // npub: npub1testkey... — has no followers, no profile, no funds.
    private const string TestPrivateKeyHex = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";

    private static readonly string[] TestRelays = TestRelayConfig.RelayUrls;

    public KeyPackagePublishDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
        ProfileConfiguration.SetAllowLocalRelays(true);
    }

    [Fact]
    public async Task PublishKeyPackage_Kind30443_AcceptedByRelays()
    {
        // --- Setup: real NostrService + real MLS service ---
        var nostrService = new NostrService();
        var pubKeyBytes = DerivePublicKeyFromPrivate(TestPrivateKeyHex);
        var pubKeyHex = Convert.ToHexString(pubKeyBytes).ToLowerInvariant();
        _output.WriteLine($"Test pubkey: {pubKeyHex}");

        var dbPath = Path.Combine(Path.GetTempPath(), $"kp_diag_{Guid.NewGuid()}.db");
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var mlsService = new ManagedMlsService(storage);
        await mlsService.InitializeAsync(TestPrivateKeyHex, pubKeyHex);

        // --- Step 1: Connect to relays ---
        _output.WriteLine("\n--- Connecting to relays ---");
        foreach (var relay in TestRelays)
        {
            try
            {
                await nostrService.ConnectAsync(relay);
                _output.WriteLine($"  Connected: {relay}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  FAILED to connect: {relay} — {ex.Message}");
            }
        }

        var connected = nostrService.ConnectedRelayUrls;
        _output.WriteLine($"\nConnected to {connected.Count} relays: {string.Join(", ", connected)}");
        Assert.True(connected.Count > 0, "Must connect to at least one relay");

        // Give relays a moment to fully establish
        await Task.Delay(2000);

        // --- Step 2: Generate a KeyPackage ---
        _output.WriteLine("\n--- Generating KeyPackage ---");
        var keyPackage = await mlsService.GenerateKeyPackageAsync();
        _output.WriteLine($"  Data: {keyPackage.Data.Length} bytes");
        _output.WriteLine($"  Ciphersuite: 0x{keyPackage.CiphersuiteId:x4}");
        _output.WriteLine($"  Tags ({keyPackage.NostrTags.Count}):");
        foreach (var tag in keyPackage.NostrTags)
            _output.WriteLine($"    [{string.Join(", ", tag)}]");

        // Verify d-tag is present (required for kind 30443)
        var dTag = keyPackage.NostrTags.FirstOrDefault(t => t.Count >= 2 && t[0] == "d");
        Assert.NotNull(dTag);
        _output.WriteLine($"  d-tag slot: {dTag[1]}");

        // --- Step 3: Publish to relays ---
        _output.WriteLine("\n--- Publishing KeyPackage (kind 30443) ---");
        string? eventId = null;
        Exception? publishError = null;

        try
        {
            eventId = await nostrService.PublishKeyPackageAsync(
                keyPackage.Data, TestPrivateKeyHex, keyPackage.NostrTags);
            _output.WriteLine($"  Published! Event ID: {eventId}");
        }
        catch (Exception ex)
        {
            publishError = ex;
            _output.WriteLine($"  PUBLISH FAILED: {ex.Message}");
        }

        Assert.Null(publishError);
        Assert.NotNull(eventId);

        // --- Step 4: Fetch back from each relay individually ---
        _output.WriteLine("\n--- Fetching KeyPackage back from relays ---");
        var foundOnRelays = new List<string>();

        foreach (var relay in TestRelays)
        {
            var found = await FetchKind30443FromRelay(relay, pubKeyHex);
            if (found.Count > 0)
            {
                foundOnRelays.Add(relay);
                _output.WriteLine($"  {relay}: FOUND {found.Count} KeyPackage(s)");
                foreach (var ev in found)
                {
                    AnalyzeEvent(ev);
                }
            }
            else
            {
                _output.WriteLine($"  {relay}: NOT FOUND");
            }
        }

        _output.WriteLine($"\n--- Summary ---");
        _output.WriteLine($"  Published event: {eventId}");
        _output.WriteLine($"  Found on {foundOnRelays.Count}/{TestRelays.Length} relays: {string.Join(", ", foundOnRelays)}");

        Assert.True(foundOnRelays.Count > 0,
            $"KeyPackage event {eventId} was not found on any relay after publishing. " +
            "Relays may be rejecting kind 30443 events.");

        // --- Step 5: Also test via NostrService.FetchKeyPackagesAsync ---
        _output.WriteLine("\n--- Fetching via NostrService.FetchKeyPackagesAsync ---");
        var fetchedPackages = (await nostrService.FetchKeyPackagesAsync(pubKeyHex)).ToList();
        _output.WriteLine($"  Found {fetchedPackages.Count} KeyPackage(s) via service");
        foreach (var pkg in fetchedPackages)
        {
            _output.WriteLine($"    EventId: {pkg.NostrEventId?[..Math.Min(16, pkg.NostrEventId?.Length ?? 0)]}...");
            _output.WriteLine($"    Data: {pkg.Data.Length} bytes, Ciphersuite: 0x{pkg.CiphersuiteId:x4}");
            _output.WriteLine($"    Relays: {string.Join(", ", pkg.RelayUrls)}");
        }

        Assert.True(fetchedPackages.Count > 0,
            "FetchKeyPackagesAsync returned no results despite relay having the event");

        // --- Cleanup ---
        await nostrService.DisconnectAsync();
        nostrService.Dispose();

        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task PublishKeyPackage_PerRelayOkResponse()
    {
        // Simpler test: publish to each relay individually and report OK/reject per relay
        var nostrService = new NostrService();
        var pubKeyBytes = DerivePublicKeyFromPrivate(TestPrivateKeyHex);
        var pubKeyHex = Convert.ToHexString(pubKeyBytes).ToLowerInvariant();

        var dbPath = Path.Combine(Path.GetTempPath(), $"kp_diag_perrelay_{Guid.NewGuid()}.db");
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var mlsService = new ManagedMlsService(storage);
        await mlsService.InitializeAsync(TestPrivateKeyHex, pubKeyHex);

        var keyPackage = await mlsService.GenerateKeyPackageAsync();
        _output.WriteLine($"KeyPackage: {keyPackage.Data.Length} bytes, {keyPackage.NostrTags.Count} tags");

        _output.WriteLine("\n--- Per-relay publish test ---");
        var results = new List<(string relay, bool ok, string? reason)>();

        foreach (var relay in TestRelays)
        {
            var result = await PublishToSingleRelay(relay, pubKeyHex, keyPackage, TestPrivateKeyHex);
            results.Add(result);
            var status = result.ok ? "ACCEPTED" : $"REJECTED ({result.reason})";
            _output.WriteLine($"  {relay}: {status}");
        }

        _output.WriteLine($"\n--- Results ---");
        var accepted = results.Count(r => r.ok);
        var rejected = results.Count(r => !r.ok);
        _output.WriteLine($"  Accepted: {accepted}/{results.Count}");
        _output.WriteLine($"  Rejected: {rejected}/{results.Count}");

        foreach (var (relay, ok, reason) in results.Where(r => !r.ok))
            _output.WriteLine($"  FAILED: {relay} — {reason}");

        Assert.True(accepted > 0, "No relay accepted the kind 30443 event");

        try { File.Delete(dbPath); } catch { }
    }

    /// <summary>
    /// Query relays for the specific event published from mobile (Amber-signed).
    /// Checks both by event ID and by author+kind to find where the mismatch is.
    /// </summary>
    [Fact]
    public async Task QueryMobileKeyPackageEvent_ByIdAndByAuthor()
    {
        // Event IDs from the mobile log (Amber-signed publishes)
        var mobileEventIds = new[]
        {
            "2ec6d0d45e89db5541dec3b6c9cebd9dd09618a8056e595a8bfbfa3180290dc4",
            "c0d5bec3b97d6061baf5961678207e58229d42e0fc93b8aed7d557358aa7837f"
        };
        // The pubkey used in FetchKeyPackagesAsync (from CurrentUser)
        var fetchPubkey = "8f7d5460983b7210e9c910a761f786ec208e8bc0f8f3213d61bf7244fcec19e0";

        var relays = new[] { TestRelayConfig.RelayUrl };

        foreach (var relay in relays)
        {
            _output.WriteLine($"\n== {relay} ==");
            try
            {
                using var ws = new ClientWebSocket();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await ws.ConnectAsync(new Uri(relay), cts.Token);

                // Query 1: by event IDs
                foreach (var evId in mobileEventIds)
                {
                    var subId = $"id_{Guid.NewGuid():N}"[..10];
                    var req = $"[\"REQ\",\"{subId}\",{{\"ids\":[\"{evId}\"]}}]";
                    await ws.SendAsync(Encoding.UTF8.GetBytes(req), WebSocketMessageType.Text, true, cts.Token);

                    var buf = new byte[65536];
                    var sb = new StringBuilder();
                    var found = false;
                    while (ws.State == WebSocketState.Open)
                    {
                        var r = await ws.ReceiveAsync(buf, cts.Token);
                        sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                        if (!r.EndOfMessage) continue;
                        var msg = sb.ToString(); sb.Clear();
                        if (msg.StartsWith("[\"EVENT\""))
                        {
                            found = true;
                            using var doc = JsonDocument.Parse(msg);
                            var ev = doc.RootElement[2];
                            var eventPubkey = ev.GetProperty("pubkey").GetString();
                            var kind = ev.GetProperty("kind").GetInt32();
                            _output.WriteLine($"  BY ID {evId[..16]}...: FOUND");
                            _output.WriteLine($"    pubkey in event: {eventPubkey}");
                            _output.WriteLine($"    pubkey we query: {fetchPubkey}");
                            _output.WriteLine($"    MATCH: {eventPubkey == fetchPubkey}");
                            _output.WriteLine($"    kind: {kind}");
                        }
                        if (msg.StartsWith("[\"EOSE\"")) break;
                    }
                    if (!found)
                        _output.WriteLine($"  BY ID {evId[..16]}...: NOT FOUND on relay");

                    var closeMsg = $"[\"CLOSE\",\"{subId}\"]";
                    await ws.SendAsync(Encoding.UTF8.GetBytes(closeMsg), WebSocketMessageType.Text, true, CancellationToken.None);
                }

                // Query 2: by author + kind (same filter as FetchKeyPackagesAsync)
                var subId2 = $"au_{Guid.NewGuid():N}"[..10];
                var req2 = $"[\"REQ\",\"{subId2}\",{{\"kinds\":[30443],\"authors\":[\"{fetchPubkey}\"],\"limit\":10}}]";
                _output.WriteLine($"  AUTHOR QUERY: kinds=[30443], authors=[{fetchPubkey[..16]}...]");
                await ws.SendAsync(Encoding.UTF8.GetBytes(req2), WebSocketMessageType.Text, true, cts.Token);

                var buf2 = new byte[65536];
                var sb2 = new StringBuilder();
                var authorCount = 0;
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf2, cts.Token);
                    sb2.Append(Encoding.UTF8.GetString(buf2, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    var msg = sb2.ToString(); sb2.Clear();
                    if (msg.StartsWith("[\"EVENT\""))
                    {
                        authorCount++;
                        using var doc = JsonDocument.Parse(msg);
                        var ev = doc.RootElement[2];
                        _output.WriteLine($"  BY AUTHOR: id={ev.GetProperty("id").GetString()?[..16]}..., kind={ev.GetProperty("kind").GetInt32()}");
                    }
                    if (msg.StartsWith("[\"EOSE\"")) break;
                }
                _output.WriteLine($"  BY AUTHOR TOTAL: {authorCount} events");

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch (Exception ex) { _output.WriteLine($"  ERROR: {ex.Message}"); }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private async Task<(string relay, bool ok, string? reason)> PublishToSingleRelay(
        string relayUrl, string pubKeyHex, Core.Models.KeyPackage keyPackage, string privateKeyHex)
    {
        try
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(new Uri(relayUrl), cts.Token);

            // Build and sign the event
            var tags = keyPackage.NostrTags;
            var content = Convert.ToBase64String(keyPackage.Data);
            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var eventJson = BuildSignedEvent(30443, content, tags, createdAt, privateKeyHex, pubKeyHex);
            var eventMessage = $"[\"EVENT\",{eventJson}]";

            await ws.SendAsync(Encoding.UTF8.GetBytes(eventMessage),
                WebSocketMessageType.Text, true, cts.Token);

            // Wait for OK response
            var buf = new byte[8192];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;

                var msg = sb.ToString();
                sb.Clear();

                if (msg.Contains("\"OK\""))
                {
                    using var doc = JsonDocument.Parse(msg);
                    var arr = doc.RootElement;
                    if (arr.GetArrayLength() >= 3 && arr[0].GetString() == "OK")
                    {
                        var accepted = arr[2].GetBoolean();
                        var reason = arr.GetArrayLength() >= 4 ? arr[3].GetString() : null;
                        return (relayUrl, accepted, reason);
                    }
                }
            }

            return (relayUrl, false, "no OK response received");
        }
        catch (Exception ex)
        {
            return (relayUrl, false, $"connection error: {ex.Message}");
        }
    }

    private async Task<List<string>> FetchKind30443FromRelay(string relayUrl, string pubKeyHex)
    {
        var events = new List<string>();
        try
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(new Uri(relayUrl), cts.Token);

            var subId = $"kpd_{Guid.NewGuid():N}"[..12];
            var filterJson = $"{{\"kinds\":[30443],\"authors\":[\"{pubKeyHex}\"],\"limit\":5}}";
            var req = $"[\"REQ\",\"{subId}\",{filterJson}]";

            await ws.SendAsync(Encoding.UTF8.GetBytes(req),
                WebSocketMessageType.Text, true, cts.Token);

            var buf = new byte[65536];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;

                var msg = sb.ToString();
                sb.Clear();

                if (msg.StartsWith("[\"EOSE\"")) break;
                if (msg.StartsWith("[\"EVENT\""))
                {
                    using var doc = JsonDocument.Parse(msg);
                    if (doc.RootElement.GetArrayLength() >= 3)
                        events.Add(doc.RootElement[2].GetRawText());
                }
            }

            // Send CLOSE
            var closeMsg = $"[\"CLOSE\",\"{subId}\"]";
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(Encoding.UTF8.GetBytes(closeMsg),
                    WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"    Fetch error from {relayUrl}: {ex.Message}");
        }
        return events;
    }

    private void AnalyzeEvent(string eventJson)
    {
        using var doc = JsonDocument.Parse(eventJson);
        var ev = doc.RootElement;
        var id = ev.GetProperty("id").GetString()!;
        var kind = ev.GetProperty("kind").GetInt32();
        var createdAt = ev.GetProperty("created_at").GetInt64();
        var content = ev.GetProperty("content").GetString() ?? "";

        _output.WriteLine($"    Event: {id[..16]}..., kind={kind}");
        _output.WriteLine($"    created_at: {DateTimeOffset.FromUnixTimeSeconds(createdAt):u}");
        _output.WriteLine($"    content: {content.Length} chars (base64)");

        try
        {
            var data = Convert.FromBase64String(content);
            _output.WriteLine($"    decoded: {data.Length} bytes");
        }
        catch { _output.WriteLine($"    content is NOT valid base64"); }

        foreach (var tag in ev.GetProperty("tags").EnumerateArray())
        {
            var parts = new List<string>();
            foreach (var item in tag.EnumerateArray()) parts.Add(item.GetString() ?? "");
            _output.WriteLine($"    tag: [{string.Join(", ", parts)}]");
        }
    }

    private static string BuildSignedEvent(int kind, string content, List<List<string>> tags,
        long createdAt, string privateKeyHex, string pubKeyHex)
    {
        // Build NIP-01 canonical serialization: [0, pubkey, created_at, kind, tags, content]
        var serialized = NostrService.SerializeForEventId(pubKeyHex, createdAt, kind, tags, content);
        var eventIdBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        var eventId = Convert.ToHexString(eventIdBytes).ToLowerInvariant();
        var signature = NostrService.SignSchnorr(eventIdBytes, Convert.FromHexString(privateKeyHex));
        var signatureHex = Convert.ToHexString(signature).ToLowerInvariant();

        // Build event JSON
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", eventId);
            writer.WriteString("pubkey", pubKeyHex);
            writer.WriteNumber("created_at", createdAt);
            writer.WriteNumber("kind", kind);
            writer.WriteStartArray("tags");
            foreach (var tag in tags)
            {
                writer.WriteStartArray();
                foreach (var v in tag) writer.WriteStringValue(v);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteString("content", content);
            writer.WriteString("sig", signatureHex);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// End-to-end test using real NostrService against localhost strfry + user's relays.
    /// Tests the exact same code path the mobile app uses (minus Amber signing).
    /// With the multi-relay OK tracker, auth-required rejections from one relay
    /// should NOT block success if other relays accept.
    /// </summary>
    [Fact]
    public async Task PublishKeyPackage_LocalhostAndUserRelays_RealNostrService()
    {
        var relays = new[]
        {
            TestRelayConfig.RelayUrl
        };

        var nostrService = new NostrService();
        var (privKey, pubKey, _, _) = nostrService.GenerateKeyPair();
        _output.WriteLine($"Test pubkey: {pubKey}");

        var dbPath = Path.Combine(Path.GetTempPath(), $"kp_diag_local_{Guid.NewGuid()}.db");
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var mlsService = new ManagedMlsService(storage);
        await mlsService.InitializeAsync(privKey, pubKey);

        // Connect
        _output.WriteLine("\n--- Connecting to relays ---");
        foreach (var relay in relays)
        {
            try { await nostrService.ConnectAsync(relay); _output.WriteLine($"  Connected: {relay}"); }
            catch (Exception ex) { _output.WriteLine($"  FAILED: {relay} — {ex.Message}"); }
        }
        _output.WriteLine($"Connected: {string.Join(", ", nostrService.ConnectedRelayUrls)}");
        Assert.True(nostrService.ConnectedRelayUrls.Count > 0, "Must connect to at least one relay");
        await Task.Delay(1000);

        // Generate + publish
        var keyPackage = await mlsService.GenerateKeyPackageAsync();
        _output.WriteLine($"\nKeyPackage: {keyPackage.Data.Length} bytes, {keyPackage.NostrTags.Count} tags");

        _output.WriteLine("\n--- Publishing via NostrService.PublishKeyPackageAsync ---");
        string? eventId = null;
        try
        {
            eventId = await nostrService.PublishKeyPackageAsync(keyPackage.Data, privKey, keyPackage.NostrTags);
            _output.WriteLine($"  Published! Event ID: {eventId}");
            _output.WriteLine($"  OK: accepted={nostrService.LastPublishOkResult.accepted}, reason={nostrService.LastPublishOkResult.reason ?? "none"}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  PUBLISH FAILED: {ex.Message}");
        }

        // Fetch back
        _output.WriteLine("\n--- Fetching via NostrService.FetchKeyPackagesAsync ---");
        var fetched = (await nostrService.FetchKeyPackagesAsync(pubKey)).ToList();
        _output.WriteLine($"  Found {fetched.Count} KeyPackage(s)");
        foreach (var pkg in fetched)
            _output.WriteLine($"    {pkg.NostrEventId} — {pkg.Data.Length}b from {string.Join(", ", pkg.RelayUrls)}");

        // Per-relay raw fetch
        _output.WriteLine("\n--- Per-relay raw fetch ---");
        foreach (var relay in relays)
        {
            var found = await FetchKind30443FromRelay(relay, pubKey);
            _output.WriteLine($"  {relay}: {found.Count} event(s)");
        }

        _output.WriteLine($"\n--- Summary ---");
        _output.WriteLine($"  Event ID: {eventId ?? "NONE (publish failed)"}");
        _output.WriteLine($"  FetchKeyPackagesAsync: {fetched.Count} found");

        await nostrService.DisconnectAsync();
        nostrService.Dispose();
        try { File.Delete(dbPath); } catch { }

        Assert.NotNull(eventId);
        Assert.True(fetched.Count > 0, $"Published {eventId} but fetch returned 0");
    }

    /// <summary>
    /// Regression test for the audit-shows-many-but-only-one-has-key-material bug.
    /// MIP-00 KeyPackage events (kind 30443) are addressable: replacing should happen in place
    /// per (kind, pubkey, d-tag). Before the fix, ManagedMlsService generated a fresh random
    /// d-tag on every publish so each rotation created a new slot, leaving stale KeyPackages
    /// on the relay whose init keys the client no longer held.
    /// This test publishes a KeyPackage twice for the same identity and asserts the relay
    /// holds exactly ONE live event afterward, with the same d-tag, and that the audit-flow
    /// fetcher (FetchKeyPackagesAsync) returns exactly one entry whose key material is held
    /// locally — i.e. the result a working audit should show.
    /// </summary>
    [Fact]
    public async Task RotateKeyPackage_Twice_RelayHoldsExactlyOneSlot()
    {
        var relays = new[] { TestRelayConfig.RelayUrl };

        var nostrService = new NostrService();
        var (privKey, pubKey, _, _) = nostrService.GenerateKeyPair();
        _output.WriteLine($"Test pubkey: {pubKey}");

        var dbPath = Path.Combine(Path.GetTempPath(), $"kp_rotate_{Guid.NewGuid()}.db");
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var mlsService = new ManagedMlsService(storage);
        await mlsService.InitializeAsync(privKey, pubKey);

        // Connect to the local relay
        _output.WriteLine("\n--- Connecting to relays ---");
        foreach (var relay in relays)
        {
            try { await nostrService.ConnectAsync(relay); _output.WriteLine($"  Connected: {relay}"); }
            catch (Exception ex) { _output.WriteLine($"  FAILED: {relay} — {ex.Message}"); }
        }
        Assert.True(nostrService.ConnectedRelayUrls.Count > 0, "Must connect to at least one relay");
        await Task.Delay(1000);

        // ---- Publish #1 ----
        _output.WriteLine("\n--- Publish #1 ---");
        var kp1 = await mlsService.GenerateKeyPackageAsync();
        var dTag1 = kp1.NostrTags.First(t => t.Count >= 2 && t[0] == "d")[1];
        _output.WriteLine($"  KP#1 d-tag: {dTag1}");

        var eventId1 = await nostrService.PublishKeyPackageAsync(kp1.Data, privKey, kp1.NostrTags);
        Assert.NotNull(eventId1);
        _output.WriteLine($"  Published event #1: {eventId1}");

        // Give the relay a moment to settle the addressable replacement bookkeeping
        await Task.Delay(500);

        // Confirm 1 event on the relay after the first publish
        var afterFirst = await FetchKind30443FromRelay(relays[0], pubKey);
        _output.WriteLine($"  Relay after publish #1: {afterFirst.Count} event(s)");
        Assert.Single(afterFirst);

        // ---- Publish #2 (rotation) ----
        // Force a tick so created_at can differ (Nostr is second-resolution) — relays use
        // created_at to pick the surviving addressable event when there's a tie on d-tag.
        await Task.Delay(1100);

        _output.WriteLine("\n--- Publish #2 (rotation) ---");
        var kp2 = await mlsService.GenerateKeyPackageAsync();
        var dTag2 = kp2.NostrTags.First(t => t.Count >= 2 && t[0] == "d")[1];
        _output.WriteLine($"  KP#2 d-tag: {dTag2}");

        // Core assertion: rotation reuses the same slot d-tag
        Assert.Equal(dTag1, dTag2);

        // Sanity: the KeyPackage bytes themselves must be different (it's a new key pair)
        Assert.False(kp1.Data.AsSpan().SequenceEqual(kp2.Data.AsSpan()),
            "Rotation must produce a fresh KeyPackage, only the slot d-tag is reused");

        var eventId2 = await nostrService.PublishKeyPackageAsync(kp2.Data, privKey, kp2.NostrTags);
        Assert.NotNull(eventId2);
        Assert.NotEqual(eventId1, eventId2);
        _output.WriteLine($"  Published event #2: {eventId2}");

        await Task.Delay(500);

        // ---- Verify the relay replaced #1 in place (still 1 live event, but the new one) ----
        var afterSecond = await FetchKind30443FromRelay(relays[0], pubKey);
        _output.WriteLine($"  Relay after publish #2: {afterSecond.Count} event(s)");

        // The whole point of the fix: addressable replacement leaves exactly one live event,
        // not one-per-publish.
        Assert.Single(afterSecond);

        // And it should be the new event, not the stale one.
        using (var doc = JsonDocument.Parse(afterSecond[0]))
        {
            var liveId = doc.RootElement.GetProperty("id").GetString();
            _output.WriteLine($"  Live event id on relay: {liveId}");
            Assert.Equal(eventId2, liveId);

            // d-tag on the live event must equal the slot id
            var dTagOnRelay = doc.RootElement.GetProperty("tags").EnumerateArray()
                .FirstOrDefault(t => t.GetArrayLength() >= 2 && t[0].GetString() == "d");
            Assert.NotEqual(default, dTagOnRelay);
            Assert.Equal(dTag1, dTagOnRelay[1].GetString());
        }

        // ---- Verify the audit code path agrees ----
        // FetchKeyPackagesAsync feeds AuditKeyPackagesAsync; it should report 1 KeyPackage,
        // and the local MLS service should hold key material for it (i.e. "Active", not "Lost").
        _output.WriteLine("\n--- Audit code path (FetchKeyPackagesAsync) ---");
        var fetched = (await nostrService.FetchKeyPackagesAsync(pubKey)).ToList();
        _output.WriteLine($"  Fetched {fetched.Count} KeyPackage(s) for audit");
        Assert.Single(fetched);

        bool hasMaterial = mlsService.HasKeyMaterialForKeyPackage(fetched[0].Data);
        _output.WriteLine($"  HasKeyMaterialForKeyPackage: {hasMaterial}");
        Assert.True(hasMaterial,
            "The single live KeyPackage on the relay must match the local key material " +
            "(the most recently rotated one) — this is the bug the fix addresses.");

        // Cleanup
        await nostrService.DisconnectAsync();
        nostrService.Dispose();
        try { File.Delete(dbPath); } catch { }
    }

    private static byte[] DerivePublicKeyFromPrivate(string privateKeyHex)
    {
        var privBytes = Convert.FromHexString(privateKeyHex);
        if (!NBitcoin.Secp256k1.Context.Instance.TryCreateECPrivKey(privBytes, out var ecPrivKey) || ecPrivKey is null)
            throw new InvalidOperationException("Invalid private key");
        var pubKey = ecPrivKey.CreateXOnlyPubKey();
        var pubBytes = new byte[32];
        pubKey.WriteToSpan(pubBytes);
        return pubBytes;
    }
}
