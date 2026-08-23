using Scramble.Marmot.Engine;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The manager's bookkeeping: pending publishes, committed-from tracking, and
/// the states that must refuse to be overwritten.
/// </summary>
[Trait("Category", "MarmotEngine")]
public class EpochManagerTests
{
    private static readonly StagedCommitHandle Staged = new(new byte[] { 7 });

    private static GroupId NewGroup() => new(Guid.NewGuid().ToByteArray());

    private static EpochManager AtEpoch(GroupId group, ulong epoch, out EpochManager manager)
    {
        manager = new EpochManager();
        manager.SetStable(group, new EpochId(epoch));
        return manager;
    }

    // -- Ingest gating --

    [Fact]
    public void UnknownGroupIsIngestible()
    {
        var manager = new EpochManager();

        // A Welcome necessarily arrives before we have state for its group.
        Assert.True(manager.CanIngest(NewGroup()));
    }

    [Fact]
    public void IngestIsGatedWhileAPublishIsInFlight()
    {
        var group = NewGroup();
        AtEpoch(group, 1, out var manager);
        Assert.True(manager.CanIngest(group));

        manager.BeginPending(group, new EpochId(1), new EpochId(2), Staged,
            manager.NextPendingRef(), PendingKind.GroupEvolution);

        Assert.False(manager.CanIngest(group));
    }

    // -- Pending lifecycle --

    [Fact]
    public void ConfirmingAPublishAdvancesTheEpochAndClearsThePending()
    {
        var group = NewGroup();
        AtEpoch(group, 1, out var manager);
        var reference = manager.NextPendingRef();
        manager.BeginPending(group, new EpochId(1), new EpochId(2), Staged,
            reference, PendingKind.GroupEvolution);

        var (confirmedGroup, epoch) = manager.ConfirmPublish(reference);

        Assert.Equal(group, confirmedGroup);
        Assert.Equal(new EpochId(2), epoch);
        Assert.IsType<EpochState.Stable>(manager.GetState(group));
        Assert.Null(manager.GroupForPending(reference));
        Assert.True(manager.CanIngest(group));
    }

    [Fact]
    public void FailingAPublishReturnsToThePriorEpoch()
    {
        var group = NewGroup();
        AtEpoch(group, 1, out var manager);
        var reference = manager.NextPendingRef();
        manager.BeginPending(group, new EpochId(1), new EpochId(2), Staged,
            reference, PendingKind.GroupEvolution);

        var (_, priorEpoch) = manager.RollbackPublish(reference);

        Assert.Equal(new EpochId(1), priorEpoch);
        Assert.Equal(new EpochId(1), manager.GetEpoch(group));
        Assert.Null(manager.GroupForPending(reference));
    }

    [Fact]
    public void PendingMetadataIsAddressableWhileInFlight()
    {
        var group = NewGroup();
        AtEpoch(group, 1, out var manager);
        var reference = manager.NextPendingRef();

        manager.BeginPending(group, new EpochId(1), new EpochId(2), Staged,
            reference, PendingKind.Disband);

        Assert.Equal(group, manager.GroupForPending(reference));
        Assert.Equal(PendingKind.Disband, manager.KindForPending(reference));
    }

    [Fact]
    public void UnknownPendingReferenceIsRejected()
    {
        var manager = new EpochManager();

        Assert.Throws<KeyNotFoundException>(() => manager.ConfirmPublish(new PendingStateRef(99)));
        Assert.Throws<KeyNotFoundException>(() => manager.RollbackPublish(new PendingStateRef(99)));
    }

    [Fact]
    public void PendingReferencesAreUnique()
    {
        var manager = new EpochManager();

        var refs = Enumerable.Range(0, 5).Select(_ => manager.NextPendingRef()).ToList();

        Assert.Equal(5, refs.Distinct().Count());
    }

    [Fact]
    public void RestoringAPendingAdvancesTheAllocatorPastIt()
    {
        var group = NewGroup();
        AtEpoch(group, 1, out var manager);

        manager.RestorePending(group, new EpochId(1), new EpochId(2), Staged,
            new PendingStateRef(42), PendingKind.GroupEvolution);

        // A fresh reference must not collide with the one just restored.
        Assert.True(manager.NextPendingRef().Value > 42);
    }

    // -- committed-from bookkeeping --

    [Fact]
    public void StagingACommitRecordsTheEpochWeCommittedFrom()
    {
        var group = NewGroup();
        AtEpoch(group, 5, out var manager);
        Assert.False(manager.WeCommittedFrom(group, new EpochId(5)));

        manager.BeginPending(group, new EpochId(5), new EpochId(6), Staged,
            manager.NextPendingRef(), PendingKind.GroupEvolution);

        Assert.True(manager.WeCommittedFrom(group, new EpochId(5)));
    }

    [Fact]
    public void ConfirmedCommitsKeepTheirCommittedFromEntry()
    {
        var group = NewGroup();
        AtEpoch(group, 5, out var manager);
        var reference = manager.NextPendingRef();
        manager.BeginPending(group, new EpochId(5), new EpochId(6), Staged,
            reference, PendingKind.GroupEvolution);

        manager.ConfirmPublish(reference);

        Assert.True(manager.WeCommittedFrom(group, new EpochId(5)));
    }

    [Fact]
    public void RollbackForgetsAnEpochItAloneRecorded()
    {
        var group = NewGroup();
        AtEpoch(group, 5, out var manager);
        var reference = manager.NextPendingRef();
        manager.BeginPending(group, new EpochId(5), new EpochId(6), Staged,
            reference, PendingKind.GroupEvolution);

        manager.RollbackPublish(reference);

        // The commit never reached anyone, so a later commit at epoch 5 is a
        // benign race and must not be treated as a fork.
        Assert.False(manager.WeCommittedFrom(group, new EpochId(5)));
    }

    /// <summary>
    /// Regression: a rolled-back publish must not erase a committed-from entry
    /// recorded by an earlier confirmed commit at the same epoch.
    /// </summary>
    /// <remarks>
    /// Upstream carried this bug and fixed it with an explicit ownership flag.
    /// Without it, the sequence below leaves the engine believing it never
    /// committed from epoch 5, so a genuine competing commit at that epoch
    /// stops being recognised as a fork and is applied as though it were
    /// ordinary traffic — silent divergence rather than recovery.
    /// </remarks>
    [Fact]
    public void RollbackPreservesACommittedFromEntryOwnedByAnEarlierCommit()
    {
        var group = NewGroup();
        AtEpoch(group, 5, out var manager);

        // A commit from epoch 5 that succeeds. It owns committed-from{5}.
        var confirmed = manager.NextPendingRef();
        manager.BeginPending(group, new EpochId(5), new EpochId(6), Staged,
            confirmed, PendingKind.GroupEvolution);
        manager.ConfirmPublish(confirmed);

        // A fork rolls us back to epoch 5, and we stage again from there.
        manager.SetStable(group, new EpochId(5));
        var second = manager.NextPendingRef();
        manager.BeginPending(group, new EpochId(5), new EpochId(6), Staged,
            second, PendingKind.GroupEvolution);

        // This one fails to publish.
        manager.RollbackPublish(second);

        Assert.True(manager.WeCommittedFrom(group, new EpochId(5)));
    }

    [Fact]
    public void PruningForgetsCommittedFromEpochsBelowTheHorizon()
    {
        var group = NewGroup();
        AtEpoch(group, 1, out var manager);

        for (ulong epoch = 1; epoch <= 5; epoch++)
        {
            manager.SetStable(group, new EpochId(epoch));
            var reference = manager.NextPendingRef();
            manager.BeginPending(group, new EpochId(epoch), new EpochId(epoch + 1), Staged,
                reference, PendingKind.GroupEvolution);
            manager.ConfirmPublish(reference);
        }

        manager.PruneCommittedFromBefore(group, new EpochId(4));

        Assert.False(manager.WeCommittedFrom(group, new EpochId(3)));
        Assert.True(manager.WeCommittedFrom(group, new EpochId(4)));
        Assert.True(manager.WeCommittedFrom(group, new EpochId(5)));
    }

    [Fact]
    public void CommittedFromIsScopedPerGroup()
    {
        var a = NewGroup();
        var b = NewGroup();
        var manager = new EpochManager();
        manager.SetStable(a, new EpochId(3));
        manager.SetStable(b, new EpochId(3));

        manager.BeginPending(a, new EpochId(3), new EpochId(4), Staged,
            manager.NextPendingRef(), PendingKind.GroupEvolution);

        Assert.True(manager.WeCommittedFrom(a, new EpochId(3)));
        Assert.False(manager.WeCommittedFrom(b, new EpochId(3)));
    }

    // -- Frozen and terminal states --

    [Fact]
    public void UnrecoverableRefusesAnOrdinaryStableWrite()
    {
        var group = NewGroup();
        AtEpoch(group, 3, out var manager);
        manager.MarkUnrecoverable(group);

        bool applied = manager.SetStable(group, new EpochId(4));

        // Routine traffic must not be able to clear a frozen group.
        Assert.False(applied);
        Assert.True(manager.IsUnrecoverable(group));
        Assert.Equal(new EpochId(3), manager.GetEpoch(group));
    }

    [Fact]
    public void RepairIsTheOnlyWayOutOfUnrecoverable()
    {
        var group = NewGroup();
        AtEpoch(group, 3, out var manager);
        manager.MarkUnrecoverable(group);

        manager.RepairToStable(group, new EpochId(8));

        Assert.False(manager.IsUnrecoverable(group));
        Assert.Equal(new EpochId(8), manager.GetEpoch(group));
        Assert.True(manager.CanIngest(group));
    }

    [Fact]
    public void RepairingAGroupThatIsNotFrozenIsRejected()
    {
        var group = NewGroup();
        AtEpoch(group, 3, out var manager);

        Assert.Throws<InvalidEpochTransitionException>(
            () => manager.RepairToStable(group, new EpochId(4)));
    }

    [Fact]
    public void DisbandedRefusesAnOrdinaryStableWrite()
    {
        var group = NewGroup();
        AtEpoch(group, 3, out var manager);
        manager.MarkDisbanded(group, new EpochId(3));

        bool applied = manager.SetStable(group, new EpochId(4));

        Assert.False(applied);
        Assert.True(manager.IsDisbanded(group));
        Assert.False(manager.CanIngest(group));
    }

    [Fact]
    public void RestoredMarkersSurviveAsIfNeverLost()
    {
        var frozen = NewGroup();
        var gone = NewGroup();
        var manager = new EpochManager();

        manager.RestoreUnrecoverable(frozen, new EpochId(2));
        manager.RestoreDisbanded(gone, new EpochId(6));

        Assert.True(manager.IsUnrecoverable(frozen));
        Assert.Equal(new EpochId(2), manager.GetEpoch(frozen));
        Assert.True(manager.IsDisbanded(gone));
        Assert.Equal(new EpochId(6), manager.GetEpoch(gone));
    }

    // -- Atomicity --

    [Fact]
    public void ARejectedTransitionLeavesTheGroupUntouched()
    {
        var group = NewGroup();
        AtEpoch(group, 1, out var manager);
        var first = manager.NextPendingRef();
        manager.BeginPending(group, new EpochId(1), new EpochId(2), Staged,
            first, PendingKind.GroupEvolution);

        // Staging a second commit while one is in flight is illegal.
        Assert.Throws<InvalidEpochTransitionException>(() =>
            manager.BeginPending(group, new EpochId(1), new EpochId(2), Staged,
                manager.NextPendingRef(), PendingKind.GroupEvolution));

        // The original pending must still be intact and confirmable.
        var (_, epoch) = manager.ConfirmPublish(first);
        Assert.Equal(new EpochId(2), epoch);
    }

    [Fact]
    public void ForkDetectionIsAcceptedFromAnyState()
    {
        var group = NewGroup();
        AtEpoch(group, 4, out var manager);
        manager.BeginPending(group, new EpochId(4), new EpochId(5), Staged,
            manager.NextPendingRef(), PendingKind.GroupEvolution);

        manager.DetectFork(group, Array.Empty<MessageId>());

        // Recovering still accepts ingest — convergence needs the inputs.
        Assert.True(manager.CanIngest(group));
        Assert.Equal(new EpochId(5), manager.GetEpoch(group));
    }
}
