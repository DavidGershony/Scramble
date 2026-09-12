using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.Engine;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.Ingest;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Ingest;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests.Convergence;

/// <summary>
/// The pass that makes a fork resolvable outside a test.
/// </summary>
/// <remarks>
/// <para>
/// Every fork here is real: two members commit from one epoch, both commits
/// reach a third, and one of them will not apply. That is the only construction
/// worth using, because the interesting failures are all in the wiring — a
/// candidate set missing our own branch, a branch described on terms nobody
/// else computed, a decision taken before the input stopped arriving — and none
/// of them shows up against a synthetic candidate list.
/// </para>
/// <para>
/// <b>Which branch wins is never left to chance.</b> Two branches of equal
/// depth are separated by committer and digest, which are fresh keys and
/// therefore a coin toss per run. So every test here makes one branch deeper,
/// and asserts the outcome that follows from the rule rather than the one that
/// happened to occur.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class ConvergencePassTests : IDisposable
{
    private readonly StorageFixture _fixture = new();
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private readonly EpochManager _epochs = new();
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];

    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddSeconds(Now);

    public void Dispose() => _fixture.Dispose();

    private EpochArchive NewArchive() =>
        new(_fixture.Provider, _cs, ConvergencePolicy.V1, () => _now);

    private MessageIngest NewIngest(EpochArchive archive) =>
        new(_fixture.Provider, _epochs, () => _now, archive);

    private ConvergencePass NewPass(EpochArchive archive) =>
        new(_fixture.Provider, _epochs, archive, _cs, ConvergencePolicy.V1, () => _now);

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

    /// <summary>
    /// Three members at a shared epoch, with that epoch archived.
    /// </summary>
    /// <remarks>
    /// Us, plus two who can each commit from the same epoch. The shared epoch is
    /// archived with a null tip because that is what it truly is — reached
    /// through a Welcome, produced by a commit we never held. It has to be
    /// restorable all the same, since it is where every branch below forks.
    /// </remarks>
    private sealed record Fork(
        LocalSigner AliceSigner, CreatedGroup Alice, MlsGroup Carol, MlsGroup Us, GroupId GroupId);

    private async Task<Fork> ForkAsync(EpochArchive archive)
    {
        var aliceSigner = new LocalSigner();

        CreatedGroup alice = await MarmotGroupBuilder.CreateAsync(
            _cs, aliceSigner, "Rakes", "", Now, Relays);

        var carolBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);
        var ourBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);

        StagedCommit staged = MarmotGroupInvite.Add(
            alice.Group, _cs, [carolBundle.KeyPackage, ourBundle.KeyPackage]);
        staged.Applied();

        MlsGroup Join(MarmotKeyPackageBundle bundle) => MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey,
            config: MarmotGroupSettings.Create());

        MlsGroup carol = Join(carolBundle);
        MlsGroup us = Join(ourBundle);

        var groupId = new GroupId(alice.GroupId);
        await _fixture.Provider.PutGroupAsync(alice.ToRecord(_now));
        _epochs.SetStable(groupId, new EpochId(us.Epoch));

        await archive.CaptureAsync(groupId, us, tip: null);

        return new Fork(aliceSigner, alice, carol, us, groupId);
    }

    /// <summary>A commit from a member, merged into them, as wire bytes.</summary>
    private static byte[] Commit(MlsGroup author)
    {
        var (commit, _) = author.CommitPublic();
        author.MergePendingCommit();

        return TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, commit).WriteTo);
    }

    /// <summary>An application message from Alice, peeled with her own secret.</summary>
    private static byte[] AppMessage(Fork fork, string text)
    {
        var peeler = new NostrGroupPeeler();

        string envelope = GroupMessages.Send(
            fork.Alice.Group,
            peeler,
            MarmotAppEvent.Chat(fork.AliceSigner.Hex, (long)Now, text),
            fork.AliceSigner.AccountPublicKey.Span);

        return peeler
            .Peel(envelope, _ => GroupMessages.ExporterSecret(fork.Alice.Group))
            .MlsBytes;
    }

    /// <summary>Moves past the settlement window so a decision is allowed.</summary>
    private void Quiesce() =>
        _now = _now.AddMilliseconds(ConvergencePolicy.V1SettlementQuiescenceMs + 1);

    // ---- Nothing to decide ----

    [Fact]
    public async Task AGroupWithNothingCompetingIsSettled()
    {
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);

        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);

        Assert.Equal(ConvergenceStatus.Settled, result.Status);
        Assert.False(result.Reorged);
        Assert.Same(fork.Us, result.Group);
        Assert.Null(result.Trace);
    }

    // ---- A real fork ----

    [Fact]
    public async Task ACommitIngestCouldNotApplyBecomesABranchToChooseFrom()
    {
        // The gap this pass closes. Ingest files an inapplicable commit as
        // retryable and stops; before this, nothing ever looked at it again, so
        // every fork was one member quietly stuck behind the rest.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        byte[] hers = Commit(fork.Alice.Group);
        byte[] carols = Commit(fork.Carol);

        // We take Carol's, so Alice's no longer applies.
        await ingest.IngestAsync(fork.Us, fork.GroupId, carols);

        IngestResult refused = await ingest.IngestAsync(fork.Us, fork.GroupId, hers);
        Assert.IsType<IngestOutcome.TransportDeferred>(refused.Outcome);

        Quiesce();
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);

        Assert.NotNull(result.Trace);
        Assert.Equal(2, result.Trace.Candidates.Count);
        Assert.All(result.Trace.Candidates, c => Assert.True(c.Eligible));
    }

    [Fact]
    public async Task TheLongerBranchIsAdoptedAndTheGroupMovesOntoIt()
    {
        // A rewind to the fork epoch and a replay of somebody else's commits
        // onto it. Depth decides it, so the outcome follows from the rule rather
        // than from whichever keys this run happened to generate.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        ulong forkEpoch = fork.Us.Epoch;

        byte[] hersFirst = Commit(fork.Alice.Group);
        byte[] hersSecond = Commit(fork.Alice.Group);
        byte[] carols = Commit(fork.Carol);

        await ingest.IngestAsync(fork.Us, fork.GroupId, carols);
        await ingest.IngestAsync(fork.Us, fork.GroupId, hersFirst);
        await ingest.IngestAsync(fork.Us, fork.GroupId, hersSecond);

        Assert.Equal(forkEpoch + 1, fork.Us.Epoch);

        Quiesce();
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);

        Assert.Equal(ConvergenceStatus.Settled, result.Status);
        Assert.True(result.Reorged);
        Assert.Equal(forkEpoch + 2, result.Group.Epoch);
        Assert.Equal(fork.Alice.Group.Epoch, result.Group.Epoch);
        Assert.NotSame(fork.Us, result.Group);
    }

    [Fact]
    public async Task TheBranchWeHoldCompetesAndCanWin()
    {
        // Our own branch is a candidate on the same terms as the rest. Leaving it
        // out would let a late-arriving commit win by being the only one there,
        // which is how a group gets talked off a history it has delivered.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        byte[] carolsFirst = Commit(fork.Carol);
        byte[] carolsSecond = Commit(fork.Carol);
        byte[] hers = Commit(fork.Alice.Group);

        await ingest.IngestAsync(fork.Us, fork.GroupId, carolsFirst);
        await ingest.IngestAsync(fork.Us, fork.GroupId, carolsSecond);
        await ingest.IngestAsync(fork.Us, fork.GroupId, hers);

        ulong ours = fork.Us.Epoch;

        Quiesce();
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);

        Assert.Equal(ConvergenceStatus.Settled, result.Status);
        Assert.False(result.Reorged);
        Assert.Same(fork.Us, result.Group);
        Assert.Equal(ours, result.Group.Epoch);
    }

    // ---- What adoption writes down ----

    [Fact]
    public async Task AdoptingABranchInvalidatesTheHistoryItReplaced()
    {
        // A member whose MLS state moved to another branch while its records
        // still describe the old one shows history that no longer exists and
        // cannot read the history that does.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        byte[] carols = Commit(fork.Carol);
        await ingest.IngestAsync(fork.Us, fork.GroupId, carols);

        // Delivered on the branch we are about to leave.
        byte[] doomed = AppMessage(fork, "said on the losing branch");
        await ingest.IngestAsync(fork.Us, fork.GroupId, doomed);

        byte[] hersFirst = Commit(fork.Alice.Group);
        byte[] hersSecond = Commit(fork.Alice.Group);
        await ingest.IngestAsync(fork.Us, fork.GroupId, hersFirst);
        await ingest.IngestAsync(fork.Us, fork.GroupId, hersSecond);

        Quiesce();
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);
        Assert.True(result.Reorged);

        MessageRecord carolsRecord =
            (await _fixture.Provider.GetMessageAsync(MessageId.FromMlsBytes(carols)))!;

        Assert.Equal(MessageRecordState.EpochInvalidated, carolsRecord.State);

        // And the commits that built the branch we took read as applied, not as
        // still waiting to be tried.
        foreach (byte[] wire in new[] { hersFirst, hersSecond })
        {
            MessageRecord adopted =
                (await _fixture.Provider.GetMessageAsync(MessageId.FromMlsBytes(wire)))!;

            Assert.Equal(MessageRecordState.Processed, adopted.State);
        }
    }

    [Fact]
    public async Task AdoptingABranchLeavesTheGroupRecordAndArchiveWhereTheStateIs()
    {
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        byte[] carols = Commit(fork.Carol);
        byte[] hersFirst = Commit(fork.Alice.Group);
        byte[] hersSecond = Commit(fork.Alice.Group);

        await ingest.IngestAsync(fork.Us, fork.GroupId, carols);
        await ingest.IngestAsync(fork.Us, fork.GroupId, hersFirst);
        await ingest.IngestAsync(fork.Us, fork.GroupId, hersSecond);

        Quiesce();
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);
        Assert.True(result.Reorged);

        var tip = new EpochId(result.Group.Epoch);

        Assert.Equal(tip, (await _fixture.Provider.GetGroupAsync(fork.GroupId))!.Epoch);
        Assert.Equal(tip, _epochs.GetEpoch(fork.GroupId));

        // Archived, so the next fork from here can be evaluated at all -- and
        // described, so our branch competes on terms rather than on a guess.
        EpochWindow window = await archive.LoadWindowAsync(fork.GroupId, tip);

        Assert.NotNull(window.Restore(tip));
        Assert.NotNull(window.TipAt(tip));
    }

    [Fact]
    public async Task WhatTheAdoptedBranchSaidIsReadableAfterwards()
    {
        // The point of adopting a branch, end to end. Rewinding the MLS state is
        // only half of it: the messages that branch carried were refused while we
        // were on the other one, and a member who adopts a history without being
        // able to read it has a group that agrees with everyone and shows an
        // empty conversation.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        byte[] carols = Commit(fork.Carol);
        await ingest.IngestAsync(fork.Us, fork.GroupId, carols);

        // Alice takes a different path and talks on it.
        byte[] hersFirst = Commit(fork.Alice.Group);
        byte[] hersSecond = Commit(fork.Alice.Group);
        byte[] saidOnHerBranch = AppMessage(fork, "over here");

        // Everything of hers reaches us, and none of it can be used yet: the
        // commits do not apply and the message does not decrypt.
        await ingest.IngestAsync(fork.Us, fork.GroupId, hersFirst);
        await ingest.IngestAsync(fork.Us, fork.GroupId, hersSecond);

        IngestResult held = await ingest.IngestAsync(fork.Us, fork.GroupId, saidOnHerBranch);
        Assert.IsType<IngestOutcome.TransportDeferred>(held.Outcome);
        Assert.Null(held.Message);

        Quiesce();
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);
        Assert.True(result.Reorged);

        // Replayed against the group the pass handed back, not the one we came
        // in with -- that instance is on a branch this member has abandoned.
        ReplayResult replayed = await ingest.ReplayAsync(result.Group, fork.GroupId);

        ReceivedGroupMessage delivered = Assert.Single(replayed.Delivered);
        Assert.Equal("over here", delivered.Event.Content);

        Assert.Equal(
            fork.AliceSigner.AccountPublicKey.ToArray(),
            delivered.SenderIdentity);
    }

    // ---- When it declines to decide ----

    [Fact]
    public async Task NothingIsAdoptedWhileInputIsStillArriving()
    {
        // Deciding inside the quiescence window means deciding on a candidate
        // set still being delivered -- and two members with different partial
        // sets reach different answers from identical rules.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        byte[] carols = Commit(fork.Carol);
        byte[] hersFirst = Commit(fork.Alice.Group);
        byte[] hersSecond = Commit(fork.Alice.Group);

        await ingest.IngestAsync(fork.Us, fork.GroupId, carols);
        await ingest.IngestAsync(fork.Us, fork.GroupId, hersFirst);
        await ingest.IngestAsync(fork.Us, fork.GroupId, hersSecond);

        ulong before = fork.Us.Epoch;

        // No Quiesce(): the last commit landed a moment ago.
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);

        Assert.Equal(ConvergenceStatus.Syncing, result.Status);
        Assert.False(result.Reorged);
        Assert.Equal(before, fork.Us.Epoch);
        Assert.Null(result.Trace);
    }

    [Fact]
    public async Task ABranchWeCannotDescribeOurOwnSideOfIsNotDecided()
    {
        // Reached through a Welcome and forked at once: the epoch we stand on was
        // produced by a commit we never held, so we cannot put our branch into
        // the comparison on the same terms as everyone else's. Blocked rather
        // than Syncing, because waiting produces nothing -- the moment that
        // answer could have been read has passed.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        byte[] hers = Commit(fork.Alice.Group);
        await ingest.IngestAsync(fork.Us, fork.GroupId, Commit(fork.Carol));

        // Drop the description of where we stand, keeping the state itself.
        var liveEpoch = new EpochId(fork.Us.Epoch);
        EpochCheckpoint standing =
            (await _fixture.Provider.GetEpochCheckpointAsync(fork.GroupId, liveEpoch))!;

        await _fixture.Provider.PutEpochCheckpointAsync(standing with { Tip = null });

        await ingest.IngestAsync(fork.Us, fork.GroupId, hers);

        Quiesce();
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);

        Assert.Equal(ConvergenceStatus.Blocked, result.Status);
        Assert.False(result.Reorged);
    }

    [Fact]
    public async Task ABranchForkingBeyondWhatWeRetainBlocksRatherThanWaits()
    {
        // The state it forks from is gone and no amount of waiting brings it
        // back. A client that showed a spinner for this would be lying to the
        // user indefinitely.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        byte[] hers = Commit(fork.Alice.Group);
        await ingest.IngestAsync(fork.Us, fork.GroupId, Commit(fork.Carol));
        await ingest.IngestAsync(fork.Us, fork.GroupId, hers);

        await _fixture.Provider.PruneEpochCheckpointsBeforeAsync(
            fork.GroupId, new EpochId(fork.Us.Epoch));

        Quiesce();
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);

        Assert.Equal(ConvergenceStatus.Blocked, result.Status);
        Assert.Contains(
            result.Refused, r => r.Reason == MaterializationRefusal.NoSnapshot);
    }

    // ---- Commits that can never be rebuilt ----

    /// <summary>
    /// Advances us past the rewind horizon on commits from Carol.
    /// </summary>
    /// <remarks>
    /// One more than the horizon, so the epoch we started at is pruned and a
    /// commit forking there can never be restored again.
    /// </remarks>
    private async Task OutrunTheHorizonAsync(Fork fork, MessageIngest ingest)
    {
        for (ulong i = 0; i <= ConvergencePolicy.V1MaxRewindCommits; i++)
            await ingest.IngestAsync(fork.Us, fork.GroupId, Commit(fork.Carol));
    }

    /// <summary>Our own commit, applied and archived, so our branch is describable.</summary>
    private async Task SelfUpdateAsync(Fork fork, EpochArchive archive)
    {
        using StagedCommit ours = MarmotSelfUpdate.Stage(fork.Us);

        byte[] wire = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, ours.Commit).WriteTo);

        ours.Publishing();
        ours.Applied();

        await archive.CaptureAsync(
            fork.GroupId,
            fork.Us,
            new CommitTip(
                ours.OrderingPriority,
                MessageId.FromMlsBytes(wire),
                Convert.FromHexString(fork.AliceSigner.Hex)));
    }

    [Fact]
    public async Task ACommitThatCanNeverBeRebuiltIsRetiredRatherThanRetriedForever()
    {
        // The horizon only moves forward, so a commit forking below it is not
        // merely unevaluable now -- it is unevaluable for good. Left retryable it
        // comes back to every later pass with the same answer, and each of those
        // passes has to report a branch it cannot assess.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        byte[] stale = Commit(fork.Alice.Group);
        await OutrunTheHorizonAsync(fork, ingest);
        await ingest.IngestAsync(fork.Us, fork.GroupId, stale);

        Quiesce();
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);

        Assert.Equal(ConvergenceStatus.Settled, result.Status);
        Assert.Contains(MessageId.FromMlsBytes(stale), result.Retired);

        MessageRecord retired =
            (await _fixture.Provider.GetMessageAsync(MessageId.FromMlsBytes(stale)))!;

        Assert.Equal(MessageRecordState.Failed, retired.State);
    }

    [Fact]
    public async Task ACommitForkingExactlyAtTheHorizonIsStillACandidate()
    {
        // The boundary, in the direction that fails quietly. Retiring one epoch
        // too eagerly throws away a branch the policy says is adoptable -- and
        // the group would never hear about it, because a retired commit is never
        // mentioned again. The horizon here has to be the one branch selection
        // applies, to the epoch.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        byte[] hers = Commit(fork.Alice.Group);

        for (ulong i = 0; i < ConvergencePolicy.V1MaxRewindCommits; i++)
            await ingest.IngestAsync(fork.Us, fork.GroupId, Commit(fork.Carol));

        await ingest.IngestAsync(fork.Us, fork.GroupId, hers);

        Quiesce();
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);

        Assert.Empty(result.Retired);
        Assert.Equal(ConvergenceStatus.Settled, result.Status);
        Assert.NotNull(result.Trace);
        Assert.Equal(2, result.Trace.Candidates.Count);
        Assert.All(result.Trace.Candidates, c => Assert.True(c.Eligible));
    }

    [Fact]
    public async Task AStaleCommitDoesNotStopTheGroupDecidingARealFork()
    {
        // The consequence, and the reason this is not cosmetic. One commit that
        // can never be rebuilt used to make every later pass report Blocked, and
        // a pass that reports Blocked decides nothing -- so a single commit
        // framed at an ancient epoch stopped the group converging at all, for
        // good, no matter what else arrived.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        byte[] stale = Commit(fork.Alice.Group);
        await OutrunTheHorizonAsync(fork, ingest);

        // A fork we can perfectly well decide: Carol commits from where we both
        // stand, and we commit from there too.
        byte[] carols = Commit(fork.Carol);
        await SelfUpdateAsync(fork, archive);

        await ingest.IngestAsync(fork.Us, fork.GroupId, carols);
        await ingest.IngestAsync(fork.Us, fork.GroupId, stale);

        Quiesce();
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);

        Assert.Equal(ConvergenceStatus.Settled, result.Status);
        Assert.NotNull(result.Trace);
        Assert.Equal(2, result.Trace.Candidates.Count);
        Assert.Contains(MessageId.FromMlsBytes(stale), result.Retired);
    }

    // ---- Our own commit is not a competitor ----

    [Fact]
    public async Task OurOwnCommitComingBackIsNotMistakenForARival()
    {
        // It arrives off the relay like anybody else's and ingest cannot apply it
        // either, so it lands in the same pile. MLS will not replay a commit it
        // authored, so one taken for a competitor materialises as a branch that
        // cannot apply -- and the race quietly has one runner.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        using StagedCommit ours = MarmotSelfUpdate.Stage(fork.Us);
        byte[] ourWire = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, ours.Commit).WriteTo);

        ours.Publishing();
        ours.Applied();

        await archive.CaptureAsync(
            fork.GroupId,
            fork.Us,
            new CommitTip(
                ours.OrderingPriority,
                MessageId.FromMlsBytes(ourWire),
                Convert.FromHexString(fork.AliceSigner.Hex)));

        // Our own echo, plus a genuine competitor.
        await ingest.IngestAsync(fork.Us, fork.GroupId, ourWire);
        await ingest.IngestAsync(fork.Us, fork.GroupId, Commit(fork.Alice.Group));

        Quiesce();
        ConvergencePassResult result = await NewPass(archive).RunAsync(fork.Us, fork.GroupId);

        Assert.Contains(
            result.Refused, r => r.Reason == MaterializationRefusal.UnreplayableOwnCommit);

        Assert.DoesNotContain(
            result.Refused, r => r.Reason == MaterializationRefusal.DoesNotApply);
    }

    // ---- Witnesses ----

    [Fact]
    public async Task ProvingABranchDoesNotSpendTheLiveGroupsKeys()
    {
        // A witness is checked by trying to read the message, and reading one
        // consumes ratchet keys. One of the groups a witness check is handed is
        // the live group, so doing it in place would spend the keys real traffic
        // needs -- and the damage would surface much later, as history that
        // cannot be read.
        EpochArchive archive = NewArchive();
        Fork fork = await ForkAsync(archive);
        MessageIngest ingest = NewIngest(archive);

        await ingest.IngestAsync(fork.Us, fork.GroupId, Commit(fork.Carol));

        // Held but never delivered, so the key it needs is still unspent.
        byte[] undelivered = AppMessage(fork, "not yet read");
        MessageId id = MessageId.FromMlsBytes(undelivered);

        await _fixture.Provider.PutMessageAsync(
            new MessageRecord(
                id,
                fork.GroupId,
                null,
                new EpochId(fork.Us.Epoch),
                MessageRecordState.PeelDeferred,
                undelivered,
                _now,
                _now));

        await ingest.IngestAsync(fork.Us, fork.GroupId, Commit(fork.Alice.Group));

        Quiesce();
        await NewPass(archive).RunAsync(fork.Us, fork.GroupId);

        // The live group can still read it. If the pass had proved branches in
        // place, this would throw.
        ReceivedGroupMessage received = GroupMessages.Receive(fork.Us, undelivered);

        Assert.Equal("not yet read", received.Event.Content);
    }
}
