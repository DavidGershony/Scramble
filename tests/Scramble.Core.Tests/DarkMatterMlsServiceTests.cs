using Scramble.Marmot.AppComponents;
using System.Text;
using System.Text.Json;
using Scramble.Core.Services;
using Scramble.Marmot;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Ingest;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Storage.Sqlite;
using Scramble.Nostr.Crypto;
using Moq;
using Xunit;
using CoreKeyPackage = Scramble.Core.Models.KeyPackage;

namespace Scramble.Core.Tests;

/// <summary>
/// <see cref="DarkMatterMlsService"/> — the seam between a service-shaped
/// contract and a session layer that owns publish-before-apply.
/// </summary>
/// <remarks>
/// <para>
/// <b>No <c>[Trait]</c>, on purpose.</b> The desktop unit gate filters
/// <c>Category!=Relay&amp;Category!=Integration</c>, so an untraited class runs
/// there and a wrongly-traited one silently does not — a whole class was lost
/// that way once, reporting a genuine pass with zero skips. Nothing here needs
/// a relay or Docker: the only transport involved is the one this service
/// deliberately does not have.
/// </para>
/// <para>
/// <b>What is worth testing here is the disagreement, not the passthrough.</b>
/// The engine's own suite already proves that groups build, commits apply and
/// messages decrypt. What it cannot prove is that the translation kept its
/// promises: that a staged commit is still staged when the caller is handed
/// bytes, that the caller's late answer moves the group exactly once, that the
/// ratchet advanced by an encrypt is written down before the bytes escape, and
/// that a member with nothing honest to do refuses rather than inventing.
/// </para>
/// </remarks>
public sealed class DarkMatterMlsServiceTests : IDisposable
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
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A stray temp file is not worth failing a run over.
            }
        }
    }

    // ------------------------------------------------------------- fixtures

    /// <summary>One identity, its own store, and a service over it.</summary>
    private sealed record Party(
        DarkMatterMlsService Service, byte[] Secret, string PublicKeyHex);

    private async Task<Party> PartyAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dm-mls-test-{Guid.NewGuid():N}.db");
        _paths.Add(path);

        var provider = new SqliteMarmotStorageProvider($"Data Source={path}");
        _providers.Add(provider);

        var service = new DarkMatterMlsService(provider);
        _services.Add(service);

        var (secret, publicKey) = Bip340.GenerateKeyPair();
        string publicKeyHex = Convert.ToHexString(publicKey).ToLowerInvariant();

        await service.InitializeAsync(
            Convert.ToHexString(secret).ToLowerInvariant(), publicKeyHex);

        return new Party(service, secret, publicKeyHex);
    }

    private SqliteMarmotStorageProvider StoreOf(Party party) =>
        _providers[_services.IndexOf(party.Service)];

    /// <summary>
    /// Publishes a party's KeyPackage the way a caller would: the service hands
    /// back tags and bytes, the caller signs the kind-30443 event.
    /// </summary>
    /// <remarks>
    /// Deliberately not a shortcut past the event. <c>StageAddMemberAsync</c>
    /// parses this envelope and verifies its id, signature and tag shape before
    /// reading a single field, so a test that handed the service raw bytes would
    /// be skipping the check that makes the invitee's account key trustworthy.
    /// </remarks>
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
        byte[] signature = Bip340.Sign(party.Secret, id);

        keyPackage.EventJson = NostrEnvelope.Write(template, id, signature);
        keyPackage.NostrEventId = Convert.ToHexString(id).ToLowerInvariant();
        keyPackage.OwnerPublicKey = party.PublicKeyHex;

        return keyPackage;
    }

    /// <summary>Alice with a group, and Bob holding a published KeyPackage.</summary>
    private async Task<(Party Alice, Party Bob, MlsGroupInfo Group)> PairAsync()
    {
        Party alice = await PartyAsync();
        Party bob = await PartyAsync();

        MlsGroupInfo group = await alice.Service.CreateGroupAsync("Rakes", Relays);

        return (alice, bob, group);
    }

    // -------------------------------------------- staging, and staying staged

    [Fact]
    public async Task StagingAnAddLeavesTheGroupWhereItWasAndTheCommitOutstanding()
    {
        // The whole reconciliation in one assertion pair. CommitAsync owns the
        // publish line and would normally resolve the commit inside itself; the
        // transport this service gives it answers Indeterminate, which is the
        // literally true answer -- the bytes have gone to a caller who has not
        // published them yet. Everything must therefore still be outstanding
        // when StageAddMemberAsync returns, because "outstanding" is exactly
        // what the contract means by "staged".
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(bob);
        MlsWelcome staged = await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);

        Assert.True(alice.Service.HasPendingCommit(group.GroupId));

        MlsGroupInfo? after = await alice.Service.GetGroupInfoAsync(group.GroupId);
        Assert.NotNull(after);
        Assert.Equal(group.Epoch, after.Epoch);

        Assert.NotNull(staged.CommitData);
        Assert.NotEmpty(staged.WelcomeData);
        Assert.Equal(bob.PublicKeyHex, staged.RecipientPublicKey);
        Assert.Equal(keyPackage.NostrEventId, staged.KeyPackageEventId);
    }

    [Fact]
    public async Task CommitDataIsAPublishableKind445AddressedToTheGroup()
    {
        // CommitData changed meaning at the cutover: it used to be raw MIP-03
        // ciphertext the caller ran back through EncryptCommitAsync, and it is
        // now a finished event. That is load-bearing for MessageService, so it
        // is pinned rather than described -- including the h tag, which is the
        // group's transport address and not its MLS id.
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(bob);
        MlsWelcome staged = await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);

        using JsonDocument document = JsonDocument.Parse(staged.CommitData!);
        JsonElement root = document.RootElement;

        Assert.Equal(445, root.GetProperty("kind").GetInt32());
        Assert.Equal(64, root.GetProperty("id").GetString()!.Length);
        Assert.Equal(128, root.GetProperty("sig").GetString()!.Length);

        string expected = Convert
            .ToHexString(alice.Service.GetNostrGroupId(group.GroupId)!)
            .ToLowerInvariant();

        List<JsonElement> tags = [.. root.GetProperty("tags").EnumerateArray()];
        JsonElement hTag = Assert.Single(tags, t => t[0].GetString() == "h");

        Assert.Equal(expected, hTag[1].GetString());

        // The MLS group id is not the transport address, and must not be: the
        // transport id is public and deriving one from the other would let a
        // relay link the group.
        Assert.NotEqual(Convert.ToHexString(group.GroupId).ToLowerInvariant(), hTag[1].GetString());
    }

    [Fact]
    public async Task MergingAdvancesTheGroupAndClearsTheOutstandingCommit()
    {
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(bob);
        await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);

        await alice.Service.MergeStagedAsync(group.GroupId);

        Assert.False(alice.Service.HasPendingCommit(group.GroupId));

        MlsGroupInfo? after = await alice.Service.GetGroupInfoAsync(group.GroupId);
        Assert.NotNull(after);
        Assert.Equal(group.Epoch + 1, after.Epoch);
        Assert.Contains(bob.PublicKeyHex, after.MemberPublicKeys);
    }

    [Fact]
    public async Task ClearingLeavesTheGroupWhereItWasAndDropsTheCommit()
    {
        // The asymmetry the engine insists on: only a definite refusal
        // authorises discarding a commit, and when that refusal comes the group
        // must be exactly where it started -- not one epoch on, and not holding
        // a commit that blocks the next one.
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(bob);
        await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);

        await alice.Service.ClearStagedAsync(group.GroupId);

        Assert.False(alice.Service.HasPendingCommit(group.GroupId));

        MlsGroupInfo? after = await alice.Service.GetGroupInfoAsync(group.GroupId);
        Assert.NotNull(after);
        Assert.Equal(group.Epoch, after.Epoch);
        Assert.DoesNotContain(bob.PublicKeyHex, after.MemberPublicKeys);
    }

    [Fact]
    public async Task AMergedGroupCanStillSend()
    {
        // This is the test for the line that is easiest to leave out. Hydration
        // settles the staged commit but never touches the epoch state machine,
        // because on its ordinary path that state was created in another
        // process. Here it was created in this one, by CommitAsync, and a group
        // left PendingPublish refuses input and refuses to be committed in for
        // the rest of the session -- silently, because everything else about it
        // looks healthy.
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(bob);
        await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);
        await alice.Service.MergeStagedAsync(group.GroupId);

        // A second commit is only possible from a settled group.
        byte[] rotation = await alice.Service.UpdateKeysAsync(group.GroupId);

        Assert.NotEmpty(rotation);
        Assert.True(alice.Service.HasPendingCommit(group.GroupId));
    }

    [Fact]
    public async Task ResolvingACommitThatWasNeverStagedIsRefused()
    {
        var (alice, _, group) = await PairAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => alice.Service.MergeStagedAsync(group.GroupId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => alice.Service.ClearStagedAsync(group.GroupId));
    }

    // ---------------------------------------------------- the two-party round

    [Fact]
    public async Task AMessageSentThroughTheAdapterIsReadThroughTheAdapter()
    {
        // End to end through this class and nothing else: create, invite,
        // publish the commit (by not publishing it, which is what a test relay
        // amounts to), merge, join from the Welcome, send, receive. Every step
        // is a contract member, so anything the translation dropped shows up
        // here rather than in the engine's own suite.
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(bob);
        MlsWelcome staged = await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);
        await alice.Service.MergeStagedAsync(group.GroupId);

        MlsGroupInfo joined = await bob.Service.ProcessWelcomeAsync(
            staged.WelcomeData, "0".PadLeft(64, '0'));

        Assert.Equal(
            Convert.ToHexString(group.GroupId), Convert.ToHexString(joined.GroupId));
        Assert.Equal("Rakes", joined.GroupName);

        byte[] envelope = await alice.Service.EncryptMessageAsync(group.GroupId, "hello 😀");

        MlsDecryptedMessage received =
            await bob.Service.DecryptMessageAsync(joined.GroupId, envelope);

        Assert.Equal("hello 😀", received.Plaintext);
        Assert.Equal(alice.PublicKeyHex, received.SenderPublicKey);
        Assert.Equal(9, received.RumorKind);
        Assert.False(received.IsCommit);

        // The id the sender reports and the id the receiver computes are the
        // same canonical NIP-01 hash. MessageService stores the first and
        // matches reactions against the second, so a divergence would show as
        // reactions that never attach.
        Assert.Equal(alice.Service.LastEncryptedRumorEventId, received.RumorEventId);
    }

    [Fact]
    public async Task AReactionArrivesAsNip25AndComesBackAsAnEmoji()
    {
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(bob);
        MlsWelcome staged = await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);
        await alice.Service.MergeStagedAsync(group.GroupId);

        MlsGroupInfo joined = await bob.Service.ProcessWelcomeAsync(
            staged.WelcomeData, "0".PadLeft(64, '0'));

        byte[] message = await alice.Service.EncryptMessageAsync(group.GroupId, "hi");
        await bob.Service.DecryptMessageAsync(joined.GroupId, message);

        string target = alice.Service.LastEncryptedRumorEventId!;

        byte[] reaction = await alice.Service.EncryptReactionAsync(
            group.GroupId, "\U0001F44D", target);

        MlsDecryptedMessage received =
            await bob.Service.DecryptMessageAsync(joined.GroupId, reaction);

        Assert.Equal(7, received.RumorKind);
        Assert.Equal(target, received.ReactionTargetEventId);

        // "+" on the wire, because that is what peers send and expect; the
        // emoji is the app's vocabulary and is restored on the way in.
        Assert.Equal("+", received.Plaintext);
        Assert.Equal("\U0001F44D", received.ReactionEmoji);
    }

    [Fact]
    public async Task AnIdentityThatIsNotOurLeafIsCaughtBeforeAnythingIsSent()
    {
        // The reason the sender identity is read off the ratchet tree and not
        // off the identity the service was initialised with: taken from
        // configuration, MarmotAppEvent.RequireSender would be comparing a
        // value with itself and would pass for a message every receiver
        // rejects. Only a service whose configured identity differs from its
        // own leaf can tell the two apart, which is what the injected proof
        // signer builds here -- the group's leaf belongs to the signer, the
        // configured identity does not.
        string path = Path.Combine(Path.GetTempPath(), $"dm-mls-test-{Guid.NewGuid():N}.db");
        _paths.Add(path);

        var provider = new SqliteMarmotStorageProvider($"Data Source={path}");
        _providers.Add(provider);

        var leafSigner = new StandaloneProofSigner();
        var service = new DarkMatterMlsService(provider, leafSigner);
        _services.Add(service);

        var (secret, publicKey) = Bip340.GenerateKeyPair();
        string configured = Convert.ToHexString(publicKey).ToLowerInvariant();

        Assert.NotEqual(leafSigner.PublicKeyHex, configured);

        await service.InitializeAsync(
            Convert.ToHexString(secret).ToLowerInvariant(), configured);

        MlsGroupInfo group = await service.CreateGroupAsync("Rakes", Relays);

        // The group's only member is the signer's account, not the configured
        // one -- so an event authored by the configured identity is not ours to
        // send, and that has to be refused here rather than discovered by a
        // peer.
        Assert.Equal([leafSigner.PublicKeyHex], group.MemberPublicKeys);

        await Assert.ThrowsAnyAsync<Exception>(
            () => service.EncryptMessageAsync(group.GroupId, "hello"));
    }

    /// <summary>A proof signer with an account key of its own.</summary>
    private sealed class StandaloneProofSigner : IAccountIdentityProofSigner
    {
        private readonly byte[] _secret;

        public StandaloneProofSigner()
        {
            var (secret, publicKey) = Bip340.GenerateKeyPair();
            _secret = secret;
            AccountPublicKey = publicKey;
        }

        public ReadOnlyMemory<byte> AccountPublicKey { get; }

        public string PublicKeyHex =>
            Convert.ToHexString(AccountPublicKey.Span).ToLowerInvariant();

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(_secret, template.ComputeId()));
    }

    [Fact]
    public async Task EncryptingWritesTheAdvancedRatchetDownBeforeReturningTheBytes()
    {
        // Sealing a message moves the sender ratchet in memory. If that is not
        // persisted before the bytes leave, a crash comes back with the
        // generation counter rewound and the next message encrypts different
        // plaintext under a key and nonce this one already used. Only
        // observable in storage, which is why this reads the record rather than
        // the service.
        var (alice, _, group) = await PairAsync();

        var groupId = new GroupId(group.GroupId);
        GroupRecord before = (await StoreOf(alice).GetGroupAsync(groupId))!;

        await alice.Service.EncryptMessageAsync(group.GroupId, "hello");

        GroupRecord after = (await StoreOf(alice).GetGroupAsync(groupId))!;

        Assert.NotNull(before.LiveState);
        Assert.NotNull(after.LiveState);
        Assert.False(before.LiveState.AsSpan().SequenceEqual(after.LiveState));

        // The epoch did not move -- a send is not a commit -- so the record's
        // two columns still describe one fact.
        Assert.Equal(before.Epoch, after.Epoch);
    }

    [Fact]
    public async Task AGroupThatRemovedUsWillNotEncrypt()
    {
        var (alice, _, group) = await PairAsync();

        var groupId = new GroupId(group.GroupId);
        GroupRecord record = (await StoreOf(alice).GetGroupAsync(groupId))!;
        await StoreOf(alice).PutGroupAsync(record with { Removed = true });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => alice.Service.EncryptMessageAsync(group.GroupId, "hello"));
    }

    // ------------------------------------------------------------- KeyPackages

    [Fact]
    public async Task AGeneratedKeyPackageIsStoredWithItsMaterialBeforeItIsHandedOut()
    {
        // The ordering the engine's publisher exists to enforce, kept here even
        // though this service does not publish: there must be no KeyPackage a
        // caller can put on a relay whose private material this device never
        // wrote down, because a Welcome sealed to it would then be unopenable.
        Party party = await PartyAsync();

        Assert.Equal(0, party.Service.GetStoredKeyPackageCount());
        Assert.Null(party.Service.GetLocalKeyPackageSlotId());

        CoreKeyPackage keyPackage = await party.Service.GenerateKeyPackageAsync();

        Assert.Equal(1, party.Service.GetStoredKeyPackageCount());
        Assert.True(party.Service.HasKeyMaterialForKeyPackage(keyPackage.Data));
        Assert.Equal(keyPackage.SlotId, party.Service.GetLocalKeyPackageSlotId());
        Assert.NotNull(keyPackage.SlotId);
        Assert.True(await party.Service.CanProcessWelcomeAsync([1, 2, 3]));

        // A second KeyPackage reuses the slot, so the addressable kind-30443
        // event is replaced in place rather than accumulating slots.
        CoreKeyPackage second = await party.Service.GenerateKeyPackageAsync();
        Assert.Equal(keyPackage.SlotId, second.SlotId);
    }

    [Fact]
    public async Task AWelcomeIsRefusedWhenNoStoredKeyPackageCanOpenIt()
    {
        // Fail-closed: without material there is nothing to try, and the
        // refusal has to say so rather than looking like a malformed Welcome.
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(bob);
        MlsWelcome staged = await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);
        await alice.Service.MergeStagedAsync(group.GroupId);

        Party stranger = await PartyAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => stranger.Service.ProcessWelcomeAsync(staged.WelcomeData, "0".PadLeft(64, '0')));

        Assert.False(await stranger.Service.CanProcessWelcomeAsync(staged.WelcomeData));
    }

    [Fact]
    public async Task SlotReconciliationReportsNoAdoption()
    {
        // Pinned rather than left to look like an oversight. The condition it
        // repairs -- a null local slot with our own KeyPackages already on a
        // relay -- cannot arise once the slot is derived from the KeyPackage
        // records themselves. Returning true would claim an adoption that did
        // not happen.
        Party party = await PartyAsync();
        CoreKeyPackage keyPackage = await party.Service.GenerateKeyPackageAsync();

        Assert.False(party.Service.TryReconcileSlotId([keyPackage]));
    }

    // ------------------------------------------------------------- lifecycle

    [Fact]
    public async Task ResetForgetsTheIdentityAndKeepsTheKeyMaterial()
    {
        // Logout must not erase KeyPackage material: every Welcome already
        // addressed to this device would become unopenable, and the material is
        // the only thing that can open them.
        Party party = await PartyAsync();
        CoreKeyPackage keyPackage = await party.Service.GenerateKeyPackageAsync();

        await party.Service.ResetAsync();

        Assert.Null(party.Service.LastEncryptedRumorEventId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => party.Service.GenerateKeyPackageAsync());

        Assert.Equal(1, party.Service.GetStoredKeyPackageCount());

        await party.Service.InitializeAsync(
            Convert.ToHexString(party.Secret).ToLowerInvariant(), party.PublicKeyHex);

        Assert.True(party.Service.HasKeyMaterialForKeyPackage(keyPackage.Data));
    }

    [Fact]
    public async Task AnIdentityWithNoPrivateKeyCannotSignAProof()
    {
        // MainViewModel passes an empty private key for external-signer users.
        // That must not fail at login -- most of the service works without one
        // -- and it must fail loudly at the first thing that needs to sign.
        string path = Path.Combine(Path.GetTempPath(), $"dm-mls-test-{Guid.NewGuid():N}.db");
        _paths.Add(path);

        var provider = new SqliteMarmotStorageProvider($"Data Source={path}");
        _providers.Add(provider);

        var service = new DarkMatterMlsService(provider);
        _services.Add(service);

        var (_, publicKey) = Bip340.GenerateKeyPair();

        await service.InitializeAsync(
            string.Empty, Convert.ToHexString(publicKey).ToLowerInvariant());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.GenerateKeyPackageAsync());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.CreateGroupAsync("Rakes", Relays));
    }

    [Fact]
    public async Task CreatingAGroupWithNoRelayIsRefused()
    {
        Party party = await PartyAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => party.Service.CreateGroupAsync("Rakes", []));
    }

    // -------------------------------------------------------------- refusals

    [Fact]
    public async Task TheMembersWithNothingHonestToDoRefuse()
    {
        // Each of these is a deliberate NotSupportedException, and each message
        // names the reason. Grouped into one test because what is being pinned
        // is that they refuse rather than return something plausible -- the
        // individual reasons live in the doc comments, where a reader looking at
        // the member will actually find them.
        var (alice, bob, group) = await PairAsync();
        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(bob);

        // Auto-merge: applies before publishing, which forks the committer
        // permanently if the publish then fails.
        await Assert.ThrowsAsync<NotSupportedException>(
            () => alice.Service.AddMemberAsync(group.GroupId, keyPackage));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => alice.Service.RemoveMemberAsync(group.GroupId, bob.PublicKeyHex));

        // Service state: nothing left in memory to export, everything already
        // durable and schema'd.
        await Assert.ThrowsAsync<NotSupportedException>(
            () => alice.Service.ExportServiceStateAsync());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => alice.Service.ImportServiceStateAsync([1, 2, 3]));

        // Group state in: the engine owns durability, and a write behind a live
        // session's back discards an epoch silently.
        await Assert.ThrowsAsync<NotSupportedException>(
            () => alice.Service.ImportGroupStateAsync(group.GroupId, [1, 2, 3]));

        // A commit is wrapped while staged, under the pre-commit secret; there
        // is nothing left to encrypt afterwards.
        await Assert.ThrowsAsync<NotSupportedException>(
            () => alice.Service.EncryptCommitAsync(group.GroupId, [1, 2, 3]));

        // Kind-445 events are signed with a fresh ephemeral key, which the
        // peeler generates itself.
        Assert.Throws<NotSupportedException>(
            () => alice.Service.SetNostrEventSigner(new LocalNostrEventSigner(
                Convert.ToHexString(alice.Secret).ToLowerInvariant())));
    }

    [Fact]
    public async Task GroupStateCanStillBeExported()
    {
        // The read-only half of the pair survives: it is what the old suite's
        // round trips were actually exercising, and it cannot fork anything.
        var (alice, _, group) = await PairAsync();

        byte[] state = await alice.Service.ExportGroupStateAsync(group.GroupId);

        Assert.NotEmpty(state);
    }

    [Fact]
    public async Task AGroupWeDoNotHaveIsNotTheSameAsOneWeCannotRebuild()
    {
        // Two different answers, kept apart. GetGroupInfoAsync says null for a
        // group we were never in -- routine -- while a member that needs the
        // group throws, naming which of the two it is.
        Party party = await PartyAsync();
        byte[] unknown = Guid.NewGuid().ToByteArray();

        Assert.Null(await party.Service.GetGroupInfoAsync(unknown));

        InvalidOperationException notStored =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => party.Service.ExportGroupStateAsync(unknown));

        Assert.Contains("stored on this device", notStored.Message);

        var groupId = new GroupId(unknown);
        await StoreOf(party).PutGroupAsync(
            new GroupRecord(
                groupId,
                new EpochId(0),
                ProtocolProfile.Current,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)
            {
                LiveState = [9, 9, 9],
            });

        InvalidOperationException unreadable =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => party.Service.ExportGroupStateAsync(unknown));

        Assert.Contains("will not load", unreadable.Message);
    }

    [Fact]
    public async Task CiphertextWithoutItsEnvelopeIsNotAccepted()
    {
        // Nothing in a Nostr event is trustworthy until its id and signature
        // verify, and the peeler is the only thing that checks them. A caller
        // that had already stripped the event down to its MIP-03 content would
        // be handing us attacker-chosen routing.
        var (alice, _, group) = await PairAsync();

        byte[] envelope = await alice.Service.EncryptMessageAsync(group.GroupId, "hello");

        using JsonDocument document = JsonDocument.Parse(envelope);
        byte[] contentOnly = Convert.FromBase64String(
            document.RootElement.GetProperty("content").GetString()!);

        // MlsIngestRefusedException rather than InvalidOperationException: a
        // refusal carries its IngestOutcome, and xUnit's ThrowsAsync matches the
        // type exactly. Asserting the category as well is the point — "it threw"
        // would also be satisfied by the engine failing for some unrelated reason.
        MlsIngestRefusedException ex =
            await Assert.ThrowsAsync<MlsIngestRefusedException>(
                () => alice.Service.DecryptMessageAsync(group.GroupId, contentOnly));

        var ignored = Assert.IsType<IngestOutcome.Ignored>(ex.Outcome);
        Assert.Equal(InputRejectionCategory.InvalidEncoding, ignored.Category);
    }

    [Fact]
    public async Task OurOwnEchoIsRefusedRatherThanDecryptedTwice()
    {
        // A relay echoes what it was given. The engine deduplicates on the MLS
        // bytes rather than the transport id, and a second delivery must not
        // consume ratchet keys a second time -- which is what "generation
        // already consumed" used to mean in the old engine.
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(bob);
        MlsWelcome staged = await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);
        await alice.Service.MergeStagedAsync(group.GroupId);

        MlsGroupInfo joined = await bob.Service.ProcessWelcomeAsync(
            staged.WelcomeData, "0".PadLeft(64, '0'));

        byte[] envelope = await alice.Service.EncryptMessageAsync(group.GroupId, "hello");

        MlsDecryptedMessage first =
            await bob.Service.DecryptMessageAsync(joined.GroupId, envelope);

        Assert.Equal("hello", first.Plaintext);

        MlsIngestRefusedException second =
            await Assert.ThrowsAsync<MlsIngestRefusedException>(
                () => bob.Service.DecryptMessageAsync(joined.GroupId, envelope));

        // On the value as well as in the text. MessageService classifies on the
        // outcome to decide whether a refusal is worth telling the user about,
        // and a duplicate is the archetype of one that is not.
        var ignored = Assert.IsType<IngestOutcome.Ignored>(second.Outcome);
        Assert.Equal(InputRejectionCategory.Duplicate, ignored.Category);
        Assert.Contains("Duplicate", second.Message);
    }

    // ------------------------------------------------ the KeyPackage binding

    /// <summary>Publishes a KeyPackage and tells the service its event id.</summary>
    private static async Task<CoreKeyPackage> PublishAndBindAsync(Party party)
    {
        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(party);

        await party.Service.MarkKeyPackagePublishedAsync(
            keyPackage, keyPackage.NostrEventId!);

        return keyPackage;
    }

    [Fact]
    public async Task ABoundWelcomeOpensAgainstTheKeyPackageItNames()
    {
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishAndBindAsync(bob);
        MlsWelcome staged = await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);
        await alice.Service.MergeStagedAsync(group.GroupId);

        MlsGroupInfo joined = await bob.Service.ProcessWelcomeAsync(
            staged.WelcomeData, "0".PadLeft(64, '0'), keyPackage.NostrEventId);

        Assert.Equal(group.GroupId, joined.GroupId);
    }

    [Fact]
    public async Task AWelcomeNamingAKeyPackageWeNeverPublishedIsRefused()
    {
        // The fail-closed check the contract could not carry before. Without
        // it the Welcome would be tried against every stored KeyPackage and
        // would succeed, because it really is sealed to one of them -- so the
        // refusal is about provenance, not about whether we *can* open it.
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishAndBindAsync(bob);
        MlsWelcome staged = await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);
        await alice.Service.MergeStagedAsync(group.GroupId);

        InvalidOperationException ex =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => bob.Service.ProcessWelcomeAsync(
                    staged.WelcomeData, "0".PadLeft(64, '0'), "f".PadLeft(64, 'f')));

        Assert.Contains("never published", ex.Message);
    }

    [Fact]
    public async Task TheSameWelcomeStillOpensWhenNoBindingIsSupplied()
    {
        // The fallback, pinned so the previous test cannot be passing because
        // the Welcome was unopenable for some unrelated reason.
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishAndBindAsync(bob);
        MlsWelcome staged = await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);
        await alice.Service.MergeStagedAsync(group.GroupId);

        MlsGroupInfo joined = await bob.Service.ProcessWelcomeAsync(
            staged.WelcomeData, "0".PadLeft(64, '0'));

        Assert.Equal(group.GroupId, joined.GroupId);
    }

    [Fact]
    public async Task BindingAnEventIdToBytesWeDoNotHoldIsRefused()
    {
        Party party = await PartyAsync();
        CoreKeyPackage keyPackage = await party.Service.GenerateKeyPackageAsync();

        var stranger = new CoreKeyPackage { Data = [1, 2, 3] };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => party.Service.MarkKeyPackagePublishedAsync(stranger, "a".PadLeft(64, 'a')));
    }

    [Fact]
    public async Task AKeyPackageIsOnlyFindableByEventIdOnceItIsBound()
    {
        // Why the publish-side member has to exist: the record carries no event
        // id until something says what it was published under, so the binding
        // above would refuse every Welcome without it.
        Party party = await PartyAsync();
        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(party);

        Assert.Null(await StoreOf(party).GetKeyPackageByEventAsync(keyPackage.NostrEventId!));

        await party.Service.MarkKeyPackagePublishedAsync(keyPackage, keyPackage.NostrEventId!);

        Assert.NotNull(await StoreOf(party).GetKeyPackageByEventAsync(keyPackage.NostrEventId!));
    }

    // ----------------------------------------------------------- admin policy

    /// <summary>Alice's group with Bob in it, both merged and joined.</summary>
    private async Task<(Party Alice, Party Bob, MlsGroupInfo Group)> TrioAsync()
    {
        var (alice, bob, group) = await PairAsync();

        CoreKeyPackage keyPackage = await PublishKeyPackageAsync(bob);
        MlsWelcome staged = await alice.Service.StageAddMemberAsync(group.GroupId, keyPackage);
        await alice.Service.MergeStagedAsync(group.GroupId);

        MlsGroupInfo joined = await bob.Service.ProcessWelcomeAsync(
            staged.WelcomeData, "0".PadLeft(64, '0'));

        // Bob's own id for the same group, not Alice's record. A refusal test
        // aimed at a group Bob does not hold would pass on "no such group"
        // while proving nothing about authority.
        Assert.Equal(group.GroupId, joined.GroupId);

        return (alice, bob, group);
    }

    [Fact]
    public async Task GrantingAdminStagesACommitAndLeavesTheGroupWhereItWas()
    {
        // The wiring, end to end: the contract's hex list reaches the engine's
        // raw account keys, the commit is staged rather than applied, and the
        // bytes handed back are a publishable envelope.
        var (alice, bob, group) = await TrioAsync();

        // Read here, not from PairAsync's record: adding Bob already advanced
        // the group, so the creation epoch would make "unchanged" and
        // "advanced by one" the same number and the assertion would pass
        // either way.
        MlsGroupInfo before = (await alice.Service.GetGroupInfoAsync(group.GroupId))!;

        byte[] envelope = await alice.Service.StageUpdateAdminPubkeysAsync(
            group.GroupId, [alice.PublicKeyHex, bob.PublicKeyHex]);

        Assert.True(alice.Service.HasPendingCommit(group.GroupId));

        MlsGroupInfo? after = await alice.Service.GetGroupInfoAsync(group.GroupId);
        Assert.NotNull(after);
        Assert.Equal(before.Epoch, after.Epoch);

        // Still the old set: staged is not applied, and the admin set a caller
        // reads before publishing must be the one the group is still in.
        Assert.Equal([alice.PublicKeyHex], alice.Service.GetAdminPubkeys(group.GroupId));

        Assert.NotEmpty(envelope);

        // Not pinned here, and it cannot be: deleting this member's
        // RequireDeferred call survives every test in this class, because
        // CallerPublishes answers Indeterminate unconditionally and nothing
        // reachable through this contract makes it answer otherwise. It is a
        // guard against a future transport, kept for the same reason
        // StageRemoveMemberAsync and UpdateKeysAsync keep theirs.
    }

    [Fact]
    public async Task MergingAnAdminGrantIsWhatChangesTheAdminSet()
    {
        var (alice, bob, group) = await TrioAsync();

        await alice.Service.StageUpdateAdminPubkeysAsync(
            group.GroupId, [alice.PublicKeyHex, bob.PublicKeyHex]);

        await alice.Service.MergeStagedAsync(group.GroupId);

        List<string> admins = alice.Service.GetAdminPubkeys(group.GroupId);

        Assert.Equal(2, admins.Count);
        Assert.Contains(alice.PublicKeyHex, admins);
        Assert.Contains(bob.PublicKeyHex, admins);
    }

    [Fact]
    public async Task AnAdminSetThatEmptiesTheGroupIsRefused()
    {
        // The one commit a v1 group cannot recover from: no succession, no
        // promotion, and every repair is itself admin-gated.
        var (alice, _, group) = await TrioAsync();

        await Assert.ThrowsAsync<AppComponentException>(
            () => alice.Service.StageUpdateAdminPubkeysAsync(group.GroupId, []));

        Assert.False(alice.Service.HasPendingCommit(group.GroupId));
    }

    [Fact]
    public async Task AnAdminWhoIsNotAMemberIsRefused()
    {
        // A listed admin with no member leaf is a phantom that activates the
        // moment a matching leaf appears, with no commit any member observed
        // granting it.
        var (alice, _, group) = await TrioAsync();
        Party stranger = await PartyAsync();

        await Assert.ThrowsAsync<AppComponentException>(
            () => alice.Service.StageUpdateAdminPubkeysAsync(
                group.GroupId, [alice.PublicKeyHex, stranger.PublicKeyHex]));

        Assert.False(alice.Service.HasPendingCommit(group.GroupId));
    }

    [Fact]
    public async Task ANonAdminCannotChangeTheAdminPolicy()
    {
        // Bob is a member, not an admin. A commit our peers would refuse is
        // worse than one we refuse: we would publish it, apply it, and be alone
        // in an epoch nobody accepted.
        var (_, bob, group) = await TrioAsync();

        // Bob holds the group -- TrioAsync pins that -- so the refusal is about
        // authority and not about a group he cannot see.
        Assert.NotNull(await bob.Service.GetGroupInfoAsync(group.GroupId));

        AppComponentException ex =
            await Assert.ThrowsAsync<AppComponentException>(
                () => bob.Service.StageUpdateAdminPubkeysAsync(
                    group.GroupId, [bob.PublicKeyHex]));

        Assert.Contains("admin", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(bob.Service.HasPendingCommit(group.GroupId));
    }

    [Fact]
    public async Task AnAdminPubkeyThatIsNotHexIsNamedAsSuch()
    {
        // The caller is a settings screen. Convert.FromHexString's own
        // FormatException does not say which entry was bad.
        var (alice, _, group) = await TrioAsync();

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => alice.Service.StageUpdateAdminPubkeysAsync(
                group.GroupId, [alice.PublicKeyHex, "not-a-key"]));

        Assert.Contains("not-a-key", ex.Message);
    }

    // ------------------------------------------------- refusals carry their reason

    /// <summary>
    /// A declined message refuses with its <see cref="IngestOutcome"/> attached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The contract <c>MessageService</c> now depends on.</b> Ingest classifies
    /// rather than throwing, and this service's caller expects a message or an
    /// exception — so the classification travels on the exception. Without it the
    /// app is back to substring-matching an engine's prose to tell a duplicate
    /// from a bad signature, which is the bug P11 step 3 fixed: the pre-flip
    /// predicate matched marmot-cs's wording and silently stopped matching
    /// anything, turning every expected refusal into a user-visible decryption
    /// error.
    /// </para>
    /// <para>
    /// It stays an <see cref="InvalidOperationException"/> by inheritance, so the
    /// callers that only catch broadly are unaffected.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnUndecodableMessageRefusesWithItsOutcomeAttached()
    {
        var (alice, _, group) = await PairAsync();

        MlsIngestRefusedException ex = await Assert.ThrowsAsync<MlsIngestRefusedException>(
            () => alice.Service.DecryptMessageAsync(group.GroupId, [0x00, 0x01, 0x02, 0x03]));

        var ignored = Assert.IsType<IngestOutcome.Ignored>(ex.Outcome);
        Assert.Equal(InputRejectionCategory.InvalidEncoding, ignored.Category);

        // The category is in the text as well as on the value, because this is
        // also what lands in a log line.
        Assert.Contains("InvalidEncoding", ex.Message);
    }

    /// <summary>
    /// Traffic from before we joined is held, not judged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case the app most needs to recognise and least ought to shout
    /// about.</b> A member admitted at epoch N cannot read what was sent at N-1 —
    /// the keys were never theirs — and a client that reports that as a decryption
    /// failure tells every new joiner their group is broken. That is the
    /// regression P11 step 3 fixed at the <see cref="MessageService"/> layer.
    /// </para>
    /// <para>
    /// <b>The outcome is <see cref="IngestOutcome.TransportDeferred"/>, and that
    /// was worth measuring rather than assuming.</b> This test was written
    /// expecting <see cref="StaleReason.PreMembership"/> — the reason whose own
    /// documentation says "from before this device joined; not decryptable and not
    /// a failure" — and the engine does not use it here. It cannot: at peel time
    /// "not readable yet" and "never readable" look identical, so it defers
    /// instead of ruling, and a later epoch or a retained snapshot may still open
    /// the message. <c>PreMembership</c> is a judgement something reaches later
    /// with more information, not one ingest makes on arrival.
    /// </para>
    /// <para>
    /// Both are classified quiet by <c>MessageService</c>, so the user-visible
    /// behaviour is the same either way — but a reader who trusted the reason name
    /// would be looking for the wrong outcome in a log.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AMessageFromBeforeWeJoinedIsDeferredRatherThanFailed()
    {
        var (alice, _, group) = await TrioAsync();

        // Sent while the group is Alice and Bob, and before Carol exists in it.
        byte[] earlier = await alice.Service.EncryptMessageAsync(group.GroupId, "before your time");

        Party carol = await PartyAsync();
        CoreKeyPackage carolKeyPackage = await PublishKeyPackageAsync(carol);

        // Publishing is two steps, and the join is fail-closed on the second.
        // Without it the Welcome is refused for naming a KeyPackage this device
        // never published — which is a different test, and one that would hide
        // this one behind a refusal that never reaches ingest.
        await carol.Service.MarkKeyPackagePublishedAsync(
            carolKeyPackage, carolKeyPackage.NostrEventId!);

        MlsWelcome staged = await alice.Service.StageAddMemberAsync(group.GroupId, carolKeyPackage);
        await alice.Service.MergeStagedAsync(group.GroupId);

        MlsGroupInfo joined = await carol.Service.ProcessWelcomeAsync(
            staged.WelcomeData,
            "0".PadLeft(64, '0'),
            carolKeyPackage.NostrEventId);

        MlsIngestRefusedException ex =
            await Assert.ThrowsAsync<MlsIngestRefusedException>(
                () => carol.Service.DecryptMessageAsync(joined.GroupId, earlier));

        Assert.IsType<IngestOutcome.TransportDeferred>(ex.Outcome);

        // Retryable, which is the engine's way of saying the bytes were kept.
        Assert.True(ex.Outcome.IsRetryable);

        // And the app stays quiet about it — the assertion that ties this to the
        // regression. IsExpectedInboundRefusal is private, so this is pinned
        // through MessageService in InboundRefusalClassificationTests; what is
        // proved here is that the outcome it will be handed is this one.
        Assert.False(ex.Outcome.Advanced);
    }

    /// <summary>
    /// Disposing the service must close the store it was built over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The symptom is a locked file, not a slow leak.</b>
    /// <c>SqliteMarmotStorageProvider</c> holds one long-lived
    /// <c>SqliteConnection</c> for the profile's database, and
    /// <see cref="DarkMatterMlsServiceFactory"/> hands that provider over and
    /// keeps no reference to it — so this service is its only owner. A
    /// <c>Dispose</c> that released only the gate left the connection open for
    /// the life of the process, which on Windows is what makes deleting or
    /// replacing a profile database fail.
    /// </para>
    /// <para>
    /// Asserted through <see cref="IDisposable"/> on a mock rather than by
    /// deleting a file, because a file-lock assertion would also be measuring
    /// <c>Microsoft.Data.Sqlite</c>'s connection pooling and could pass or fail
    /// for reasons that have nothing to do with this call.
    /// </para>
    /// </remarks>
    [Fact]
    public void DisposingTheServiceDisposesTheStoreItWasGiven()
    {
        var storage = new Mock<IMarmotStorageProvider>();
        var disposable = storage.As<IDisposable>();

        using (new DarkMatterMlsService(storage.Object))
        {
        }

        disposable.Verify(d => d.Dispose(), Times.Once());
    }

    /// <summary>
    /// A provider that is not disposable must not make disposal throw.
    /// </summary>
    /// <remarks>
    /// <see cref="IMarmotStorageProvider"/> does not extend
    /// <see cref="IDisposable"/>, so a caller may legitimately pass one that
    /// holds nothing — and an in-memory provider in a test is the obvious case.
    /// </remarks>
    [Fact]
    public void DisposingTheServiceOverANonDisposableStoreDoesNotThrow()
    {
        var storage = new Mock<IMarmotStorageProvider>();
        var service = new DarkMatterMlsService(storage.Object);

        service.Dispose();
    }
}
