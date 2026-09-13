using Scramble.Marmot.Engine;
using Scramble.Marmot.Storage;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The epoch state machine written down, and read back after a restart.
/// </summary>
/// <remarks>
/// Two things are under test and they are different. One is that what a restart
/// reads back is enough to reconcile a commit whose fate is unknown. The other
/// is the order the writes go in: entering a stored state before the group
/// moves, leaving it after, so that every crash window errs towards believing a
/// group is still pending rather than towards forgetting a commit that may
/// already be on a relay.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class DurableEpochManagerTests
{
    private static readonly StagedCommitHandle Staged = new(new byte[] { 0x11, 0x22, 0x33 });

    private static GroupId NewGroup() => new(Guid.NewGuid().ToByteArray());

    private static DateTimeOffset Clock() => DateTimeOffset.UnixEpoch;

    /// <summary>
    /// An in-memory store that records what the state machine looked like at
    /// the moment of each write.
    /// </summary>
    /// <remarks>
    /// The ordering is the thing being tested, and it is invisible to a store
    /// that only remembers rows: both orderings end with the same row and the
    /// same state. Observing the in-memory state from inside the write is what
    /// tells them apart.
    /// </remarks>
    private sealed class TracingStore : IEpochStateStorage
    {
        private readonly Dictionary<GroupId, EpochStateRecord> _rows = new();

        /// <summary>What the manager said at the moment of the write.</summary>
        public Func<string>? Observe { get; set; }

        public List<string> Trace { get; } = new();

        public bool FailWrites { get; set; }

        public bool FailClears { get; set; }

        public int Count => _rows.Count;

        public Task PutEpochStateAsync(EpochStateRecord record, CancellationToken ct = default)
        {
            Trace.Add($"put {record.Kind} while {Observe?.Invoke() ?? "?"}");
            if (FailWrites)
                throw new IOException("the database is gone");

            _rows[record.GroupId] = record;
            return Task.CompletedTask;
        }

        public Task<EpochStateRecord?> GetEpochStateAsync(
            GroupId groupId, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(groupId, out EpochStateRecord? row) ? row : null);

        public Task<IReadOnlyList<EpochStateRecord>> ListEpochStatesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EpochStateRecord>>(_rows.Values.ToList());

        public Task<bool> ClearEpochStateAsync(GroupId groupId, CancellationToken ct = default)
        {
            Trace.Add($"clear while {Observe?.Invoke() ?? "?"}");
            if (FailClears)
                throw new IOException("the database is gone");

            return Task.FromResult(_rows.Remove(groupId));
        }
    }

    private static (DurableEpochManager Durable, EpochManager Epochs, TracingStore Store) NewManager(
        GroupId? observed = null)
    {
        var epochs = new EpochManager();
        var store = new TracingStore();
        if (observed is { } group)
            store.Observe = () => epochs.GetState(group)?.Name ?? "no state";

        return (new DurableEpochManager(epochs, store, Clock), epochs, store);
    }

    // -- Write ordering --

    [Fact]
    public async Task AStagedCommitIsRecordedBeforeTheGroupMoves()
    {
        // The window between the two is the one a crash can land in. Written
        // first, it contains a group we believe is mid-publish when it is not,
        // which a relay query settles. Written second, it would contain a
        // commit about to be published that nothing remembers staging.
        var group = NewGroup();
        (DurableEpochManager durable, EpochManager epochs, TracingStore store) = NewManager(group);
        await durable.SetStableAsync(group, new EpochId(1));

        await durable.BeginPendingAsync(
            group, new EpochId(1), new EpochId(2), Staged, PendingKind.GroupEvolution);

        Assert.Equal("put PendingPublish while Stable", store.Trace.Last());
        Assert.False(epochs.CanIngest(group));
    }

    [Fact]
    public async Task AConfirmedPublishIsForgottenOnlyAfterTheGroupHasMoved()
    {
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager(group);
        await durable.SetStableAsync(group, new EpochId(1));
        PendingStateRef pending = await durable.BeginPendingAsync(
            group, new EpochId(1), new EpochId(2), Staged, PendingKind.GroupEvolution);

        await durable.ConfirmPublishAsync(pending);

        Assert.Equal("clear while Stable", store.Trace.Last());
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task ADiscardedPublishIsForgottenOnlyAfterTheGroupHasMoved()
    {
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager(group);
        await durable.SetStableAsync(group, new EpochId(1));
        PendingStateRef pending = await durable.BeginPendingAsync(
            group, new EpochId(1), new EpochId(2), Staged, PendingKind.GroupEvolution);

        await durable.RollbackPublishAsync(pending);

        Assert.Equal("clear while Stable", store.Trace.Last());
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task AGroupIsRecordedFrozenBeforeItStops()
    {
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager(group);
        await durable.SetStableAsync(group, new EpochId(5));

        await durable.MarkUnrecoverableAsync(group);

        Assert.Equal("put Unrecoverable while Stable", store.Trace.Last());
    }

    [Fact]
    public async Task AGroupIsRecordedDisbandedBeforeItStops()
    {
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager(group);
        await durable.SetStableAsync(group, new EpochId(5));

        await durable.MarkDisbandedAsync(group, new EpochId(5));

        Assert.Equal("put Disbanded while Stable", store.Trace.Last());
    }

    [Fact]
    public async Task ARepairIsForgottenOnlyAfterTheGroupHasUnfrozen()
    {
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager(group);
        await durable.MarkUnrecoverableAsync(group);

        await durable.RepairToStableAsync(group, new EpochId(7));

        Assert.Equal("clear while Stable", store.Trace.Last());
        Assert.Equal(0, store.Count);
    }

    // -- A write that fails --

    [Fact]
    public async Task AGroupThatCannotBeRecordedAsPendingNeverStages()
    {
        // A commit that was never staged is nothing at all. A commit staged and
        // not recorded is a commit the next session cannot account for, so the
        // failure has to land on the harmless side of that.
        var group = NewGroup();
        (DurableEpochManager durable, EpochManager epochs, TracingStore store) = NewManager(group);
        await durable.SetStableAsync(group, new EpochId(1));
        store.FailWrites = true;

        await Assert.ThrowsAsync<IOException>(() => durable.BeginPendingAsync(
            group, new EpochId(1), new EpochId(2), Staged, PendingKind.GroupEvolution));

        Assert.True(epochs.GetState(group)!.IsStable);
        Assert.True(epochs.CanIngest(group));
    }

    [Fact]
    public async Task APublishThatCannotBeForgottenStaysReconcilable()
    {
        // The other side of the ordering. The clear failing leaves the group
        // advanced in memory and the row behind, so a restart comes back
        // believing a settled publish is still in flight -- a relay query and a
        // second confirmation, rather than a lost commit.
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager(group);
        await durable.SetStableAsync(group, new EpochId(1));
        PendingStateRef pending = await durable.BeginPendingAsync(
            group, new EpochId(1), new EpochId(2), Staged, PendingKind.GroupEvolution);
        store.FailClears = true;

        await Assert.ThrowsAsync<IOException>(() => durable.ConfirmPublishAsync(pending));

        EpochStateRecord? row = await store.GetEpochStateAsync(group);
        Assert.Equal(EpochStateKind.PendingPublish, row!.Kind);
        Assert.Equal(Staged, row.StagedCommit);
    }

    [Fact]
    public async Task AnIllegalStageRecordsNothing()
    {
        // The move is refused because the group is frozen. Writing the row
        // first has to mean writing it only once the move is known to be legal,
        // or a refused stage would overwrite the refusal that caused it.
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager(group);
        await durable.MarkUnrecoverableAsync(group);

        await Assert.ThrowsAsync<InvalidEpochTransitionException>(() => durable.BeginPendingAsync(
            group, new EpochId(1), new EpochId(2), Staged, PendingKind.GroupEvolution));

        EpochStateRecord? row = await store.GetEpochStateAsync(group);
        Assert.Equal(EpochStateKind.Unrecoverable, row!.Kind);
    }

    // -- What a restart reads back --

    [Fact]
    public async Task AGroupMidPublishComesBackKnowingIt()
    {
        // The whole point. Before this, a restart read a staged commit as no
        // state at all: CanIngest answered true and the commit that may already
        // be on a relay had nobody left to reconcile it.
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager();
        await durable.SetStableAsync(group, new EpochId(4));
        PendingStateRef pending = await durable.BeginPendingAsync(
            group, new EpochId(4), new EpochId(5), Staged, PendingKind.Disband);

        var restarted = new EpochManager();
        int restored = await new DurableEpochManager(restarted, store, Clock).RestoreAsync();

        Assert.Equal(1, restored);
        var state = Assert.IsType<EpochState.PendingPublish>(restarted.GetState(group));
        Assert.Equal(new EpochId(5), state.Epoch);
        Assert.Equal(Staged, state.StagedCommit);
        Assert.Equal(pending, state.Reference);
        Assert.False(restarted.CanIngest(group));

        // Named by the operation it was, so a disband is not retried as an
        // ordinary evolution.
        Assert.Equal(PendingKind.Disband, restarted.KindForPending(pending));
        Assert.Equal(group, restarted.GroupForPending(pending));
    }

    [Fact]
    public async Task ARestoredPublishRollsBackToTheEpochItLeft()
    {
        // The prior epoch is not the new one minus one -- it is where the group
        // actually stood -- so it is stored rather than derived, and this is
        // what proves it came back.
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager();
        await durable.SetStableAsync(group, new EpochId(4));
        PendingStateRef pending = await durable.BeginPendingAsync(
            group, new EpochId(4), new EpochId(9), Staged, PendingKind.GroupEvolution);

        var restarted = new DurableEpochManager(new EpochManager(), store, Clock);
        await restarted.RestoreAsync();

        (GroupId rolledBack, EpochId priorEpoch) = await restarted.RollbackPublishAsync(pending);

        Assert.Equal(group, rolledBack);
        Assert.Equal(new EpochId(4), priorEpoch);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task ARestoredPublishStillRecognisesOurOwnCommitAtItsEpoch()
    {
        // Restoring the pending re-establishes the committed-from entry too,
        // which is what separates a fork from a late-arriving commit. Without
        // it a competing commit at the epoch we published from reads as
        // somebody else's work arriving out of order.
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager();
        await durable.SetStableAsync(group, new EpochId(4));
        await durable.BeginPendingAsync(
            group, new EpochId(4), new EpochId(5), Staged, PendingKind.GroupEvolution);

        var restarted = new EpochManager();
        await new DurableEpochManager(restarted, store, Clock).RestoreAsync();

        Assert.True(restarted.WeCommittedFrom(group, new EpochId(4)));
    }

    [Fact]
    public async Task ARestoredReferenceIsNotHandedOutAgain()
    {
        // The allocator is a counter and it does not survive a restart on its
        // own. Reissuing a live reference would have two publishes answering to
        // one id, and confirming either would settle the wrong one.
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager();
        await durable.SetStableAsync(group, new EpochId(1));
        PendingStateRef first = await durable.BeginPendingAsync(
            group, new EpochId(1), new EpochId(2), Staged, PendingKind.GroupEvolution);

        var restarted = new DurableEpochManager(new EpochManager(), store, Clock);
        await restarted.RestoreAsync();
        await restarted.ConfirmPublishAsync(first);

        PendingStateRef next = await restarted.BeginPendingAsync(
            group, new EpochId(2), new EpochId(3), Staged, PendingKind.GroupEvolution);

        Assert.NotEqual(first, next);
    }

    [Fact]
    public async Task AFrozenGroupComesBackFrozen()
    {
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager();
        await durable.SetStableAsync(group, new EpochId(6));
        await durable.MarkUnrecoverableAsync(group);

        var restarted = new EpochManager();
        await new DurableEpochManager(restarted, store, Clock).RestoreAsync();

        Assert.True(restarted.IsUnrecoverable(group));
        Assert.False(restarted.CanIngest(group));
        Assert.Equal(new EpochId(6), restarted.GetEpoch(group));
    }

    [Fact]
    public async Task ADisbandedGroupComesBackDisbanded()
    {
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager();
        await durable.SetStableAsync(group, new EpochId(2));
        await durable.MarkDisbandedAsync(group, new EpochId(3));

        var restarted = new EpochManager();
        await new DurableEpochManager(restarted, store, Clock).RestoreAsync();

        Assert.True(restarted.IsDisbanded(group));
        Assert.False(restarted.CanIngest(group));
        Assert.Equal(new EpochId(3), restarted.GetEpoch(group));
    }

    [Fact]
    public async Task ARepairedGroupDoesNotComeBackFrozen()
    {
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager();
        await durable.MarkUnrecoverableAsync(group);
        await durable.RepairToStableAsync(group, new EpochId(8));

        var restarted = new EpochManager();
        await new DurableEpochManager(restarted, store, Clock).RestoreAsync();

        Assert.True(restarted.CanIngest(group));
        Assert.False(restarted.IsUnrecoverable(group));
    }

    [Fact]
    public async Task AForkedGroupDoesNotComeBackFrozenEither()
    {
        // Recovering is not stored -- it ingests, and its buffered ids are
        // already durable as message rows -- but a group leaving Unrecoverable
        // this way must not be restored into the state it just left.
        var group = NewGroup();
        (DurableEpochManager durable, EpochManager epochs, TracingStore store) = NewManager();
        await durable.MarkUnrecoverableAsync(group);

        await durable.DetectForkAsync(group, Array.Empty<MessageId>());

        Assert.IsType<EpochState.Recovering>(epochs.GetState(group));
        Assert.Equal(0, store.Count);

        var restarted = new EpochManager();
        await new DurableEpochManager(restarted, store, Clock).RestoreAsync();
        Assert.True(restarted.CanIngest(group));
    }

    [Fact]
    public async Task AnOrdinaryGroupStoresNothingAtAll()
    {
        // Stable has no row: the epoch is already durable on the group record
        // and authoritative in the MLS state, and an absent row is what the
        // manager already reads as ingestible.
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager();

        await durable.SetStableAsync(group, new EpochId(1));
        PendingStateRef pending = await durable.BeginPendingAsync(
            group, new EpochId(1), new EpochId(2), Staged, PendingKind.GroupEvolution);
        await durable.ConfirmPublishAsync(pending);

        Assert.Equal(0, store.Count);

        var restarted = new EpochManager();
        Assert.Equal(0, await new DurableEpochManager(restarted, store, Clock).RestoreAsync());
        Assert.Null(restarted.GetState(group));
        Assert.True(restarted.CanIngest(group));
    }

    [Fact]
    public async Task SettlingAGroupMidPublishDropsTheCommitItWasHolding()
    {
        // What a reorg does: a pass adopts somebody else's branch and settles
        // the group on it, over the top of a publish of ours. The row has to go
        // with it -- left behind, the next restart restores a pending publish
        // on a branch the group has already abandoned, and refuses to ingest
        // until someone reconciles a commit that lost.
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager();
        await durable.SetStableAsync(group, new EpochId(1));
        await durable.BeginPendingAsync(
            group, new EpochId(1), new EpochId(2), Staged, PendingKind.GroupEvolution);

        Assert.True(await durable.SetStableAsync(group, new EpochId(7)));

        Assert.Equal(0, store.Count);

        var restarted = new EpochManager();
        await new DurableEpochManager(restarted, store, Clock).RestoreAsync();
        Assert.True(restarted.CanIngest(group));
    }

    [Fact]
    public async Task ARefusedSettleLeavesTheRefusalStanding()
    {
        // SetStable refuses to overwrite a frozen or disbanded group, and the
        // durable record has to refuse with it -- otherwise the next restart
        // resumes a group the engine declined to resume in memory.
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager();
        await durable.MarkDisbandedAsync(group, new EpochId(3));

        Assert.False(await durable.SetStableAsync(group, new EpochId(4)));

        EpochStateRecord? row = await store.GetEpochStateAsync(group);
        Assert.Equal(EpochStateKind.Disbanded, row!.Kind);
    }

    [Fact]
    public async Task RestoreMustRunBeforeAnythingElseTouchesTheGroup()
    {
        // Not a rule the class can enforce, but one worth pinning: a manager
        // that has already moved a group on refuses the restore rather than
        // silently rewinding it.
        var group = NewGroup();
        (DurableEpochManager durable, _, TracingStore store) = NewManager();
        await durable.SetStableAsync(group, new EpochId(1));
        await durable.BeginPendingAsync(
            group, new EpochId(1), new EpochId(2), Staged, PendingKind.GroupEvolution);

        var restarted = new EpochManager();
        restarted.SetStable(group, new EpochId(1));
        var second = new DurableEpochManager(restarted, store, Clock);
        await second.RestoreAsync();

        // Restoring onto a Stable group is the legal case and it works.
        Assert.False(restarted.CanIngest(group));

        // Restoring twice is not: the group is no longer Stable.
        await Assert.ThrowsAsync<InvalidEpochTransitionException>(() => second.RestoreAsync());
    }
}
