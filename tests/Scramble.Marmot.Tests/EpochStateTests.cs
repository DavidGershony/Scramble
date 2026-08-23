using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The transition table, exhaustively: every legal move, and every illegal one
/// it must refuse.
/// </summary>
[Trait("Category", "MarmotEngine")]
public class EpochStateTests
{
    private static readonly StagedCommitHandle Staged = new(new byte[] { 1 });
    private static readonly PendingStateRef Ref = new(1);

    private static EpochState Stable(ulong epoch = 1) => new EpochState.Stable(new EpochId(epoch));

    private static EpochState Pending(ulong epoch = 2) =>
        Stable().BeginPending(new EpochId(epoch), Staged, Ref);

    private static EpochState Merging(ulong epoch = 2) => Pending(epoch).ConfirmPublish();

    private static EpochState Recovering() => Stable().DetectFork(Array.Empty<MessageId>());

    private static EpochState Unrecoverable() => Stable().ToUnrecoverable();

    private static EpochState Disbanded(ulong epoch = 1) => Recovering().SettleToDisbanded(new EpochId(epoch));

    public static TheoryData<string, EpochState> AllStates() => new()
    {
        { nameof(EpochState.Stable), Stable() },
        { nameof(EpochState.PendingPublish), Pending() },
        { nameof(EpochState.Merging), Merging() },
        { nameof(EpochState.Recovering), Recovering() },
        { nameof(EpochState.Unrecoverable), Unrecoverable() },
        { nameof(EpochState.Disbanded), Disbanded() },
    };

    // -- Legal transitions --

    [Fact]
    public void StableBeginsPending()
    {
        var next = Stable(1).BeginPending(new EpochId(2), Staged, Ref);

        var pending = Assert.IsType<EpochState.PendingPublish>(next);
        Assert.Equal(new EpochId(2), pending.Epoch);
        Assert.Equal(Ref, pending.Reference);
    }

    [Fact]
    public void PendingConfirmsToMerging()
    {
        var next = Pending(2).ConfirmPublish();

        Assert.Equal(new EpochId(2), Assert.IsType<EpochState.Merging>(next).Epoch);
    }

    [Fact]
    public void PendingRollsBackToPriorEpoch()
    {
        // The epoch reverts to where we were, not to the one we hoped to reach.
        var next = Pending(2).RollbackPending(new EpochId(1));

        Assert.Equal(new EpochId(1), Assert.IsType<EpochState.Stable>(next).Epoch);
    }

    [Fact]
    public void MergingSettlesToStable()
    {
        var next = Merging(2).MergeToStable(new EpochId(2));

        Assert.Equal(new EpochId(2), Assert.IsType<EpochState.Stable>(next).Epoch);
    }

    [Fact]
    public void UnrecoverableRepairsToStable()
    {
        var next = Unrecoverable().RepairToStable(new EpochId(9));

        Assert.Equal(new EpochId(9), Assert.IsType<EpochState.Stable>(next).Epoch);
    }

    [Fact]
    public void RecoveringSettlesToDisbanded()
    {
        var next = Recovering().SettleToDisbanded(new EpochId(4));

        Assert.Equal(new EpochId(4), Assert.IsType<EpochState.Disbanded>(next).Epoch);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void ForkDetectionIsLegalFromEveryState(string name, EpochState state)
    {
        Assert.NotNull(name);

        var next = state.DetectFork(Array.Empty<MessageId>());

        // The last stable epoch carries across, whatever we came from.
        Assert.Equal(state.CurrentEpoch, Assert.IsType<EpochState.Recovering>(next).LastStableEpoch);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void UnrecoverableIsReachableFromEveryState(string name, EpochState state)
    {
        Assert.NotNull(name);

        var next = state.ToUnrecoverable();

        Assert.Equal(state.CurrentEpoch, Assert.IsType<EpochState.Unrecoverable>(next).LastStableEpoch);
    }

    // -- Illegal transitions --

    [Theory]
    [MemberData(nameof(AllStates))]
    public void OnlyStableMayBeginPending(string name, EpochState state)
    {
        if (state is EpochState.Stable)
            return;

        var ex = Assert.Throws<InvalidEpochTransitionException>(
            () => state.BeginPending(new EpochId(2), Staged, Ref));
        Assert.Equal(name, ex.From);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void OnlyPendingMayConfirmPublish(string name, EpochState state)
    {
        if (state is EpochState.PendingPublish)
            return;

        var ex = Assert.Throws<InvalidEpochTransitionException>(() => state.ConfirmPublish());
        Assert.Equal(name, ex.From);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void OnlyPendingMayRollBack(string name, EpochState state)
    {
        if (state is EpochState.PendingPublish)
            return;

        var ex = Assert.Throws<InvalidEpochTransitionException>(
            () => state.RollbackPending(new EpochId(1)));
        Assert.Equal(name, ex.From);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void OnlyMergingMayMergeToStable(string name, EpochState state)
    {
        if (state is EpochState.Merging)
            return;

        var ex = Assert.Throws<InvalidEpochTransitionException>(
            () => state.MergeToStable(new EpochId(2)));
        Assert.Equal(name, ex.From);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void OnlyUnrecoverableMayRepair(string name, EpochState state)
    {
        if (state is EpochState.Unrecoverable)
            return;

        var ex = Assert.Throws<InvalidEpochTransitionException>(
            () => state.RepairToStable(new EpochId(2)));
        Assert.Equal(name, ex.From);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void OnlyRecoveringMayDisband(string name, EpochState state)
    {
        if (state is EpochState.Recovering)
            return;

        var ex = Assert.Throws<InvalidEpochTransitionException>(
            () => state.SettleToDisbanded(new EpochId(2)));
        Assert.Equal(name, ex.From);
    }

    [Fact]
    public void DisbandedHasNoWayOut()
    {
        var disbanded = Disbanded();

        // Every ordinary transition is refused. Fork detection and freezing
        // remain callable by construction, but nothing returns it to service.
        Assert.Throws<InvalidEpochTransitionException>(
            () => disbanded.BeginPending(new EpochId(2), Staged, Ref));
        Assert.Throws<InvalidEpochTransitionException>(() => disbanded.ConfirmPublish());
        Assert.Throws<InvalidEpochTransitionException>(() => disbanded.MergeToStable(new EpochId(2)));
        Assert.Throws<InvalidEpochTransitionException>(() => disbanded.RepairToStable(new EpochId(2)));
    }

    // -- Derived properties --

    [Fact]
    public void AmbiguousStatesReportTheLastStableEpochNotTheCurrentOne()
    {
        var recovering = new EpochState.Stable(new EpochId(7)).DetectFork(Array.Empty<MessageId>());
        var frozen = new EpochState.Stable(new EpochId(7)).ToUnrecoverable();

        // While forked the current epoch is undefined, so the last epoch we can
        // still assert is what callers must see.
        Assert.Equal(new EpochId(7), recovering.CurrentEpoch);
        Assert.Equal(new EpochId(7), frozen.CurrentEpoch);
    }

    [Fact]
    public void PendingReportsTheEpochItWillReach()
    {
        Assert.Equal(new EpochId(2), Pending(2).CurrentEpoch);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void OnlyStableAndRecoveringAcceptIngest(string name, EpochState state)
    {
        bool expected = name is nameof(EpochState.Stable) or nameof(EpochState.Recovering);

        Assert.Equal(expected, state.CanIngest);
    }

    [Fact]
    public void PublishInFlightBuffersRatherThanIngesting()
    {
        // Applying someone else's commit mid-publish would race our own.
        Assert.False(Pending().CanIngest);
        Assert.False(Merging().CanIngest);
    }

    [Fact]
    public void FrozenAndTerminalStatesRefuseIngest()
    {
        Assert.False(Unrecoverable().CanIngest);
        Assert.False(Disbanded().CanIngest);
    }

    [Fact]
    public void RecoveringCarriesItsBufferedInput()
    {
        var buffered = new[] { MessageId.FromMlsBytes(new byte[] { 1 }) };

        var recovering = Assert.IsType<EpochState.Recovering>(Stable().DetectFork(buffered));

        Assert.Equal(buffered, recovering.Buffered);
    }
}
