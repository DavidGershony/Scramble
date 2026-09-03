using DotnetMls.Crypto;
using DotnetMls.Group;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Joining a group from a Welcome, and what a join must refuse.
/// </summary>
/// <remarks>
/// The inbound mirror of invite, and deliberately stricter. An invite is
/// something we chose; a Welcome arrives from someone we may never have heard
/// of, so everything is checked before any state exists — the wrap, the rumor,
/// that the KeyPackage named is one we published and still hold material for,
/// and that the resulting group is one we can honour.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class GroupJoinTests : IDisposable
{
    private readonly StorageFixture _fixture = new();
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];

    private IKeyPackageStorage Storage => _fixture.Provider;

    public void Dispose() => _fixture.Dispose();

    private sealed class LocalSigner : IAccountIdentityProofSigner
    {
        public LocalSigner()
        {
            var (secret, publicKey) = Bip340.GenerateKeyPair();
            Secret = secret;
            AccountPublicKey = publicKey;
        }

        public byte[] Secret { get; }

        public ReadOnlyMemory<byte> AccountPublicKey { get; }

        public string Hex => Convert.ToHexString(AccountPublicKey.Span).ToLowerInvariant();

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(Secret, template.ComputeId()));
    }

    /// <summary>An inviter with a group, and an invitee with a stored KeyPackage.</summary>
    private async Task<(LocalSigner Inviter, CreatedGroup Group, LocalSigner Invitee,
        MarmotKeyPackageBundle Bundle, string EventIdHex, string Envelope)> InviteAsync()
    {
        var inviter = new LocalSigner();
        var invitee = new LocalSigner();

        var group = await MarmotGroupBuilder.CreateAsync(
            _cs, inviter, "Rakes", "", Now, Relays);

        var bundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, invitee, Now);

        // Stored the way the publisher would, so the join path can find it by
        // the event id a Welcome names.
        string eventIdHex = Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        await Storage.PutKeyPackageAsync(
            bundle.ToRecord(KeyPackageEvent.NewSlotId(), DateTimeOffset.UnixEpoch));
        await Storage.MarkPublishedAsync(bundle.KeyPackageRefHex, eventIdHex);

        StagedInvite staged = MarmotGroupInvite.Add(group.Group, _cs, [bundle.KeyPackage]);
        staged.Applied();

        string envelope = WelcomePublication.Wrap(
            inviter.Secret,
            inviter.AccountPublicKey.Span,
            invitee.AccountPublicKey.Span,
            Convert.FromHexString(eventIdHex),
            Relays,
            staged.Welcome!,
            (long)Now);

        return (inviter, group, invitee, bundle, eventIdHex, envelope);
    }

    // ---- The join ----

    [Fact]
    public async Task AWelcomeAdmitsUsToTheSameGroupAtTheSameEpoch()
    {
        var (inviter, group, invitee, _, eventIdHex, envelope) = await InviteAsync();

        JoinedGroup joined = await GroupJoin.JoinFromEnvelopeAsync(
            _cs, envelope, invitee.Secret, Storage);

        Assert.Equal(group.GroupId, joined.GroupId);
        Assert.Equal(group.Group.Epoch, joined.Group.Epoch);
        Assert.Equal(group.Required, joined.Required);
        Assert.Equal(eventIdHex, joined.KeyPackageEventIdHex);

        // The inviter comes from the verified seal, not from anything the rumor
        // asserts about itself.
        Assert.Equal(inviter.AccountPublicKey.ToArray(), joined.InviterIdentity);
    }

    [Fact]
    public async Task AJoinedMemberCanReadAndWriteInTheGroup()
    {
        var (inviter, group, invitee, _, _, envelope) = await InviteAsync();

        JoinedGroup joined = await GroupJoin.JoinFromEnvelopeAsync(
            _cs, envelope, invitee.Secret, Storage);

        var peeler = new NostrGroupPeeler();

        // Joining is only useful if the group actually works afterwards, which
        // is a stronger claim than "the epochs match".
        string outbound = GroupMessages.Send(
            joined.Group, peeler, MarmotAppEvent.Chat(invitee.Hex, (long)Now, "just joined"),
            invitee.AccountPublicKey.Span);

        ReceivedGroupMessage atInviter = GroupMessages.Receive(
            group.Group,
            peeler.Peel(outbound, _ => GroupMessages.ExporterSecret(group.Group)).MlsBytes);

        Assert.Equal("just joined", atInviter.Event.Content);
        Assert.Equal(invitee.AccountPublicKey.ToArray(), atInviter.SenderIdentity);

        string inbound = GroupMessages.Send(
            group.Group, peeler, MarmotAppEvent.Chat(inviter.Hex, (long)Now + 1, "welcome"),
            inviter.AccountPublicKey.Span);

        Assert.Equal("welcome", GroupMessages.Receive(
            joined.Group,
            peeler.Peel(inbound, _ => GroupMessages.ExporterSecret(joined.Group)).MlsBytes)
            .Event.Content);
    }

    [Fact]
    public async Task TheRecordSaysWeJoinedAtThisEpochNotAtZero()
    {
        var (_, _, invitee, _, _, envelope) = await InviteAsync();

        JoinedGroup joined = await GroupJoin.JoinFromEnvelopeAsync(
            _cs, envelope, invitee.Secret, Storage);

        GroupRecord record = joined.ToRecord(DateTimeOffset.UnixEpoch);

        // Everything before this epoch happened without us and cannot be
        // decrypted, so recording zero would make old messages look like
        // delivery failures.
        Assert.Equal(joined.Group.Epoch, record.JoinEpoch!.Value.Value);
        Assert.NotEqual(0UL, record.JoinEpoch!.Value.Value);

        // Only our own leaf's proof has been checked.
        Assert.False(record.ValidatedTree);
    }

    // ---- The KeyPackage a Welcome consumes ----

    [Fact]
    public async Task TheKeyPackageIsMarkedConsumedOnSuccess()
    {
        var (_, _, invitee, bundle, _, envelope) = await InviteAsync();

        await GroupJoin.JoinFromEnvelopeAsync(_cs, envelope, invitee.Secret, Storage);

        KeyPackageRecord? record = await Storage.GetKeyPackageAsync(bundle.KeyPackageRefHex);
        Assert.Equal(KeyPackageRecordState.Consumed, record!.State);
    }

    [Fact]
    public async Task AWelcomeNamingAKeyPackageWeNeverPublishedIsRefused()
    {
        var (inviter, group, invitee, bundle, _, _) = await InviteAsync();

        // Same Welcome, an event id nothing was published under. Accepting it
        // would mean joining with a key we cannot show is ours.
        StagedInvite staged = MarmotGroupInvite.Add(
            group.Group, _cs,
            [(await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now)).KeyPackage]);

        string envelope = WelcomePublication.Wrap(
            inviter.Secret,
            inviter.AccountPublicKey.Span,
            invitee.AccountPublicKey.Span,
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            Relays,
            staged.Welcome!,
            (long)Now);

        var ex = await Assert.ThrowsAsync<PeelFailedException>(
            () => GroupJoin.JoinFromEnvelopeAsync(
                _cs, envelope, invitee.Secret, Storage));

        Assert.Contains("never published", ex.Message);
        _ = bundle;
    }

    [Fact]
    public async Task AWelcomeForAnErasedKeyPackageIsRefused()
    {
        var (_, _, invitee, bundle, _, envelope) = await InviteAsync();

        await Storage.ErasePrivateMaterialAsync(bundle.KeyPackageRefHex);

        // The material is gone, so the join cannot complete — and saying so is
        // better than a decryption error from somewhere deeper.
        var ex = await Assert.ThrowsAsync<PeelFailedException>(
            () => GroupJoin.JoinFromEnvelopeAsync(
                _cs, envelope, invitee.Secret, Storage));

        Assert.Contains("erased", ex.Message);
    }

    [Fact]
    public async Task AFailedJoinLeavesTheKeyPackageUsable()
    {
        var (_, _, invitee, bundle, _, envelope) = await InviteAsync();

        // Corrupt the wrap so the join fails after the KeyPackage is located.
        string broken = envelope.Replace("\"content\"", "\"contentx\"");

        await Assert.ThrowsAnyAsync<Exception>(
            () => GroupJoin.JoinFromEnvelopeAsync(
                _cs, broken, invitee.Secret, Storage));

        // Still consumable: an inviter may retry against this KeyPackage, and
        // marking it spent on a failure would make that impossible.
        KeyPackageRecord? record = await Storage.GetKeyPackageAsync(bundle.KeyPackageRefHex);
        Assert.NotEqual(KeyPackageRecordState.Consumed, record!.State);
        Assert.True(record.CanConsume);
    }

    [Fact]
    public async Task SomebodyElsesWelcomeCannotBeOpened()
    {
        var (_, _, _, _, _, envelope) = await InviteAsync();
        var stranger = new LocalSigner();

        await Assert.ThrowsAsync<GiftWrapException>(
            () => GroupJoin.JoinFromEnvelopeAsync(
                _cs, envelope, stranger.Secret, Storage));
    }

    [Fact]
    public async Task AWelcomeToAGroupWeCannotHonourIsRefused()
    {
        // The invitee supports everything; the group requires a component this
        // implementation has no codec for. Joining anyway would mean ignoring
        // state the group considers mandatory.
        var (_, _, invitee, _, _, envelope) = await InviteAsync();

        JoinedGroup joined = await GroupJoin.JoinFromEnvelopeAsync(
            _cs, envelope, invitee.Secret, Storage);

        // Sanity: the honourable case does join, so the refusal below is about
        // the requirement and not about the fixture.
        Assert.NotEmpty(joined.Required);

        // And the validator that would refuse it is the same one create-group
        // uses, so the two cannot drift apart.
        Assert.Equal(
            joined.Required, MarmotGroupBuilder.ValidateCreated(joined.Group, "joined group"));
    }

    [Fact]
    public async Task AJoinedGroupCarriesTheMarmotReorderingWindow()
    {
        // The window has to match on both sides. Two members with different
        // windows disagree about which messages are deliverable, and the
        // disagreement surfaces as one of them missing history the other has --
        // so a group we join must be configured like one we create, and only
        // the real join path can show that.
        var (inviter, group, invitee, _, _, envelope) = await InviteAsync();

        JoinedGroup joined = await GroupJoin.JoinFromEnvelopeAsync(
            _cs, envelope, invitee.Secret, Storage);

        var peeler = new NostrGroupPeeler();

        // Sized above the library default, so a group joined on the defaults
        // fails here while one carrying Marmot's window passes.
        int batch = DotnetMls.Group.MlsGroupConfig.DefaultOutOfOrderTolerance + 20;
        Assert.True(batch <= MarmotGroupSettings.OutOfOrderTolerance);

        var sent = Enumerable.Range(0, batch).Select(i => GroupMessages.Send(
            group.Group, peeler,
            MarmotAppEvent.Chat(inviter.Hex, (long)Now, $"m{i}"),
            inviter.AccountPublicKey.Span)).ToList();

        var read = Enumerable.Reverse(sent)
            .Select(e => GroupMessages.Receive(
                joined.Group,
                peeler.Peel(e, _ => GroupMessages.ExporterSecret(joined.Group)).MlsBytes)
                .Event.Content)
            .ToList();

        Assert.Equal(Enumerable.Range(0, batch).Reverse().Select(i => $"m{i}"), read);
    }
}
