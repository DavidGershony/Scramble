using System.Text;
using System.Text.Json;
using Scramble.Core.Services;
using Scramble.Marmot;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Storage.Sqlite;
using Scramble.Nostr.Crypto;
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

        // No AppDataUpdate builder exists, by decision.
        await Assert.ThrowsAsync<NotSupportedException>(
            () => alice.Service.StageUpdateAdminPubkeysAsync(group.GroupId, [bob.PublicKeyHex]));

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

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => alice.Service.DecryptMessageAsync(group.GroupId, contentOnly));
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

        InvalidOperationException second =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => bob.Service.DecryptMessageAsync(joined.GroupId, envelope));

        Assert.Contains("Duplicate", second.Message);
    }
}
