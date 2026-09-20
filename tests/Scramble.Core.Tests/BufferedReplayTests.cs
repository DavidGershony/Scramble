using System.Reactive.Linq;
using System.Reactive.Subjects;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Marmot.Ingest;
using Scramble.Marmot.Storage.Sqlite;
using Scramble.Nostr.Crypto;
using Xunit;
using CoreKeyPackage = Scramble.Core.Models.KeyPackage;

namespace Scramble.Core.Tests;

/// <summary>
/// Messages the engine held because it could not read them yet, and whether the
/// app ever comes back for them.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for.</b> `MessageIngest` refuses inbound traffic while one of
/// our own commits is staged and unacknowledged — "applying an inbound commit
/// while our own is staged and unacknowledged would fork us from the epoch we are
/// about to ask everyone to adopt" — and answers
/// <see cref="IngestOutcome.Buffered"/>. The bytes are durable and the engine
/// re-runs them through `ReplayAsync` once the group can read them again. Before
/// this, nothing in the app ever asked it to: `ReplayAsync`, `ConvergeAsync` and
/// `DrainAsync` had zero callers, so a message that arrived in that window stayed
/// on disk unread. The engine's own docs call that "silently dropped the message
/// while reporting that it kept it".
/// </para>
/// <para>
/// <b>Written from the contract, not the implementation</b>
/// (`.claude/skills/split-and-mutate`). These tests were authored against a fixed
/// API contract before the implementation existed and without reading it, because
/// a test written by the implementer encodes the same misreading as the code.
/// </para>
/// <para>
/// <b>The first test asserts the window actually opened before asserting anything
/// about replay.</b> That is deliberate: a test that provokes no buffering would
/// pass on an app that never replays anything, and this repo has recorded six
/// tests that read convincingly and verified nothing.
/// </para>
/// </remarks>
public sealed class BufferedReplayTests : IDisposable
{
    private readonly List<string> _paths = [];
    private readonly List<SqliteMarmotStorageProvider> _providers = [];
    private readonly List<DarkMatterMlsService> _services = [];

    private static readonly string[] Relays = ["wss://relay.example.com"];

    public void Dispose()
    {
        foreach (DarkMatterMlsService service in _services)
            service.Dispose();
        foreach (SqliteMarmotStorageProvider provider in _providers)
            provider.Dispose();
        foreach (string path in _paths)
        {
            try { File.Delete(path); }
            catch (IOException) { /* a stray temp file is not worth a failure */ }
        }
    }

    private sealed record Party(DarkMatterMlsService Service, byte[] Secret, string PublicKeyHex);

    private async Task<Party> PartyAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"replay-test-{Guid.NewGuid():N}.db");
        _paths.Add(path);

        var provider = new SqliteMarmotStorageProvider($"Data Source={path}");
        _providers.Add(provider);

        var service = new DarkMatterMlsService(provider);
        _services.Add(service);

        var (secret, publicKey) = Bip340.GenerateKeyPair();
        string publicKeyHex = Convert.ToHexString(publicKey).ToLowerInvariant();
        await service.InitializeAsync(Convert.ToHexString(secret).ToLowerInvariant(), publicKeyHex);

        return new Party(service, secret, publicKeyHex);
    }

    /// <summary>A KeyPackage published the way a caller publishes one.</summary>
    private static async Task<CoreKeyPackage> PublishKeyPackageAsync(Party party)
    {
        CoreKeyPackage keyPackage = await party.Service.GenerateKeyPackageAsync();

        var template = new NostrEventTemplate(
            party.PublicKeyHex,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            30443,
            keyPackage.NostrTags.Select(t => (IReadOnlyList<string>)t).ToList(),
            Convert.ToBase64String(keyPackage.Data));

        byte[] id = template.ComputeId();
        keyPackage.EventJson = NostrEnvelope.Write(template, id, Bip340.Sign(party.Secret, id));
        keyPackage.NostrEventId = Convert.ToHexString(id).ToLowerInvariant();
        keyPackage.OwnerPublicKey = party.PublicKeyHex;
        return keyPackage;
    }

    /// <summary>Alice hosting a group Bob has joined, both on real engines.</summary>
    private async Task<(Party Alice, Party Bob, byte[] GroupId)> JoinedPairAsync()
    {
        Party alice = await PartyAsync();
        Party bob = await PartyAsync();

        MlsGroupInfo group = await alice.Service.CreateGroupAsync("Replay", Relays);

        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(bob);
        MlsWelcome staged = await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);
        await alice.Service.MergeStagedAsync(group.GroupId);

        await bob.Service.ProcessWelcomeAsync(staged.WelcomeData, "0".PadLeft(64, '0'));

        return (alice, bob, group.GroupId);
    }

    // ------------------------------------------------------------- the claim

    [Fact]
    public async Task AMessageHeldWhileOurCommitWasOutstandingArrivesAfterTheMerge()
    {
        var (alice, bob, groupId) = await JoinedPairAsync();

        // Alice stages a commit and does not resolve it. From here the engine
        // refuses inbound traffic rather than risk applying it against a state
        // that may still roll back.
        await alice.Service.UpdateKeysAsync(groupId);
        Assert.True(alice.Service.HasPendingCommit(groupId));

        byte[] envelope = await bob.Service.EncryptMessageAsync(groupId, "held while you committed");

        // The honesty gate. If this does not throw Buffered then the window this
        // whole feature exists for was never open, and everything below would be
        // passing for the wrong reason.
        MlsIngestRefusedException refused =
            await Assert.ThrowsAsync<MlsIngestRefusedException>(
                () => alice.Service.DecryptMessageAsync(groupId, envelope));
        Assert.IsType<IngestOutcome.Buffered>(refused.Outcome);

        // Nothing is readable yet, so nothing should come back yet either.
        Assert.Empty(await alice.Service.ReplayBufferedMessagesAsync(groupId));

        // Resolving the publish is what makes the group ingestible again.
        await alice.Service.MergeStagedAsync(groupId);

        IReadOnlyList<MlsDecryptedMessage> replayed =
            await alice.Service.ReplayBufferedMessagesAsync(groupId);

        MlsDecryptedMessage message = Assert.Single(replayed);
        Assert.Equal("held while you committed", message.Plaintext);
        Assert.Equal(bob.PublicKeyHex, message.SenderPublicKey);
        Assert.False(message.IsCommit);
    }

    [Fact]
    public async Task AHeldMessageArrivesAfterTheStagedCommitIsClearedToo()
    {
        // The other way a publish resolves. A commit that is abandoned leaves the
        // group where it was, so a message framed at that epoch is readable again
        // — and a caller that only replayed after a merge would lose exactly the
        // messages that arrived during a failed publish, which is the worst time
        // to lose them.
        var (alice, bob, groupId) = await JoinedPairAsync();

        await alice.Service.UpdateKeysAsync(groupId);
        byte[] envelope = await bob.Service.EncryptMessageAsync(groupId, "held during a failed publish");

        await Assert.ThrowsAsync<MlsIngestRefusedException>(
            () => alice.Service.DecryptMessageAsync(groupId, envelope));

        await alice.Service.ClearStagedAsync(groupId);

        MlsDecryptedMessage message =
            Assert.Single(await alice.Service.ReplayBufferedMessagesAsync(groupId));
        Assert.Equal("held during a failed publish", message.Plaintext);
    }

    [Fact]
    public async Task ReplayingWithNothingHeldIsEmptyRatherThanAnError()
    {
        // The common case by a wide margin: this is called after every commit,
        // and almost always there is nothing waiting. It must be cheap and quiet
        // — a refusal here would make the caller treat routine success as failure.
        var (alice, _, groupId) = await JoinedPairAsync();

        Assert.Empty(await alice.Service.ReplayBufferedMessagesAsync(groupId));
    }

    [Fact]
    public async Task AMessageIsReplayedOnceAndNotAgain()
    {
        // Delivery is not idempotent from the user's side: a message handed over
        // twice is a duplicate in the conversation. The engine retires what it
        // replays, and this pins that the contract does not re-offer it.
        var (alice, bob, groupId) = await JoinedPairAsync();

        await alice.Service.UpdateKeysAsync(groupId);
        byte[] envelope = await bob.Service.EncryptMessageAsync(groupId, "only once");
        await Assert.ThrowsAsync<MlsIngestRefusedException>(
            () => alice.Service.DecryptMessageAsync(groupId, envelope));
        await alice.Service.MergeStagedAsync(groupId);

        Assert.Single(await alice.Service.ReplayBufferedMessagesAsync(groupId));
        Assert.Empty(await alice.Service.ReplayBufferedMessagesAsync(groupId));
    }

    [Fact]
    public async Task TheLegacyBackendAnswersEmptyRatherThanRefusing()
    {
        // ManagedMlsService is still constructed by tests until marmot-cs goes at
        // step 5, and MessageService calls this after every commit regardless of
        // which engine is underneath. A refusal here would break those paths for
        // a capability the old engine simply does not have.
        var legacy = new ManagedMlsService();

        Assert.Empty(await legacy.ReplayBufferedMessagesAsync([0x01, 0x02, 0x03]));
    }
}

/// <summary>
/// Whether <see cref="MessageService"/> asks for held messages at the moments a
/// publish resolves, and what it does with what comes back.
/// </summary>
/// <remarks>
/// Mocked deliberately: what is under test is the wiring — that the call happens
/// at the right moments, that a replayed message reaches the conversation, that it
/// cannot arrive twice, and that a failure in recovery cannot fail the commit that
/// triggered it. Whether the engine really replays is
/// <see cref="BufferedReplayTests"/>'s job, on real engines.
/// </remarks>
public sealed class BufferedReplayWiringTests : IDisposable
{
    private readonly Mock<IStorageService> _storage = new();
    private readonly Mock<INostrService> _nostr = new();
    private readonly Mock<IMlsService> _mls = new();
    private readonly Subject<NostrEventReceived> _events = new();
    private readonly MessageService _sut;

    private static readonly byte[] GroupId = [0x01, 0x02, 0x03, 0x04];
    private const string ChatId = "chat-1";
    private const string MemberPubKey = "cc";

    private readonly List<Message> _saved = [];
    private bool _commitPending;

    private readonly Chat _chat = new()
    {
        Id = ChatId,
        Name = "Replay",
        Type = ChatType.Group,
        MlsGroupId = GroupId,
        ParticipantPublicKeys = [new string('a', 64), new string('c', 64)],
    };

    public BufferedReplayWiringTests()
    {
        _nostr.Setup(n => n.Events).Returns(_events.AsObservable());
        _nostr.Setup(n => n.ConnectedRelayUrls).Returns(new List<string> { "wss://relay.example.com" });
        _nostr.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>())).ReturnsAsync("commit-event-1");

        _storage.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        _storage.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(new User
        {
            Id = "user-1",
            PublicKeyHex = new string('a', 64),
            PrivateKeyHex = new string('b', 64),
            Npub = "npub1test",
            DisplayName = "Test User",
            CreatedAt = DateTime.UtcNow,
        });
        _storage.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { _chat });
        _storage.Setup(s => s.GetChatAsync(ChatId)).ReturnsAsync(_chat);
        _storage.Setup(s => s.GetChatByGroupIdAsync(It.IsAny<string>())).ReturnsAsync(_chat);
        _storage.Setup(s => s.SaveChatAsync(It.IsAny<Chat>())).Returns(Task.CompletedTask);
        _storage.Setup(s => s.GetUsersByPublicKeysAsync(It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(new Dictionary<string, User>());

        // A store that remembers, so "not delivered twice" can be asserted through
        // the contract's own dedup key rather than by counting mock calls.
        _storage.Setup(s => s.SaveMessageAsync(It.IsAny<Message>()))
            .Callback((Message m) => _saved.Add(m))
            .Returns(Task.CompletedTask);
        _storage.Setup(s => s.GetMessageByRumorEventIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => _saved.FirstOrDefault(m => m.RumorEventId == id));
        _storage.Setup(s => s.MessageExistsByNostrEventIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => _saved.Any(m => m.NostrEventId == id));

        _mls.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _mls.Setup(m => m.GetAdminPubkeys(It.IsAny<byte[]>())).Returns(new List<string>());
        _mls.Setup(m => m.HasPendingCommit(It.IsAny<byte[]>())).Returns(() => _commitPending);
        _mls.Setup(m => m.StageRemoveMemberAsync(It.IsAny<byte[]>(), It.IsAny<string>()))
            .Callback(() => _commitPending = true)
            .ReturnsAsync([0xAA, 0xBB]);
        _mls.Setup(m => m.MergeStagedAsync(It.IsAny<byte[]>()))
            .Callback(() => _commitPending = false)
            .Returns(Task.CompletedTask);
        _mls.Setup(m => m.ClearStagedAsync(It.IsAny<byte[]>()))
            .Callback(() => _commitPending = false)
            .Returns(Task.CompletedTask);
        _mls.Setup(m => m.ReplayBufferedMessagesAsync(It.IsAny<byte[]>()))
            .ReturnsAsync(Array.Empty<MlsDecryptedMessage>());

        _sut = new MessageService(_storage.Object, _nostr.Object, _mls.Object);
    }

    public void Dispose()
    {
        _events.Dispose();
        _sut.Dispose();
    }

    private static MlsDecryptedMessage Held(string text, string rumorId, long createdAt = 0) => new()
    {
        SenderPublicKey = new string('c', 64),
        Plaintext = text,
        Epoch = 2,
        RumorKind = 9,
        RumorEventId = rumorId,
        RumorCreatedAt = createdAt,
    };

    [Fact]
    public async Task ResolvingACommitAsksTheEngineForHeldMessages()
    {
        // The whole defect was that nobody ever asked. Every commit path resolves
        // a publish, and each one is a moment when something held may have become
        // readable.
        await _sut.InitializeAsync();

        await _sut.RemoveMemberAsync(ChatId, MemberPubKey);

        _mls.Verify(m => m.ReplayBufferedMessagesAsync(GroupId), Times.AtLeastOnce);
    }

    [Fact]
    public async Task AHeldMessageReachesTheConversation()
    {
        await _sut.InitializeAsync();
        _mls.Setup(m => m.ReplayBufferedMessagesAsync(It.IsAny<byte[]>()))
            .ReturnsAsync([Held("held while you committed", "rumor-1")]);

        var seen = new List<Message>();
        using var sub = _sut.NewMessages.Subscribe(seen.Add);

        await _sut.RemoveMemberAsync(ChatId, MemberPubKey);
        await Task.Delay(200);

        Assert.Contains(seen, m => m.Content == "held while you committed");
        Assert.Contains(_saved, m => m.Content == "held while you committed");
    }

    [Fact]
    public async Task AHeldMessageAlreadyInTheConversationIsNotAddedTwice()
    {
        // A replay is recovery, and recovery that duplicates is its own bug. The
        // dedup cannot use the Nostr event id — a replayed message never had one,
        // it arrived as MLS bytes — so the rumor id is the key.
        await _sut.InitializeAsync();
        _mls.Setup(m => m.ReplayBufferedMessagesAsync(It.IsAny<byte[]>()))
            .ReturnsAsync([Held("only once", "rumor-1")]);

        var seen = new List<Message>();
        using var sub = _sut.NewMessages.Subscribe(seen.Add);

        await _sut.RemoveMemberAsync(ChatId, MemberPubKey);
        await Task.Delay(200);
        await _sut.RemoveMemberAsync(ChatId, MemberPubKey);
        await Task.Delay(200);

        Assert.Single(seen.Where(m => m.Content == "only once"));
        Assert.Single(_saved.Where(m => m.Content == "only once"));
    }

    [Fact]
    public async Task AReplayedMessageKeepsTheTimeItWasSent()
    {
        // Not in the contract I wrote — the implementer added RumorCreatedAt and
        // said so, and it is right: a replayed message has no transport event to
        // take a time from, so stamping it with the moment of the replay sorts an
        // old message to the bottom of a conversation it belongs in the middle of.
        // Kept, and therefore tested.
        await _sut.InitializeAsync();
        var sent = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds();
        _mls.Setup(m => m.ReplayBufferedMessagesAsync(It.IsAny<byte[]>()))
            .ReturnsAsync([Held("sent three hours ago", "rumor-time", sent)]);

        await _sut.RemoveMemberAsync(ChatId, MemberPubKey);
        await Task.Delay(200);

        Message stored = Assert.Single(_saved.Where(m => m.Content == "sent three hours ago"));
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(sent).UtcDateTime,
            stored.Timestamp);
    }

    [Fact]
    public async Task AFailedPublishAlsoAsksForHeldMessages()
    {
        // Added because a mutation survived: deleting the replay call from the
        // rollback path left all nine of these tests green. The merge path was
        // covered and this one was not, and it is the arm that matters more — a
        // publish that fails is precisely when something else's traffic has been
        // piling up behind our unacknowledged commit. Clearing the commit makes
        // the group readable again, so the obligation is identical to a merge's.
        await _sut.InitializeAsync();
        _nostr.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(new PublishUnconfirmedException("event-1", 445, "no relay accepted"));
        _mls.Setup(m => m.ReplayBufferedMessagesAsync(It.IsAny<byte[]>()))
            .ReturnsAsync([Held("held while the publish failed", "rumor-rollback")]);

        var seen = new List<Message>();
        using var sub = _sut.NewMessages.Subscribe(seen.Add);

        await Assert.ThrowsAsync<PublishUnconfirmedException>(
            () => _sut.RemoveMemberAsync(ChatId, MemberPubKey));
        await Task.Delay(200);

        _mls.Verify(m => m.ClearStagedAsync(GroupId), Times.Once);
        _mls.Verify(m => m.ReplayBufferedMessagesAsync(GroupId), Times.AtLeastOnce);
        Assert.Contains(seen, m => m.Content == "held while the publish failed");
    }

    [Fact]
    public async Task AFailingReplayDoesNotFailTheCommitThatTriggeredIt()
    {
        // The commit succeeded and the group has moved. Replay is recovery after
        // the fact, so a failure in it must not be reported as the commit having
        // failed — the same reasoning that makes RollbackStagedCommitAsync never
        // throw.
        await _sut.InitializeAsync();
        _mls.Setup(m => m.ReplayBufferedMessagesAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(new InvalidOperationException("replay exploded"));

        await _sut.RemoveMemberAsync(ChatId, MemberPubKey);

        _mls.Verify(m => m.MergeStagedAsync(GroupId), Times.Once);
        Assert.DoesNotContain(MemberPubKey, _chat.ParticipantPublicKeys);
    }
}
