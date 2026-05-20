using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
using Scramble.Diagnostics.WhitenoiseInterop;
using Xunit;
namespace Scramble.Diagnostics;

/// <summary>
/// Full end-to-end interop test: 2 real Scramble instances + 1 Whitenoise Docker,
/// all connected to the same relay (ws://localhost:7777, the docker test relay).
///
/// Uses the real MessageService flow (CreateGroupAsync → AddMemberAsync → RescanInvites → AcceptInvite → SendMessage).
/// Every message direction is tested with full epoch logging.
///
/// Prerequisites:
///   docker compose -f docker-compose.test.yml up -d --build
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "FullE2E")]
public class FullE2EGroupInteropTests : IAsyncLifetime
{
    private static string RelayUrl => TestRelayConfig.RelayUrl;
    // Same relay, but addressable from inside the WN docker container via compose DNS.
    // WN must register this URL (not localhost:7777, which is unreachable from the container).
    private const string WnLocalRelayUrl = "ws://nostr-relay:8080";

    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();
    private readonly List<NostrService> _nostrServices = new();
    private readonly List<MessageService> _messageServices = new();
    private WhitenoiseDockerClient? _wnClient;

    public FullE2EGroupInteropTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _wnClient = new WhitenoiseDockerClient(_output);
        try
        {
            await _wnClient.EnsureRunningAsync();
        }
        catch
        {
            _output.WriteLine("WARNING: Whitenoise Docker container not running. WN tests will be skipped.");
            _wnClient = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_wnClient != null)
            await _wnClient.DisposeAsync();

        foreach (var ms in _messageServices)
            ms.Dispose();

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

    private record OCUser(
        string Name,
        string PubKeyHex, string PrivKeyHex,
        NostrService NostrService,
        StorageService Storage,
        IMlsService MlsService,
        MessageService MessageService);

    private async Task<OCUser> CreateOCUser(string name, string backend = "managed")
    {
        var nostrService = new NostrService();
        _nostrServices.Add(nostrService);
        var keys = nostrService.GenerateKeyPair();

        var dbPath = Path.Combine(Path.GetTempPath(), $"oc_e2e_{name}_{Guid.NewGuid()}.db");
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

        // Connect to relay
        await nostrService.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        _output.WriteLine($"[{backend}] Created '{name}': {keys.publicKeyHex[..16]}... connected to {RelayUrl}");
        _output.WriteLine($"  Relay status: {string.Join(", ", nostrService.ConnectedRelayUrls)}");

        return new OCUser(name, keys.publicKeyHex, keys.privateKeyHex,
            nostrService, storage, mlsService, messageService);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 1: 3 Scramble users, full MessageService flow, all managed
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E2E_3Users_AllOC_Managed_FullFlow()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  FULL E2E: 3 Scramble users (managed) via relay");
        _output.WriteLine($"  Relay: {RelayUrl}");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var alice = await CreateOCUser("Alice");
        var bob = await CreateOCUser("Bob");
        var charlie = await CreateOCUser("Charlie");

        // Step 1: Bob and Charlie publish KeyPackages
        _output.WriteLine("\n[Step 1] Publishing KeyPackages");
        var kpBob = await bob.MlsService.GenerateKeyPackageAsync();
        await bob.NostrService.PublishKeyPackageAsync(kpBob.Data, bob.PrivKeyHex, kpBob.NostrTags);
        var kpCharlie = await charlie.MlsService.GenerateKeyPackageAsync();
        await charlie.NostrService.PublishKeyPackageAsync(kpCharlie.Data, charlie.PrivKeyHex, kpCharlie.NostrTags);
        await Task.Delay(2000);
        _output.WriteLine("  KeyPackages published");

        // Step 2: Alice creates group and adds Bob + Charlie via MessageService
        _output.WriteLine("\n[Step 2] Alice creates group with Bob and Charlie");
        var chat = await alice.MessageService.CreateGroupAsync("E2E Test Group",
            new[] { bob.PubKeyHex, charlie.PubKeyHex });
        _output.WriteLine($"  Group created: chatId={chat.Id}, mlsGroupId={Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant()[..16]}...");
        _output.WriteLine($"  Participants: {string.Join(", ", chat.ParticipantPublicKeys.Select(p => p[..16] + "..."))}");

        // Subscribe Alice to group messages so she receives others' messages via relay
        var aliceNostrGroupId = chat.NostrGroupId != null
            ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();
        await alice.NostrService.SubscribeToGroupMessagesAsync(new[] { aliceNostrGroupId });
        await Task.Delay(3000);

        // Step 3: Bob subscribes to welcomes and accepts invite
        _output.WriteLine("\n[Step 3] Bob accepts invite");
        await bob.NostrService.SubscribeToWelcomesAsync(bob.PubKeyHex, bob.PrivKeyHex);
        await Task.Delay(2000);
        await bob.MessageService.RescanInvitesAsync();
        var bobInvites = (await bob.Storage.GetPendingInvitesAsync()).ToList();
        _output.WriteLine($"  Bob has {bobInvites.Count} invite(s)");
        Assert.NotEmpty(bobInvites);
        var chatBob = await bob.MessageService.AcceptInviteAsync(bobInvites[0].Id);
        _output.WriteLine($"  Bob joined: epoch={chatBob.MlsEpoch}");

        // Subscribe Bob to group messages
        var bobNostrGroupId = chatBob.NostrGroupId != null
            ? Convert.ToHexString(chatBob.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chatBob.MlsGroupId!).ToLowerInvariant();
        await bob.NostrService.SubscribeToGroupMessagesAsync(new[] { bobNostrGroupId });

        // Step 4: Charlie subscribes to welcomes and accepts invite
        _output.WriteLine("\n[Step 4] Charlie accepts invite");
        await charlie.NostrService.SubscribeToWelcomesAsync(charlie.PubKeyHex, charlie.PrivKeyHex);
        await Task.Delay(2000);
        await charlie.MessageService.RescanInvitesAsync();
        var charlieInvites = (await charlie.Storage.GetPendingInvitesAsync()).ToList();
        _output.WriteLine($"  Charlie has {charlieInvites.Count} invite(s)");
        Assert.NotEmpty(charlieInvites);
        var chatCharlie = await charlie.MessageService.AcceptInviteAsync(charlieInvites[0].Id);
        _output.WriteLine($"  Charlie joined: epoch={chatCharlie.MlsEpoch}");

        // Subscribe Charlie to group messages
        var charlieNostrGroupId = chatCharlie.NostrGroupId != null
            ? Convert.ToHexString(chatCharlie.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chatCharlie.MlsGroupId!).ToLowerInvariant();
        await charlie.NostrService.SubscribeToGroupMessagesAsync(new[] { charlieNostrGroupId });

        // Step 5: Verify epoch sync
        // Bob's MessageService subscription (OnNostrEventReceived → HandleGroupMessageEventAsync)
        // automatically processes the kind-445 commit that added Charlie. Wait for delivery.
        _output.WriteLine("\n[Step 5] Waiting for subscription-based epoch sync");
        await Task.Delay(3000);

        var nostrGroupIdHex = chat.NostrGroupId != null
            ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();

        // Epoch check — all users must be at the same epoch
        var aliceEpoch = (await alice.MlsService.GetGroupInfoAsync(chat.MlsGroupId!))?.Epoch;
        var bobEpoch = (await bob.MlsService.GetGroupInfoAsync(chatBob.MlsGroupId!))?.Epoch;
        var charlieEpoch = (await charlie.MlsService.GetGroupInfoAsync(chatCharlie.MlsGroupId!))?.Epoch;
        _output.WriteLine($"  EPOCH CHECK: Alice={aliceEpoch}, Bob={bobEpoch}, Charlie={charlieEpoch}");
        Assert.Equal(aliceEpoch, bobEpoch);
        Assert.Equal(aliceEpoch, charlieEpoch);

        // Step 6: Each user sends a message — verify via storage (subscription auto-decrypts)
        // The MessageService subscription (OnNostrEventReceived → HandleGroupMessageEventAsync)
        // automatically decrypts incoming kind-445 events and saves them to storage.
        // We verify the full relay round-trip by checking each user's storage for received messages.
        _output.WriteLine("\n[Step 6] Round-trip messaging via real relay");

        // Alice sends
        _output.WriteLine("\n  Alice sending...");
        await alice.MessageService.SendMessageAsync(chat.Id, "Hello from Alice E2E!");
        await Task.Delay(5000); // Wait for relay delivery + subscription processing

        var bobMsgs = (await bob.Storage.GetMessagesForChatAsync(chatBob.Id)).ToList();
        var charlieMsgs = (await charlie.Storage.GetMessagesForChatAsync(chatCharlie.Id)).ToList();
        _output.WriteLine($"  Bob has {bobMsgs.Count} messages, Charlie has {charlieMsgs.Count} messages");
        var bobGotAlice = bobMsgs.Any(m => m.Content.Contains("Hello from Alice"));
        var charlieGotAlice = charlieMsgs.Any(m => m.Content.Contains("Hello from Alice"));
        _output.WriteLine($"  Bob got Alice: {bobGotAlice}, Charlie got Alice: {charlieGotAlice}");

        Assert.True(bobGotAlice, $"Bob must receive Alice's message. Bob's messages: [{string.Join(", ", bobMsgs.Select(m => m.Content))}]");
        Assert.True(charlieGotAlice, $"Charlie must receive Alice's message. Charlie's messages: [{string.Join(", ", charlieMsgs.Select(m => m.Content))}]");

        // Bob sends
        _output.WriteLine("\n  Bob sending...");
        await bob.MessageService.SendMessageAsync(chatBob.Id, "Hello from Bob E2E!");
        await Task.Delay(5000);

        var aliceMsgs = (await alice.Storage.GetMessagesForChatAsync(chat.Id)).ToList();
        charlieMsgs = (await charlie.Storage.GetMessagesForChatAsync(chatCharlie.Id)).ToList();
        var aliceGotBob = aliceMsgs.Any(m => m.Content.Contains("Hello from Bob"));
        var charlieGotBob = charlieMsgs.Any(m => m.Content.Contains("Hello from Bob"));
        _output.WriteLine($"  Alice got Bob: {aliceGotBob}, Charlie got Bob: {charlieGotBob}");

        Assert.True(aliceGotBob, $"Alice must receive Bob's message. Alice's messages: [{string.Join(", ", aliceMsgs.Select(m => m.Content))}]");
        Assert.True(charlieGotBob, $"Charlie must receive Bob's message. Charlie's messages: [{string.Join(", ", charlieMsgs.Select(m => m.Content))}]");

        // Charlie sends
        _output.WriteLine("\n  Charlie sending...");
        await charlie.MessageService.SendMessageAsync(chatCharlie.Id, "Hello from Charlie E2E!");
        await Task.Delay(5000);

        aliceMsgs = (await alice.Storage.GetMessagesForChatAsync(chat.Id)).ToList();
        bobMsgs = (await bob.Storage.GetMessagesForChatAsync(chatBob.Id)).ToList();
        var aliceGotCharlie = aliceMsgs.Any(m => m.Content.Contains("Hello from Charlie"));
        var bobGotCharlie = bobMsgs.Any(m => m.Content.Contains("Hello from Charlie"));
        _output.WriteLine($"  Alice got Charlie: {aliceGotCharlie}, Bob got Charlie: {bobGotCharlie}");

        Assert.True(aliceGotCharlie, $"Alice must receive Charlie's message. Alice's messages: [{string.Join(", ", aliceMsgs.Select(m => m.Content))}]");
        Assert.True(bobGotCharlie, $"Bob must receive Charlie's message. Bob's messages: [{string.Join(", ", bobMsgs.Select(m => m.Content))}]");

        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  FULL E2E TEST COMPLETE — all 6 message paths verified");
        _output.WriteLine("═══════════════════════════════════════════════════════════");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 1b: Offline catch-up — Bob misses a commit while offline, fetches from relay
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E2E_OfflineCatchUp_BobProcessesMissedCommitFromRelay()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  OFFLINE CATCH-UP: Bob misses Charlie's add, syncs from relay");
        _output.WriteLine($"  Relay: {RelayUrl}");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var alice = await CreateOCUser("Alice");
        var bob = await CreateOCUser("Bob");
        var charlie = await CreateOCUser("Charlie");

        // Step 1: KeyPackages
        _output.WriteLine("\n[Step 1] Publishing KeyPackages");
        var kpBob = await bob.MlsService.GenerateKeyPackageAsync();
        await bob.NostrService.PublishKeyPackageAsync(kpBob.Data, bob.PrivKeyHex, kpBob.NostrTags);
        var kpCharlie = await charlie.MlsService.GenerateKeyPackageAsync();
        await charlie.NostrService.PublishKeyPackageAsync(kpCharlie.Data, charlie.PrivKeyHex, kpCharlie.NostrTags);
        await Task.Delay(2000);

        // Step 2: Alice creates group with Bob + Charlie
        _output.WriteLine("\n[Step 2] Alice creates group");
        var chat = await alice.MessageService.CreateGroupAsync("Offline Catch-Up Test",
            new[] { bob.PubKeyHex, charlie.PubKeyHex });
        _output.WriteLine($"  Group created, Alice epoch={chat.MlsEpoch}");
        await Task.Delay(3000);

        // Step 3: Bob accepts invite but does NOT subscribe to group messages.
        // This simulates Bob being offline when Charlie is added — he won't receive
        // the kind-445 commit event via live subscription.
        _output.WriteLine("\n[Step 3] Bob accepts invite (no group subscription — simulating offline)");
        await bob.NostrService.SubscribeToWelcomesAsync(bob.PubKeyHex, bob.PrivKeyHex);
        await Task.Delay(2000);
        await bob.MessageService.RescanInvitesAsync();
        var bobInvites = (await bob.Storage.GetPendingInvitesAsync()).ToList();
        Assert.NotEmpty(bobInvites);
        var chatBob = await bob.MessageService.AcceptInviteAsync(bobInvites[0].Id);
        _output.WriteLine($"  Bob joined: epoch={chatBob.MlsEpoch}");

        // Disconnect Bob to ensure no events arrive via subscription
        await bob.NostrService.DisconnectAsync();
        _output.WriteLine("  Bob disconnected from relay");

        // Step 4: Charlie accepts invite (Bob is offline)
        _output.WriteLine("\n[Step 4] Charlie accepts invite (Bob is offline)");
        await charlie.NostrService.SubscribeToWelcomesAsync(charlie.PubKeyHex, charlie.PrivKeyHex);
        await Task.Delay(2000);
        await charlie.MessageService.RescanInvitesAsync();
        var charlieInvites = (await charlie.Storage.GetPendingInvitesAsync()).ToList();
        Assert.NotEmpty(charlieInvites);
        var chatCharlie = await charlie.MessageService.AcceptInviteAsync(charlieInvites[0].Id);
        _output.WriteLine($"  Charlie joined: epoch={chatCharlie.MlsEpoch}");

        // Verify Bob is behind — he should still be at epoch 1
        var bobEpochBefore = (await bob.MlsService.GetGroupInfoAsync(chatBob.MlsGroupId!))?.Epoch;
        var aliceEpoch = (await alice.MlsService.GetGroupInfoAsync(chat.MlsGroupId!))?.Epoch;
        _output.WriteLine($"  Alice epoch={aliceEpoch}, Bob epoch={bobEpochBefore} (should be behind)");

        // Step 5: Bob comes back online and catches up from the relay.
        // This is the real-world scenario: fetch missed kind-445 events and process them.
        _output.WriteLine("\n[Step 5] Bob reconnects and catches up from relay");
        await bob.NostrService.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        var nostrGroupIdHex = chat.NostrGroupId != null
            ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();

        var groupEvents = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 445 }, @__h = new[] { nostrGroupIdHex }, limit = 50 });
        _output.WriteLine($"  Found {groupEvents.Count} kind-445 events on relay");

        var bobProcessedCommit = false;
        var commitErrors = new List<string>();
        foreach (var ev in groupEvents)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev);
                var content = doc.RootElement.GetProperty("content").GetString()!;
                var bytes = Convert.FromBase64String(content);
                var result = await bob.MlsService.DecryptMessageAsync(chatBob.MlsGroupId!, bytes);
                if (result.IsCommit)
                {
                    bobProcessedCommit = true;
                    _output.WriteLine($"  Bob processed commit (epoch transition)");
                }
                else
                    _output.WriteLine($"  Bob decrypted: \"{result.Plaintext}\"");
            }
            catch (Exception ex)
            {
                // Some events will fail (e.g., Bob's own add commit from epoch 0) — that's expected.
                // Only the Charlie-add commit (at Bob's current epoch) should succeed.
                commitErrors.Add($"{ex.GetType().Name}: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
                _output.WriteLine($"  Bob skip: {ex.GetType().Name}: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
            }
        }

        Assert.True(bobProcessedCommit,
            $"Bob must process at least one commit to catch up. Errors: {string.Join("; ", commitErrors)}");

        // Epoch check — Bob must now match Alice and Charlie
        aliceEpoch = (await alice.MlsService.GetGroupInfoAsync(chat.MlsGroupId!))?.Epoch;
        var bobEpochAfter = (await bob.MlsService.GetGroupInfoAsync(chatBob.MlsGroupId!))?.Epoch;
        var charlieEpoch = (await charlie.MlsService.GetGroupInfoAsync(chatCharlie.MlsGroupId!))?.Epoch;
        _output.WriteLine($"\n  EPOCH CHECK: Alice={aliceEpoch}, Bob={bobEpochAfter}, Charlie={charlieEpoch}");
        Assert.Equal(aliceEpoch, bobEpochAfter);
        Assert.Equal(aliceEpoch, charlieEpoch);

        // Step 6: Verify Bob can send and receive at the new epoch
        _output.WriteLine("\n[Step 6] Post-catch-up messaging");

        // Subscribe everyone to group messages now
        await alice.NostrService.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHex });
        var bobGroupId = chatBob.NostrGroupId != null
            ? Convert.ToHexString(chatBob.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chatBob.MlsGroupId!).ToLowerInvariant();
        await bob.NostrService.SubscribeToGroupMessagesAsync(new[] { bobGroupId });
        var charlieGroupId = chatCharlie.NostrGroupId != null
            ? Convert.ToHexString(chatCharlie.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chatCharlie.MlsGroupId!).ToLowerInvariant();
        await charlie.NostrService.SubscribeToGroupMessagesAsync(new[] { charlieGroupId });

        await bob.MessageService.SendMessageAsync(chatBob.Id, "Bob is back online!");
        await Task.Delay(5000);

        var aliceMsgs = (await alice.Storage.GetMessagesForChatAsync(chat.Id)).ToList();
        var charlieRecv = (await charlie.Storage.GetMessagesForChatAsync(chatCharlie.Id)).ToList();
        Assert.True(aliceMsgs.Any(m => m.Content.Contains("Bob is back")),
            $"Alice must receive Bob's post-catch-up message. Messages: [{string.Join(", ", aliceMsgs.Select(m => m.Content))}]");
        Assert.True(charlieRecv.Any(m => m.Content.Contains("Bob is back")),
            $"Charlie must receive Bob's post-catch-up message. Messages: [{string.Join(", ", charlieRecv.Select(m => m.Content))}]");

        _output.WriteLine("  Alice and Charlie received Bob's post-catch-up message");
        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  OFFLINE CATCH-UP TEST COMPLETE");
        _output.WriteLine("═══════════════════════════════════════════════════════════");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 2: 2 OC + 1 WN full E2E via relay
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E2E_3Users_2OC_1WN_FullFlow()
    {
        Assert.SkipWhen(_wnClient == null, "Whitenoise Docker not running");

        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  FULL E2E: 2 Scramble + 1 Whitenoise via relay");
        _output.WriteLine($"  Relay: {RelayUrl}");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var alice = await CreateOCUser("Alice");
        var bob = await CreateOCUser("Bob");
        var wnPubkey = await _wnClient!.CreateIdentityAsync();
        _output.WriteLine($"Charlie (WN): {wnPubkey[..16]}...");

        // Add test relay to WN so it publishes KPs and subscribes there.
        // Must use the docker-DNS URL — localhost:7777 is unreachable from inside the WN container.
        await _wnClient.AddRelayAsync(WnLocalRelayUrl);
        await Task.Delay(3000);

        // Step 1: Bob publishes KeyPackage
        _output.WriteLine("\n[Step 1] Publishing KeyPackages");
        var kpBob = await bob.MlsService.GenerateKeyPackageAsync();
        await bob.NostrService.PublishKeyPackageAsync(kpBob.Data, bob.PrivKeyHex, kpBob.NostrTags);
        await Task.Delay(3000);

        // Wait for WN to publish KeyPackage
        _output.WriteLine("  Waiting for WN KeyPackage...");
        List<KeyPackage> wnKps = new();
        for (int i = 0; i < 20; i++)
        {
            wnKps = (await alice.NostrService.FetchKeyPackagesAsync(wnPubkey)).ToList();
            if (wnKps.Count > 0) break;
            await Task.Delay(2000);
        }
        _output.WriteLine($"  Found {wnKps.Count} WN KeyPackage(s)");
        Assert.NotEmpty(wnKps);

        // Step 2: Alice creates group with Bob + WN Charlie.
        // Pass relayUrlsOverride so the relay URL baked into the MLS group state and
        // Welcomes is the docker-DNS URL reachable from inside the WN container; without
        // this, WN gets group_relays=["ws://localhost:7777"] which it cannot reach and
        // therefore never receives Alice's kind 445 messages.
        _output.WriteLine("\n[Step 2] Alice creates group");
        var chat = await alice.MessageService.CreateGroupAsync("E2E OC+WN Group",
            new[] { bob.PubKeyHex, wnPubkey },
            relayUrlsOverride: new[] { WnLocalRelayUrl });
        _output.WriteLine($"  Group: {Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant()[..16]}...");
        _output.WriteLine($"  Participants: {chat.ParticipantPublicKeys.Count}");

        // Subscribe Alice to group messages (mirrors real app — ChatListViewModel does this)
        var nostrGroupIdHex = chat.NostrGroupId != null
            ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();
        await alice.NostrService.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHex });
        await Task.Delay(3000);

        // Step 3: Bob accepts invite
        _output.WriteLine("\n[Step 3] Bob accepts invite");
        await bob.NostrService.SubscribeToWelcomesAsync(bob.PubKeyHex, bob.PrivKeyHex);
        await Task.Delay(2000);
        await bob.MessageService.RescanInvitesAsync();
        var bobInvites = (await bob.Storage.GetPendingInvitesAsync()).ToList();
        _output.WriteLine($"  Bob invites: {bobInvites.Count}");
        Assert.NotEmpty(bobInvites);
        var chatBob = await bob.MessageService.AcceptInviteAsync(bobInvites[0].Id);
        _output.WriteLine($"  Bob joined: epoch={chatBob.MlsEpoch}");

        // Subscribe Bob to group messages (mirrors real app)
        var bobSubGroupId = chatBob.NostrGroupId != null
            ? Convert.ToHexString(chatBob.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chatBob.MlsGroupId!).ToLowerInvariant();
        await bob.NostrService.SubscribeToGroupMessagesAsync(
            new[] { bobSubGroupId },
            DateTimeOffset.UtcNow.AddMinutes(-5));

        // Step 4: Wait for WN to auto-accept
        _output.WriteLine("\n[Step 4] Waiting for WN to join group");
        var wnGroups = await _wnClient.PollUntilAsync(
            () => _wnClient.ListGroupsAsync(),
            g => g.Count > 0,
            timeoutMs: 30000, intervalMs: 2000,
            description: "WN joins group");

        if (wnGroups.Count == 0)
        {
            var invites = await _wnClient.ListInvitesAsync();
            _output.WriteLine($"  WN invites: {invites.Count}");
            if (invites.Count > 0)
            {
                await _wnClient.AcceptInviteAsync(invites[0].GroupIdHex);
                await Task.Delay(3000);
                wnGroups = await _wnClient.ListGroupsAsync();
            }
        }
        _output.WriteLine($"  WN groups: {wnGroups.Count}");
        Assert.NotEmpty(wnGroups);

        // Step 5: Wait for subscription-based epoch sync
        _output.WriteLine("\n[Step 5] Waiting for epoch sync via subscription");
        await Task.Delay(5000);

        var aliceEpoch = (await alice.MlsService.GetGroupInfoAsync(chat.MlsGroupId!))?.Epoch;
        var bobEpoch = (await bob.MlsService.GetGroupInfoAsync(chatBob.MlsGroupId!))?.Epoch;
        _output.WriteLine($"  EPOCH CHECK: Alice={aliceEpoch}, Bob={bobEpoch}, WN=unknown");
        Assert.Equal(aliceEpoch, bobEpoch);

        // Step 6: Alice sends — verify Bob (via storage) and WN both receive
        _output.WriteLine("\n[Step 6] Alice sends message");
        await alice.MessageService.SendMessageAsync(chat.Id, "Alice E2E to all!");
        await Task.Delay(5000);

        // Bob receives via subscription
        var bobMsgs = (await bob.Storage.GetMessagesForChatAsync(chatBob.Id)).ToList();
        _output.WriteLine($"  Bob stored messages: {bobMsgs.Count}");
        Assert.True(bobMsgs.Any(m => m.Content.Contains("Alice E2E")),
            $"Bob must receive Alice's message via subscription. Stored: [{string.Join(", ", bobMsgs.Select(m => m.Content))}]");

        // WN receives via its own mechanism
        var wnMsgs = await _wnClient.PollUntilAsync(
            () => _wnClient.FetchMessagesAsync(wnGroups[0].GroupIdHex),
            m => m.Any(x => x.Content.Contains("Alice E2E")),
            timeoutMs: 15000, intervalMs: 2000,
            description: "WN sees Alice's message");
        Assert.Contains(wnMsgs, m => m.Content == "Alice E2E to all!");
        _output.WriteLine($"  WN decrypted Alice's message");

        // Step 7: Bob sends — verify Alice (via storage) and WN both receive
        _output.WriteLine("\n[Step 7] Bob sends message");
        await bob.MessageService.SendMessageAsync(chatBob.Id, "Bob E2E to all!");
        await Task.Delay(5000);

        var aliceMsgs = (await alice.Storage.GetMessagesForChatAsync(chat.Id)).ToList();
        _output.WriteLine($"  Alice stored messages: {aliceMsgs.Count}");
        Assert.True(aliceMsgs.Any(m => m.Content.Contains("Bob E2E")),
            $"Alice must receive Bob's message via subscription. Stored: [{string.Join(", ", aliceMsgs.Select(m => m.Content))}]");

        var wnMsgs2 = await _wnClient.PollUntilAsync(
            () => _wnClient.FetchMessagesAsync(wnGroups[0].GroupIdHex),
            m => m.Any(x => x.Content.Contains("Bob E2E")),
            timeoutMs: 15000, intervalMs: 2000,
            description: "WN sees Bob's message");
        Assert.Contains(wnMsgs2, m => m.Content == "Bob E2E to all!");
        _output.WriteLine($"  WN decrypted Bob's message");

        // Step 8: WN sends — verify Alice and Bob both receive via storage
        _output.WriteLine("\n[Step 8] WN sends message");
        await _wnClient.SendMessageAsync(wnGroups[0].GroupIdHex, "Charlie WN E2E!");
        _output.WriteLine("  Waiting for WN publish + subscription delivery...");

        // Poll Bob's storage until WN message arrives (or timeout)
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        bool aliceGotWn = false, bobGotWn = false;
        while (DateTimeOffset.UtcNow < deadline && (!aliceGotWn || !bobGotWn))
        {
            await Task.Delay(3000);
            if (!aliceGotWn)
            {
                aliceMsgs = (await alice.Storage.GetMessagesForChatAsync(chat.Id)).ToList();
                aliceGotWn = aliceMsgs.Any(m => m.Content.Contains("Charlie WN"));
            }
            if (!bobGotWn)
            {
                bobMsgs = (await bob.Storage.GetMessagesForChatAsync(chatBob.Id)).ToList();
                bobGotWn = bobMsgs.Any(m => m.Content.Contains("Charlie WN"));
            }
        }

        // Log final state before assertions
        aliceMsgs = (await alice.Storage.GetMessagesForChatAsync(chat.Id)).ToList();
        bobMsgs = (await bob.Storage.GetMessagesForChatAsync(chatBob.Id)).ToList();
        _output.WriteLine($"\n  Alice's stored messages ({aliceMsgs.Count}):");
        foreach (var m in aliceMsgs)
            _output.WriteLine($"    [{m.SenderPublicKey[..Math.Min(16, m.SenderPublicKey.Length)]}] \"{m.Content}\"");
        _output.WriteLine($"  Bob's stored messages ({bobMsgs.Count}):");
        foreach (var m in bobMsgs)
            _output.WriteLine($"    [{m.SenderPublicKey[..Math.Min(16, m.SenderPublicKey.Length)]}] \"{m.Content}\"");

        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  RESULTS:");
        _output.WriteLine($"    WN saw Alice: True");
        _output.WriteLine($"    WN saw Bob:   True");
        _output.WriteLine($"    Alice saw WN: {aliceGotWn}");
        _output.WriteLine($"    Bob saw WN:   {bobGotWn}");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        Assert.True(aliceGotWn, $"Alice must receive WN message via subscription. Stored: [{string.Join(", ", aliceMsgs.Select(m => m.Content))}]");
        Assert.True(bobGotWn, $"Bob must receive WN message via subscription. Stored: [{string.Join(", ", bobMsgs.Select(m => m.Content))}]");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 3: Add member after chat established + messages exchanged
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E2E_AddMemberAfterMessaging()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  Add member after chat established + messages exchanged");
        _output.WriteLine($"  Relay: {RelayUrl}");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var alice = await CreateOCUser("Alice");
        var bob = await CreateOCUser("Bob");

        // Step 1: Create 2-user group, exchange messages
        _output.WriteLine("\n[Step 1] Create group with Alice + Bob");
        var kpBob = await bob.MlsService.GenerateKeyPackageAsync();
        await bob.NostrService.PublishKeyPackageAsync(kpBob.Data, bob.PrivKeyHex, kpBob.NostrTags);
        await Task.Delay(2000);

        var chat = await alice.MessageService.CreateGroupAsync("Late Joiner Test",
            new[] { bob.PubKeyHex });
        await Task.Delay(2000);

        await bob.NostrService.SubscribeToWelcomesAsync(bob.PubKeyHex, bob.PrivKeyHex);
        await Task.Delay(2000);
        await bob.MessageService.RescanInvitesAsync();
        var bobInvites = (await bob.Storage.GetPendingInvitesAsync()).ToList();
        Assert.NotEmpty(bobInvites);
        var chatBob = await bob.MessageService.AcceptInviteAsync(bobInvites[0].Id);
        _output.WriteLine($"  Bob joined: epoch={chatBob.MlsEpoch}");

        // Step 2: Exchange messages at epoch 1
        _output.WriteLine("\n[Step 2] Exchange messages (epoch 1)");
        await alice.MessageService.SendMessageAsync(chat.Id, "Alice msg at epoch 1");
        await Task.Delay(2000);
        await bob.MessageService.SendMessageAsync(chatBob.Id, "Bob msg at epoch 1");
        await Task.Delay(2000);
        _output.WriteLine("  Messages exchanged at epoch 1");

        // Step 3: Add Charlie after messages
        _output.WriteLine("\n[Step 3] Add Charlie (late joiner)");
        var charlie = await CreateOCUser("Charlie");
        var kpCharlie = await charlie.MlsService.GenerateKeyPackageAsync();
        await charlie.NostrService.PublishKeyPackageAsync(kpCharlie.Data, charlie.PrivKeyHex, kpCharlie.NostrTags);
        await Task.Delay(2000);

        await alice.MessageService.AddMemberAsync(chat.Id, charlie.PubKeyHex);
        _output.WriteLine("  Alice added Charlie");
        await Task.Delay(3000);

        // Bob must process the commit
        var nostrGroupIdHex = chat.NostrGroupId != null
            ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();

        // Subscribe Bob with since to catch the commit
        var bobSubGroupId = chatBob.NostrGroupId != null
            ? Convert.ToHexString(chatBob.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chatBob.MlsGroupId!).ToLowerInvariant();
        await bob.NostrService.SubscribeToGroupMessagesAsync(
            new[] { bobSubGroupId },
            DateTimeOffset.UtcNow.AddMinutes(-5));
        await Task.Delay(3000);

        // Charlie accepts
        await charlie.NostrService.SubscribeToWelcomesAsync(charlie.PubKeyHex, charlie.PrivKeyHex);
        await Task.Delay(2000);
        await charlie.MessageService.RescanInvitesAsync();
        var charlieInvites = (await charlie.Storage.GetPendingInvitesAsync()).ToList();
        Assert.NotEmpty(charlieInvites);
        var chatCharlie = await charlie.MessageService.AcceptInviteAsync(charlieInvites[0].Id);
        _output.WriteLine($"  Charlie joined: epoch={chatCharlie.MlsEpoch}");

        // Step 4: Everyone sends at new epoch
        _output.WriteLine("\n[Step 4] Messages after Charlie joins");
        await alice.MessageService.SendMessageAsync(chat.Id, "Alice after Charlie joined");
        await Task.Delay(2000);
        await bob.MessageService.SendMessageAsync(chatBob.Id, "Bob after Charlie joined");
        await Task.Delay(2000);
        await charlie.MessageService.SendMessageAsync(chatCharlie.Id, "Charlie first message!");
        await Task.Delay(2000);

        // Epoch check — all must match after late joiner
        var aliceEpoch = (await alice.MlsService.GetGroupInfoAsync(chat.MlsGroupId!))?.Epoch;
        var bobEpoch = (await bob.MlsService.GetGroupInfoAsync(chatBob.MlsGroupId!))?.Epoch;
        var charlieEpoch = (await charlie.MlsService.GetGroupInfoAsync(chatCharlie.MlsGroupId!))?.Epoch;
        _output.WriteLine($"\n  EPOCH CHECK: Alice={aliceEpoch}, Bob={bobEpoch}, Charlie={charlieEpoch}");
        Assert.Equal(aliceEpoch, bobEpoch);
        Assert.Equal(aliceEpoch, charlieEpoch);

        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  Late joiner test COMPLETE");
        _output.WriteLine("═══════════════════════════════════════════════════════════");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Helper: Fetch raw events from relay
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<List<string>> FetchRawEventsFromRelay(string relayUrl, object filter)
    {
        var events = new List<string>();
        var subId = $"e2e_{Guid.NewGuid():N}"[..16];

        using var ws = new System.Net.WebSockets.ClientWebSocket();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(new Uri(relayUrl), cts.Token);

            var filterJson = JsonSerializer.Serialize(filter)
                .Replace("\"__h\"", "\"#h\"").Replace("\"__p\"", "\"#p\"");
            await ws.SendAsync(Encoding.UTF8.GetBytes($"[\"REQ\",\"{subId}\",{filterJson}]"),
                System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token);

            var buffer = new byte[65536];
            var sb = new StringBuilder();
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var msg = sb.ToString();
                sb.Clear();

                if (msg.StartsWith("[\"EVENT\""))
                {
                    using var doc = JsonDocument.Parse(msg);
                    if (doc.RootElement.GetArrayLength() >= 3)
                        events.Add(doc.RootElement[2].GetRawText());
                }
                else if (msg.StartsWith("[\"EOSE\"")) break;
            }

            if (ws.State == System.Net.WebSockets.WebSocketState.Open)
                await ws.SendAsync(Encoding.UTF8.GetBytes($"[\"CLOSE\",\"{subId}\"]"),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  Relay fetch error: {ex.Message}");
        }
        return events;
    }
}

