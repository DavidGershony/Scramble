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
/// Leaving a group, and carrying handshake messages over kind-445.
/// </summary>
/// <remarks>
/// <para>
/// Leaving is the one membership change nobody can make alone: RFC 9420 §12.2
/// needs the committer to stay a member, so the leaver proposes and someone else
/// commits. Most of what is checked here is that asymmetry surviving contact
/// with the transport — the proposal has to reach other members, be cached by
/// them, and be committed by one of them, and each of those is a place where a
/// leave silently fails to happen.
/// </para>
/// <para>
/// The handshake transport is exercised alongside it because it is new with the
/// same slice: before this, no commit or proposal had ever been put on the wire
/// at all — groups were bootstrapped from a Welcome and only application
/// messages travelled afterwards.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class GroupLeaveTests
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

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(Secret, template.ComputeId()));
    }

    private static NostrGroupPeeler Peeler() => new();

    /// <summary>A creator plus two joined members, all at one epoch.</summary>
    /// <remarks>
    /// Three, not two: with two members "the sender" and "not the committer"
    /// are the same leaf, so a self-remove that resolved to the wrong one would
    /// still look correct.
    /// </remarks>
    private async Task<(CreatedGroup Alice, MlsGroup Bob, LocalSigner BobSigner,
        MlsGroup Carol, LocalSigner CarolSigner)> TrioAsync()
    {
        var alice = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        var bobSigner = new LocalSigner();
        var carolSigner = new LocalSigner();
        var bobBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, bobSigner, Now);
        var carolBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, carolSigner, Now);

        StagedInvite staged = MarmotGroupInvite.Add(
            alice.Group, _cs, [bobBundle.KeyPackage, carolBundle.KeyPackage]);
        staged.Applied();

        MlsGroup Join(MarmotKeyPackageBundle bundle) => MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey);

        return (alice, Join(bobBundle), bobSigner, Join(carolBundle), carolSigner);
    }

    /// <summary>Puts a handshake message through the wire and back.</summary>
    private static ReceivedHandshake Deliver(MlsGroup from, MlsGroup to, PublicMessage message)
    {
        var peeler = Peeler();
        string envelope = message.Content.ContentType == ContentType.Commit
            ? GroupHandshake.Wrap(from, peeler, message)
            : GroupHandshake.WrapProposal(from, peeler, message);

        // Peeled with the receiver's own secret, which is the point: if the two
        // sides were at different epochs this would fail here.
        return GroupHandshake.Receive(
            to, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(to)).MlsBytes);
    }

    // ---- Requesting ----

    [Fact]
    public async Task RequestingToLeaveChangesNothingUntilSomeoneCommitsIt()
    {
        var (alice, bob, _, _, _) = await TrioAsync();
        ulong epoch = bob.Epoch;

        PublicMessage request = MarmotGroupLeave.Request(bob);

        // Bob is still a member, still at the same epoch, and still able to read
        // the group. A client that hid the group here would be showing an
        // outcome that has not happened.
        Assert.Equal(epoch, bob.Epoch);
        Assert.Equal(3, bob.GetMembers().Count);
        Assert.Equal(3, alice.Group.GetMembers().Count);
        Assert.Equal(ContentType.Proposal, request.Content.ContentType);
        Assert.False(bob.HasPendingCommit);
    }

    [Fact]
    public async Task ASoleMemberCannotLeave()
    {
        // Nobody would remain to commit it, so the request could never resolve.
        var alone = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        var ex = Assert.Throws<InvalidOperationException>(
            () => MarmotGroupLeave.Request(alone.Group));

        Assert.Contains("sole member", ex.Message);
    }

    // ---- The round trip ----

    [Fact]
    public async Task AMemberLeavesWhenAnotherCommitsTheRequest()
    {
        var (alice, bob, bobSigner, carol, _) = await TrioAsync();
        byte[] bobAccount = bobSigner.AccountPublicKey.ToArray();

        PublicMessage request = MarmotGroupLeave.Request(bob);
        Assert.Equal(HandshakeOutcome.ProposalCached, Deliver(bob, alice.Group, request).Outcome);
        Assert.Equal(HandshakeOutcome.ProposalCached, Deliver(bob, carol, request).Outcome);

        StagedInvite staged = Assert.IsType<StagedInvite>(
            MarmotGroupLeave.CommitDepartures(alice.Group));

        Assert.Equal(bobAccount, Assert.Single(staged.AddedAccounts));
        Assert.Null(staged.Welcome);

        // Publish-before-apply: wrapped while the commit is still pending, so it
        // travels under the epoch every recipient is actually at.
        var peeler = Peeler();
        string envelope = GroupHandshake.Wrap(alice.Group, peeler, staged.Commit);
        staged.Applied();

        var received = GroupHandshake.Receive(
            carol, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(carol)).MlsBytes);

        Assert.Equal(HandshakeOutcome.CommitApplied, received.Outcome);
        Assert.Equal(alice.Group.Epoch, carol.Epoch);
        Assert.Equal(2, carol.GetMembers().Count);
        Assert.DoesNotContain(
            carol.GetMembers(), m => m.identity.AsSpan().SequenceEqual(bobAccount));
    }

    [Fact]
    public async Task TheLeaverLearnsFromTheCommitThatTheyAreOut()
    {
        // Bob has to be told, and the only thing that tells him is the commit
        // itself. Reporting it rather than throwing matters: the group state
        // afterwards is unusable, so the caller needs to know why.
        var (alice, bob, _, carol, _) = await TrioAsync();
        ulong bobEpoch = bob.Epoch;

        PublicMessage request = MarmotGroupLeave.Request(bob);
        Deliver(bob, alice.Group, request);
        Deliver(bob, carol, request);
        Deliver(bob, bob, request);

        StagedInvite staged = MarmotGroupLeave.CommitDepartures(alice.Group)!;
        var peeler = Peeler();
        string envelope = GroupHandshake.Wrap(alice.Group, peeler, staged.Commit);
        staged.Applied();

        var received = GroupHandshake.Receive(
            bob, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(bob)).MlsBytes);

        Assert.Equal(HandshakeOutcome.RemovedByCommit, received.Outcome);

        // Bob's group is left where it was rather than half-advanced. He cannot
        // reach the new epoch at all -- an UpdatePath encrypts path secrets only
        // to remaining members -- so there is no state to move to, and what he
        // still holds belongs to the epoch he was removed in.
        Assert.Equal(bobEpoch, bob.Epoch);
        Assert.NotEqual(alice.Group.Epoch, bob.Epoch);
        Assert.False(bob.HasPendingCommit);
    }

    [Fact]
    public async Task TheDepartedCannotReadWhatTheGroupSendsNext()
    {
        // The transport key is derived per epoch, so a removed member holds a
        // secret for an epoch the group has left. This is what makes a leave
        // mean something rather than being a bookkeeping change.
        var (alice, bob, _, carol, _) = await TrioAsync();

        PublicMessage request = MarmotGroupLeave.Request(bob);
        Deliver(bob, alice.Group, request);
        Deliver(bob, carol, request);

        StagedInvite staged = MarmotGroupLeave.CommitDepartures(alice.Group)!;
        var peeler = Peeler();
        GroupHandshake.Wrap(alice.Group, peeler, staged.Commit);
        staged.Applied();

        Assert.NotEqual(
            GroupMessages.ExporterSecret(alice.Group), GroupMessages.ExporterSecret(bob));
    }

    // ---- Who leaves ----

    [Fact]
    public async Task TheDepartingMemberIsTheSenderAndNotTheCommitter()
    {
        // The proposal has no body: its sender is the only thing naming who
        // leaves. Were the committer's leaf used, this would remove Alice.
        var (alice, _, _, carol, carolSigner) = await TrioAsync();

        Deliver(carol, alice.Group, MarmotGroupLeave.Request(carol));

        StagedInvite staged = MarmotGroupLeave.CommitDepartures(alice.Group)!;

        Assert.Equal(carolSigner.AccountPublicKey.ToArray(), Assert.Single(staged.AddedAccounts));
    }

    [Fact]
    public async Task OurOwnRequestIsSkippedRatherThanCommitted()
    {
        // We cache our own proposal like any other, and committing it is the one
        // thing RFC 9420 forbids. Treating that as an error would let our own
        // pending departure block everyone else's.
        var (alice, bob, bobSigner, carol, _) = await TrioAsync();

        Deliver(alice.Group, alice.Group, MarmotGroupLeave.Request(alice.Group));
        Deliver(bob, alice.Group, MarmotGroupLeave.Request(bob));

        StagedInvite staged = Assert.IsType<StagedInvite>(
            MarmotGroupLeave.CommitDepartures(alice.Group));

        Assert.Equal(bobSigner.AccountPublicKey.ToArray(), Assert.Single(staged.AddedAccounts));
        Assert.NotNull(carol);
    }

    [Fact]
    public async Task NobodyLeavingCommitsNothing()
    {
        // Null rather than an empty commit: an empty commit is a real operation
        // with a cost, and a caller polling for departures would emit one every
        // time it looked.
        var (alice, _, _, _, _) = await TrioAsync();

        Assert.Null(MarmotGroupLeave.CommitDepartures(alice.Group));
        Assert.False(alice.Group.HasPendingCommit);
    }

    [Fact]
    public async Task TheLastOtherMemberCanLeave()
    {
        // A group of one is an ordinary outcome, and this is the most common
        // leave there is: the other person in a two-person conversation walks
        // away. Refusing it -- as an earlier version of this did, on the theory
        // that a group of one is degenerate -- breaks the common case to guard
        // an uncommon one that is not actually harmful.
        var (alice, bob, _, carol, carolSigner) = await TrioAsync();

        // To Bob as well: a commit cites the proposal by hash, so a member who
        // never cached it cannot resolve what the commit does.
        PublicMessage carolRequest = MarmotGroupLeave.Request(carol);
        Deliver(carol, alice.Group, carolRequest);
        Deliver(carol, bob, carolRequest);

        StagedInvite first = MarmotGroupLeave.CommitDepartures(alice.Group)!;
        var peeler = Peeler();
        string envelope = GroupHandshake.Wrap(alice.Group, peeler, first.Commit);
        first.Applied();
        GroupHandshake.Receive(
            bob, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(bob)).MlsBytes);

        Assert.Equal(2, alice.Group.GetMembers().Count);
        Assert.NotNull(carolSigner);

        // And now Bob, the last other member.
        Deliver(bob, alice.Group, MarmotGroupLeave.Request(bob));
        StagedInvite second = MarmotGroupLeave.CommitDepartures(alice.Group)!;
        GroupHandshake.Wrap(alice.Group, Peeler(), second.Commit);
        second.Applied();

        Assert.Single(alice.Group.GetMembers());
        Assert.Equal(alice.Group.MyLeafIndex, alice.Group.GetMembers()[0].leafIndex);
    }

    // ---- Departure colliding with removal ----

    [Fact]
    public async Task ARequestFromSomeoneBeingRemovedOutrightIsDroppedFirst()
    {
        // Both resolve to the same leaf and a commit carrying both removes it
        // twice, which the library refuses -- so without dropping the request
        // an admin evicting a departing member finds every commit blocked.
        var (alice, bob, bobSigner, _, _) = await TrioAsync();
        byte[] bobAccount = bobSigner.AccountPublicKey.ToArray();

        Deliver(bob, alice.Group, MarmotGroupLeave.Request(bob));

        Assert.Equal(1, MarmotGroupLeave.DropRequestsFrom(alice.Group, bobAccount));
        Assert.Null(MarmotGroupLeave.CommitDepartures(alice.Group));

        StagedInvite staged = MarmotGroupInvite.Remove(alice.Group, [bobAccount]);
        staged.Applied();

        Assert.DoesNotContain(
            alice.Group.GetMembers(), m => m.identity.AsSpan().SequenceEqual(bobAccount));
    }

    [Fact]
    public async Task DroppingNamesTheAccountAndLeavesOtherRequestsAlone()
    {
        var (alice, bob, bobSigner, carol, _) = await TrioAsync();

        Deliver(bob, alice.Group, MarmotGroupLeave.Request(bob));
        Deliver(carol, alice.Group, MarmotGroupLeave.Request(carol));

        Assert.Equal(1, MarmotGroupLeave.DropRequestsFrom(
            alice.Group, bobSigner.AccountPublicKey.Span));

        StagedInvite staged = MarmotGroupLeave.CommitDepartures(alice.Group)!;
        Assert.Single(staged.AddedAccounts);
        Assert.DoesNotContain(
            staged.AddedAccounts, a => a.AsSpan().SequenceEqual(bobSigner.AccountPublicKey.Span));
    }

    // ---- The handshake transport ----

    [Fact]
    public async Task ACommitMustBeWrappedBeforeItIsApplied()
    {
        // Applying first moves the group to the epoch the commit creates, so the
        // envelope would be sealed with a key no recipient has -- an event that
        // looks fine and reaches nobody.
        var (alice, bob, _, _, _) = await TrioAsync();

        Deliver(bob, alice.Group, MarmotGroupLeave.Request(bob));

        StagedInvite staged = MarmotGroupLeave.CommitDepartures(alice.Group)!;
        staged.Applied();

        var ex = Assert.Throws<InvalidOperationException>(
            () => GroupHandshake.Wrap(alice.Group, Peeler(), staged.Commit));

        Assert.Contains("Wrap before applying", ex.Message);
    }

    [Fact]
    public async Task AnApplicationMessageIsNotAHandshake()
    {
        // The two share a transport and are framed differently: a handshake is a
        // signed PublicMessage, an application message an encrypted
        // PrivateMessage. Reading one as the other must fail rather than
        // half-succeed.
        var (alice, _, _, _, _) = await TrioAsync();
        var peeler = Peeler();

        string envelope = GroupMessages.Send(
            alice.Group,
            peeler,
            MarmotAppEvent.Chat(
                Convert.ToHexString(AliceAccount(alice)).ToLowerInvariant(), (long)Now, "hello"),
            AliceAccount(alice));

        var ex = Assert.Throws<MarmotAppEventException>(() => GroupHandshake.Receive(
            alice.Group, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(alice.Group)).MlsBytes));

        Assert.Contains("handshake", ex.Message);
    }

    [Fact]
    public async Task AHandshakeIsNotAnApplicationMessage()
    {
        var (alice, bob, _, _, _) = await TrioAsync();
        var peeler = Peeler();

        string envelope = GroupHandshake.WrapProposal(
            bob, peeler, MarmotGroupLeave.Request(bob));

        var ex = Assert.Throws<MarmotAppEventException>(() => GroupMessages.Receive(
            alice.Group, peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(alice.Group)).MlsBytes));

        Assert.Contains("application message", ex.Message);
    }

    [Fact]
    public async Task WrapRefusesToSendACommitAsAProposalOrTheReverse()
    {
        var (alice, bob, _, _, _) = await TrioAsync();

        PublicMessage request = MarmotGroupLeave.Request(bob);
        Assert.Throws<ArgumentException>(() => GroupHandshake.Wrap(bob, Peeler(), request));

        Deliver(bob, alice.Group, request);
        StagedInvite staged = MarmotGroupLeave.CommitDepartures(alice.Group)!;

        Assert.Throws<ArgumentException>(
            () => GroupHandshake.WrapProposal(alice.Group, Peeler(), staged.Commit));
    }

    private static byte[] AliceAccount(CreatedGroup alice) =>
        alice.Group.GetMembers().Single(m => m.leafIndex == alice.Group.MyLeafIndex).identity;
}
