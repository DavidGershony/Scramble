using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
using Xunit;

namespace Scramble.Diagnostics.RelayHarness;

/// <summary>
/// Drives Scramble's <see cref="MessageService.AddMemberAsync"/> against a single
/// <see cref="FaultyRelay"/> with fault knobs to verify MIP-03 compliance:
/// AddMemberAsync stages the commit, publishes it, advances local state only on a
/// relay's confirmation, and rolls back without one.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the Dark Matter engine, which is what the app registers.</b> These tests
/// ran on <c>ManagedMlsService</c> until P11's flip, and the distinction is not
/// cosmetic: what a staging call returns there is MIP-03 ciphertext, and here it
/// is a finished signed kind-445. The publish member is different in consequence
/// — <see cref="INostrService.PublishCommitEventAsync"/> puts the event on the
/// wire as it stands — and the confirmation it demands is the whole subject of
/// this file. Testing the confirmation against an engine the app no longer uses
/// would prove it for a path that no longer exists.
/// </para>
/// <para>
/// <b>And it is the only place the no-OK path can be proved.</b> A relay that
/// takes an event and never answers is not something a mock can stand in for: the
/// timeout, the tracker and the exception are all in <c>NostrService</c>, between
/// the socket and the caller.
/// </para>
/// </remarks>
[Trait("Category", "RelayHarness")]
public class PublishFailureTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();
    private readonly List<NostrService> _nostrServices = new();
    private readonly List<MessageService> _messageServices = new();
    private FaultyRelay? _relay;

    public PublishFailureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _relay = new FaultyRelay();
        await _relay.StartAsync();
        _output.WriteLine($"FaultyRelay listening at {_relay.WsUrl}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ms in _messageServices) ms.Dispose();
        foreach (var ns in _nostrServices)
        {
            try { await ns.DisconnectAsync(); }
            catch { }
            (ns as IDisposable)?.Dispose();
        }
        if (_relay != null) await _relay.DisposeAsync();

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var p in _dbPaths)
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch { }
        }
    }

    private record ScrambleUser(
        string Name, string PubKeyHex, string PrivKeyHex, string Npub, string Nsec,
        NostrService NostrService, StorageService Storage, IMlsService MlsService,
        MessageService MessageService);

    private async Task<ScrambleUser> CreateUserAsync(string name)
    {
        var nostr = new NostrService();
        _nostrServices.Add(nostr);

        var keys = nostr.GenerateKeyPair();
        var dbPath = Path.Combine(Path.GetTempPath(), $"scramble_relay_fault_{name}_{Guid.NewGuid()}.db");
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

        IMlsService mls = DarkMatterMlsServiceFactory.Create(storage);
        await mls.InitializeAsync(keys.privateKeyHex, keys.publicKeyHex);
        var messages = new MessageService(storage, nostr, mls);
        _messageServices.Add(messages);
        await messages.InitializeAsync();

        _output.WriteLine($"Created '{name}': pubkey={keys.publicKeyHex[..16]}...");
        return new ScrambleUser(name, keys.publicKeyHex, keys.privateKeyHex,
            keys.npub, keys.nsec, nostr, storage, mls, messages);
    }

    [Fact]
    public async Task AddMember_OkDropped_ThrowsPublishUnconfirmed_AndLocalStateUnchanged()
    {
        Assert.NotNull(_relay);

        // Setup: Alice and Bob, both connected to the FaultyRelay. Bob publishes
        // his KeyPackage (the relay accepts it normally for now — DropOk is off).
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");

        await alice.NostrService.ConnectAsync(_relay.WsUrl);
        await bob.NostrService.ConnectAsync(_relay.WsUrl);
        await Task.Delay(500);

        var bobKp = await bob.MlsService.GenerateKeyPackageAsync();
        var bobKpEventId = await bob.NostrService.PublishKeyPackageAsync(
            bobKp.Data, bob.PrivKeyHex, bobKp.NostrTags);
        _output.WriteLine($"Bob KP published normally: event={bobKpEventId[..16]}...");
        await Task.Delay(500);

        // Alice creates the group (no members yet, so no commit publish).
        var groupRelays = new[] { _relay.WsUrl };
        var groupInfo = await alice.MlsService.CreateGroupAsync("Fault Test Group", groupRelays);
        var nostrGroupId = alice.MlsService.GetNostrGroupId(groupInfo.GroupId);
        var nostrGroupIdHex = nostrGroupId != null
            ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Fault Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { alice.PubKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await alice.Storage.SaveChatAsync(chat);

        // ── The fault ──
        // From now on, every EVENT the relay accepts is stored + broadcast,
        // but no OK is sent back. Scramble's PublishEventAsync will time out
        // on the OK wait (5s) and return the event id with a warning.
        _relay.Faults.DropOk = true;
        _output.WriteLine("Fault enabled: DropOk = true");

        // ── The act ──
        // MIP-03 compliance: AddMember MUST throw when no relay confirms the commit.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var thrown = await Assert.ThrowsAsync<PublishUnconfirmedException>(
            () => alice.MessageService.AddMemberAsync(chat.Id, bob.PubKeyHex));
        sw.Stop();
        _output.WriteLine($"AddMember threw {thrown.GetType().Name} in {sw.ElapsedMilliseconds}ms: {thrown.Message}");

        // ── Assertions ──
        // 1. AddMember threw PublishUnconfirmedException — MIP-03 §"Commit Message Race Conditions" step 2.
        Assert.Equal(445, thrown.Kind);

        // 2. The dedup marker was NOT written — local state should not have changed.
        var messages = await alice.Storage.GetMessagesForChatAsync(chat.Id);
        var commitMarker = messages.FirstOrDefault(m => m.Type == MessageType.System && m.Content == "[commit]");
        Assert.Null(commitMarker);
        _output.WriteLine("COMPLIANCE: no dedup marker written (commit not confirmed).");

        // 3. The participants list was NOT updated — Bob did not join.
        var aliceChat = await alice.Storage.GetChatAsync(chat.Id);
        Assert.NotNull(aliceChat);
        Assert.DoesNotContain(bob.PubKeyHex, aliceChat!.ParticipantPublicKeys);
        _output.WriteLine("COMPLIANCE: participants not updated (commit not confirmed).");
    }

    [Fact]
    public async Task AddMember_RelayBlackholesEvents_ThrowsAndRollsBack_NoStateDivergence()
    {
        Assert.NotNull(_relay);

        // Setup as before — KP publish happens cleanly before the fault is enabled.
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");

        await alice.NostrService.ConnectAsync(_relay.WsUrl);
        await bob.NostrService.ConnectAsync(_relay.WsUrl);
        await Task.Delay(500);

        var bobKp = await bob.MlsService.GenerateKeyPackageAsync();
        await bob.NostrService.PublishKeyPackageAsync(bobKp.Data, bob.PrivKeyHex, bobKp.NostrTags);
        await Task.Delay(500);

        // Bob also subscribes to his own welcome inbox so any inbound 1059
        // gift-wrap would land in his pending invites — proving that under
        // the fault, no welcome ever reached him.
        await bob.NostrService.SubscribeToWelcomesAsync(bob.PubKeyHex, bob.PrivKeyHex);
        await Task.Delay(500);

        var groupRelays = new[] { _relay.WsUrl };
        var groupInfo = await alice.MlsService.CreateGroupAsync("Blackhole Test Group", groupRelays);
        var nostrGroupId = alice.MlsService.GetNostrGroupId(groupInfo.GroupId);

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Blackhole Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { alice.PubKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await alice.Storage.SaveChatAsync(chat);

        var relayEventCountBefore = _relay.StoredEventCount;
        _output.WriteLine($"Pre-fault relay event count: {relayEventCountBefore}");

        // ── The fault ──
        // Strongest variant: the relay completely drops EVENTs. No OK, no store,
        // no broadcast. From Scramble's perspective: TCP send succeeded, OK never
        // arrived, code path keeps going as we saw in the previous test. From
        // Bob's perspective: nothing happened — no commit, no welcome, no event.
        _relay.Faults.BlackholeEvent = true;
        _output.WriteLine("Fault enabled: BlackholeEvent = true");

        // MIP-03 compliance: AddMember MUST throw when events are blackholed (no OK received).
        var thrown = await Assert.ThrowsAsync<PublishUnconfirmedException>(
            () => alice.MessageService.AddMemberAsync(chat.Id, bob.PubKeyHex));
        _output.WriteLine($"AddMember threw: {thrown.Message}");

        // ── Assertions ──
        // 1. Relay accepted no new events under the fault.
        Assert.Equal(relayEventCountBefore, _relay.StoredEventCount);
        _output.WriteLine($"Relay still has {_relay.StoredEventCount} event(s) — commit + welcome both blackholed.");

        // 2. Alice's local view does NOT show Bob as a member (staged commit was rolled back).
        var aliceChat = await alice.Storage.GetChatAsync(chat.Id);
        Assert.NotNull(aliceChat);
        Assert.DoesNotContain(bob.PubKeyHex, aliceChat!.ParticipantPublicKeys);
        _output.WriteLine("COMPLIANCE: participants not updated — staged commit rolled back.");

        // 3. Alice's local MLS state did NOT advance (staged commit was cleared).
        Assert.Equal(groupInfo.Epoch, aliceChat.MlsEpoch);
        _output.WriteLine("COMPLIANCE: epoch unchanged — no silent state divergence.");
    }
}
