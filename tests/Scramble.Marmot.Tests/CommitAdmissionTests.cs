using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.Ingest;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Ingest;
using Scramble.Marmot.Storage;
using Scramble.Nostr.Crypto;
using Xunit;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Whether an inbound commit is one this member accepts at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>The test that matters here is the one with a non-admin committer.</b> MLS
/// authenticates a commit's sender and does not judge them, so a
/// <c>GroupContextExtensions</c> proposal from any member rewrites the
/// GroupContext wholesale — the <c>app_data_dictionary</c> and the admin policy
/// inside it. A suite that only ever exercises an admin's commit passes
/// identically with no guard at all, which is precisely the shape this
/// codebase keeps getting bitten by; so
/// <see cref="ANonAdminRewritingTheAdminPolicyIsRefused"/> is the load-bearing
/// case and everything else is either its control or a guard against
/// over-refusal.
/// </para>
/// <para>
/// <b>The other half of the suite is about what must keep working.</b> A guard
/// that rejects legitimate commits is worse than the hole it closes: a member
/// refused a commit the rest of the group applied is stranded at an epoch
/// nobody else is at, and v1 has no recovery path from there. So the ordinary
/// self-update, the leave flow, the admin's own governance commit and the
/// convergence replay all have tests asserting they still apply.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class CommitAdmissionTests : IDisposable
{
    private readonly StorageFixture _fixture = new();
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];

    private readonly DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddSeconds(Now);
    private readonly EpochManager _epochs = new();

    public void Dispose() => _fixture.Dispose();

    private sealed class LocalSigner : IAccountIdentityProofSigner
    {
        private readonly byte[] _secret;

        public LocalSigner()
        {
            var (secret, publicKey) = Bip340.GenerateKeyPair();
            _secret = secret;
            AccountPublicKey = publicKey;
        }

        public ReadOnlyMemory<byte> AccountPublicKey { get; }

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(_secret, template.ComputeId()));
    }

    /// <summary>
    /// Alice created the group and is its only admin; Bob and Carol joined.
    /// </summary>
    /// <remarks>
    /// Three members rather than two because a removal has to be judged by
    /// somebody it does not remove — the member being removed cannot process
    /// the commit at all, so a pair would only ever reach the eviction path.
    /// </remarks>
    private sealed record Trio(
        CreatedGroup Alice,
        byte[] AliceAccount,
        MlsGroup Bob,
        byte[] BobAccount,
        MlsGroup Carol,
        GroupId GroupId);

    private async Task<Trio> TrioAsync()
    {
        CreatedGroup alice = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        var bobSigner = new LocalSigner();
        var bobBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, bobSigner, Now);
        var carolBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);

        StagedCommit staged = MarmotGroupInvite.Add(
            alice.Group, _cs, [bobBundle.KeyPackage, carolBundle.KeyPackage]);
        staged.Applied();

        MlsGroup Join(MarmotKeyPackageBundle bundle) => MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey,
            config: MarmotGroupSettings.Create());

        var groupId = new GroupId(alice.GroupId);
        await _fixture.Provider.PutGroupAsync(alice.ToRecord(_now));

        MlsGroup bob = Join(bobBundle);
        MlsGroup carol = Join(carolBundle);

        _epochs.SetStable(groupId, new EpochId(carol.Epoch));

        return new Trio(
            alice,
            alice.Admins.Single(),
            bob,
            bobSigner.AccountPublicKey.ToArray(),
            carol,
            groupId);
    }

    // ---- Helpers ----

    private static MarmotDictionary DictionaryOf(MlsGroup group) =>
        MarmotDictionary.Decode(
            group.GroupContext.Extensions
                .Single(e => e.ExtensionType == MarmotDictionary.ExtensionType)
                .ExtensionData);

    private static IReadOnlyList<byte[]> AdminsOf(MlsGroup group) =>
        AdminPolicy.Decode(DictionaryOf(group).Get(AppComponent.GroupAdminPolicy)!).Admins;

    /// <summary>
    /// A commit that rewrites the GroupContext's dictionary behind MLS's back.
    /// </summary>
    /// <remarks>
    /// <b>No <c>AppDataUpdate</c> proposal anywhere in it.</b> That is the
    /// vector: MLS's own component guard checks a commit's AppDataUpdates
    /// against the resulting dictionary and returns early when there are none,
    /// so a <c>GroupContextExtensions</c> proposal replaces the whole extension
    /// set with nothing to check it against. Every member's leaf advertises
    /// <c>app_data_dictionary</c>, so RFC 9420 §12.1.7 has no objection either.
    /// </remarks>
    private static PublicMessage RewriteDictionary(MlsGroup author, Action<MarmotDictionary> edit)
    {
        MarmotDictionary dictionary = DictionaryOf(author);
        edit(dictionary);

        Extension[] rewritten = [.. author.GroupContext.Extensions
            .Select(e => e.ExtensionType == MarmotDictionary.ExtensionType
                ? new Extension(e.ExtensionType, dictionary.Encode())
                : e)];

        var (commit, _) = author.CommitPublic([new GroupContextExtensionsProposal(rewritten)]);
        return commit;
    }

    private static byte[] Serialize(PublicMessage message) =>
        TlsCodec.Serialize(new MlsMessage(WireFormat.MlsPublicMessage, message).WriteTo);

    private ReceivedHandshake Deliver(MlsGroup to, PublicMessage message) =>
        GroupHandshake.Receive(to, _cs, Serialize(message));

    private MessageIngest NewIngest() =>
        new(_fixture.Provider, _epochs, _cs, () => _now);

    // ---- The hole ----

    [Fact]
    public async Task ANonAdminRewritingTheAdminPolicyIsRefused()
    {
        // The whole finding, in one test. Bob is an ordinary member. He builds a
        // commit whose GroupContextExtensions proposal replaces 0x8003 with his
        // own key alone, and hands it to Alice. Before this guard existed Alice
        // applied it, and every admin check she ran afterwards agreed Bob was in
        // charge -- because it read the list he had just written.
        Trio t = await TrioAsync();

        PublicMessage attack = RewriteDictionary(
            t.Bob,
            d => d.Set(
                AppComponent.GroupAdminPolicy, AdminPolicy.Create([t.BobAccount]).Encode()));

        ulong epochBefore = t.Alice.Group.Epoch;

        var ex = Assert.Throws<UnauthorizedCommitException>(() => Deliver(t.Alice.Group, attack));

        Assert.Contains("not an active admin", ex.Message);

        // Refused means nothing moved, not merely that something was reported.
        Assert.Equal(epochBefore, t.Alice.Group.Epoch);
        Assert.Equal([t.AliceAccount], AdminsOf(t.Alice.Group));
    }

    [Fact]
    public async Task ANonAdminRewritingTheAdminPolicyIsRefusedByCarolToo()
    {
        // A member with no special standing reaches the same answer, which is
        // what stops the attack from merely needing a different target. Two
        // members disagreeing about whether a commit is acceptable is a fork.
        Trio t = await TrioAsync();

        PublicMessage attack = RewriteDictionary(
            t.Bob,
            d => d.Set(
                AppComponent.GroupAdminPolicy, AdminPolicy.Create([t.BobAccount]).Encode()));

        var ex = Assert.Throws<UnauthorizedCommitException>(() => Deliver(t.Carol, attack));

        // Asserted on the reason, not merely on the refusal. Mutation testing
        // found this test passing with the authorization rule deleted, because
        // the integrity rule catches the same commit for a different reason --
        // a test that accepts either is not pinning either.
        Assert.Contains("not an active admin", ex.Message);
        Assert.Equal([t.AliceAccount], AdminsOf(t.Carol));
    }

    [Fact]
    public async Task AnAdminsUnproposedDictionaryRewriteIsRefusedToo()
    {
        // The second half, isolated. Alice *is* an admin, so the authorization
        // rule has nothing to say -- and the commit is still refused, because a
        // dictionary entry changed that no AppDataUpdate in the commit accounts
        // for. Without this, an admin could write component bytes that never
        // passed a validator, by the same route.
        //
        // This test is what proves the integrity half is load-bearing on its
        // own: delete the authorization call and it still fails.
        Trio t = await TrioAsync();

        PublicMessage rewrite = RewriteDictionary(
            t.Alice.Group,
            d => d.Set(AppComponent.GroupAdminPolicy, AdminPolicy.Create([t.BobAccount]).Encode()));

        var ex = Assert.Throws<UnauthorizedCommitException>(() => Deliver(t.Carol, rewrite));

        Assert.Contains("outside an AppDataUpdate proposal", ex.Message);
        Assert.Equal([t.AliceAccount], AdminsOf(t.Carol));
    }

    [Fact]
    public async Task ANonAdminDroppingTheAdminPolicyEntirelyIsRefused()
    {
        // The other end of the same vector: not seizing the component but
        // deleting it. A group that reaches an epoch with no admin list has
        // frozen its membership and settings for good -- every commit that
        // could repair it is admin-gated.
        Trio t = await TrioAsync();

        PublicMessage attack = RewriteDictionary(
            t.Bob, d => d.Remove(AppComponent.GroupAdminPolicy));

        var ex = Assert.Throws<UnauthorizedCommitException>(() => Deliver(t.Alice.Group, attack));

        Assert.Contains("not an active admin", ex.Message);
        Assert.Equal([t.AliceAccount], AdminsOf(t.Alice.Group));
    }

    // ---- What must keep applying ----

    [Fact]
    public async Task ANonAdminsSelfUpdateStillApplies()
    {
        // The first of the two shapes a non-admin may commit. Refusing this
        // would strand every member the moment they rotated their own leaf,
        // which is the most routine commit there is.
        Trio t = await TrioAsync();
        ulong before = t.Alice.Group.Epoch;

        var (commit, _) = t.Bob.CommitPublic();

        Assert.Equal(HandshakeOutcome.CommitApplied, Deliver(t.Alice.Group, commit).Outcome);
        Assert.Equal(before + 1, t.Alice.Group.Epoch);
    }

    [Fact]
    public async Task ANonAdminsSelfRemoveStillApplies()
    {
        // The second allowed shape, and the one that arrives by reference: a
        // SelfRemove has to be committed by somebody else, so Alice caches Bob's
        // proposal and commits it, and Carol has to accept the result.
        //
        // This is also the case that exercises the reference-resolution path in
        // the view builder -- the kinds are resolved against the receiver's own
        // proposal cache, which is cleared the moment the commit applies.
        Trio t = await TrioAsync();

        PublicMessage request = MarmotGroupLeave.Request(t.Bob);
        Assert.Equal(HandshakeOutcome.ProposalCached, Deliver(t.Alice.Group, request).Outcome);
        Assert.Equal(HandshakeOutcome.ProposalCached, Deliver(t.Carol, request).Outcome);

        using StagedCommit? departure = MarmotGroupLeave.CommitDepartures(t.Alice.Group);
        Assert.NotNull(departure);

        Assert.Equal(
            HandshakeOutcome.CommitApplied, Deliver(t.Carol, departure!.Commit).Outcome);
        Assert.Equal(2, t.Carol.GetMembers().Count);
    }

    [Fact]
    public async Task AnAdminsAdminPolicyCommitStillApplies()
    {
        // The governance change the send path builds, arriving at a peer. If
        // this were refused the engine could stage an admin grant it could never
        // deliver, which is worse than having no grant at all: the sender would
        // be alone in an epoch nobody accepted.
        Trio t = await TrioAsync();

        using StagedCommit staged = MarmotGroupAdminPolicy.Stage(
            t.Alice.Group, _cs, [t.AliceAccount, t.BobAccount]);

        Assert.Equal(HandshakeOutcome.CommitApplied, Deliver(t.Carol, staged.Commit).Outcome);

        IReadOnlyList<byte[]> admins = AdminsOf(t.Carol);
        Assert.Equal(2, admins.Count);
        Assert.Contains(admins, a => a.AsSpan().SequenceEqual(t.BobAccount));
    }

    [Fact]
    public async Task AnAdminsRemovalOfAMemberStillApplies()
    {
        // An admin-gated commit from an actual admin. The guard must read the
        // committer, not merely notice that the commit is privileged.
        Trio t = await TrioAsync();

        using StagedCommit staged = MarmotGroupInvite.Remove(t.Alice.Group, [t.BobAccount]);

        Assert.Equal(HandshakeOutcome.CommitApplied, Deliver(t.Carol, staged.Commit).Outcome);
        Assert.Equal(2, t.Carol.GetMembers().Count);
    }

    [Fact]
    public async Task ACommitThatRemovesUsIsReportedRatherThanRefused()
    {
        // Being evicted is a legitimate outcome of an ordinary commit, and the
        // resulting epoch is unreadable to us -- so the probe cannot apply it
        // either. That must come back as the eviction it is and not as a
        // refusal, or a removed member would retry forever.
        Trio t = await TrioAsync();

        using StagedCommit staged = MarmotGroupInvite.Remove(t.Alice.Group, [t.BobAccount]);

        Assert.Equal(HandshakeOutcome.RemovedByCommit, Deliver(t.Bob, staged.Commit).Outcome);
    }

    // ---- What is newly refused ----

    [Fact]
    public async Task ANonAdminRemovingAnotherMemberIsRefused()
    {
        // New behaviour, and stated as a test rather than left implicit: before
        // this guard a non-admin could evict anybody and every peer applied it.
        // CommitAuthorization already called this shape privileged -- nothing
        // consulted it.
        Trio t = await TrioAsync();

        using StagedCommit staged = MarmotGroupInvite.Remove(t.Bob, [t.AliceAccount]);

        var ex = Assert.Throws<UnauthorizedCommitException>(() => Deliver(t.Carol, staged.Commit));

        Assert.Contains("not an active admin", ex.Message);
        Assert.Equal(3, t.Carol.GetMembers().Count);
    }

    // ---- The second door ----

    [Fact]
    public async Task IngestRefusesAnUnauthorizedCommitWithoutBurningIt()
    {
        // Refused and reported, but left in the state a convergence pass reads.
        //
        // <b>The refusal here is made against the wrong state whenever a fork is
        // involved.</b> The gate that lets it run is an epoch-*number* match, and
        // after a fork two branches sit at the same number with different trees
        // and different admin lists — so a peer's commit is judged with its
        // committer's leaf resolved in our tree and its authority read from our
        // policy. Where the fork raced an add, a remove or an admin change, that
        // verdict is about the wrong member under the wrong policy.
        //
        // Filing Failed would make such a verdict permanent: ConvergencePass
        // lists Retryable only, so the branch would never be scored again. This
        // pins that it is not, and the test below pins that deferring costs the
        // refusal nothing.
        Trio t = await TrioAsync();

        PublicMessage attack = RewriteDictionary(
            t.Bob,
            d => d.Set(
                AppComponent.GroupAdminPolicy, AdminPolicy.Create([t.BobAccount]).Encode()));

        byte[] wire = Serialize(attack);

        IngestResult result = await NewIngest().IngestAsync(t.Carol, t.GroupId, wire);

        var ignored = Assert.IsType<IngestOutcome.Ignored>(result.Outcome);
        Assert.Equal(InputRejectionCategory.AuthorizationFailed, ignored.Category);

        // Nothing was applied -- the refusal is real, not a deferral of the
        // decision to apply.
        Assert.Equal(3, t.Carol.GetMembers().Count);

        MessageRecord? record = await _fixture.Provider.GetMessageAsync(
            MessageId.FromMlsBytes(wire));

        Assert.NotNull(record);
        Assert.Equal(MessageRecordState.Retryable, record!.State);

        // And it is genuinely visible to a pass, rather than merely not-Failed.
        IReadOnlyList<MessageRecord> retryable = await _fixture.Provider
            .ListMessagesByStateAsync(t.GroupId, MessageRecordState.Retryable);

        Assert.Contains(retryable, r => r.Id == record.Id);
    }

    [Fact]
    public async Task ConvergenceRefusesAnUnauthorizedCommitRatherThanReplayingIt()
    {
        // The replay path is the other way into ProcessCommit, and it restores
        // its own probe at the fork epoch -- so it can and must ask the same
        // question there. A branch built from a commit nobody was allowed to
        // make is not a branch this member may be argued onto.
        Trio t = await TrioAsync();

        byte[] snapshot = t.Carol.Export();
        var archive = new Dictionary<ulong, byte[]> { [t.Carol.Epoch] = snapshot };

        var sourceEpoch = new EpochId(t.Bob.Epoch);

        PublicMessage attack = RewriteDictionary(
            t.Bob,
            d => d.Set(
                AppComponent.GroupAdminPolicy, AdminPolicy.Create([t.BobAccount]).Encode()));

        byte[] wire = Serialize(attack);

        var materializer = new CandidateMaterializer(
            ConvergencePolicy.V1,
            epoch => archive.TryGetValue(epoch.Value, out byte[]? blob)
                ? MlsGroup.Import(blob, _cs)
                : null);

        var liveTip = new CommitTip(
            CommitOrderingPriority.Ordinary,
            new MessageId([.. Enumerable.Repeat((byte)0x5a, 32)]),
            [.. Enumerable.Repeat((byte)0x77, 32)]);

        MaterializationResult result = materializer.Materialize(
            t.Carol,
            liveTip,
            [new StoredCommit(MessageId.FromMlsBytes(wire), sourceEpoch, wire, IsOurs: false)],
            _ => []);

        RefusedCommit refused = Assert.Single(result.Refused);
        Assert.Equal(MaterializationRefusal.Unauthorized, refused.Reason);

        // Only the branch we are already on survives to be compared.
        Assert.Equal(liveTip.BranchId, Assert.Single(result.Candidates).Id);
    }

    [Fact]
    public async Task ConvergenceRefusesANonAdminsRemovalOnAuthorityAlone()
    {
        // A removal touches no dictionary entry, so the integrity rule has
        // nothing to say about it and only the authorization rule can refuse
        // it. Without this test, deleting the authorization call from the
        // convergence path would leave every convergence test still green --
        // which is what the first mutation run found.
        Trio t = await TrioAsync();

        var archive = new Dictionary<ulong, byte[]> { [t.Carol.Epoch] = t.Carol.Export() };
        var sourceEpoch = new EpochId(t.Bob.Epoch);

        using StagedCommit staged = MarmotGroupInvite.Remove(t.Bob, [t.AliceAccount]);
        byte[] wire = Serialize(staged.Commit);

        var materializer = new CandidateMaterializer(
            ConvergencePolicy.V1,
            epoch => archive.TryGetValue(epoch.Value, out byte[]? blob)
                ? MlsGroup.Import(blob, _cs)
                : null);

        var liveTip = new CommitTip(
            CommitOrderingPriority.Ordinary,
            new MessageId([.. Enumerable.Repeat((byte)0x5a, 32)]),
            [.. Enumerable.Repeat((byte)0x77, 32)]);

        MaterializationResult result = materializer.Materialize(
            t.Carol,
            liveTip,
            [new StoredCommit(MessageId.FromMlsBytes(wire), sourceEpoch, wire, IsOurs: false)],
            _ => []);

        RefusedCommit refused = Assert.Single(result.Refused);
        Assert.Equal(MaterializationRefusal.Unauthorized, refused.Reason);
    }

    [Fact]
    public async Task ConvergenceStillBuildsABranchFromAnOrdinaryCommit()
    {
        // The control for the test above: the same machinery, a commit a
        // non-admin is allowed to make, and a branch that materialises. Without
        // this, a guard that refused everything would look identical.
        Trio t = await TrioAsync();

        var archive = new Dictionary<ulong, byte[]> { [t.Carol.Epoch] = t.Carol.Export() };
        var sourceEpoch = new EpochId(t.Bob.Epoch);

        var (commit, _) = t.Bob.CommitPublic();
        byte[] wire = Serialize(commit);

        var materializer = new CandidateMaterializer(
            ConvergencePolicy.V1,
            epoch => archive.TryGetValue(epoch.Value, out byte[]? blob)
                ? MlsGroup.Import(blob, _cs)
                : null);

        var liveTip = new CommitTip(
            CommitOrderingPriority.Ordinary,
            new MessageId([.. Enumerable.Repeat((byte)0x5a, 32)]),
            [.. Enumerable.Repeat((byte)0x77, 32)]);

        MaterializationResult result = materializer.Materialize(
            t.Carol,
            liveTip,
            [new StoredCommit(MessageId.FromMlsBytes(wire), sourceEpoch, wire, IsOurs: false)],
            _ => []);

        Assert.Empty(result.Refused);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task ACommitterWeCannotResolveIsRefusedRatherThanWavedThrough()
    {
        // "We could not tell who sent this" is not evidence that they were
        // entitled to send it. The sender leaf is restamped to one nobody
        // holds, which is what a forged frame looks like from here, and the
        // rule has to fail closed on it.
        //
        // The membership tag would refuse this a step later anyway. That is not
        // a reason to let it past: the guard runs first, so its answer is the
        // one that decides whether a commit is even considered.
        Trio t = await TrioAsync();

        using StagedCommit staged = MarmotGroupInvite.Remove(t.Alice.Group, [t.BobAccount]);

        staged.Commit.Content.Sender.LeafIndex = 99;

        var ex = Assert.Throws<UnauthorizedCommitException>(() => Deliver(t.Carol, staged.Commit));

        Assert.Contains("no member of this", ex.Message);
        Assert.Equal(3, t.Carol.GetMembers().Count);
    }

    [Fact]
    public async Task ACommitFramedAgainstAnotherEpochIsDeferredRatherThanRefused()
    {
        // The rule this protects: a commit is judged against the admin list its
        // author saw, and that list belongs to the epoch it was framed against.
        // Bob's commit forks from the epoch we have already left, so the list
        // here is not the one it should be measured by -- and a terminal
        // refusal would destroy a branch that convergence is supposed to judge
        // on its own terms, at the epoch it actually forks from.
        //
        // Deferring is the answer even though this particular commit will be
        // refused later, because the two paths must reach that verdict from the
        // same evidence.
        Trio t = await TrioAsync();

        using StagedCommit fork = MarmotGroupInvite.Remove(t.Bob, [t.AliceAccount]);
        byte[] wire = Serialize(fork.Commit);

        // Carol moves on, so Bob's commit now names an epoch she has left.
        var (advance, _) = t.Alice.Group.CommitPublic();
        Assert.Equal(HandshakeOutcome.CommitApplied, Deliver(t.Carol, advance).Outcome);
        _epochs.SetStable(t.GroupId, new EpochId(t.Carol.Epoch));

        IngestResult result = await NewIngest().IngestAsync(t.Carol, t.GroupId, wire);

        Assert.IsType<IngestOutcome.TransportDeferred>(result.Outcome);

        MessageRecord? record = await _fixture.Provider.GetMessageAsync(
            MessageId.FromMlsBytes(wire));

        Assert.Equal(MessageRecordState.Retryable, record!.State);
    }

    [Fact]
    public async Task AReorgWillNotWalkOntoABranchThroughAnUnauthorizedCommit()
    {
        // Reorg picks its own commits -- "the first stored commit forking from
        // the epoch reached" -- so it is not guaranteed to replay the chain
        // that was scored. Here the unauthorized commit sits ahead of the
        // legitimate one in the stored list and reaches the same tip epoch, so
        // a Reorg that trusted materialization would adopt it and call the
        // branch rebuilt.
        Trio t = await TrioAsync();

        var archive = new Dictionary<ulong, byte[]> { [t.Carol.Epoch] = t.Carol.Export() };
        var forkEpoch = new EpochId(t.Carol.Epoch);

        var (ordinary, _) = t.Bob.CommitPublic();
        byte[] ordinaryWire = Serialize(ordinary);

        PublicMessage attack = RewriteDictionary(
            t.Alice.Group,
            d => d.Set(
                AppComponent.GroupAdminPolicy, AdminPolicy.Create([t.BobAccount]).Encode()));

        byte[] attackWire = Serialize(attack);

        var materializer = new CandidateMaterializer(
            ConvergencePolicy.V1,
            epoch => archive.TryGetValue(epoch.Value, out byte[]? blob)
                ? MlsGroup.Import(blob, _cs)
                : null);

        var liveTip = new CommitTip(
            CommitOrderingPriority.Ordinary,
            new MessageId([.. Enumerable.Repeat((byte)0x5a, 32)]),
            [.. Enumerable.Repeat((byte)0x77, 32)]);

        var legitimate = new StoredCommit(
            MessageId.FromMlsBytes(ordinaryWire), forkEpoch, ordinaryWire, IsOurs: false);

        BranchCandidate winner = Assert.Single(
            materializer
                .Materialize(t.Carol, liveTip, [legitimate], _ => [])
                .Candidates,
            c => !string.Equals(c.Id, liveTip.BranchId, StringComparison.Ordinal));

        var unauthorized = new StoredCommit(
            MessageId.FromMlsBytes(attackWire), forkEpoch, attackWire, IsOurs: false);

        Assert.Throws<UnauthorizedCommitException>(
            () => materializer.Reorg(winner, [unauthorized, legitimate]));
    }

    // ---- The rule reads the pre-commit epoch ----

    [Fact]
    public async Task AuthorityIsReadFromTheEpochTheCommitWasBuiltOn()
    {
        // Bob becomes an admin, and only then may he commit a governance
        // change. The point of the test is the ordering: the commit Bob makes
        // second is authorised by the epoch Alice's commit produced, which is
        // the epoch Bob built his on -- not by the one his own commit produces.
        //
        // A guard reading the resulting epoch instead would authorise the attack
        // in the first test, because the attacker writes themselves in.
        Trio t = await TrioAsync();

        using StagedCommit grant = MarmotGroupAdminPolicy.Stage(
            t.Alice.Group, _cs, [t.AliceAccount, t.BobAccount]);

        Assert.Equal(HandshakeOutcome.CommitApplied, Deliver(t.Bob, grant.Commit).Outcome);
        Assert.Equal(HandshakeOutcome.CommitApplied, Deliver(t.Carol, grant.Commit).Outcome);
        grant.Applied();

        using StagedCommit byBob = MarmotGroupAdminPolicy.Stage(t.Bob, _cs, [t.BobAccount]);

        Assert.Equal(HandshakeOutcome.CommitApplied, Deliver(t.Carol, byBob.Commit).Outcome);
        Assert.Equal([t.BobAccount], AdminsOf(t.Carol));
    }
}
