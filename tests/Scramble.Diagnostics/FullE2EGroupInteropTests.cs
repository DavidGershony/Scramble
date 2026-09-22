using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
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
    public FullE2EGroupInteropTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // No Whitenoise client any more: the one test here that needed one is gone
    // (see Test 2's note below), and a fixture that starts a peer nothing uses
    // would keep printing a warning about a container that is not coming back.
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

    private async Task<OCUser> CreateOCUser(string name)
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

        // The engine the app registers. These drive MessageService and
        // NostrService end to end, and both are written against the Dark Matter
        // engine since P11's flip: a commit reaches the publish as a finished
        // kind-445, and a KeyPackage is read back with the engine's own codec,
        // which refuses the legacy engine's tag shape. Running these on
        // marmot-cs would test a product that no longer exists.
        IMlsService mlsService = DarkMatterMlsServiceFactory.Create(storage);
        await mlsService.InitializeAsync(keys.privateKeyHex, keys.publicKeyHex);

        var messageService = new MessageService(storage, nostrService, mlsService);
        _messageServices.Add(messageService);
        await messageService.InitializeAsync();

        // Connect to relay
        await nostrService.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        _output.WriteLine($"Created '{name}': {keys.publicKeyHex[..16]}... connected to {RelayUrl}");
        _output.WriteLine($"  Relay status: {string.Join(", ", nostrService.ConnectedRelayUrls)}");

        return new OCUser(name, keys.publicKeyHex, keys.privateKeyHex,
            nostrService, storage, mlsService, messageService);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 1: 3 Scramble users, full MessageService flow, all managed
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E2E_3Users_AllOC_FullFlow()
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
        await KeyPackagePublishing.PublishAndBindAsync(
            bob.NostrService, bob.MlsService, kpBob, bob.PrivKeyHex);
        var kpCharlie = await charlie.MlsService.GenerateKeyPackageAsync();
        await KeyPackagePublishing.PublishAndBindAsync(
            charlie.NostrService, charlie.MlsService, kpCharlie, charlie.PrivKeyHex);
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
        await KeyPackagePublishing.PublishAndBindAsync(
            bob.NostrService, bob.MlsService, kpBob, bob.PrivKeyHex);
        var kpCharlie = await charlie.MlsService.GenerateKeyPackageAsync();
        await KeyPackagePublishing.PublishAndBindAsync(
            charlie.NostrService, charlie.MlsService, kpCharlie, charlie.PrivKeyHex);
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
                // The whole event, not its content. The engine's peeler is the only
                // thing that verifies an event's id and signature, so it ingests the
                // envelope and refuses bare ciphertext rather than routing on fields
                // nobody checked. MessageService does the same thing with
                // NostrEventReceived.RawJson; catching up from a relay is the same
                // job by hand.
                var result = await bob.MlsService.DecryptMessageAsync(
                    chatBob.MlsGroupId!, System.Text.Encoding.UTF8.GetBytes(ev));
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

    // Test 2 was E2E_3Users_2OC_1WN_FullFlow: two Scramble users and one
    // Whitenoise peer. Removed 2026-09-21. It was the fourth of the required
    // gate's four permanent skips, and unlike the three in
    // WhitenoiseGroupInteropTests it could not be retagged out of
    // Category=Integration -- xUnit traits are additive and this class is
    // Integration for the tests that do run.
    //
    // Its peer is retired (whitenoise-rs archived upstream), so it could never
    // run again as written. What it uniquely covered -- a mixed group with a
    // second implementation in it -- is covered against a live peer by
    // DarkMatterInterop, and the all-Scramble three-user flow by
    // E2E_3Users_AllOC_FullFlow below.

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
        await KeyPackagePublishing.PublishAndBindAsync(
            bob.NostrService, bob.MlsService, kpBob, bob.PrivKeyHex);
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
        await KeyPackagePublishing.PublishAndBindAsync(
            charlie.NostrService, charlie.MlsService, kpCharlie, charlie.PrivKeyHex);
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

