using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
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
/// The durable half of leaving.
/// </summary>
/// <remarks>
/// One failure mode motivates all of this, and it is silent: a self_remove
/// proposal is valid only in the epoch it was framed against, so a departure
/// request overtaken by <i>any</i> other commit — someone joining, someone else
/// leaving, a key rotation — is dropped by every member without anyone
/// rejecting it or reporting a failure. The member stays in the group while
/// their client shows them as gone. So most of the tests below are about what
/// happens after an unrelated commit, not about leaving as such.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class LeaveCoordinatorTests : IDisposable
{
    private readonly StorageFixture _fixture = new();
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];

    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddSeconds(Now);

    public void Dispose() => _fixture.Dispose();

    private LeaveCoordinator NewCoordinator() => new(_fixture.Provider, () => _now);

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

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(Secret, template.ComputeId()));
    }

    private sealed record Trio(
        CreatedGroup Alice, GroupId AliceId, MlsGroup Bob, GroupId BobId, MlsGroup Carol);

    /// <summary>Three members at one epoch, with Alice's and Bob's records stored.</summary>
    private async Task<Trio> TrioAsync()
    {
        var alice = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        var bobBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);
        var carolBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);

        StagedInvite staged = MarmotGroupInvite.Add(
            alice.Group, _cs, [bobBundle.KeyPackage, carolBundle.KeyPackage]);
        staged.Applied();

        MlsGroup Join(MarmotKeyPackageBundle bundle) => MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey);

        var groupId = new GroupId(alice.GroupId);
        await _fixture.Provider.PutGroupAsync(alice.ToRecord(_now));

        return new Trio(alice, groupId, Join(bobBundle), groupId, Join(carolBundle));
    }

    private static NostrGroupPeeler Peeler() => new();

    /// <summary>Puts a handshake message through the wire and back.</summary>
    private static ReceivedHandshake Deliver(MlsGroup from, MlsGroup to, PublicMessage message)
    {
        var peeler = Peeler();
        string envelope = message.Content.ContentType == ContentType.Commit
            ? GroupHandshake.Wrap(from, peeler, message)
            : GroupHandshake.WrapProposal(from, peeler, message);

        return GroupHandshake.Receive(
            to, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(to)).MlsBytes);
    }

    // ---- Recording the intent ----

    [Fact]
    public async Task RequestingRecordsTheIntentAgainstTheCurrentEpoch()
    {
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        PublicMessage proposal = await coordinator.RequestAsync(trio.Bob, trio.BobId);

        LeaveRequest stored = (await _fixture.Provider.GetLeaveRequestAsync(trio.BobId))!;

        Assert.Equal(ContentType.Proposal, proposal.Content.ContentType);
        Assert.Equal(new EpochId(trio.Bob.Epoch), stored.RequestedInEpoch);
        Assert.Equal(new EpochId(trio.Bob.Epoch), stored.ProposedInEpoch);
        Assert.Equal(_now, stored.CreatedAt);
        Assert.True(await coordinator.IsLeavingAsync(trio.BobId));
    }

    [Fact]
    public async Task TheIntentIsRecordedEvenIfThePublishNeverHappens()
    {
        // The proposal is returned to the caller, so we cannot know whether it
        // reached a relay. Recording first is what makes an unpublished request
        // recoverable: the next repropose sends it. Recording afterwards would
        // lose the intent to a crash and leave the member in a group they
        // believe they have left, which is the failure this type exists for.
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        await coordinator.RequestAsync(trio.Bob, trio.BobId);

        // Nothing was published, and a fresh coordinator over the same storage
        // still finds the intent -- which is what a cold start looks like.
        Assert.True(await NewCoordinator().IsLeavingAsync(trio.BobId));
    }

    [Fact]
    public async Task AskingTwiceIsRefused()
    {
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        await coordinator.RequestAsync(trio.Bob, trio.BobId);

        var ex = await Assert.ThrowsAsync<GroupDepartedException>(
            () => coordinator.RequestAsync(trio.Bob, trio.BobId));

        Assert.Contains("already has an outstanding leave request", ex.Message);
    }

    [Fact]
    public async Task ASoleMembersRequestIsNotRecorded()
    {
        // Request refuses it -- nobody could commit it -- so recording the
        // intent would leave a request no repropose could ever satisfy.
        var alone = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);
        var groupId = new GroupId(alone.GroupId);
        await _fixture.Provider.PutGroupAsync(alone.ToRecord(_now));

        var coordinator = NewCoordinator();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.RequestAsync(alone.Group, groupId));

        Assert.Null(await _fixture.Provider.GetLeaveRequestAsync(groupId));
        Assert.False(await coordinator.IsLeavingAsync(groupId));
    }

    // ---- Surviving an epoch change ----

    [Fact]
    public async Task AnUnrelatedCommitDropsTheProposalAndTheIntentSurvivesIt()
    {
        // The whole reason this type exists. Bob's request is live and
        // committable, and then an unrelated commit lands first and discards it
        // everywhere -- with nobody rejecting it and nothing reporting a
        // failure. Only the durable intent survives that.
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        PublicMessage bobRequest = await coordinator.RequestAsync(trio.Bob, trio.BobId);
        Deliver(trio.Bob, trio.Alice.Group, bobRequest);
        Deliver(trio.Bob, trio.Carol, bobRequest);

        Assert.NotNull(MarmotGroupLeave.CommitDepartures(trio.Alice.Group));
        trio.Alice.Group.ClearPendingCommit();

        // Something else entirely lands first: Alice rotates her own leaf. It
        // has nothing to do with Bob, which is the point -- any commit at all
        // discards every cached proposal, and none of them reports having done
        // so.
        var (commit, _) = trio.Alice.Group.CommitPublic();
        var peeler = Peeler();
        string envelope = GroupHandshake.Wrap(trio.Alice.Group, peeler, commit);
        trio.Alice.Group.MergePendingCommit();

        var outcome = GroupHandshake.Receive(
            trio.Bob,
            peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(trio.Bob)).MlsBytes);
        Assert.Equal(HandshakeOutcome.CommitApplied, outcome.Outcome);
        await coordinator.ObserveAsync(trio.BobId, outcome.Outcome);

        // Bob's proposal is gone from everyone's cache -- Alice included.
        Assert.Empty(trio.Alice.Group.CachedProposals);
        Assert.Null(MarmotGroupLeave.CommitDepartures(trio.Alice.Group));

        // But the intent is not, and reproposing sends it against the new epoch.
        Assert.True(await coordinator.IsLeavingAsync(trio.BobId));

        PublicMessage? again = await coordinator.ReproposeIfStaleAsync(trio.Bob, trio.BobId);
        Assert.NotNull(again);

        Deliver(trio.Bob, trio.Alice.Group, again!);
        Assert.NotNull(MarmotGroupLeave.CommitDepartures(trio.Alice.Group));
    }

    [Fact]
    public async Task ReproposingIsANoOpAtTheEpochAlreadyProposedIn()
    {
        // Cheap to call on every epoch change, which is how it is meant to be
        // used -- so the common case must not put a duplicate on the wire.
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        await coordinator.RequestAsync(trio.Bob, trio.BobId);

        Assert.Null(await coordinator.ReproposeIfStaleAsync(trio.Bob, trio.BobId));
        Assert.Null(await coordinator.ReproposeIfStaleAsync(trio.Bob, trio.BobId));
    }

    [Fact]
    public async Task ReproposingWithNoRequestDoesNothing()
    {
        var trio = await TrioAsync();

        Assert.Null(await NewCoordinator().ReproposeIfStaleAsync(trio.Bob, trio.BobId));
    }

    [Fact]
    public async Task ReproposingRecordsTheEpochItWasSentIn()
    {
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        await coordinator.RequestAsync(trio.Bob, trio.BobId);

        // An unrelated commit: Alice adds nobody but rotates her own leaf.
        var (commit, _) = trio.Alice.Group.CommitPublic();
        var peeler = Peeler();
        string envelope = GroupHandshake.Wrap(trio.Alice.Group, peeler, commit);
        trio.Alice.Group.MergePendingCommit();
        GroupHandshake.Receive(
            trio.Bob, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(trio.Bob)).MlsBytes);

        Assert.NotNull(await coordinator.ReproposeIfStaleAsync(trio.Bob, trio.BobId));

        LeaveRequest stored = (await _fixture.Provider.GetLeaveRequestAsync(trio.BobId))!;
        Assert.Equal(new EpochId(trio.Bob.Epoch), stored.ProposedInEpoch);

        // The original epoch is kept, so how long the intent has been
        // outstanding stays answerable.
        Assert.NotEqual(stored.RequestedInEpoch, stored.ProposedInEpoch);

        // And it is now a no-op again until the next epoch.
        Assert.Null(await coordinator.ReproposeIfStaleAsync(trio.Bob, trio.BobId));
    }

    // ---- Resolving ----

    [Fact]
    public async Task BeingRemovedClearsTheIntentAndMarksTheGroup()
    {
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        await coordinator.RequestAsync(trio.Bob, trio.BobId);
        await coordinator.ObserveAsync(trio.BobId, HandshakeOutcome.RemovedByCommit);

        Assert.False(await coordinator.IsLeavingAsync(trio.BobId));
        Assert.True((await _fixture.Provider.GetGroupAsync(trio.BobId))!.Removed);
    }

    [Fact]
    public async Task AnEvictionResolvesTheSameWayWithNoRequest()
    {
        // Being removed and leaving reach the same end, so the observation does
        // not depend on having asked.
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        await coordinator.ObserveAsync(trio.BobId, HandshakeOutcome.RemovedByCommit);

        Assert.True((await _fixture.Provider.GetGroupAsync(trio.BobId))!.Removed);
    }

    [Fact]
    public async Task ARemovedGroupIsKeptRatherThanDeleted()
    {
        // The history stays readable; what changes is that it stops being a
        // group we can act in.
        var trio = await TrioAsync();

        await NewCoordinator().ObserveAsync(trio.BobId, HandshakeOutcome.RemovedByCommit);

        Assert.NotNull(await _fixture.Provider.GetGroupAsync(trio.BobId));
        Assert.DoesNotContain(
            await _fixture.Provider.ListLiveGroupsAsync(), g => g.Id == trio.BobId);
    }

    [Fact]
    public async Task AnOrdinaryCommitResolvesNothing()
    {
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        await coordinator.RequestAsync(trio.Bob, trio.BobId);
        await coordinator.ObserveAsync(trio.BobId, HandshakeOutcome.CommitApplied);
        await coordinator.ObserveAsync(trio.BobId, HandshakeOutcome.ProposalCached);

        Assert.True(await coordinator.IsLeavingAsync(trio.BobId));
        Assert.False((await _fixture.Provider.GetGroupAsync(trio.BobId))!.Removed);
    }

    // ---- The send gate ----

    [Fact]
    public async Task SendingIsAllowedUntilWeAskToLeave()
    {
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        await coordinator.RequireCanSendAsync(trio.BobId);

        await coordinator.RequestAsync(trio.Bob, trio.BobId);

        var ex = await Assert.ThrowsAsync<GroupDepartedException>(
            () => coordinator.RequireCanSendAsync(trio.BobId));

        Assert.Contains("outstanding leave request", ex.Message);
    }

    [Fact]
    public async Task ARemovedGroupCannotSend()
    {
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        await coordinator.ObserveAsync(trio.BobId, HandshakeOutcome.RemovedByCommit);

        var ex = await Assert.ThrowsAsync<GroupDepartedException>(
            () => coordinator.RequireCanSendAsync(trio.BobId));

        Assert.Contains("been removed", ex.Message);
    }

    [Fact]
    public async Task ARemovedGroupCannotBeLeftAgain()
    {
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        await coordinator.ObserveAsync(trio.BobId, HandshakeOutcome.RemovedByCommit);

        await Assert.ThrowsAsync<GroupDepartedException>(
            () => coordinator.RequestAsync(trio.Bob, trio.BobId));
    }

    [Fact]
    public async Task ReceivingIsNotGatedWhileLeaving()
    {
        // A leaver stays a member until someone commits the request, and the
        // message that finally lets them go arrives during exactly this window.
        // Gating reads here would hide it.
        var trio = await TrioAsync();
        var coordinator = NewCoordinator();

        PublicMessage request = await coordinator.RequestAsync(trio.Bob, trio.BobId);
        Deliver(trio.Bob, trio.Alice.Group, request);
        Deliver(trio.Bob, trio.Carol, request);

        StagedInvite staged = MarmotGroupLeave.CommitDepartures(trio.Alice.Group)!;
        var peeler = Peeler();
        string envelope = GroupHandshake.Wrap(trio.Alice.Group, peeler, staged.Commit);
        staged.Applied();

        var outcome = GroupHandshake.Receive(
            trio.Bob, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(trio.Bob)).MlsBytes);

        Assert.Equal(HandshakeOutcome.RemovedByCommit, outcome.Outcome);

        await coordinator.ObserveAsync(trio.BobId, outcome.Outcome);
        Assert.False(await coordinator.IsLeavingAsync(trio.BobId));
    }
}
