using Scramble.Marmot.AppComponents;

namespace Scramble.Marmot.Engine.Convergence;

/// <summary>One candidate as the selector saw it.</summary>
/// <param name="BranchId">The candidate's id.</param>
/// <param name="Eligible">Whether it forked inside the rewind horizon.</param>
/// <param name="RejectionReasons">Why it was refused, if it was.</param>
/// <param name="Score">Its score, whether or not it was eligible.</param>
public sealed record CandidateEvaluation(
    string BranchId,
    bool Eligible,
    IReadOnlyList<string> RejectionReasons,
    BranchScore Score);

/// <summary>One rule, compared between the winner and the runner-up.</summary>
/// <param name="RuleName">
/// The rule's name, matching upstream's strings exactly — the conformance
/// vectors assert on <c>decisive_rule</c> by name, so these are wire values
/// rather than labels.
/// </param>
/// <param name="WinnerValue">The winner's value, rendered.</param>
/// <param name="OtherValue">The runner-up's value, rendered.</param>
/// <param name="Decisive">Whether this rule is the one that settled it.</param>
public sealed record RuleEvaluation(
    string RuleName, string WinnerValue, string OtherValue, bool Decisive);

/// <summary>Why the selector chose what it chose.</summary>
/// <param name="SelectedBranchId">The winner, or null if none was eligible.</param>
/// <param name="Candidates">Every candidate, ordered by id.</param>
/// <param name="RuleTrace">
/// The rule-by-rule comparison against the runner-up. Empty when there was no
/// winner or no second eligible candidate — with one candidate no rule decides
/// anything, and saying so is more honest than naming the first rule.
/// </param>
/// <param name="LosingBranchIds">Everything not selected, ordered by id.</param>
public sealed record BranchSelectionTrace(
    string? SelectedBranchId,
    IReadOnlyList<CandidateEvaluation> Candidates,
    IReadOnlyList<RuleEvaluation> RuleTrace,
    IReadOnlyList<string> LosingBranchIds);

/// <summary>
/// Branch selection, with a record of the reasoning.
/// </summary>
/// <remarks>
/// <para>
/// The selection is identical to <see cref="BranchSelection.SelectCanonical"/> —
/// this adds only the account of how it was reached. That account is not
/// decoration: a fork resolves by every member independently computing the same
/// answer, so when two members disagree the only useful question is <i>which
/// rule</i> they diverged on, and without a trace that has to be reconstructed
/// from two devices after the fact.
/// </para>
/// <para>
/// <b>The trace is a pure function of the candidate set.</b> Candidates are
/// ordered by id and witnesses aggregated into sets before anything is recorded,
/// so two members holding the same candidates produce identical traces
/// regardless of arrival order. Upstream's conformance vectors compare on
/// <c>decisive_rule</c>, which only means anything if that holds.
/// </para>
/// </remarks>
public static class BranchSelectionAudit
{
    /// <summary>Rule names, in the order they are applied.</summary>
    public static readonly IReadOnlyList<string> RuleNames =
    [
        "effective_commit_depth",
        "witness_quorum_met",
        "app_witness_score",
        "tip_priority",
        "tip_committer",
        "tip_digest",
    ];

    /// <summary>Selects a branch and records why.</summary>
    public static BranchSelectionTrace SelectCanonicalTraced(
        ulong currentTipEpoch,
        IReadOnlyList<BranchCandidate> candidates,
        ConvergencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(policy);

        var ordered = candidates.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

        var evaluations = ordered
            .Select(candidate =>
            {
                bool eligible = BranchSelection.IsEligible(currentTipEpoch, candidate, policy);

                return new CandidateEvaluation(
                    candidate.Id,
                    eligible,
                    eligible ? [] : ["beyond_rewind_horizon"],
                    BranchSelection.Score(candidate, policy));
            })
            .ToList();

        BranchCandidate? winner = BranchSelection.SelectCanonical(
            currentTipEpoch, ordered, policy);

        IReadOnlyList<RuleEvaluation> ruleTrace = [];

        if (winner is not null)
        {
            BranchCandidate? runnerUp = BranchSelection.SelectCanonical(
                currentTipEpoch,
                ordered
                    .Where(c => !string.Equals(c.Id, winner.Id, StringComparison.Ordinal))
                    .ToList(),
                policy);

            if (runnerUp is not null)
            {
                ruleTrace = BuildRuleTrace(
                    BranchSelection.Score(winner, policy),
                    BranchSelection.Score(runnerUp, policy));
            }
        }

        return new BranchSelectionTrace(
            winner?.Id,
            evaluations,
            ruleTrace,
            evaluations
                .Where(e => !string.Equals(e.BranchId, winner?.Id, StringComparison.Ordinal))
                .Select(e => e.BranchId)
                .ToList());
    }

    /// <summary>
    /// Walks the rules in order, marking the first that separates the two.
    /// </summary>
    /// <remarks>
    /// The last three compare reversed, exactly as
    /// <see cref="BranchSelection.Compare"/> does. Recording them the intuitive
    /// way round would produce a trace naming the wrong rule as decisive on
    /// precisely the ties a tie-break exists for.
    /// </remarks>
    private static IReadOnlyList<RuleEvaluation> BuildRuleTrace(
        BranchScore winner, BranchScore other)
    {
        (string Name, int Ordering, string Winner, string Other)[] entries =
        [
            ("effective_commit_depth",
                winner.EffectiveCommitDepth.CompareTo(other.EffectiveCommitDepth),
                winner.EffectiveCommitDepth.ToString(),
                other.EffectiveCommitDepth.ToString()),

            ("witness_quorum_met",
                winner.WitnessQuorumMet.CompareTo(other.WitnessQuorumMet),
                Rendered(winner.WitnessQuorumMet),
                Rendered(other.WitnessQuorumMet)),

            ("app_witness_score",
                winner.AppWitnessScore.CompareTo(other.AppWitnessScore),
                winner.AppWitnessScore.ToString(),
                other.AppWitnessScore.ToString()),

            ("tip_priority",
                other.TipPriority.CompareTo(winner.TipPriority),
                PriorityName(winner.TipPriority),
                PriorityName(other.TipPriority)),

            ("tip_committer",
                ((ReadOnlySpan<byte>)other.TipCommitter).SequenceCompareTo(winner.TipCommitter),
                Hex(winner.TipCommitter),
                Hex(other.TipCommitter)),

            ("tip_digest",
                ((ReadOnlySpan<byte>)other.TipDigest).SequenceCompareTo(winner.TipDigest),
                Hex(winner.TipDigest),
                Hex(other.TipDigest)),
        ];

        var trace = new List<RuleEvaluation>(entries.Length);
        bool decided = false;

        foreach (var (name, ordering, winnerValue, otherValue) in entries)
        {
            bool decisive = !decided && ordering != 0;
            decided |= decisive;

            trace.Add(new RuleEvaluation(name, winnerValue, otherValue, decisive));
        }

        return trace;
    }

    /// <summary>The stable name of a commit-ordering priority, for a trace.</summary>
    public static string PriorityName(CommitOrderingPriority priority) => priority switch
    {
        CommitOrderingPriority.Ordinary => "ordinary",
        CommitOrderingPriority.Privileged => "privileged",
        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
    };

    private static string Rendered(bool value) => value ? "true" : "false";

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}
