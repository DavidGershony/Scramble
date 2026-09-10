using Scramble.Marmot.Engine.Convergence;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Deciding when a group has heard enough to choose a history.
/// </summary>
/// <remarks>
/// The question before branch selection. Deciding early is not a smaller
/// mistake than deciding badly: a member that decides from a partial candidate
/// set reaches a different answer than a peer with a different partial set,
/// from the same rules. Most of what is checked here is that the two bounds
/// fail in opposite directions and that neither can be talked out of firing.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class CanonicalizationPipelineTests
{
    private static readonly CanonicalizationPipeline Pipeline = new(ConvergencePolicy.V1);

    private static MessageId Id(string seed) =>
        MessageId.FromMlsBytes(System.Text.Encoding.UTF8.GetBytes(seed));

    private static CanonicalizationOutcome Outcome(
        IEnumerable<string>? inputs = null,
        IEnumerable<string>? resolved = null,
        bool blocked = false) =>
        new(
            (inputs ?? []).Select(Id).ToHashSet(),
            (resolved ?? []).Select(Id).ToHashSet(),
            blocked);

    // ---- The quiescence window ----

    [Fact]
    public void InputThatJustArrivedLeavesTheGroupSyncing()
    {
        // Deciding now would mean choosing from a candidate set still being
        // delivered, then choosing again when the rest of it lands.
        ConvergenceStatus status = Pipeline.Classify(
            nowMs: 1_000, lastRelevantInputMs: 900, Outcome());

        Assert.Equal(ConvergenceStatus.Syncing, status);
    }

    [Fact]
    public void TheWindowIsExactlyAsLongAsItSays()
    {
        // One millisecond either side of the boundary. An off-by-one here makes
        // every member decide a millisecond apart, which is exactly when two
        // members see different candidate sets.
        ulong window = ConvergencePolicy.V1SettlementQuiescenceMs;

        Assert.Equal(
            ConvergenceStatus.Syncing,
            Pipeline.Classify(window - 1, 0, Outcome()));

        Assert.Equal(
            ConvergenceStatus.Settled,
            Pipeline.Classify(window, 0, Outcome()));
    }

    [Fact]
    public void QuiescenceIsCheckedBeforeCompleteness()
    {
        // Order matters. An unclassified input during the window is expected
        // rather than work outstanding, so testing completeness first would
        // report Resolving for a pass that has not begun.
        ConvergenceStatus status = Pipeline.Classify(
            nowMs: 100,
            lastRelevantInputMs: 100,
            Outcome(inputs: ["a", "b"], resolved: []));

        Assert.Equal(ConvergenceStatus.Syncing, status);
    }

    [Fact]
    public void ABackwardsClockReadsAsInputJustArrived()
    {
        // Saturating rather than wrapping. An unsigned subtraction that went
        // negative would produce an enormous elapsed time and settle the group
        // instantly, on whatever partial set it happened to hold.
        ConvergenceStatus status = Pipeline.Classify(
            nowMs: 500, lastRelevantInputMs: 5_000, Outcome());

        Assert.Equal(ConvergenceStatus.Syncing, status);
    }

    // ---- Completeness ----

    [Fact]
    public void AnUnclassifiedInputKeepsThePassResolving()
    {
        ConvergenceStatus status = Pipeline.Classify(
            nowMs: 10_000,
            lastRelevantInputMs: 0,
            Outcome(inputs: ["a", "b"], resolved: ["a"]));

        Assert.Equal(ConvergenceStatus.Resolving, status);
    }

    [Fact]
    public void EveryInputAccountedForSettles()
    {
        // "Resolved" is deliberately wider than "accepted": a dropped or
        // deferred message is accounted for. Requiring acceptance would leave a
        // group permanently Resolving over a message it correctly refused.
        ConvergenceStatus status = Pipeline.Classify(
            nowMs: 10_000,
            lastRelevantInputMs: 0,
            Outcome(inputs: ["a", "b"], resolved: ["a", "b"]));

        Assert.Equal(ConvergenceStatus.Settled, status);
    }

    [Fact]
    public void NoInputAtAllSettles()
    {
        // A quiet group with nothing pending is settled, not stuck. This is the
        // ordinary steady state and it must not read as an anomaly.
        Assert.Equal(
            ConvergenceStatus.Settled,
            Pipeline.Classify(10_000, 0, Outcome()));
    }

    // ---- Blocking ----

    [Fact]
    public void ABlockingErrorIsNotReportedAsSyncing()
    {
        // The distinction a client acts on. Syncing resolves by waiting;
        // Blocked never does, and showing a spinner for it lies to the user
        // indefinitely.
        ConvergenceStatus status = Pipeline.Classify(
            nowMs: 10_000,
            lastRelevantInputMs: 0,
            Outcome(inputs: ["a"], resolved: ["a"], blocked: true));

        Assert.Equal(ConvergenceStatus.Blocked, status);
    }

    [Fact]
    public void UnresolvedInputOutranksABlockingError()
    {
        // Upstream's order, and the useful one: while input is still being
        // classified the pass may yet resolve what looks blocking, so reporting
        // Blocked early would be a verdict on an unfinished pass.
        ConvergenceStatus status = Pipeline.Classify(
            nowMs: 10_000,
            lastRelevantInputMs: 0,
            Outcome(inputs: ["a", "b"], resolved: ["a"], blocked: true));

        Assert.Equal(ConvergenceStatus.Resolving, status);
    }

    // ---- The pass cap ----

    [Fact]
    public void APassExpiresOnTheCapRegardlessOfQuiet()
    {
        // The bound that cannot be pushed back. The quiescence window resets on
        // every arrival, so a peer delivering one candidate just inside it could
        // otherwise hold a pass open forever.
        ulong cap = ConvergencePolicy.V1MaxConvergencePassMs;

        Assert.False(Pipeline.HasExpired(nowMs: cap, startedMs: 0));
        Assert.True(Pipeline.HasExpired(nowMs: cap + 1, startedMs: 0));
    }

    [Fact]
    public void TheCapIsMeasuredFromTheStartOfThePassNotTheLastInput()
    {
        // The whole reason it is a separate bound. A pass started long ago is
        // expired even if input arrived a moment ago -- which is precisely the
        // trickle a hostile or unlucky peer produces.
        ulong started = 0;
        ulong now = ConvergencePolicy.V1MaxConvergencePassMs + 500;

        Assert.True(Pipeline.HasExpired(now, started));

        // And the quiescence check, given that same recent input, still says
        // Syncing -- so without the cap the pass would never end.
        Assert.Equal(
            ConvergenceStatus.Syncing,
            Pipeline.Classify(now, lastRelevantInputMs: now - 1, Outcome()));
    }

    [Fact]
    public void AFreshPassHasNotExpired()
    {
        Assert.False(Pipeline.HasExpired(nowMs: 0, startedMs: 0));
        Assert.False(Pipeline.HasExpired(nowMs: 1, startedMs: 0));
    }

    [Fact]
    public void TheDeadlineDoesNotWrapAround()
    {
        // A pass started near the end of the clock's range must not produce a
        // deadline in the past, which would expire every pass immediately.
        Assert.Equal(ulong.MaxValue, Pipeline.DeadlineFor(ulong.MaxValue));
        Assert.Equal(
            ConvergencePolicy.V1MaxConvergencePassMs, Pipeline.DeadlineFor(0));
    }

    // ---- The policy it runs under ----

    [Fact]
    public void ThePipelineUsesThePinnedPolicy()
    {
        // Both bounds are consensus values. A pipeline running its own numbers
        // would settle at a different moment from every peer, on a different
        // candidate set.
        Assert.True(Pipeline.Policy.IsPinnedV1);
        Assert.Equal(1_000u, ConvergencePolicy.V1SettlementQuiescenceMs);
        Assert.Equal(5_000u, ConvergencePolicy.V1MaxConvergencePassMs);
    }
}
