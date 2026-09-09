using System.Text;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Rotating a member's own leaf.
/// </summary>
/// <remarks>
/// The membership-neutral commit: nobody joins, nobody leaves, and the point is
/// the epoch change itself. Forward secrecy in MLS is a property of moving
/// forward, so a group that never commits keeps its key material indefinitely
/// and a device compromised today reads everything since the last commit.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class SelfUpdateTests
{
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];

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

    private async Task<(LocalSigner AliceSigner, CreatedGroup Alice, MlsGroup Bob)> PairAsync()
    {
        var aliceSigner = new LocalSigner();

        CreatedGroup alice = await MarmotGroupBuilder.CreateAsync(
            _cs, aliceSigner, "Rakes", "", Now, Relays);

        var bundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);
        StagedCommit staged = MarmotGroupInvite.Add(alice.Group, _cs, [bundle.KeyPackage]);
        staged.Applied();

        MlsGroup bob = MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey,
            config: MarmotGroupSettings.Create());

        return (aliceSigner, alice, bob);
    }

    private static NostrGroupPeeler Peeler() => new();

    [Fact]
    public async Task ARotationAdvancesTheEpochAndChangesNobody()
    {
        var (_, alice, bob) = await PairAsync();
        ulong before = alice.Group.Epoch;
        int members = alice.Group.GetMembers().Count;

        StagedCommit staged = MarmotSelfUpdate.Stage(alice.Group);

        var peeler = Peeler();
        string envelope = GroupHandshake.Wrap(alice.Group, peeler, staged.Commit);
        staged.Applied();

        GroupHandshake.Receive(
            bob, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(bob)).MlsBytes);

        Assert.Equal(before + 1, alice.Group.Epoch);
        Assert.Equal(alice.Group.Epoch, bob.Epoch);
        Assert.Equal(members, alice.Group.GetMembers().Count);
        Assert.Empty(staged.AffectedAccounts);
        Assert.Null(staged.Welcome);
    }

    [Fact]
    public async Task TheGroupStillWorksAfterRotating()
    {
        // The assertion that matters more than the epoch number: rotating the
        // leaf must not break the key schedule for anyone.
        var (aliceSigner, alice, bob) = await PairAsync();

        StagedCommit staged = MarmotSelfUpdate.Stage(alice.Group);
        var peeler = Peeler();
        string envelope = GroupHandshake.Wrap(alice.Group, peeler, staged.Commit);
        staged.Applied();
        GroupHandshake.Receive(
            bob, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(bob)).MlsBytes);

        string message = GroupMessages.Send(
            alice.Group, peeler,
            MarmotAppEvent.Chat(aliceSigner.Hex, (long)Now, "after rotating"),
            aliceSigner.AccountPublicKey.Span);

        Assert.Equal(
            "after rotating",
            GroupMessages.Receive(
                bob, peeler.Peel(message, _ => GroupMessages.ExporterSecret(bob)).MlsBytes)
                .Event.Content);
    }

    [Fact]
    public async Task TheOldEpochsTransportKeyIsRetired()
    {
        // What the rotation is for. If the exporter secret did not move, the
        // commit would cost an epoch and buy nothing.
        var (_, alice, _) = await PairAsync();
        byte[] before = GroupMessages.ExporterSecret(alice.Group);

        StagedCommit staged = MarmotSelfUpdate.Stage(alice.Group);
        GroupHandshake.Wrap(alice.Group, Peeler(), staged.Commit);
        staged.Applied();

        Assert.NotEqual(before, GroupMessages.ExporterSecret(alice.Group));
    }

    [Fact]
    public async Task ARotationDoesNotCommitSomebodyElsesPendingDeparture()
    {
        // The rule that makes this safe to run on a timer. A member's
        // self_remove may be sitting in the cache, and a routine key rotation
        // that silently evicted them would be a governance action disguised as
        // maintenance.
        var (_, alice, bob) = await PairAsync();

        var carolBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);
        StagedCommit invite = MarmotGroupInvite.Add(alice.Group, _cs, [carolBundle.KeyPackage]);
        var peeler = Peeler();
        string inviteWire = GroupHandshake.Wrap(alice.Group, peeler, invite.Commit);
        invite.Applied();
        GroupHandshake.Receive(
            bob, peeler.Peel(inviteWire, _ => GroupMessages.ExporterSecret(bob)).MlsBytes);

        // Bob asks to leave; Alice caches the request.
        PublicMessage request = MarmotGroupLeave.Request(bob);
        string requestWire = GroupHandshake.WrapProposal(bob, peeler, request);
        GroupHandshake.Receive(
            alice.Group,
            peeler.Peel(requestWire, _ => GroupMessages.ExporterSecret(alice.Group)).MlsBytes);

        Assert.NotEmpty(alice.Group.CachedProposals);
        int before = alice.Group.GetMembers().Count;

        StagedCommit rotation = MarmotSelfUpdate.Stage(alice.Group);
        GroupHandshake.Wrap(alice.Group, Peeler(), rotation.Commit);
        rotation.Applied();

        // Bob is still a member: the rotation took nobody with it.
        Assert.Equal(before, alice.Group.GetMembers().Count);
        Assert.Empty(rotation.AffectedAccounts);
    }

    [Fact]
    public async Task RotatingWithACommitAlreadyStagedIsRefused()
    {
        // Two pending commits cannot both be published, and picking one
        // silently would discard the other.
        var (_, alice, _) = await PairAsync();

        var bundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);
        MarmotGroupInvite.Add(alice.Group, _cs, [bundle.KeyPackage]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => MarmotSelfUpdate.Stage(alice.Group));

        Assert.Contains("already has a staged commit", ex.Message);
    }

    [Fact]
    public async Task TheRotationIsNotAppliedUntilItIsPublished()
    {
        // Publish-before-apply. A rotation applied locally and never published
        // is the worst of both: the member leaves the epoch everyone else is
        // in, having gained no forward secrecy the group agrees about.
        var (_, alice, _) = await PairAsync();
        ulong before = alice.Group.Epoch;

        StagedCommit staged = MarmotSelfUpdate.Stage(alice.Group);

        Assert.Equal(before, alice.Group.Epoch);
        Assert.True(alice.Group.HasPendingCommit);

        staged.Discard();

        Assert.Equal(before, alice.Group.Epoch);
        Assert.False(alice.Group.HasPendingCommit);
    }
}
