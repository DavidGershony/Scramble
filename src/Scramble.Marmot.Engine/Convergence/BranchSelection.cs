using Scramble.Marmot.AppComponents;

namespace Scramble.Marmot.Engine.Convergence;

/// <summary>
/// An application message observed on a branch, as evidence the branch is real.
/// </summary>
/// <param name="Epoch">The epoch the message was sent in.</param>
/// <param name="Sender">The sending member's account key.</param>
public sealed record AppWitness(ulong Epoch, byte[] Sender);

/// <summary>
/// One competing history, from where it forked to where it currently ends.
/// </summary>
/// <param name="Id">A stable identifier, unique within a selection.</param>
/// <param name="ForkEpoch">The last epoch this branch shares with the others.</param>
/// <param name="TipEpoch">The epoch of this branch's newest commit.</param>
/// <param name="TipPriority">Whether the tip commit needed admin authority.</param>
/// <param name="TipCommitter">The tip committer's account key.</param>
/// <param name="TipDigest">The tip commit's digest, 32 bytes.</param>
/// <param name="AppWitnesses">Application messages seen on this branch.</param>
public sealed record BranchCandidate(
    string Id,
    ulong ForkEpoch,
    ulong TipEpoch,
    CommitOrderingPriority TipPriority,
    byte[] TipCommitter,
    byte[] TipDigest,
    IReadOnlyList<AppWitness> AppWitnesses);

/// <summary>How a branch scores, in the order the rules are applied.</summary>
/// <param name="ValidCommitDepth">Commits on the branch since the fork.</param>
/// <param name="EffectiveCommitDepth">Depth plus any witness boost.</param>
/// <param name="WitnessQuorumMet">Whether the branch met witness quorum.</param>
/// <param name="AppWitnessScore">Capped count of distinct witnessing senders.</param>
/// <param name="TipPriority">The tip commit's ordering class.</param>
/// <param name="TipCommitter">The tip committer's account key.</param>
/// <param name="TipDigest">The tip commit's digest.</param>
public sealed record BranchScore(
    ulong ValidCommitDepth,
    ulong EffectiveCommitDepth,
    bool WitnessQuorumMet,
    int AppWitnessScore,
    CommitOrderingPriority TipPriority,
    byte[] TipCommitter,
    byte[] TipDigest);

/// <summary>
/// Choosing which of several competing histories a forked group keeps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member must reach the same answer from the same candidate set, and
/// nothing else about them may matter.</b> Not arrival order, not who forked,
/// not who is asking. A selector that is even slightly order-dependent produces
/// a group that never reconverges, because each member keeps choosing the branch
/// it happened to see first.
/// </para>
/// <para>
/// That is why the comparison ends in a digest tie-break rather than in
/// "whichever came first": every rule below is a total order over the candidate
/// set, so the result is a pure function of the set. It is also why the scoring
/// aggregates witnesses into sets keyed by epoch and sender — receiving the same
/// member's message twice must not make a branch look better.
/// </para>
/// <para>
/// The rules, most significant first, from
/// <c>cgka-engine/src/convergence.rs</c> <c>compare_scores</c>:
/// </para>
/// <list type="number">
/// <item>Greater effective commit depth — the longer valid history wins.</item>
/// <item>Witness quorum met.</item>
/// <item>Higher app-witness score.</item>
/// <item><b>Lower</b> tip priority, then <b>lower</b> committer, then
/// <b>lower</b> digest. Note the inversion: these three are deliberately
/// reversed upstream, and reproducing them the intuitive way round would give a
/// selector that agrees with the reference implementation on every case except
/// the tied ones — which are the only cases a tie-break exists for.</item>
/// </list>
/// </remarks>
public static class BranchSelection
{
    /// <summary>
    /// Whether a branch forks recently enough to be considered at all.
    /// </summary>
    /// <remarks>
    /// The horizon is measured from the <i>current tip</i>, not from the
    /// candidate's own tip: the question is how much delivered history adopting
    /// this branch would discard.
    /// </remarks>
    public static bool IsEligible(
        ulong currentTipEpoch, BranchCandidate branch, ConvergencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(policy);

        return SaturatingSub(currentTipEpoch, branch.ForkEpoch) <= policy.MaxRewindCommits;
    }

    /// <summary>Scores a branch under a policy.</summary>
    public static BranchScore Score(BranchCandidate branch, ConvergencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(policy);

        ulong depth = SaturatingSub(branch.TipEpoch, branch.ForkEpoch);
        bool quorum = WitnessQuorumMet(branch.AppWitnesses, policy);

        return new BranchScore(
            ValidCommitDepth: depth,
            EffectiveCommitDepth: SaturatingAdd(
                depth, quorum ? policy.MaxWitnessOverrideDepth : 0),
            WitnessQuorumMet: quorum,
            AppWitnessScore: AppWitnessScore(branch.AppWitnesses, policy),
            TipPriority: branch.TipPriority,
            TipCommitter: branch.TipCommitter,
            TipDigest: branch.TipDigest);
    }

    /// <summary>
    /// Picks the branch a forked group should keep, or null if none is eligible.
    /// </summary>
    /// <remarks>
    /// Null is a real outcome, not an error: when every candidate forks beyond
    /// the rewind horizon there is no branch this member may adopt, and the
    /// caller has to surface that rather than pick the least bad one.
    /// </remarks>
    public static BranchCandidate? SelectCanonical(
        ulong currentTipEpoch,
        IReadOnlyList<BranchCandidate> candidates,
        ConvergencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(policy);

        BranchCandidate? best = null;
        BranchScore? bestScore = null;

        foreach (BranchCandidate candidate in candidates)
        {
            if (!IsEligible(currentTipEpoch, candidate, policy))
                continue;

            BranchScore score = Score(candidate, policy);

            if (bestScore is null || Compare(score, bestScore) > 0)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    /// Orders two scores. Positive when <paramref name="a"/> wins.
    /// </summary>
    /// <remarks>
    /// The last three rules compare in reverse, matching upstream exactly. That
    /// looks like a mistake and is not: what matters is only that every member
    /// applies the same direction, and this is the direction the reference
    /// implementation applies.
    /// </remarks>
    public static int Compare(BranchScore a, BranchScore b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        int byDepth = a.EffectiveCommitDepth.CompareTo(b.EffectiveCommitDepth);
        if (byDepth != 0)
            return byDepth;

        int byQuorum = a.WitnessQuorumMet.CompareTo(b.WitnessQuorumMet);
        if (byQuorum != 0)
            return byQuorum;

        int byWitness = a.AppWitnessScore.CompareTo(b.AppWitnessScore);
        if (byWitness != 0)
            return byWitness;

        // Reversed, as upstream has them.
        int byPriority = b.TipPriority.CompareTo(a.TipPriority);
        if (byPriority != 0)
            return byPriority;

        int byCommitter = CompareBytes(b.TipCommitter, a.TipCommitter);
        if (byCommitter != 0)
            return byCommitter;

        return CompareBytes(b.TipDigest, a.TipDigest);
    }

    /// <summary>
    /// Whether enough distinct senders spoke on enough epochs of this branch.
    /// </summary>
    /// <remarks>
    /// A quorum of zero on either axis means "no quorum is achievable", not
    /// "every branch qualifies" — a policy that demanded nothing would hand the
    /// depth boost to every candidate and stop being a tie-break at all.
    /// </remarks>
    public static bool WitnessQuorumMet(
        IReadOnlyList<AppWitness> witnesses, ConvergencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(witnesses);
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.WitnessQuorumSendersPerEpoch == 0 || policy.WitnessQuorumEpochs == 0)
            return false;

        int qualifying = 0;
        foreach (var senders in ByEpoch(witnesses).Values)
        {
            if (senders.Count >= policy.WitnessQuorumSendersPerEpoch)
                qualifying++;
        }

        return qualifying >= policy.WitnessQuorumEpochs;
    }

    /// <summary>
    /// Distinct witnessing senders per epoch, each epoch capped at the quorum.
    /// </summary>
    /// <remarks>
    /// Capped so that one very busy epoch cannot outweigh several quiet ones:
    /// the score is meant to measure how broadly a branch was observed, not how
    /// much traffic it carried.
    /// </remarks>
    public static int AppWitnessScore(
        IReadOnlyList<AppWitness> witnesses, ConvergencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(witnesses);
        ArgumentNullException.ThrowIfNull(policy);

        int total = 0;
        foreach (var senders in ByEpoch(witnesses).Values)
            total += Math.Min(senders.Count, policy.WitnessQuorumSendersPerEpoch);

        return total;
    }

    /// <summary>
    /// Groups witnesses into distinct senders per epoch.
    /// </summary>
    /// <remarks>
    /// A set per epoch, so the same member's second message in one epoch adds
    /// nothing. Without that, a branch could be made to look well-witnessed by
    /// one member talking to themselves.
    /// </remarks>
    private static SortedDictionary<ulong, HashSet<string>> ByEpoch(
        IReadOnlyList<AppWitness> witnesses)
    {
        var byEpoch = new SortedDictionary<ulong, HashSet<string>>();

        foreach (AppWitness witness in witnesses)
        {
            ArgumentNullException.ThrowIfNull(witness);

            if (!byEpoch.TryGetValue(witness.Epoch, out HashSet<string>? senders))
            {
                senders = new HashSet<string>(StringComparer.Ordinal);
                byEpoch[witness.Epoch] = senders;
            }

            senders.Add(Convert.ToHexString(witness.Sender));
        }

        return byEpoch;
    }

    /// <summary>Lexicographic comparison, shortest-is-a-prefix ordering first.</summary>
    private static int CompareBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        a.SequenceCompareTo(b);

    private static ulong SaturatingSub(ulong a, ulong b) => a > b ? a - b : 0;

    private static ulong SaturatingAdd(ulong a, ulong b) =>
        a > ulong.MaxValue - b ? ulong.MaxValue : a + b;
}
