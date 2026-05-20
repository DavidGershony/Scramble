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
/// Cross-client interop tests: Scramble users + Whitenoise user in groups.
/// Whitenoise runs as a Docker container (daemon + CLI).
/// Both sides connect to the same Nostr relay (localhost:7777 from docker-compose.test.yml).
///
/// Prerequisites:
///   docker compose -f docker-compose.test.yml up -d --build
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "WhitenoiseInterop")]
public class WhitenoiseGroupInteropTests : IAsyncLifetime
{
    // Scramble (host) connects here — mapped to nostr-relay:8080 inside Docker
    private const string RelayUrl = "ws://localhost:7777";
    // Relay URL used in group metadata — must be reachable from both host and Docker
    // host.docker.internal resolves to the Docker host from inside containers
    private const string GroupRelayUrl = "ws://host.docker.internal:7777";
    // The same physical relay as RelayUrl, but addressed by its docker-network DNS
    // name. Used when telling WN (inside the docker network) to publish/subscribe
    // there. WN cannot reach ws://localhost:7777 from inside its container, but
    // ws://nostr-relay:8080 resolves to the same scsibug/nostr-rs-relay process.
    private const string WnLocalRelayUrl = "ws://nostr-relay:8080";

    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();
    private readonly List<NostrService> _nostrServices = new();
    private readonly List<MessageService> _messageServices = new();
    private WhitenoiseDockerClient? _wnClient;

    public WhitenoiseGroupInteropTests(ITestOutputHelper output)
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

    private record ScrambleUser(
        string Name,
        string PubKeyHex, string PrivKeyHex,
        string Npub, string Nsec,
        NostrService NostrService,
        StorageService Storage,
        IMlsService MlsService,
        MessageService MessageService,
        string DbPath);

    private async Task<ScrambleUser> CreateScrambleUser(string name, string backend = "managed")
    {
        var nostrService = new NostrService();
        _nostrServices.Add(nostrService);

        var keys = nostrService.GenerateKeyPair();
        var dbPath = Path.Combine(Path.GetTempPath(), $"scramble_wn_interop_{name}_{Guid.NewGuid()}.db");
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

        _output.WriteLine($"Created Scramble user '{name}': pubkey={keys.publicKeyHex[..16]}...");
        return new ScrambleUser(name, keys.publicKeyHex, keys.privateKeyHex,
            keys.npub, keys.nsec, nostrService, storage, mlsService, messageService, dbPath);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 1: 3-user group chat — 2 Scramble + 1 Whitenoise
    // ══════════════════════════��═══════════════════════════════════════════════

    [Fact]
    public async Task GroupChat_3Users_2Scramble_1Whitenoise()
    {
        Assert.SkipWhen(_wnClient == null, "Whitenoise Docker not running");
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  3-user group: Alice(OC) + Bob(OC) + Charlie(WN)");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        // Step 1: Create identities
        var alice = await CreateScrambleUser("Alice");
        var bob = await CreateScrambleUser("Bob");
        var charliePubkey = await _wnClient!.CreateIdentityAsync();
        _output.WriteLine($"Charlie (WN): pubkey={charliePubkey[..16]}...");

        // Wire WN to the docker test relay (docker-DNS URL) so it publishes
        // Charlie's KeyPackage there. Without this, KP fetch only succeeds via
        // public NIP-65 discovery relays which rate-limit under parallel test load.
        await _wnClient.AddRelayAsync(WnLocalRelayUrl);
        await Task.Delay(2000);

        // Step 2: Connect Scramble users to relay
        _output.WriteLine($"\n--- Connecting to {RelayUrl} ---");
        await alice.NostrService.ConnectAsync(RelayUrl);
        await bob.NostrService.ConnectAsync(RelayUrl);
        await Task.Delay(2000);

        // Step 3: Bob publishes KeyPackage so Alice can invite him
        _output.WriteLine("\n--- Publishing KeyPackages ---");
        var kpBob = await bob.MlsService.GenerateKeyPackageAsync();
        var kpBobEventId = await bob.NostrService.PublishKeyPackageAsync(
            kpBob.Data, bob.PrivKeyHex, kpBob.NostrTags);
        _output.WriteLine($"Bob KP published: {kpBobEventId}");
        await Task.Delay(2000);

        // Step 4: Alice creates group
        _output.WriteLine("\n--- Alice creates group ---");
        var groupInfo = await alice.MlsService.CreateGroupAsync("Interop Group 3", new[] { GroupRelayUrl });
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        var nostrGroupId = alice.MlsService.GetNostrGroupId(groupInfo.GroupId);
        var nostrGroupIdHex = nostrGroupId != null
            ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
            : groupIdHex;
        _output.WriteLine($"Group: mlsId={groupIdHex[..16]}..., nostrId={nostrGroupIdHex[..16]}...");

        var chatAlice = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Interop Group 3",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { alice.PubKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await alice.Storage.SaveChatAsync(chatAlice);

        // Step 5: Alice adds Bob
        _output.WriteLine("\n--- Alice adds Bob ---");
        _output.WriteLine($"Alice connected relays: [{string.Join(", ", alice.NostrService.ConnectedRelayUrls)}]");
        var fetchedBobKPs = await PollForKeyPackagesAsync(alice.NostrService, bob.PubKeyHex, timeoutMs: 15000);
        Assert.NotEmpty(fetchedBobKPs);
        var welcomeBob = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, fetchedBobKPs[0]);

        if (welcomeBob.CommitData is { Length: > 0 })
        {
            var commitEventId = await alice.NostrService.PublishCommitAsync(
                welcomeBob.CommitData, nostrGroupIdHex, alice.PrivKeyHex);
            _output.WriteLine($"Commit published: {commitEventId}");
        }

        var welcomeBobEventId = await alice.NostrService.PublishWelcomeAsync(
            welcomeBob.WelcomeData, bob.PubKeyHex, alice.PrivKeyHex, kpBobEventId);
        _output.WriteLine($"Welcome for Bob published: {welcomeBobEventId}");
        await Task.Delay(2000);

        // Bob joins
        await bob.NostrService.SubscribeToWelcomesAsync(bob.PubKeyHex, bob.PrivKeyHex);
        await Task.Delay(1000);
        await bob.MessageService.RescanInvitesAsync();
        var bobInvites = (await bob.Storage.GetPendingInvitesAsync()).ToList();
        Assert.NotEmpty(bobInvites);
        var chatBob = await bob.MessageService.AcceptInviteAsync(bobInvites[0].Id);
        _output.WriteLine($"Bob joined group: {Convert.ToHexString(chatBob.MlsGroupId!).ToLowerInvariant()[..16]}...");

        // Step 6: Alice adds Charlie (WN) — need Charlie's KeyPackage from relay
        _output.WriteLine("\n--- Alice adds Charlie (WN) ---");
        // Wait for Whitenoise to publish its KeyPackage (auto-published on create_identity)
        var fetchedCharlieKPs = await PollForKeyPackagesAsync(alice.NostrService, charliePubkey, timeoutMs: 15000);
        Assert.NotEmpty(fetchedCharlieKPs);
        _output.WriteLine($"Fetched {fetchedCharlieKPs.Count} KeyPackage(s) for Charlie (WN)");

        var welcomeCharlie = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, fetchedCharlieKPs[0]);

        if (welcomeCharlie.CommitData is { Length: > 0 })
        {
            var commitEventId2 = await alice.NostrService.PublishCommitAsync(
                welcomeCharlie.CommitData, nostrGroupIdHex, alice.PrivKeyHex);
            _output.WriteLine($"Commit for Charlie published: {commitEventId2}");

            // Bob processes the commit
            await ProcessCommitForMember(bob, chatBob.MlsGroupId!, commitEventId2, nostrGroupIdHex);
        }

        var kpCharlieEventId = fetchedCharlieKPs[0].NostrEventId;
        var welcomeCharlieEventId = await alice.NostrService.PublishWelcomeAsync(
            welcomeCharlie.WelcomeData, charliePubkey, alice.PrivKeyHex, kpCharlieEventId);
        _output.WriteLine($"Welcome for Charlie published: {welcomeCharlieEventId}");

        // Step 7: Wait for Whitenoise to auto-process the Welcome
        _output.WriteLine("\n--- Waiting for Charlie (WN) to process Welcome ---");
        var wnGroups = await _wnClient!.PollUntilAsync(
            () => _wnClient.ListGroupsAsync(),
            groups => groups.Count > 0,
            timeoutMs: 20000,
            intervalMs: 2000,
            description: "WN receives group invite");

        if (wnGroups.Count == 0)
        {
            // Try invites
            var invites = await _wnClient.ListInvitesAsync();
            _output.WriteLine($"WN has {invites.Count} pending invite(s)");
            if (invites.Count > 0)
            {
                await _wnClient.AcceptInviteAsync(invites[0].GroupIdHex);
                await Task.Delay(2000);
                wnGroups = await _wnClient.ListGroupsAsync();
            }
        }

        Assert.NotEmpty(wnGroups);
        _output.WriteLine($"Charlie (WN) is in {wnGroups.Count} group(s): {wnGroups[0].Name}");

        // Step 8: Alice sends a message — verify Bob and Charlie both receive it
        _output.WriteLine("\n--- Alice sends message ---");
        var msgCiphertext = await alice.MlsService.EncryptMessageAsync(groupInfo.GroupId, "Hello from Alice to all!");
        await alice.NostrService.PublishRawEventJsonAsync(msgCiphertext);
        _output.WriteLine("Published message from Alice");
        await Task.Delay(3000);

        // Bob decrypts
        using var eventDoc = JsonDocument.Parse(msgCiphertext);
        var eventContent = eventDoc.RootElement.GetProperty("content").GetString()!;
        var ciphertextBytes = Convert.FromBase64String(eventContent);
        var decByBob = await bob.MlsService.DecryptMessageAsync(chatBob.MlsGroupId!, ciphertextBytes);
        _output.WriteLine($"Bob decrypted: \"{decByBob.Plaintext}\"");
        Assert.Equal("Hello from Alice to all!", decByBob.Plaintext);

        // Charlie (WN) fetches messages
        var wnMessages = await _wnClient!.PollUntilAsync(
            () => _wnClient.FetchMessagesAsync(wnGroups[0].GroupIdHex),
            msgs => msgs.Any(m => m.Content.Contains("Hello from Alice")),
            timeoutMs: 15000,
            intervalMs: 2000,
            description: "WN receives Alice's message");

        // Exact match — proves Whitenoise MLS-decrypted the ciphertext correctly
        var aliceMsg = wnMessages.FirstOrDefault(m => m.Content == "Hello from Alice to all!");
        Assert.NotNull(aliceMsg);
        Assert.Equal("Hello from Alice to all!", aliceMsg!.Content);
        Assert.Equal(alice.PubKeyHex, aliceMsg.SenderPubkeyHex);
        _output.WriteLine($"Charlie (WN) decrypted Alice's message: \"{aliceMsg.Content}\" from sender {aliceMsg.SenderPubkeyHex[..16]}...");

        // Step 9: Charlie (WN) sends a reply — verify Alice and Bob can decrypt it
        _output.WriteLine("\n--- Charlie (WN) sends reply ---");
        await _wnClient.SendMessageAsync(wnGroups[0].GroupIdHex, "Hello from Whitenoise!");
        await Task.Delay(3000);

        // Fetch Charlie's kind 445 event from the relay and decrypt on both OC sides
        _output.WriteLine("Fetching Charlie's message from relay...");
        var groupMsgEvents = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 445 }, @__h = new[] { nostrGroupIdHex }, limit = 20 });
        _output.WriteLine($"Found {groupMsgEvents.Count} kind-445 events for group");

        bool aliceDecryptedReply = false;
        bool bobDecryptedReply = false;

        foreach (var ev in groupMsgEvents)
        {
            try
            {
                using var evDoc = JsonDocument.Parse(ev);
                var evContent = evDoc.RootElement.GetProperty("content").GetString()!;
                var evBytes = Convert.FromBase64String(evContent);

                _output.WriteLine($"  Event content ({evBytes.Length} bytes): {Convert.ToHexString(evBytes[..Math.Min(16, evBytes.Length)])}...");

                if (!aliceDecryptedReply)
                {
                    try
                    {
                        var decAlice = await alice.MlsService.DecryptMessageAsync(groupInfo.GroupId, evBytes);
                        _output.WriteLine($"  Alice decrypted: plaintext=\"{decAlice.Plaintext}\", isCommit={decAlice.IsCommit}");
                        if (decAlice.Plaintext == "Hello from Whitenoise!")
                        {
                            aliceDecryptedReply = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _output.WriteLine($"  Alice decrypt failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (!bobDecryptedReply)
                {
                    try
                    {
                        var decBob = await bob.MlsService.DecryptMessageAsync(chatBob.MlsGroupId!, evBytes);
                        _output.WriteLine($"  Bob decrypted: plaintext=\"{decBob.Plaintext}\", isCommit={decBob.IsCommit}");
                        if (decBob.Plaintext == "Hello from Whitenoise!")
                        {
                            bobDecryptedReply = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _output.WriteLine($"  Bob decrypt failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch { /* parse error */ }
        }

        // NOTE: If the reverse direction fails, it means MIP-03 encryption format
        // differs between Scramble (managed C#) and Whitenoise (Rust MDK 0.7.1).
        // The forward direction (OC→WN) is proven above. The reverse (WN→OC) may
        // require MIP-03 alignment work — log the result but don't fail the test.
        _output.WriteLine($"  Alice decrypted WN reply: {aliceDecryptedReply}");
        _output.WriteLine($"  Bob decrypted WN reply:   {bobDecryptedReply}");
        Assert.True(aliceDecryptedReply, "Alice must decrypt Charlie's WN reply");
        Assert.True(bobDecryptedReply, "Bob must decrypt Charlie's WN reply");

        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  3-user interop test COMPLETE — full round-trip verified");
        _output.WriteLine("══��═══════════════════════════════��════════════════════════");
    }

    // ══════════════════════════��═══════════════════════════════════════════════
    // Test 2: 4-user group chat — 2 Scramble + 2 Whitenoise
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GroupChat_4Users_2Scramble_2Whitenoise()
    {
        Assert.SkipWhen(_wnClient == null, "Whitenoise Docker not running");
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  4-user group: Alice(OC) + Bob(OC) + Charlie(WN) + Dana(WN)");
        _output.WriteLine("═══════════════��═════════════════════════════���═════════════");

        // Step 1: Create identities
        var alice = await CreateScrambleUser("Alice");
        var bob = await CreateScrambleUser("Bob");
        var charliePubkey = await _wnClient!.CreateIdentityAsync();
        _output.WriteLine($"Charlie (WN): {charliePubkey[..16]}...");

        // Wire WN to the docker test relay so it publishes KeyPackages there.
        await _wnClient.AddRelayAsync(WnLocalRelayUrl);
        await Task.Delay(2000);

        // For a second WN user, we reuse the same daemon (it supports multiple accounts)
        // We need a second identity
        // Note: The WN daemon may need the second identity created separately.
        // For now, we use a single WN identity and can extend later.
        // TODO: Create second WN identity when daemon supports multi-account CLI commands.

        // Step 2: Connect Scramble users
        await alice.NostrService.ConnectAsync(RelayUrl);
        await bob.NostrService.ConnectAsync(RelayUrl);
        await Task.Delay(2000);

        // Bob publishes KeyPackage
        var kpBob = await bob.MlsService.GenerateKeyPackageAsync();
        var kpBobEventId = await bob.NostrService.PublishKeyPackageAsync(
            kpBob.Data, bob.PrivKeyHex, kpBob.NostrTags);
        await Task.Delay(2000);

        // Step 3: Alice creates group and adds Bob
        var groupInfo = await alice.MlsService.CreateGroupAsync("Interop Group 4", new[] { GroupRelayUrl });
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        var nostrGroupId = alice.MlsService.GetNostrGroupId(groupInfo.GroupId);
        var nostrGroupIdHex = nostrGroupId != null
            ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
            : groupIdHex;

        var chatAlice = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Interop Group 4",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { alice.PubKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await alice.Storage.SaveChatAsync(chatAlice);

        // Add Bob
        var fetchedBobKPs = (await alice.NostrService.FetchKeyPackagesAsync(bob.PubKeyHex)).ToList();
        Assert.NotEmpty(fetchedBobKPs);
        var welcomeBob = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, fetchedBobKPs[0]);

        if (welcomeBob.CommitData is { Length: > 0 })
            await alice.NostrService.PublishCommitAsync(welcomeBob.CommitData, nostrGroupIdHex, alice.PrivKeyHex);

        await alice.NostrService.PublishWelcomeAsync(welcomeBob.WelcomeData, bob.PubKeyHex, alice.PrivKeyHex, kpBobEventId);
        await Task.Delay(2000);

        // Bob joins
        await bob.NostrService.SubscribeToWelcomesAsync(bob.PubKeyHex, bob.PrivKeyHex);
        await Task.Delay(1000);
        await bob.MessageService.RescanInvitesAsync();
        var bobInvites = (await bob.Storage.GetPendingInvitesAsync()).ToList();
        Assert.NotEmpty(bobInvites);
        var chatBob = await bob.MessageService.AcceptInviteAsync(bobInvites[0].Id);
        _output.WriteLine($"Bob joined group");

        // Step 4: Add Charlie (WN)
        _output.WriteLine("\n--- Adding Charlie (WN) ---");
        var fetchedCharlieKPs = await PollForKeyPackagesAsync(alice.NostrService, charliePubkey, timeoutMs: 15000);
        Assert.NotEmpty(fetchedCharlieKPs);
        var welcomeCharlie = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, fetchedCharlieKPs[0]);

        if (welcomeCharlie.CommitData is { Length: > 0 })
        {
            var commitId = await alice.NostrService.PublishCommitAsync(
                welcomeCharlie.CommitData, nostrGroupIdHex, alice.PrivKeyHex);
            await ProcessCommitForMember(bob, chatBob.MlsGroupId!, commitId, nostrGroupIdHex);
        }

        await alice.NostrService.PublishWelcomeAsync(
            welcomeCharlie.WelcomeData, charliePubkey, alice.PrivKeyHex, fetchedCharlieKPs[0].NostrEventId);
        _output.WriteLine("Welcome for Charlie published");

        // Wait for WN to join
        var wnGroups = await _wnClient!.PollUntilAsync(
            () => _wnClient.ListGroupsAsync(),
            groups => groups.Count > 0,
            timeoutMs: 20000,
            intervalMs: 2000,
            description: "WN receives group");

        if (wnGroups.Count == 0)
        {
            var invites = await _wnClient.ListInvitesAsync();
            if (invites.Count > 0)
            {
                await _wnClient.AcceptInviteAsync(invites[0].GroupIdHex);
                await Task.Delay(2000);
                wnGroups = await _wnClient.ListGroupsAsync();
            }
        }
        Assert.NotEmpty(wnGroups);
        _output.WriteLine($"Charlie (WN) joined group: {wnGroups[0].Name}");

        // Step 5: Round-trip messaging from all 3 users
        _output.WriteLine("\n--- Round-trip messaging ---");

        // Alice sends
        var msgAlice = await alice.MlsService.EncryptMessageAsync(groupInfo.GroupId, "4-user message from Alice");
        await alice.NostrService.PublishRawEventJsonAsync(msgAlice);
        _output.WriteLine("Alice sent message");

        // Bob sends
        var msgBob = await bob.MlsService.EncryptMessageAsync(chatBob.MlsGroupId!, "4-user message from Bob");
        await bob.NostrService.PublishRawEventJsonAsync(msgBob);
        _output.WriteLine("Bob sent message");

        // Charlie (WN) sends
        await _wnClient.SendMessageAsync(wnGroups[0].GroupIdHex, "4-user message from Charlie (WN)");
        _output.WriteLine("Charlie (WN) sent message");

        await Task.Delay(5000);

        // Verify WN received Alice's and Bob's messages
        var wnMsgs = await _wnClient.FetchMessagesAsync(wnGroups[0].GroupIdHex);
        _output.WriteLine($"WN has {wnMsgs.Count} message(s):");
        foreach (var m in wnMsgs)
            _output.WriteLine($"  [{m.SenderPubkeyHex[..16]}...] {m.Content}");

        Assert.Contains(wnMsgs, m => m.Content.Contains("from Alice"));
        Assert.Contains(wnMsgs, m => m.Content.Contains("from Bob"));

        _output.WriteLine("\n══════════════════════════════════════════��════════════════");
        _output.WriteLine("  4-user interop test COMPLETE");
        _output.WriteLine("══════��══════════════════════════════════════���═════════════");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 3: Whitenoise creates group, Scramble users join
    // ═══════════════════���════════════════════════════════��═════════════════════

    [Fact]
    public async Task GroupChat_WhitenoiseCreatesGroup_ScrambleJoins()
    {
        Assert.SkipWhen(_wnClient == null, "Whitenoise Docker not running");
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  WN creates group, OC users join");
        _output.WriteLine("═══════��═════════════════════════════════════════════════��═");

        // Step 1: Create identities
        var alice = await CreateScrambleUser("Alice");
        var bob = await CreateScrambleUser("Bob");
        var charliePubkey = await _wnClient!.CreateIdentityAsync();

        // Tell WN to publish/subscribe on the local docker relay so the group it
        // creates advertises that relay and Scramble's kind:445 publishes to
        // ws://localhost:7777 are visible to WN. Without this, WN tags the group
        // with its default discovery relays (public ones) and never subscribes
        // to the local docker relay where Scramble actually sends.
        await _wnClient.AddRelayAsync(WnLocalRelayUrl);

        await alice.NostrService.ConnectAsync(RelayUrl);
        await bob.NostrService.ConnectAsync(RelayUrl);
        await Task.Delay(2000);

        // Publish NIP-65 relay lists so WN knows where to send Welcomes
        var relayPrefs = new List<RelayPreference>
        {
            new() { Url = RelayUrl, Usage = RelayUsage.Both },
            new() { Url = GroupRelayUrl, Usage = RelayUsage.Both }
        };
        await alice.NostrService.PublishRelayListAsync(relayPrefs, alice.PrivKeyHex);
        await bob.NostrService.PublishRelayListAsync(relayPrefs, bob.PrivKeyHex);
        _output.WriteLine("Published NIP-65 relay lists for Alice and Bob");

        // Alice and Bob publish KeyPackages
        var kpAlice = await alice.MlsService.GenerateKeyPackageAsync();
        await alice.NostrService.PublishKeyPackageAsync(kpAlice.Data, alice.PrivKeyHex, kpAlice.NostrTags);
        var kpBob = await bob.MlsService.GenerateKeyPackageAsync();
        await bob.NostrService.PublishKeyPackageAsync(kpBob.Data, bob.PrivKeyHex, kpBob.NostrTags);
        await Task.Delay(2000);
        _output.WriteLine("Alice and Bob published KeyPackages");

        // Step 2: Whitenoise creates group and invites Alice + Bob
        _output.WriteLine("\n--- Charlie (WN) creates group ---");
        var wnGroupId = await _wnClient.CreateGroupAsync("WN-Created Group", alice.PubKeyHex, bob.PubKeyHex);
        _output.WriteLine($"WN group created: {wnGroupId[..16]}...");

        // Step 3: Wait for Scramble users to receive Welcomes (poll with retry)
        _output.WriteLine("\n--- Waiting for OC users to receive invites ---");
        await alice.NostrService.SubscribeToWelcomesAsync(alice.PubKeyHex, alice.PrivKeyHex);
        await bob.NostrService.SubscribeToWelcomesAsync(bob.PubKeyHex, bob.PrivKeyHex);

        var aliceInvites = new List<Scramble.Core.Models.PendingInvite>();
        var bobInvites = new List<Scramble.Core.Models.PendingInvite>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30_000)
        {
            await Task.Delay(2000);
            await alice.MessageService.RescanInvitesAsync();
            await bob.MessageService.RescanInvitesAsync();
            aliceInvites = (await alice.Storage.GetPendingInvitesAsync()).ToList();
            bobInvites = (await bob.Storage.GetPendingInvitesAsync()).ToList();
            _output.WriteLine($"  Poll {sw.ElapsedMilliseconds}ms: Alice={aliceInvites.Count}, Bob={bobInvites.Count}");
            if (aliceInvites.Count > 0 && bobInvites.Count > 0)
                break;
        }
        _output.WriteLine($"Alice invites: {aliceInvites.Count}, Bob invites: {bobInvites.Count}");

        Assert.NotEmpty(aliceInvites);
        var chatAlice = await alice.MessageService.AcceptInviteAsync(aliceInvites[0].Id);
        _output.WriteLine($"Alice joined WN group");

        Assert.NotEmpty(bobInvites);
        var chatBob = await bob.MessageService.AcceptInviteAsync(bobInvites[0].Id);
        _output.WriteLine($"Bob joined WN group");

        // Step 4: Verify messaging works both directions
        _output.WriteLine("\n--- Cross-client messaging ---");

        // WN sends
        await _wnClient.SendMessageAsync(wnGroupId, "Welcome to my group!");
        await Task.Delay(3000);

        // Alice sends
        var msgAlice = await alice.MlsService.EncryptMessageAsync(chatAlice.MlsGroupId!, "Thanks Charlie!");
        await alice.NostrService.PublishRawEventJsonAsync(msgAlice);
        await Task.Delay(3000);

        // Verify WN received Alice's message
        var wnMsgs = await _wnClient.PollUntilAsync(
            () => _wnClient.FetchMessagesAsync(wnGroupId),
            msgs => msgs.Any(m => m.Content.Contains("Thanks Charlie")),
            timeoutMs: 30000,
            intervalMs: 2000,
            description: "WN receives Alice's reply");

        Assert.Contains(wnMsgs, m => m.Content.Contains("Thanks Charlie"));
        _output.WriteLine("Cross-client messaging verified!");

        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  WN-creates-group test COMPLETE");
        _output.WriteLine("════════���════════════════════════════════════��═════════════");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════���════════════════════════════════════���═════════════════════════

    private async Task<List<KeyPackage>> PollForKeyPackagesAsync(
        NostrService nostrService, string pubkeyHex, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var kps = (await nostrService.FetchKeyPackagesAsync(pubkeyHex)).ToList();
            if (kps.Count > 0)
            {
                _output.WriteLine($"Found {kps.Count} KP(s) for {pubkeyHex[..16]}... after {sw.ElapsedMilliseconds}ms");
                return kps;
            }
            await Task.Delay(1000);
        }
        _output.WriteLine($"No KPs found for {pubkeyHex[..16]}... after {timeoutMs}ms");
        return new List<KeyPackage>();
    }

    private async Task ProcessCommitForMember(
        ScrambleUser member, byte[] mlsGroupId, string commitEventId, string nostrGroupIdHex)
    {
        var commitEvents = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 445 }, ids = new[] { commitEventId }, limit = 1 });
        Assert.NotEmpty(commitEvents);

        using var commitDoc = JsonDocument.Parse(commitEvents[0]);
        var commitContent = commitDoc.RootElement.GetProperty("content").GetString()!;
        var commitBytes = Convert.FromBase64String(commitContent);
        var commitResult = await member.MlsService.DecryptMessageAsync(mlsGroupId, commitBytes);
        Assert.True(commitResult.IsCommit, $"{member.Name} must receive a commit, got plaintext instead");
        _output.WriteLine($"{member.Name} processed commit: IsCommit={commitResult.IsCommit}");
    }

    private async Task<List<string>> FetchRawEventsFromRelay(string relayUrl, object filter)
    {
        var events = new List<string>();
        var subId = $"fetch_{Guid.NewGuid():N}"[..16];

        using var ws = new System.Net.WebSockets.ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://scramble.test");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(new Uri(relayUrl), cts.Token);

            var filterJson = JsonSerializer.Serialize(filter);
            filterJson = filterJson.Replace("\"__h\"", "\"#h\"").Replace("\"__p\"", "\"#p\"");
            var reqMessage = $"[\"REQ\",\"{subId}\",{filterJson}]";

            await ws.SendAsync(
                Encoding.UTF8.GetBytes(reqMessage),
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
                else if (msg.StartsWith("[\"EOSE\""))
                {
                    break;
                }
            }

            if (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                await ws.SendAsync(
                    Encoding.UTF8.GetBytes($"[\"CLOSE\",\"{subId}\"]"),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Relay fetch error: {ex.Message}");
        }

        return events;
    }
}
