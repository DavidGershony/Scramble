using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Marmot;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
using Xunit;
namespace Scramble.Diagnostics;

/// <summary>
/// Investigation tests for Scramble ↔ marmot-ts web app interop.
/// Uses ws://localhost:7777 (docker test relay) for hermetic testing.
///
/// These tests create real users, publish real events, and log every detail
/// to diagnose format mismatches between Scramble and the web app.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "WebInterop")]
public class WebAppInteropInvestigationTests : IAsyncLifetime
{
    private static string RelayUrl => TestRelayConfig.RelayUrl;

    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();
    private readonly List<NostrService> _nostrServices = new();
    private readonly List<MessageService> _messageServices = new();

    public WebAppInteropInvestigationTests(ITestOutputHelper output)
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
        foreach (var ms in _messageServices)
            ms.Dispose();

        foreach (var ns in _nostrServices)
        {
            try { await ns.DisconnectAsync(); }
            catch { /* best-effort */ }
            (ns as IDisposable)?.Dispose();
        }

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        foreach (var path in _dbPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* temp file */ }
        }
    }

    private record UserContext(
        string Name,
        string PubKeyHex, string PrivKeyHex,
        string Npub, string Nsec,
        NostrService NostrService,
        StorageService Storage,
        IMlsService MlsService,
        MessageService MessageService,
        string DbPath);

    private async Task<UserContext> CreateUser(string name, string backend = "managed")
    {
        var nostrService = new NostrService();
        _nostrServices.Add(nostrService);

        var keys = nostrService.GenerateKeyPair();
        var dbPath = Path.Combine(Path.GetTempPath(), $"scramble_webinterop_{name}_{Guid.NewGuid()}.db");
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

        IMlsService mlsService = backend switch
        {
            "managed" => new ManagedMlsService(storage),
            "rust" => new MlsService(storage),
            _ => throw new ArgumentException($"Unknown backend '{backend}'")
        };

        var messageService = new MessageService(storage, nostrService, mlsService);
        _messageServices.Add(messageService);

        await messageService.InitializeAsync();

        _output.WriteLine($"Created user '{name}':");
        _output.WriteLine($"  pubkey: {keys.publicKeyHex}");
        _output.WriteLine($"  npub:   {keys.npub}");
        _output.WriteLine($"  nsec:   {keys.nsec}");

        return new UserContext(name, keys.publicKeyHex, keys.privateKeyHex,
            keys.npub, keys.nsec, nostrService, storage, mlsService, messageService, dbPath);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Test 1: Full 3-user flow on relay2.angor.io with detailed logging
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullFlow_ThreeUsers_CreateGroup_InviteAcceptMessage()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: Full 3-user interop flow on relay2.angor.io");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        // ── Step 1: Create 3 users ──
        var alice = await CreateUser("Alice");
        var bob = await CreateUser("Bob");
        var charlie = await CreateUser("Charlie");

        // ── Step 2: Connect all to relay ──
        _output.WriteLine($"\n--- Connecting all users to {RelayUrl} ---");
        await alice.NostrService.ConnectAsync(RelayUrl);
        await bob.NostrService.ConnectAsync(RelayUrl);
        await charlie.NostrService.ConnectAsync(RelayUrl);
        await Task.Delay(2000); // let WSS connections stabilize
        _output.WriteLine("All connected.");

        // ── Step 3: Bob and Charlie publish KeyPackages ──
        _output.WriteLine("\n--- Publishing KeyPackages ---");

        var kpBob = await bob.MlsService.GenerateKeyPackageAsync();
        var kpBobEventId = await bob.NostrService.PublishKeyPackageAsync(
            kpBob.Data, bob.PrivKeyHex, kpBob.NostrTags);
        _output.WriteLine($"Bob KeyPackage published: eventId={kpBobEventId}");
        _output.WriteLine($"  Data length: {kpBob.Data.Length} bytes");
        _output.WriteLine($"  Tags: {FormatTags(kpBob.NostrTags)}");

        var kpCharlie = await charlie.MlsService.GenerateKeyPackageAsync();
        var kpCharlieEventId = await charlie.NostrService.PublishKeyPackageAsync(
            kpCharlie.Data, charlie.PrivKeyHex, kpCharlie.NostrTags);
        _output.WriteLine($"Charlie KeyPackage published: eventId={kpCharlieEventId}");

        await Task.Delay(2000); // let relay store them

        // ── Step 4: Fetch and inspect raw KeyPackage events from relay ──
        _output.WriteLine("\n--- Fetching raw KeyPackage events from relay ---");
        var bobKpRaw = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 30443 }, authors = new[] { bob.PubKeyHex }, limit = 5 });
        LogRawEvents("Bob's KeyPackage events", bobKpRaw);

        var charlieKpRaw = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 30443 }, authors = new[] { charlie.PubKeyHex }, limit = 5 });
        LogRawEvents("Charlie's KeyPackage events", charlieKpRaw);

        // ── Step 5: Alice creates a group ──
        _output.WriteLine("\n--- Alice creates group ---");
        var groupInfo = await alice.MlsService.CreateGroupAsync("Interop Test Group", new[] { "wss://relay.test" });
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        var nostrGroupId = alice.MlsService.GetNostrGroupId(groupInfo.GroupId);
        var nostrGroupIdHex = nostrGroupId != null
            ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
            : groupIdHex;
        _output.WriteLine($"Group created:");
        _output.WriteLine($"  MLS GroupId: {groupIdHex}");
        _output.WriteLine($"  Nostr GroupId (h-tag): {nostrGroupIdHex}");
        _output.WriteLine($"  Epoch: {groupInfo.Epoch}");

        // Save chat for Alice
        var chatAlice = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Interop Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { alice.PubKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await alice.Storage.SaveChatAsync(chatAlice);

        // ── Step 6: Alice fetches Bob's KeyPackage from relay ──
        _output.WriteLine("\n--- Alice fetches Bob's KeyPackage ---");
        var fetchedKPs = (await alice.NostrService.FetchKeyPackagesAsync(bob.PubKeyHex)).ToList();
        Assert.NotEmpty(fetchedKPs);
        _output.WriteLine($"Fetched {fetchedKPs.Count} KeyPackage(s) for Bob");
        _output.WriteLine($"  EventId: {fetchedKPs[0].NostrEventId}");
        _output.WriteLine($"  Data: {fetchedKPs[0].Data.Length} bytes");
        if (!string.IsNullOrEmpty(fetchedKPs[0].EventJson))
        {
            _output.WriteLine($"  Full event JSON:");
            LogPrettyJson(fetchedKPs[0].EventJson);
        }

        // ── Step 7: Alice adds Bob to group (produces Welcome + Commit) ──
        _output.WriteLine("\n--- Alice adds Bob to group ---");
        var welcome = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, fetchedKPs[0]);
        _output.WriteLine($"AddMember result:");
        _output.WriteLine($"  WelcomeData: {welcome.WelcomeData.Length} bytes");
        _output.WriteLine($"  WelcomeData first 32 hex: {Convert.ToHexString(welcome.WelcomeData[..Math.Min(32, welcome.WelcomeData.Length)])}");
        _output.WriteLine($"  CommitData: {welcome.CommitData?.Length ?? 0} bytes");
        _output.WriteLine($"  RecipientPubKey: {welcome.RecipientPublicKey}");
        _output.WriteLine($"  KeyPackageEventId: {welcome.KeyPackageEventId}");

        // ── Step 7a: Publish commit event (kind 445) — MISSING in current code! ──
        if (welcome.CommitData != null && welcome.CommitData.Length > 0)
        {
            _output.WriteLine("\n--- Publishing commit event (kind 445) for group members ---");
            var commitEventId = await alice.NostrService.PublishCommitAsync(
                welcome.CommitData, nostrGroupIdHex, alice.PrivKeyHex);
            _output.WriteLine($"Commit published: eventId={commitEventId}");

            await Task.Delay(1000);

            // Fetch and inspect raw commit event
            var commitRaw = await FetchRawEventsFromRelay(RelayUrl,
                new { kinds = new[] { 445 }, @__h = new[] { nostrGroupIdHex }, limit = 5 });
            LogRawEvents("Commit events for group", commitRaw);
        }
        else
        {
            _output.WriteLine("WARNING: No commit data returned from AddMember!");
        }

        // ── Step 8: Alice publishes Welcome (NIP-59 gift wrapped kind 444) ──
        _output.WriteLine("\n--- Alice publishes Welcome for Bob ---");
        var welcomeEventId = await alice.NostrService.PublishWelcomeAsync(
            welcome.WelcomeData, bob.PubKeyHex, alice.PrivKeyHex, kpBobEventId);
        _output.WriteLine($"Welcome published: eventId={welcomeEventId}");

        await Task.Delay(2000);

        // Fetch and inspect raw gift wrap event
        var giftWrapRaw = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 1059 }, @__p = new[] { bob.PubKeyHex }, limit = 5 });
        LogRawEvents("Gift wrap events for Bob", giftWrapRaw);

        // ── Step 9: Bob subscribes and receives the invite ──
        _output.WriteLine("\n--- Bob subscribes to welcomes ---");
        await bob.NostrService.SubscribeToWelcomesAsync(bob.PubKeyHex, bob.PrivKeyHex);
        await Task.Delay(1000);

        // Use rescan to pick up the already-published welcome
        _output.WriteLine("Bob rescanning for invites...");
        await bob.MessageService.RescanInvitesAsync();

        var bobInvites = (await bob.Storage.GetPendingInvitesAsync()).ToList();
        _output.WriteLine($"Bob pending invites: {bobInvites.Count}");
        foreach (var inv in bobInvites)
        {
            _output.WriteLine($"  Invite: id={inv.Id}, sender={inv.SenderPublicKey[..16]}..., " +
                              $"welcomeLen={inv.WelcomeData.Length}, eventId={inv.NostrEventId[..16]}...");
        }
        Assert.NotEmpty(bobInvites);

        // ── Step 10: Bob accepts the invite ──
        _output.WriteLine("\n--- Bob accepts invite ---");
        var chatBob = await bob.MessageService.AcceptInviteAsync(bobInvites[0].Id);
        _output.WriteLine($"Bob joined group:");
        _output.WriteLine($"  ChatId: {chatBob.Id}");
        _output.WriteLine($"  Name: {chatBob.Name}");
        _output.WriteLine($"  MlsGroupId: {Convert.ToHexString(chatBob.MlsGroupId!).ToLowerInvariant()}");
        var bobNostrGroupId = chatBob.NostrGroupId != null
            ? Convert.ToHexString(chatBob.NostrGroupId).ToLowerInvariant()
            : "null";
        _output.WriteLine($"  NostrGroupId: {bobNostrGroupId}");
        _output.WriteLine($"  Epoch: {chatBob.MlsEpoch}");
        _output.WriteLine($"  Participants: [{string.Join(", ", chatBob.ParticipantPublicKeys.Select(k => k[..16] + "..."))}]");

        // Verify group IDs match
        Assert.Equal(
            nostrGroupIdHex,
            bobNostrGroupId != "null" ? bobNostrGroupId : Convert.ToHexString(chatBob.MlsGroupId!).ToLowerInvariant());

        // ── Step 11: Alice sends a message ──
        _output.WriteLine("\n--- Alice sends message ---");
        var msgCiphertext = await alice.MlsService.EncryptMessageAsync(groupInfo.GroupId, "Hello from Alice!");
        _output.WriteLine($"Encrypted message: {msgCiphertext.Length} bytes");
        var aliceMsgEventId = await alice.NostrService.PublishRawEventJsonAsync(msgCiphertext);
        _output.WriteLine($"Published kind 445 message: eventId={aliceMsgEventId}");

        // Parse and log the encrypted event
        _output.WriteLine("Encrypted event JSON:");
        LogPrettyJson(Encoding.UTF8.GetString(msgCiphertext));

        await Task.Delay(2000);

        // Fetch and inspect raw group messages
        var groupMsgRaw = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 445 }, @__h = new[] { nostrGroupIdHex }, limit = 10 });
        LogRawEvents("Group messages (kind 445)", groupMsgRaw);

        // ── Step 12: Bob decrypts Alice's message ──
        _output.WriteLine("\n--- Bob decrypts Alice's message ---");
        try
        {
            // Parse the event JSON to get the content (base64 ciphertext)
            using var eventDoc = JsonDocument.Parse(msgCiphertext);
            var eventContent = eventDoc.RootElement.GetProperty("content").GetString()!;
            var ciphertextBytes = Convert.FromBase64String(eventContent);
            _output.WriteLine($"Ciphertext: {ciphertextBytes.Length} bytes, first 32 hex: {Convert.ToHexString(ciphertextBytes[..Math.Min(32, ciphertextBytes.Length)])}");

            var decrypted = await bob.MlsService.DecryptMessageAsync(chatBob.MlsGroupId!, ciphertextBytes);
            _output.WriteLine($"Decrypted: \"{decrypted.Plaintext}\"");
            Assert.Equal("Hello from Alice!", decrypted.Plaintext);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"DECRYPTION FAILED: {ex.GetType().Name}: {ex.Message}");
            _output.WriteLine($"  Stack: {ex.StackTrace}");
            // Don't fail — log for investigation
        }

        // ── Step 13: Bob sends a reply ──
        _output.WriteLine("\n--- Bob sends reply ---");
        try
        {
            var bobCiphertext = await bob.MlsService.EncryptMessageAsync(chatBob.MlsGroupId!, "Hello from Bob!");
            var bobMsgEventId = await bob.NostrService.PublishRawEventJsonAsync(bobCiphertext);
            _output.WriteLine($"Bob's message published: eventId={bobMsgEventId}");

            _output.WriteLine("Bob's encrypted event JSON:");
            LogPrettyJson(Encoding.UTF8.GetString(bobCiphertext));

            await Task.Delay(1000);

            // Alice decrypts Bob's message
            using var bobEventDoc = JsonDocument.Parse(bobCiphertext);
            var bobContent = bobEventDoc.RootElement.GetProperty("content").GetString()!;
            var bobCiphertextBytes = Convert.FromBase64String(bobContent);

            var decryptedByAlice = await alice.MlsService.DecryptMessageAsync(groupInfo.GroupId, bobCiphertextBytes);
            _output.WriteLine($"Alice decrypted Bob's message: \"{decryptedByAlice.Plaintext}\"");
            Assert.Equal("Hello from Bob!", decryptedByAlice.Plaintext);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"BOB REPLY FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        // ── Step 14: Alice adds Charlie ──
        _output.WriteLine("\n--- Alice adds Charlie to group ---");
        var fetchedCharlieKPs = (await alice.NostrService.FetchKeyPackagesAsync(charlie.PubKeyHex)).ToList();
        Assert.NotEmpty(fetchedCharlieKPs);
        _output.WriteLine($"Fetched {fetchedCharlieKPs.Count} KeyPackage(s) for Charlie");

        var welcomeCharlie = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, fetchedCharlieKPs[0]);
        _output.WriteLine($"AddMember (Charlie): welcome={welcomeCharlie.WelcomeData.Length} bytes, commit={welcomeCharlie.CommitData?.Length ?? 0} bytes");

        // Publish commit for Charlie's addition
        if (welcomeCharlie.CommitData != null && welcomeCharlie.CommitData.Length > 0)
        {
            var commitEventId2 = await alice.NostrService.PublishCommitAsync(
                welcomeCharlie.CommitData, nostrGroupIdHex, alice.PrivKeyHex);
            _output.WriteLine($"Commit for Charlie published: eventId={commitEventId2}");

            // Bob must process this commit to advance his epoch!
            // In production, this happens via SubscribeToGroupMessagesAsync + HandleGroupMessageEventAsync.
            // Here we do it directly to simulate the flow.
            _output.WriteLine("Bob processing commit to advance epoch...");
            try
            {
                // Fetch the commit event from relay
                var commitEvents = await FetchRawEventsFromRelay(RelayUrl,
                    new { kinds = new[] { 445 }, ids = new[] { commitEventId2 }, limit = 1 });
                if (commitEvents.Count > 0)
                {
                    using var commitDoc = JsonDocument.Parse(commitEvents[0]);
                    var commitContent = commitDoc.RootElement.GetProperty("content").GetString()!;
                    var commitBytes = Convert.FromBase64String(commitContent);
                    _output.WriteLine($"Commit event fetched: {commitBytes.Length} bytes (MIP-03 encrypted)");

                    // DecryptMessageAsync now handles commits gracefully
                    var commitResult = await bob.MlsService.DecryptMessageAsync(chatBob.MlsGroupId!, commitBytes);
                    _output.WriteLine($"Bob processed commit: IsCommit={commitResult.IsCommit}");
                    Assert.True(commitResult.IsCommit);
                }
                else
                {
                    _output.WriteLine("WARNING: Could not fetch commit event from relay");
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Bob commit processing failed: {ex.GetType().Name}: {ex.Message}");
                _output.WriteLine($"  Stack: {ex.StackTrace}");
            }
        }

        // Publish Welcome for Charlie
        var welcomeCharlieEventId = await alice.NostrService.PublishWelcomeAsync(
            welcomeCharlie.WelcomeData, charlie.PubKeyHex, alice.PrivKeyHex, kpCharlieEventId);
        _output.WriteLine($"Welcome for Charlie published: eventId={welcomeCharlieEventId}");

        await Task.Delay(2000);

        // ── Step 15: Charlie receives and accepts invite ──
        _output.WriteLine("\n--- Charlie subscribes and accepts ---");
        await charlie.NostrService.SubscribeToWelcomesAsync(charlie.PubKeyHex, charlie.PrivKeyHex);
        await Task.Delay(1000);
        await charlie.MessageService.RescanInvitesAsync();

        var charlieInvites = (await charlie.Storage.GetPendingInvitesAsync()).ToList();
        _output.WriteLine($"Charlie pending invites: {charlieInvites.Count}");
        Assert.NotEmpty(charlieInvites);

        var chatCharlie = await charlie.MessageService.AcceptInviteAsync(charlieInvites[0].Id);
        _output.WriteLine($"Charlie joined group: MlsGroupId={Convert.ToHexString(chatCharlie.MlsGroupId!).ToLowerInvariant()}");

        // ── Step 16: Alice sends message to all ──
        _output.WriteLine("\n--- Alice sends message to all 3 members ---");
        try
        {
            var msgAll = await alice.MlsService.EncryptMessageAsync(groupInfo.GroupId, "Hello everyone!");
            await alice.NostrService.PublishRawEventJsonAsync(msgAll);
            _output.WriteLine("Published group message for all");

            using var allDoc = JsonDocument.Parse(msgAll);
            var allContent = allDoc.RootElement.GetProperty("content").GetString()!;
            var allCiphertextBytes = Convert.FromBase64String(allContent);

            // Bob decrypts
            var decByBob = await bob.MlsService.DecryptMessageAsync(chatBob.MlsGroupId!, allCiphertextBytes);
            _output.WriteLine($"Bob decrypted: \"{decByBob.Plaintext}\"");
            Assert.Equal("Hello everyone!", decByBob.Plaintext);

            // Charlie decrypts
            var decByCharlie = await charlie.MlsService.DecryptMessageAsync(chatCharlie.MlsGroupId!, allCiphertextBytes);
            _output.WriteLine($"Charlie decrypted: \"{decByCharlie.Plaintext}\"");
            Assert.Equal("Hello everyone!", decByCharlie.Plaintext);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"MULTI-USER MESSAGE FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        // ── Final: Fetch all group events for analysis ──
        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  FINAL EVENT DUMP: All events on relay for this group");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var allGroupEvents = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 445 }, @__h = new[] { nostrGroupIdHex }, limit = 50 });
        LogRawEvents("All kind 445 events", allGroupEvents);

        var allGiftWraps = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 1059 }, @__p = new[] { bob.PubKeyHex }, limit = 10 });
        LogRawEvents("All gift wraps for Bob", allGiftWraps);

        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  TEST COMPLETE");
        _output.WriteLine("═══════════════════════════════════════════════════════════");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Test 2: Fetch existing events from relay to compare with web app
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FetchAndAnalyzeExistingRelayEvents()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: Fetch existing events from relay2.angor.io");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        // Fetch recent KeyPackage events (kind 30443)
        _output.WriteLine("\n--- Recent KeyPackage events (kind 30443) ---");
        var kpEvents = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 30443 }, limit = 10 });
        LogRawEvents("Recent KeyPackages", kpEvents);

        // Analyze each KP event for tag format
        foreach (var ev in kpEvents)
        {
            AnalyzeKeyPackageEvent(ev);
        }

        // Fetch recent group events (kind 445)
        _output.WriteLine("\n--- Recent group events (kind 445) ---");
        var groupEvents = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 445 }, limit = 10 });
        LogRawEvents("Recent group events", groupEvents);

        // Analyze each group event
        foreach (var ev in groupEvents)
        {
            AnalyzeGroupEvent(ev);
        }

        // Fetch recent gift wraps (kind 1059)
        _output.WriteLine("\n--- Recent gift wrap events (kind 1059) ---");
        var giftWraps = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 1059 }, limit = 10 });
        LogRawEvents("Recent gift wraps", giftWraps);

        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  ANALYSIS COMPLETE");
        _output.WriteLine("═══════════════════════════════════════════════════════════");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Test 3: KeyPackage format deep comparison
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task KeyPackageFormatAnalysis()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: KeyPackage format analysis");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var user = await CreateUser("KPTestUser");
        await user.NostrService.ConnectAsync(RelayUrl);
        await Task.Delay(2000);

        // Generate and publish KeyPackage
        var kp = await user.MlsService.GenerateKeyPackageAsync();
        _output.WriteLine($"Generated KeyPackage: {kp.Data.Length} bytes");
        _output.WriteLine($"Tags from MDK:");
        foreach (var tag in kp.NostrTags)
        {
            _output.WriteLine($"  [{string.Join(", ", tag.Select(t => $"\"{t}\""))}]");
        }

        var eventId = await user.NostrService.PublishKeyPackageAsync(
            kp.Data, user.PrivKeyHex, kp.NostrTags);
        _output.WriteLine($"Published: {eventId}");
        await Task.Delay(2000);

        // Fetch the event back and compare
        var rawEvents = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 30443 }, authors = new[] { user.PubKeyHex }, limit = 1 });

        if (rawEvents.Count > 0)
        {
            _output.WriteLine("\n--- Published event as seen by relay ---");
            LogPrettyJson(rawEvents[0]);
            AnalyzeKeyPackageEvent(rawEvents[0]);

            // Compare with marmot-ts expected format
            _output.WriteLine("\n--- Format comparison with marmot-ts ---");
            using var doc = JsonDocument.Parse(rawEvents[0]);
            var root = doc.RootElement;
            var tags = root.GetProperty("tags");

            var hasVersion = false;
            var hasCiphersuite = false;
            var hasExtensions = false;
            var hasEncoding = false;
            var hasRelays = false;
            var hasI = false;
            var hasClient = false;

            foreach (var tag in tags.EnumerateArray())
            {
                var tagName = tag[0].GetString();
                switch (tagName)
                {
                    case "mls_protocol_version": hasVersion = true; break;
                    case "mls_ciphersuite": hasCiphersuite = true; break;
                    case "mls_extensions": hasExtensions = true; break;
                    case "encoding": hasEncoding = true; break;
                    case "relays": hasRelays = true; break;
                    case "i": hasI = true; break;
                    case "client": hasClient = true; break;
                }
            }

            _output.WriteLine($"  mls_protocol_version: {(hasVersion ? "PRESENT" : "MISSING")} (required by marmot-ts)");
            _output.WriteLine($"  mls_ciphersuite:      {(hasCiphersuite ? "PRESENT" : "MISSING")} (required by marmot-ts)");
            _output.WriteLine($"  mls_extensions:       {(hasExtensions ? "PRESENT" : "MISSING")} (required by marmot-ts)");
            _output.WriteLine($"  encoding:             {(hasEncoding ? "PRESENT" : "MISSING")} (required by marmot-ts)");
            _output.WriteLine($"  relays:               {(hasRelays ? "PRESENT" : "MISSING")} (required by marmot-ts)");
            _output.WriteLine($"  i (KP ref):           {(hasI ? "PRESENT" : "MISSING")} (required by marmot-ts)");
            _output.WriteLine($"  client:               {(hasClient ? "PRESENT" : "MISSING")} (optional, marmot-ts uses 'marmot-chat')");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Test 4: Gift wrap format deep analysis
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GiftWrapFormatAnalysis()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  TEST: Gift wrap (NIP-59) format analysis");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var alice = await CreateUser("GWAlice");
        var bob = await CreateUser("GWBob");

        await alice.NostrService.ConnectAsync(RelayUrl);
        await bob.NostrService.ConnectAsync(RelayUrl);
        await Task.Delay(2000);

        // Bob publishes KeyPackage
        var kpBob = await bob.MlsService.GenerateKeyPackageAsync();
        var kpEventId = await bob.NostrService.PublishKeyPackageAsync(
            kpBob.Data, bob.PrivKeyHex, kpBob.NostrTags);
        await Task.Delay(2000);

        // Alice creates group and adds Bob
        var groupInfo = await alice.MlsService.CreateGroupAsync("GW Test Group", new[] { "wss://relay.test" });
        var fetchedKPs = (await alice.NostrService.FetchKeyPackagesAsync(bob.PubKeyHex)).ToList();
        Assert.NotEmpty(fetchedKPs);

        var welcome = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, fetchedKPs[0]);

        // Publish Welcome
        var welcomeEventId = await alice.NostrService.PublishWelcomeAsync(
            welcome.WelcomeData, bob.PubKeyHex, alice.PrivKeyHex, kpEventId);
        _output.WriteLine($"Welcome published: {welcomeEventId}");
        await Task.Delay(2000);

        // Fetch raw gift wrap from relay
        var giftWraps = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 1059 }, @__p = new[] { bob.PubKeyHex }, limit = 5 });
        _output.WriteLine($"\nFound {giftWraps.Count} gift wraps for Bob");

        foreach (var gw in giftWraps)
        {
            _output.WriteLine("\n--- Gift Wrap event ---");
            LogPrettyJson(gw);

            // Analyze structure
            using var doc = JsonDocument.Parse(gw);
            var root = doc.RootElement;

            var gwId = root.GetProperty("id").GetString();
            var gwPubkey = root.GetProperty("pubkey").GetString();
            var gwKind = root.GetProperty("kind").GetInt32();
            var gwContent = root.GetProperty("content").GetString();

            _output.WriteLine($"  id: {gwId}");
            _output.WriteLine($"  pubkey: {gwPubkey} (should be ephemeral, NOT alice's: {alice.PubKeyHex})");
            _output.WriteLine($"  kind: {gwKind} (should be 1059)");
            _output.WriteLine($"  content length: {gwContent?.Length ?? 0} chars");

            Assert.Equal(1059, gwKind);
            Assert.NotEqual(alice.PubKeyHex, gwPubkey); // Must be ephemeral

            // Try to unwrap using Bob's private key
            _output.WriteLine("\n  Unwrapping with Bob's private key...");
            try
            {
                var gwPubkeyHex = gwPubkey!;
                var sealConvKey = MarmotCs.Protocol.Nip44.Nip44Encryption.DeriveConversationKey(
                    Convert.FromHexString(bob.PrivKeyHex), Convert.FromHexString(gwPubkeyHex));
                var sealJson = MarmotCs.Protocol.Nip44.Nip44Encryption.Decrypt(gwContent!, sealConvKey);
                _output.WriteLine("  Seal JSON:");
                LogPrettyJson(sealJson);

                // Parse seal and decrypt rumor
                using var sealDoc = JsonDocument.Parse(sealJson);
                var seal = sealDoc.RootElement;
                var sealPubkey = seal.GetProperty("pubkey").GetString()!;
                var sealContent = seal.GetProperty("content").GetString()!;
                var sealKind = seal.GetProperty("kind").GetInt32();

                _output.WriteLine($"\n  Seal pubkey: {sealPubkey} (should be Alice's: {alice.PubKeyHex})");
                _output.WriteLine($"  Seal kind: {sealKind} (should be 13)");
                Assert.Equal(13, sealKind);
                Assert.Equal(alice.PubKeyHex, sealPubkey);

                var rumorConvKey = MarmotCs.Protocol.Nip44.Nip44Encryption.DeriveConversationKey(
                    Convert.FromHexString(bob.PrivKeyHex), Convert.FromHexString(sealPubkey));
                var rumorJson = MarmotCs.Protocol.Nip44.Nip44Encryption.Decrypt(sealContent, rumorConvKey);
                _output.WriteLine("\n  Rumor (kind 444 Welcome) JSON:");
                LogPrettyJson(rumorJson);

                // Parse rumor details
                using var rumorDoc = JsonDocument.Parse(rumorJson);
                var rumor = rumorDoc.RootElement;
                var rumorKind = rumor.GetProperty("kind").GetInt32();
                var rumorPubkey = rumor.GetProperty("pubkey").GetString();
                var rumorContent = rumor.GetProperty("content").GetString();

                _output.WriteLine($"\n  Rumor kind: {rumorKind} (should be 444)");
                _output.WriteLine($"  Rumor pubkey: {rumorPubkey}");
                _output.WriteLine($"  Rumor content length: {rumorContent?.Length ?? 0} chars (base64 welcome)");

                Assert.Equal(444, rumorKind);

                // Decode welcome content
                if (!string.IsNullOrEmpty(rumorContent))
                {
                    var welcomeBytes = Convert.FromBase64String(rumorContent);
                    _output.WriteLine($"  Welcome bytes: {welcomeBytes.Length} bytes");
                    _output.WriteLine($"  Welcome first 16 hex: {Convert.ToHexString(welcomeBytes[..Math.Min(16, welcomeBytes.Length)])}");

                    // Check MLSMessage header
                    if (welcomeBytes.Length >= 4)
                    {
                        var version = (welcomeBytes[0] << 8) | welcomeBytes[1];
                        var wireFormat = (welcomeBytes[2] << 8) | welcomeBytes[3];
                        _output.WriteLine($"  MLSMessage version: 0x{version:X4} (expect 0x0001 = mls10)");
                        _output.WriteLine($"  MLSMessage wireFormat: 0x{wireFormat:X4} (expect 0x0003 = mls_welcome)");
                    }
                }

                // Analyze rumor tags
                if (rumor.TryGetProperty("tags", out var rumorTags))
                {
                    _output.WriteLine($"\n  Rumor tags:");
                    foreach (var tag in rumorTags.EnumerateArray())
                    {
                        var parts = new List<string>();
                        foreach (var item in tag.EnumerateArray())
                            parts.Add(item.GetString() ?? "");
                        _output.WriteLine($"    [{string.Join(", ", parts.Select(p => $"\"{p}\""))}]");
                    }
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  UNWRAP FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helper: Fetch raw events from a relay via WebSocket REQ/EOSE
    // ══════════════════════════════════════════════════════════════════════

    private async Task<List<string>> FetchRawEventsFromRelay(string relayUrl, object filter)
    {
        var events = new List<string>();
        var subId = $"fetch_{Guid.NewGuid():N}"[..16];

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://scramble.test");

        try
        {
            var uri = new Uri(relayUrl);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(uri, cts.Token);

            // Build REQ message — handle the special __h and __p tag filters
            var filterJson = JsonSerializer.Serialize(filter);
            // Replace __h with #h and __p with #p for Nostr tag filters
            filterJson = filterJson.Replace("\"__h\"", "\"#h\"").Replace("\"__p\"", "\"#p\"");
            var reqMessage = $"[\"REQ\",\"{subId}\",{filterJson}]";
            _output.WriteLine($"  REQ: {reqMessage}");

            await ws.SendAsync(
                Encoding.UTF8.GetBytes(reqMessage),
                WebSocketMessageType.Text, true, cts.Token);

            // Read until EOSE
            var buffer = new byte[65536];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var msg = sb.ToString();
                sb.Clear();

                if (msg.StartsWith("[\"EVENT\""))
                {
                    // Extract event JSON (third element in array)
                    using var doc = JsonDocument.Parse(msg);
                    var arr = doc.RootElement;
                    if (arr.GetArrayLength() >= 3)
                    {
                        events.Add(arr[2].GetRawText());
                    }
                }
                else if (msg.StartsWith("[\"EOSE\""))
                {
                    break;
                }
                else if (msg.StartsWith("[\"NOTICE\""))
                {
                    _output.WriteLine($"  NOTICE from relay: {msg}");
                }
            }

            // Close subscription
            var closeMsg = $"[\"CLOSE\",\"{subId}\"]";
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(
                    Encoding.UTF8.GetBytes(closeMsg),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  Relay fetch error: {ex.GetType().Name}: {ex.Message}");
        }

        return events;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helpers: Logging and analysis
    // ══════════════════════════════════════════════════════════════════════

    private void LogRawEvents(string label, List<string> events)
    {
        _output.WriteLine($"\n  [{label}] — {events.Count} event(s):");
        foreach (var ev in events)
        {
            LogPrettyJson(ev);
        }
    }

    private void LogPrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var formatted = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            foreach (var line in formatted.Split('\n'))
            {
                _output.WriteLine($"    {line}");
            }
        }
        catch
        {
            _output.WriteLine($"    (raw) {json}");
        }
    }

    private void AnalyzeKeyPackageEvent(string eventJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(eventJson);
            var root = doc.RootElement;

            var id = root.GetProperty("id").GetString();
            var pubkey = root.GetProperty("pubkey").GetString();
            var kind = root.GetProperty("kind").GetInt32();
            var content = root.GetProperty("content").GetString();

            _output.WriteLine($"\n  KP Analysis for {id?[..16]}...:");
            _output.WriteLine($"    pubkey: {pubkey?[..16]}...");
            _output.WriteLine($"    kind: {kind}");
            _output.WriteLine($"    content length: {content?.Length ?? 0} chars");

            if (!string.IsNullOrEmpty(content))
            {
                try
                {
                    var data = Convert.FromBase64String(content);
                    _output.WriteLine($"    decoded: {data.Length} bytes");
                    _output.WriteLine($"    first 16 hex: {Convert.ToHexString(data[..Math.Min(16, data.Length)])}");
                }
                catch
                {
                    _output.WriteLine($"    content is NOT valid base64");
                }
            }

            // Analyze tags
            if (root.TryGetProperty("tags", out var tags))
            {
                _output.WriteLine($"    tags ({tags.GetArrayLength()}):");
                foreach (var tag in tags.EnumerateArray())
                {
                    var parts = new List<string>();
                    foreach (var item in tag.EnumerateArray())
                        parts.Add(item.GetString() ?? "");
                    _output.WriteLine($"      [{string.Join(", ", parts.Select(p => $"\"{p}\""))}]");

                    // Specific checks
                    if (parts.Count >= 2)
                    {
                        switch (parts[0])
                        {
                            case "mls_protocol_version":
                                _output.WriteLine($"        → Version: {parts[1]} (marmot-ts expects '1.0')");
                                break;
                            case "mls_ciphersuite":
                                _output.WriteLine($"        → Ciphersuite: {parts[1]} (marmot-ts expects '0x0001')");
                                break;
                            case "encoding":
                                _output.WriteLine($"        → Encoding: {parts[1]} (marmot-ts expects 'base64')");
                                break;
                            case "client":
                                _output.WriteLine($"        → Client: {parts[1]} (marmot-ts uses 'marmot-chat')");
                                break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  KP Analysis failed: {ex.Message}");
        }
    }

    private void AnalyzeGroupEvent(string eventJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(eventJson);
            var root = doc.RootElement;

            var id = root.GetProperty("id").GetString();
            var pubkey = root.GetProperty("pubkey").GetString();
            var kind = root.GetProperty("kind").GetInt32();
            var content = root.GetProperty("content").GetString();

            _output.WriteLine($"\n  Group Event Analysis for {id?[..16]}...:");
            _output.WriteLine($"    pubkey: {pubkey?[..16]}...");
            _output.WriteLine($"    kind: {kind}");

            // Check if pubkey looks ephemeral (compare with known pubkeys)
            _output.WriteLine($"    pubkey looks ephemeral: likely yes (per MIP-03)");

            if (!string.IsNullOrEmpty(content))
            {
                try
                {
                    var data = Convert.FromBase64String(content);
                    _output.WriteLine($"    content: {data.Length} bytes");
                    _output.WriteLine($"    first 32 hex: {Convert.ToHexString(data[..Math.Min(32, data.Length)])}");

                    // Check nonce size (first 12 bytes) + minimum ciphertext
                    if (data.Length >= 28)
                    {
                        _output.WriteLine($"    nonce (12 bytes): {Convert.ToHexString(data[..12])}");
                        _output.WriteLine($"    ciphertext: {data.Length - 12} bytes (includes 16-byte auth tag)");
                        _output.WriteLine($"    plaintext size estimate: {data.Length - 28} bytes");
                    }
                    else
                    {
                        _output.WriteLine($"    WARNING: payload too small ({data.Length} < 28 bytes minimum)");
                    }
                }
                catch
                {
                    _output.WriteLine($"    content is NOT valid base64");
                }
            }

            // Analyze tags
            if (root.TryGetProperty("tags", out var tags))
            {
                _output.WriteLine($"    tags:");
                foreach (var tag in tags.EnumerateArray())
                {
                    var parts = new List<string>();
                    foreach (var item in tag.EnumerateArray())
                        parts.Add(item.GetString() ?? "");
                    _output.WriteLine($"      [{string.Join(", ", parts.Select(p => $"\"{p}\""))}]");

                    if (parts.Count >= 2 && (parts[0] == "h" || parts[0] == "g"))
                    {
                        _output.WriteLine($"        → Group ID: {parts[1]}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  Group event analysis failed: {ex.Message}");
        }
    }

    private static string FormatTags(List<List<string>> tags)
    {
        return string.Join(", ",
            tags.Select(t => $"[{string.Join(", ", t.Select(v => $"\"{v}\""))}]"));
    }
}
