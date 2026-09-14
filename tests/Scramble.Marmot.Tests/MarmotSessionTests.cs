using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.Ingest;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Engine.Session;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Ingest;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The session layer: open a group, keep it correct, close it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is being tested is the composition, not the parts.</b> Each piece
/// underneath — the archive, the durable epoch manager, the publisher, ingest,
/// the convergence pass — has its own suite. What none of them could test is
/// what happens when one live group's lifetime runs through all of them, and
/// that is where the gap was: a commit of ours was never written down anywhere,
/// so a crash after publishing it lost the epoch the rest of the group had
/// already moved to.
/// </para>
/// <para>
/// <b>Every restart here goes back to storage.</b> A test that carried an
/// <see cref="MlsGroup"/> across a simulated crash would be testing nothing:
/// the whole question is what survives in bytes.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class MarmotSessionTests : IDisposable
{
    private readonly StorageFixture _fixture = new();
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];

    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddSeconds(Now);

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

    /// <summary>A relay that always gives the same answer, and counts.</summary>
    private sealed class FixedRelay(CommitPublishOutcome outcome) : ICommitRelay
    {
        public int Sends { get; private set; }

        /// <summary>Set by a test that wants to watch the group mid-publish.</summary>
        public MarmotSession? Watching { get; set; }

        /// <summary>The epoch the session was on while the bytes were in flight.</summary>
        public ulong? EpochDuringSend { get; private set; }

        public Task<CommitPublishOutcome> PublishAsync(
            string envelope, CancellationToken ct = default)
        {
            Sends++;
            EpochDuringSend = Watching?.Group.Epoch;
            return Task.FromResult(outcome);
        }
    }

    /// <summary>A transport that is not there, which is what a restart has.</summary>
    private sealed class UnreachableRelay : ICommitRelay
    {
        public Task<CommitPublishOutcome> PublishAsync(
            string envelope, CancellationToken ct = default) =>
            throw new NotSupportedException("Hydration must not need a relay.");
    }

    private MarmotSessionHost Host(ICommitRelay relay) =>
        new(_fixture.Provider, _cs, relay, ConvergencePolicy.V1, () => _now);

    private sealed record Pair(
        MarmotSession Us, MlsGroup Them, LocalSigner TheirSigner, GroupId GroupId);

    /// <summary>Us as a session, and one other member who is just an MLS group.</summary>
    private async Task<Pair> PairAsync(ICommitRelay relay)
    {
        CreatedGroup us = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        var theirSigner = new LocalSigner();
        var bundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, theirSigner, Now);

        StagedCommit staged = MarmotGroupInvite.Add(us.Group, _cs, [bundle.KeyPackage]);
        staged.Applied();

        MlsGroup them = MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey,
            config: MarmotGroupSettings.Create());

        MarmotSession session = await Host(relay).AdoptAsync(us.ToRecord(_now), us.Group);

        return new Pair(session, them, theirSigner, new GroupId(us.GroupId));
    }

    // ---- Why an open failed ----

    [Fact]
    public async Task AGroupWeDoNotHaveIsNotTheSameAsOneWeCannotRebuild()
    {
        // These were one answer -- null -- until 2026-09-14. The first is
        // routine; the second is a group whose history we are holding and
        // cannot reach, which somebody has to see.
        MarmotSessionHost host = Host(new UnreachableRelay());

        SessionOpenResult missing = await host.OpenAsync(StorageFixture.NewGroupId());

        Assert.False(missing.Opened);
        Assert.Equal(SessionOpenRefusal.NotStored, missing.Refusal);
    }

    [Fact]
    public async Task AGroupWhoseStateWillNotLoadIsRefusedRatherThanThrown()
    {
        // The defect this replaced quarantine for. MlsGroup.Import reads
        // straight into a TlsReader, so corrupt state threw out of OpenAsync --
        // and in the per-group loop the app layer will write, that one throw
        // takes down every other group with it.
        Pair pair = await PairAsync(new UnreachableRelay());
        await pair.Us.CloseAsync();

        GroupRecord stored = (await _fixture.Provider.GetGroupAsync(pair.GroupId))!;
        Assert.NotNull(stored.LiveState);

        await _fixture.Provider.PutGroupAsync(
            stored with { LiveState = [0xff, 0xff, 0xff, 0xff] });

        MarmotSessionHost host = Host(new UnreachableRelay());
        await host.RestoreAsync();

        SessionOpenResult broken = await host.OpenAsync(pair.GroupId);

        Assert.False(broken.Opened);
        Assert.Equal(SessionOpenRefusal.StateUnreadable, broken.Refusal);
    }

    [Fact]
    public async Task AGroupWithNothingRetainedSaysSoRatherThanReadingAsAbsent()
    {
        // A record written before live state was durable, whose archived epochs
        // have since been pruned past. Distinct from NotStored: we are still in
        // this group, and cannot get to it.
        var groupId = StorageFixture.NewGroupId();
        await _fixture.Provider.PutGroupAsync(StorageFixture.Group(groupId));

        MarmotSessionHost host = Host(new UnreachableRelay());
        await host.RestoreAsync();

        SessionOpenResult nothing = await host.OpenAsync(groupId);

        Assert.False(nothing.Opened);
        Assert.Equal(SessionOpenRefusal.NoRetainedState, nothing.Refusal);
    }

    [Fact]
    public async Task OneUnopenableGroupDoesNotTakeDownTheOpenOfAnother()
    {
        // The loop P11 will write, and the reason the distinction is worth a
        // type. Upstream needed a quarantine container for this because its
        // hydration is eager and account-wide; ours only needs the open to
        // return rather than throw.
        Pair good = await PairAsync(new UnreachableRelay());
        await good.Us.CloseAsync();

        var broken = StorageFixture.NewGroupId();
        await _fixture.Provider.PutGroupAsync(
            StorageFixture.Group(broken) with { LiveState = [0x01, 0x02] });

        MarmotSessionHost host = Host(new UnreachableRelay());
        await host.RestoreAsync();

        var opened = new List<GroupId>();
        var refused = new List<SessionOpenRefusal>();

        foreach (GroupId id in new[] { broken, good.GroupId })
        {
            SessionOpenResult result = await host.OpenAsync(id);

            if (result.Session is { } session)
                opened.Add(session.GroupId);
            else
                refused.Add(result.Refusal!.Value);
        }

        Assert.Equal([good.GroupId], opened);
        Assert.Equal([SessionOpenRefusal.StateUnreadable], refused);
    }

    /// <summary>The group as a fresh process would find it.</summary>
    private async Task<MarmotSession?> RestartAsync(GroupId groupId)
    {
        MarmotSessionHost host = Host(new UnreachableRelay());
        await host.RestoreAsync();
        return (await host.OpenAsync(groupId)).Session;
    }

    private static byte[] Serialize(PublicMessage message) =>
        TlsCodec.Serialize(new MlsMessage(WireFormat.MlsPublicMessage, message).WriteTo);

    /// <summary>An application message from the member who is not a session.</summary>
    private static byte[] AppMessage(Pair pair, string text)
    {
        var peeler = new NostrGroupPeeler();

        string envelope = GroupMessages.Send(
            pair.Them,
            peeler,
            MarmotAppEvent.Chat(pair.TheirSigner.Hex, (long)Now, text),
            pair.TheirSigner.AccountPublicKey.Span);

        return peeler.Peel(envelope, _ => GroupMessages.ExporterSecret(pair.Them)).MlsBytes;
    }

    /// <summary>A commit from the member who stayed an ordinary group.</summary>
    private static byte[] TheirCommit(Pair pair)
    {
        using StagedCommit staged = MarmotSelfUpdate.Stage(pair.Them);
        byte[] wire = Serialize(staged.Commit);
        staged.Publishing();
        staged.Applied();
        return wire;
    }

    private static Task<CommitPublishOutcome> RotateAsync(MarmotSession session) =>
        session.CommitAsync(MarmotSelfUpdate.Stage, _ => "envelope");

    // ---- Opening ----

    [Fact]
    public async Task OpeningAGroupWeDoNotHaveIsNothingRatherThanAnError()
    {
        Assert.Null(await RestartAsync(StorageFixture.NewGroupId()));
    }

    [Fact]
    public async Task AGroupThatHasOnlyEverBeenCreatedComesBackAtEpochZero()
    {
        CreatedGroup us = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        var groupId = new GroupId(us.GroupId);
        await Host(new UnreachableRelay()).AdoptAsync(us.ToRecord(_now), us.Group);

        MarmotSession? revived = await RestartAsync(groupId);

        Assert.NotNull(revived);
        Assert.Equal(0UL, revived!.Group.Epoch);
        Assert.Null(revived.Recovery);
    }

    // ---- Publishing ----

    [Fact]
    public async Task TheLiveGroupDoesNotAdvanceWhileTheCommitIsInFlight()
    {
        // The rule the whole publish path exists for. Deriving the state a
        // commit produces means merging it, and if the session let that merge
        // land on the group it hands out, a relay saying no would leave this
        // member an epoch ahead of everyone with no way back.
        var relay = new FixedRelay(CommitPublishOutcome.Accepted);
        Pair pair = await PairAsync(relay);
        relay.Watching = pair.Us;

        ulong before = pair.Us.Group.Epoch;

        Assert.Equal(CommitPublishOutcome.Accepted, await RotateAsync(pair.Us));

        Assert.Equal(before, relay.EpochDuringSend);
        Assert.Equal(before + 1, pair.Us.Group.Epoch);
    }

    [Fact]
    public async Task AnAcceptedCommitLeavesNothingOutstandingAndIsDurable()
    {
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));
        ulong before = pair.Us.Group.Epoch;

        await RotateAsync(pair.Us);

        Assert.Null(await _fixture.Provider.GetStagedCommitAsync(pair.GroupId));
        Assert.Null(await _fixture.Provider.GetCommitPublishAttemptAsync(pair.GroupId));

        MarmotSession? revived = await RestartAsync(pair.GroupId);

        Assert.Equal(before + 1, revived!.Group.Epoch);

        // Nothing was recovered, because nothing was outstanding.
        Assert.Null(revived.Recovery);
    }

    [Fact]
    public async Task AnAcceptedCommitArchivesTheEpochItProduced()
    {
        // The gap that made NothingPersistsTheStateAGroupIsActuallyIn fail:
        // the archive was fed only from the inbound commit path, so a group
        // whose own member does all the committing archived nothing and could
        // never evaluate a branch forking from where it stands.
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));
        ulong before = pair.Us.Group.Epoch;

        await RotateAsync(pair.Us);

        EpochCheckpoint? checkpoint = await _fixture.Provider.GetEpochCheckpointAsync(
            pair.GroupId, new EpochId(before + 1));

        Assert.NotNull(checkpoint);

        // With a tip, not without one. A checkpoint that cannot say what commit
        // produced it leaves this member unable to state its own branch's terms,
        // and the convergence pass reports Blocked rather than choosing.
        Assert.NotNull(checkpoint!.Tip);
    }

    [Fact]
    public async Task ARejectedCommitLeavesTheGroupWhereItWasAndFreeToCommitAgain()
    {
        var relay = new FixedRelay(CommitPublishOutcome.Rejected);
        Pair pair = await PairAsync(relay);
        ulong before = pair.Us.Group.Epoch;

        Assert.Equal(CommitPublishOutcome.Rejected, await RotateAsync(pair.Us));

        Assert.Equal(before, pair.Us.Group.Epoch);
        Assert.Null(await _fixture.Provider.GetStagedCommitAsync(pair.GroupId));

        // Not stranded: a refused commit must not block the retry, or a relay
        // saying no once takes the group out of service permanently.
        Assert.Equal(CommitPublishOutcome.Rejected, await RotateAsync(pair.Us));
        Assert.Equal(2, relay.Sends);
        Assert.Equal(before, pair.Us.Group.Epoch);
    }

    [Fact]
    public async Task AnUnansweredPublishKeepsTheCommitRatherThanTidyingItAway()
    {
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Indeterminate));
        ulong before = pair.Us.Group.Epoch;

        Assert.Equal(CommitPublishOutcome.Indeterminate, await RotateAsync(pair.Us));

        // The group has not moved -- we cannot vouch for the commit -- but
        // everything needed to settle it later is still on disk.
        Assert.Equal(before, pair.Us.Group.Epoch);

        StagedCommitRecord? staged = await _fixture.Provider.GetStagedCommitAsync(pair.GroupId);
        Assert.NotNull(staged);
        Assert.Equal(before + 1, staged!.NewEpoch.Value);

        Assert.Equal(
            StrandedCommitVerdict.Reconcile,
            await Host(new UnreachableRelay()).Publisher.ClassifyAsync(pair.GroupId));
    }

    // ---- Recovering ----

    [Fact]
    public async Task ACommitTheRelayNeverAnsweredIsAdoptedAndStillOwesAnAnswer()
    {
        // Reconcile has to choose without knowing, and it adopts: the
        // pre-commit state is still in the record and the archive, so a
        // reconciliation that comes back "it never landed" can rewind -- while
        // abandoning a commit a relay is serving cannot be undone at all,
        // because MLS refuses to let a member process a commit it authored.
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Indeterminate));
        ulong before = pair.Us.Group.Epoch;

        await RotateAsync(pair.Us);

        MarmotSession? revived = await RestartAsync(pair.GroupId);

        Assert.Equal(before + 1, revived!.Group.Epoch);
        Assert.Equal(StrandedCommitVerdict.Reconcile, revived.Recovery!.Verdict);
        Assert.True(revived.Recovery.ReconciliationOwed);

        // The rows survive, because they are what make the commit findable by
        // whatever eventually asks the relay.
        Assert.NotNull(await _fixture.Provider.GetStagedCommitAsync(pair.GroupId));
    }

    [Fact]
    public async Task ACommitTheRelayTookIsAdoptedAndSettled()
    {
        // The crash between "the relay has it" and "we have applied it". The
        // rest of the group has already moved.
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));
        ulong before = pair.Us.Group.Epoch;

        await RotateAsync(pair.Us);

        // Put the row back exactly as a crash before the clear would have left
        // it: the relay answered yes, and nothing after that ran.
        StagedCommitRecord staged = await StagedFromLastCommitAsync(pair, before);

        MarmotSession? revived = await RestartAsync(pair.GroupId);

        Assert.Equal(before + 1, revived!.Group.Epoch);
        Assert.Equal(StrandedCommitVerdict.Adopt, revived.Recovery!.Verdict);
        Assert.False(revived.Recovery.ReconciliationOwed);

        // Settled, not merely adopted: an accepted commit needs no further
        // question asked of anybody.
        Assert.Null(await _fixture.Provider.GetStagedCommitAsync(pair.GroupId));
        Assert.Null(await _fixture.Provider.GetCommitPublishAttemptAsync(pair.GroupId));
        Assert.Equal(staged.NewEpoch.Value, revived.Group.Epoch);
    }

    [Fact]
    public async Task ACommitTheRelayRefusedIsAbandonedRatherThanAdopted()
    {
        // The other side of the same coin. A prepared state exists, so the
        // tempting move is to use it -- but the relay said no, so nobody else
        // has the commit and adopting it advances this member alone.
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));
        ulong before = pair.Us.Group.Epoch;

        await RotateAsync(pair.Us);
        StagedCommitRecord staged = await StagedFromLastCommitAsync(pair, before);

        await _fixture.Provider.PutCommitPublishAttemptAsync(
            CommitPublishAttempt
                .HandedToTransport(pair.GroupId, staged.Tip.Commit, staged.NewEpoch, _now)
                .Resolved(CommitPublishState.Rejected, _now));

        // The record still describes where the group was before the commit,
        // which is where a refused commit leaves it.
        await RewindLiveStateAsync(pair, before);

        MarmotSession? revived = await RestartAsync(pair.GroupId);

        Assert.Equal(before, revived!.Group.Epoch);
        Assert.Equal(StrandedCommitVerdict.Abandon, revived.Recovery!.Verdict);
        Assert.Null(await _fixture.Provider.GetStagedCommitAsync(pair.GroupId));
    }

    [Fact]
    public async Task APreparedCommitWithNoPublishBehindItIsAbandoned()
    {
        // No row at all is a positive statement, not missing information: a
        // commit that never reached a transport left nothing behind, so nobody
        // else can have seen it.
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));
        ulong before = pair.Us.Group.Epoch;

        await RotateAsync(pair.Us);
        await StagedFromLastCommitAsync(pair, before);
        await _fixture.Provider.ClearCommitPublishAttemptAsync(pair.GroupId);
        await RewindLiveStateAsync(pair, before);

        MarmotSession? revived = await RestartAsync(pair.GroupId);

        Assert.Equal(before, revived!.Group.Epoch);
        Assert.Equal(StrandedCommitVerdict.Abandon, revived.Recovery!.Verdict);
    }

    // ---- Ingest ----

    [Fact]
    public async Task SomebodyElsesCommitMovesTheDurableStateTooNotOnlyTheObject()
    {
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));
        ulong before = pair.Us.Group.Epoch;

        IngestResult result = await pair.Us.IngestAsync(TheirCommit(pair));

        Assert.IsType<IngestOutcome.Processed>(result.Outcome);
        Assert.Equal(before + 1, pair.Us.Group.Epoch);

        MarmotSession? revived = await RestartAsync(pair.GroupId);

        Assert.Equal(before + 1, revived!.Group.Epoch);

        // And the recovered group is the real thing, not merely a group with
        // the right number on it.
        ReceivedGroupMessage received = GroupMessages.Receive(
            revived.Group, AppMessage(pair, "still here"));

        Assert.Equal("still here", received.Event.Content);
    }

    [Fact]
    public async Task AMessageHeldWhileWeWereMidPublishIsDeliveredOnceThePublishFinishes()
    {
        // The promise IngestOutcome.Buffered makes. Nothing on the ingest path
        // can keep it -- what has to change is outside it -- so the session
        // owes a replay once the publish that blocked it is settled.
        var relay = new FixedRelay(CommitPublishOutcome.Indeterminate);
        Pair pair = await PairAsync(relay);

        byte[] message = AppMessage(pair, "spoken over");

        await RotateAsync(pair.Us);

        IngestResult held = await pair.Us.IngestAsync(message);
        Assert.IsType<IngestOutcome.Buffered>(held.Outcome);

        ReplayResult blocked = await pair.Us.ReplayAsync();
        Assert.Empty(blocked.Delivered);

        // The publish settles, the group becomes ingestible, and the message
        // that was held becomes readable.
        StagedCommitRecord staged =
            (await _fixture.Provider.GetStagedCommitAsync(pair.GroupId))!;

        await _fixture.Provider.ClearStagedCommitAsync(pair.GroupId);
        await _fixture.Provider.ClearCommitPublishAttemptAsync(pair.GroupId);
        await _fixture.Provider.ClearEpochStateAsync(pair.GroupId);

        MarmotSession revived = (await RestartAsync(pair.GroupId))!;
        Assert.Equal(staged.NewEpoch.Value - 1, revived.Group.Epoch);

        ReplayResult replayed = await revived.ReplayAsync();

        Assert.Single(replayed.Delivered);
        Assert.Equal("spoken over", replayed.Delivered[0].Event.Content);
    }

    [Fact]
    public async Task AConvergencePassOverAQuietGroupSettlesAndReplaysNothing()
    {
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));

        var (result, replay) = await pair.Us.ConvergeAsync();

        Assert.Equal(ConvergenceStatus.Settled, result.Status);
        Assert.False(result.Reorged);
        Assert.Empty(replay.Delivered);
    }

    [Fact]
    public async Task AReorgMovesTheSessionAndDeliversWhatTheBranchItAdoptedSaid()
    {
        // The reorg half of ConvergeAsync had no test at all: every existing
        // case returns early on Reorged == false, so keeping the pre-reorg
        // group, never persisting the move, and skipping the replay it owes all
        // passed the suite untouched.
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));

        // They take a longer path and talk on it. Depth decides the branch, so
        // the outcome follows from the rule rather than from whichever keys
        // this run generated.
        byte[] theirsFirst = TheirCommit(pair);
        byte[] theirsSecond = TheirCommit(pair);
        byte[] saidOnTheirBranch = AppMessage(pair, "over here");

        // We commit once from the epoch we shared -- depth 1 against their 2.
        await RotateAsync(pair.Us);
        ulong ours = pair.Us.Group.Epoch;

        await pair.Us.IngestAsync(theirsFirst);
        await pair.Us.IngestAsync(theirsSecond);

        IngestResult held = await pair.Us.IngestAsync(saidOnTheirBranch);
        Assert.Null(held.Message);

        _now = _now.AddMilliseconds(ConvergencePolicy.V1SettlementQuiescenceMs + 1);

        var (result, replay) = await pair.Us.ConvergeAsync();

        Assert.True(result.Reorged);

        // The session is on the branch it chose, not the one it came in on.
        Assert.Equal(pair.Them.Epoch, pair.Us.Group.Epoch);
        Assert.NotEqual(ours, pair.Us.Group.Epoch);

        // And what that branch carried is delivered, which is the whole reason
        // a reorg owes a replay: those messages were refused while we were on
        // the other branch, and ingest deduplicates on content.
        ReceivedGroupMessage delivered = Assert.Single(replay.Delivered);
        Assert.Equal("over here", delivered.Event.Content);
    }

    [Fact]
    public async Task AReorgSurvivesTheRestartThatFollowsIt()
    {
        // Moving the live group without writing it down leaves a session that
        // is correct until the process ends and wrong immediately afterwards --
        // back on a branch the group abandoned, with the messages it delivered
        // unreadable.
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));

        byte[] theirsFirst = TheirCommit(pair);
        byte[] theirsSecond = TheirCommit(pair);

        await RotateAsync(pair.Us);
        await pair.Us.IngestAsync(theirsFirst);
        await pair.Us.IngestAsync(theirsSecond);

        _now = _now.AddMilliseconds(ConvergencePolicy.V1SettlementQuiescenceMs + 1);

        var (result, _) = await pair.Us.ConvergeAsync();
        Assert.True(result.Reorged);

        ulong adopted = pair.Us.Group.Epoch;

        MarmotSession revived = (await RestartAsync(pair.GroupId))!;

        Assert.Equal(adopted, revived.Group.Epoch);
    }

    [Fact]
    public async Task AGroupStoredBeforeItCarriedItsOwnStateIsRebuiltFromTheArchive()
    {
        // Rows written before live state was durable at all. The archive is a
        // weaker source -- it says which epochs a group passed through, not
        // where it is -- but it is what those rows have, and refusing to open
        // them would strand every group already on disk.
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));

        GroupRecord record = (await _fixture.Provider.GetGroupAsync(pair.GroupId))!;
        ulong epoch = pair.Us.Group.Epoch;

        await _fixture.Provider.PutGroupAsync(record with { LiveState = null });

        MarmotSession? revived = await RestartAsync(pair.GroupId);

        Assert.NotNull(revived);
        Assert.Equal(epoch, revived!.Group.Epoch);
    }

    // ---- Custody ----

    [Fact]
    public async Task ACommitNobodyPublishedLeavesNothingBehindWhenItIsDisposed()
    {
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));

        using (StagedCommit staged = MarmotSelfUpdate.Stage(pair.Us.Group))
        {
            // Prepared, because the group is one a session owns.
            Assert.NotNull(staged.PreparedState);
            Assert.NotNull(await _fixture.Provider.GetStagedCommitAsync(pair.GroupId));
        }

        Assert.Null(await _fixture.Provider.GetStagedCommitAsync(pair.GroupId));
    }

    [Fact]
    public async Task AStaleReferenceToAReplacedGroupCannotWriteThroughToIt()
    {
        // Two objects bound to one group would each prepare commits into the
        // same single row, and the second would overwrite the first's
        // description of a commit that may already be on a relay.
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Rejected));

        MlsGroup stale = pair.Us.Group;
        await RotateAsync(pair.Us);

        Assert.NotSame(stale, pair.Us.Group);

        using StagedCommit orphan = MarmotSelfUpdate.Stage(stale);

        Assert.Null(orphan.PreparedState);
        Assert.Null(await _fixture.Provider.GetStagedCommitAsync(pair.GroupId));
    }

    [Fact]
    public async Task AReopenedGroupCanStillCommitDurably()
    {
        // Hydration has to hand back a group under custody, not merely a group.
        // One that came back unbound would publish commits nothing wrote down
        // -- which is the state the whole engine was in before this layer.
        await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));

        GroupRecord record = (await _fixture.Provider.ListGroupsAsync())[0];

        MarmotSessionHost host = Host(new FixedRelay(CommitPublishOutcome.Indeterminate));
        await host.RestoreAsync();

        MarmotSession session = (await host.OpenAsync(record.Id)).Require();
        ulong before = session.Group.Epoch;

        Assert.Equal(CommitPublishOutcome.Indeterminate, await RotateAsync(session));

        StagedCommitRecord? staged = await _fixture.Provider.GetStagedCommitAsync(record.Id);

        Assert.NotNull(staged);
        Assert.Equal(before + 1, staged!.NewEpoch.Value);
    }

    // ---- Closing ----

    [Fact]
    public async Task ClosingReleasesTheGroupAndLeavesItsStateOnDisk()
    {
        Pair pair = await PairAsync(new FixedRelay(CommitPublishOutcome.Accepted));

        await pair.Us.IngestAsync(TheirCommit(pair));

        MlsGroup closed = pair.Us.Group;
        ulong reached = closed.Epoch;

        await pair.Us.CloseAsync();

        GroupRecord record = (await _fixture.Provider.GetGroupAsync(pair.GroupId))!;

        // Already true before the close -- every path that moves the group
        // writes -- which is why CloseAsync's own write is a backstop rather
        // than the thing under test here.
        Assert.Equal(reached, record.Epoch.Value);
        Assert.NotNull(record.LiveState);

        // This is the part only closing does: the group is no longer under
        // custody, so a commit staged on it writes nothing.
        using StagedCommit orphan = MarmotSelfUpdate.Stage(closed);

        Assert.Null(orphan.PreparedState);
        Assert.Null(await _fixture.Provider.GetStagedCommitAsync(pair.GroupId));
    }

    /// <summary>
    /// Re-files the last commit as one the process died in the middle of.
    /// </summary>
    /// <remarks>
    /// The session clears these rows the moment a publish settles, which is
    /// exactly the behaviour under test everywhere else — so a crash scenario
    /// has to put back what a crash would have left. Built from the archived
    /// checkpoint rather than from a fresh export, so the state written here is
    /// the same state the commit actually produced.
    /// </remarks>
    private async Task<StagedCommitRecord> StagedFromLastCommitAsync(Pair pair, ulong before)
    {
        EpochCheckpoint checkpoint = (await _fixture.Provider.GetEpochCheckpointAsync(
            pair.GroupId, new EpochId(before + 1)))!;

        var staged = new StagedCommitRecord(
            pair.GroupId,
            checkpoint.Epoch,
            checkpoint.GroupState,
            checkpoint.Tip!,
            _now);

        await _fixture.Provider.PutStagedCommitAsync(staged);

        await _fixture.Provider.PutCommitPublishAttemptAsync(
            CommitPublishAttempt
                .HandedToTransport(pair.GroupId, staged.Tip.Commit, staged.NewEpoch, _now)
                .Resolved(CommitPublishState.Accepted, _now));

        return staged;
    }

    /// <summary>Puts the group's durable state back at the epoch it committed from.</summary>
    private async Task RewindLiveStateAsync(Pair pair, ulong epoch)
    {
        EpochCheckpoint checkpoint = (await _fixture.Provider.GetEpochCheckpointAsync(
            pair.GroupId, new EpochId(epoch)))!;

        GroupRecord record = (await _fixture.Provider.GetGroupAsync(pair.GroupId))!;

        await _fixture.Provider.PutGroupAsync(
            record with { Epoch = checkpoint.Epoch, LiveState = checkpoint.GroupState });
    }
}
