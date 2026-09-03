using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The constants that decide which branch of a forked group survives.
/// </summary>
/// <remarks>
/// Branch selection has to produce the same answer on every member's device.
/// A member running different constants is not slightly out of tune — it is
/// computing a different function, and it will disagree exactly when a fork
/// makes agreement matter. So these tests are mostly about refusing policies
/// rather than about applying them.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class ConvergencePolicyTests
{
    [Fact]
    public void TheV1ConstantsArePinnedToTheirValues()
    {
        // Pinned by value, not by "whatever the default happens to be": these
        // are consensus constants, so a change is a protocol change and must
        // fail here rather than quietly re-tune every member that ships it.
        var policy = ConvergencePolicy.V1;

        Assert.Equal(5u, policy.MaxRewindCommits);
        Assert.Equal(2, policy.WitnessQuorumSendersPerEpoch);
        Assert.Equal(1, policy.WitnessQuorumEpochs);
        Assert.Equal(1u, policy.MaxWitnessOverrideDepth);
        Assert.Equal(5u, policy.AppMessagePastEpochLimit);
        Assert.Equal(1_000u, policy.SettlementQuiescenceMs);
        Assert.Equal(5_000u, policy.MaxConvergencePassMs);
    }

    [Fact]
    public void ADefaultPolicyIsTheV1Baseline()
    {
        Assert.True(new ConvergencePolicy().IsPinnedV1);
        Assert.Equal(ConvergencePolicy.V1, new ConvergencePolicy());
    }

    [Fact]
    public void AnyDeviationFromV1IsRefused()
    {
        // One field is enough. There is no negotiation mechanism, so "close to
        // v1" is not a weaker version of conforming - it is a fork waiting for
        // the first disagreement.
        var tweaked = ConvergencePolicy.V1 with { SettlementQuiescenceMs = 999 };

        var ex = Assert.Throws<ConvergencePolicyException>(
            () => tweaked.RequireAcceptable(ConvergencePolicy.V1AppMessagePastEpochLimit));

        Assert.Contains("pinned v1 baseline", ex.Message);
    }

    [Fact]
    public void AWitnessBoostBeyondTheRewindHorizonIsRefused()
    {
        // The boost is a tie-break. If it could exceed the rollback horizon,
        // app-message volume alone could push a branch past an arbitrarily
        // longer branch of valid commits -- talking a group onto a shorter
        // history rather than merely breaking a tie on it.
        var policy = ConvergencePolicy.V1 with
        {
            MaxRewindCommits = 2,
            MaxWitnessOverrideDepth = 3,
        };

        var ex = Assert.Throws<ConvergencePolicyException>(policy.Validate);

        Assert.Contains("exceeds the rewind horizon", ex.Message);
    }

    [Fact]
    public void TheBoostMayEqualTheHorizon()
    {
        // The bound is "must not exceed", so equality is the edge that stays
        // legal. Off-by-one here would refuse a policy upstream accepts.
        var policy = ConvergencePolicy.V1 with
        {
            MaxRewindCommits = 3,
            MaxWitnessOverrideDepth = 3,
        };

        policy.Validate();
    }

    [Fact]
    public void TheAppWindowMustEqualTheMlsPastEpochWindow()
    {
        // Two answers to the same question from different sides: how far back a
        // message is still readable, and how far back one still counts as a
        // witness. Whichever is larger, the mismatch changes a branch's score --
        // so two members with different pairings score the same branch
        // differently, which is the one thing selection may not do.
        var ex = Assert.Throws<ConvergencePolicyException>(
            () => ConvergencePolicy.V1.RequireWindowMatches(3));

        Assert.Contains("must equal the MLS past-epoch window", ex.Message);
    }

    [Fact]
    public void TheV1PolicyIsAcceptableAgainstItsOwnWindow()
    {
        ConvergencePolicy.V1.RequireAcceptable(ConvergencePolicy.V1AppMessagePastEpochLimit);
    }

    [Fact]
    public void AcceptanceChecksTheWindowEvenWhenThePolicyIsPinned()
    {
        // Ordering matters: a pinned policy paired with the wrong MLS window is
        // still wrong, and reporting "not pinned" for it would send whoever
        // hits this looking in the wrong place.
        var ex = Assert.Throws<ConvergencePolicyException>(
            () => ConvergencePolicy.V1.RequireAcceptable(4));

        Assert.Contains("must equal the MLS past-epoch window", ex.Message);
        Assert.DoesNotContain("pinned v1", ex.Message);
    }

    [Fact]
    public void TheAppWindowMatchesTheWindowMarmotGroupsRunWith()
    {
        // The MLS side of the same equality. If MarmotGroupSettings and the
        // convergence policy ever disagree, every group we create fails
        // acceptance -- which is the intended outcome, and this says so before
        // a group has to.
        Assert.Equal(
            ConvergencePolicy.V1AppMessagePastEpochLimit,
            ConvergencePolicy.V1.AppMessagePastEpochLimit);
    }
}

/// <summary>
/// Choosing between competing histories after a fork.
/// </summary>
/// <remarks>
/// Every member must reach the same answer from the same candidates, and from
/// nothing else — not arrival order, not who forked, not who is asking. Most of
/// what is checked here is that independence, because a selector that is even
/// slightly order-dependent produces a group that never reconverges: each member
/// keeps choosing whichever branch it happened to see first.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class BranchSelectionTests
{
    private static readonly ConvergencePolicy Policy = ConvergencePolicy.V1;

    private static byte[] Key(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static BranchCandidate Branch(
        string id,
        ulong forkEpoch = 1,
        ulong tipEpoch = 2,
        CommitOrderingPriority priority = CommitOrderingPriority.Ordinary,
        byte committer = 0xAA,
        byte digest = 0xBB,
        IReadOnlyList<AppWitness>? witnesses = null) =>
        new(id, forkEpoch, tipEpoch, priority, Key(committer), Key(digest), witnesses ?? []);

    // ---- Eligibility ----

    [Fact]
    public void ABranchInsideTheRewindHorizonIsEligible()
    {
        Assert.True(BranchSelection.IsEligible(6, Branch("a", forkEpoch: 1), Policy));
    }

    [Fact]
    public void ABranchBeyondTheRewindHorizonIsNot()
    {
        // Measured from the current tip, not the candidate's: the question is
        // how much already-delivered history adopting it would discard.
        Assert.False(BranchSelection.IsEligible(7, Branch("a", forkEpoch: 1), Policy));
    }

    [Fact]
    public void NoEligibleCandidateSelectsNothing()
    {
        // Null is an outcome, not a failure. When every branch forks beyond the
        // horizon there is no branch this member may adopt, and picking the
        // least bad one would be adopting a history it just refused.
        var candidates = new[] { Branch("a", forkEpoch: 1), Branch("b", forkEpoch: 0) };

        Assert.Null(BranchSelection.SelectCanonical(20, candidates, Policy));
    }

    [Fact]
    public void AnIneligibleBranchIsStillScoredInTheTrace()
    {
        // Scored so the trace can say what was rejected and why, rather than
        // leaving a gap that reads like the candidate was never seen.
        var trace = BranchSelectionAudit.SelectCanonicalTraced(
            20, [Branch("a", forkEpoch: 1)], Policy);

        CandidateEvaluation only = Assert.Single(trace.Candidates);

        Assert.False(only.Eligible);
        Assert.Equal("beyond_rewind_horizon", Assert.Single(only.RejectionReasons));
        Assert.Null(trace.SelectedBranchId);
    }

    // ---- The rules, in order ----

    [Fact]
    public void TheLongerBranchWins()
    {
        var shallow = Branch("a", forkEpoch: 1, tipEpoch: 2);
        var deep = Branch("b", forkEpoch: 1, tipEpoch: 5);

        Assert.Equal("b", BranchSelection.SelectCanonical(5, [shallow, deep], Policy)!.Id);
    }

    [Fact]
    public void AWitnessQuorumCanOutweighOneCommitOfDepth()
    {
        // The boost is exactly MaxWitnessOverrideDepth, so it settles a
        // one-commit difference and no more. That bound is the whole reason it
        // is safe to let observed traffic influence selection at all.
        var deeper = Branch("a", forkEpoch: 1, tipEpoch: 3);
        var witnessed = Branch(
            "b", forkEpoch: 1, tipEpoch: 2,
            witnesses: [new AppWitness(2, Key(1)), new AppWitness(2, Key(2))]);

        Assert.True(BranchSelection.WitnessQuorumMet(witnessed.AppWitnesses, Policy));

        // Equal effective depth (3 vs 2+1), so quorum decides it.
        var trace = BranchSelectionAudit.SelectCanonicalTraced(3, [deeper, witnessed], Policy);

        Assert.Equal("b", trace.SelectedBranchId);
        Assert.Equal(
            "witness_quorum_met",
            trace.RuleTrace.Single(r => r.Decisive).RuleName);
    }

    [Fact]
    public void AWitnessQuorumCannotOutweighTwo()
    {
        var deeper = Branch("a", forkEpoch: 1, tipEpoch: 4);
        var witnessed = Branch(
            "b", forkEpoch: 1, tipEpoch: 2,
            witnesses: [new AppWitness(2, Key(1)), new AppWitness(2, Key(2))]);

        Assert.Equal("a", BranchSelection.SelectCanonical(4, [deeper, witnessed], Policy)!.Id);
    }

    [Fact]
    public void TheDecisiveRuleIsTheCommitterWhenEverythingElseTies()
    {
        // The shape upstream's convergence-committer-selected vector asserts:
        // identical depth, no quorum either side, decided on tip_committer.
        var a = Branch("a", committer: 0x01, digest: 0x10);
        var b = Branch("b", committer: 0x02, digest: 0x10);

        var trace = BranchSelectionAudit.SelectCanonicalTraced(2, [a, b], Policy);

        RuleEvaluation decisive = trace.RuleTrace.Single(r => r.Decisive);

        Assert.Equal("tip_committer", decisive.RuleName);
        Assert.False(trace.Candidates.All(c => c.Score.WitnessQuorumMet));

        // Lower committer wins: the last three rules compare reversed upstream,
        // and reproducing them the intuitive way round would agree everywhere
        // except on the ties a tie-break exists for.
        Assert.Equal("a", trace.SelectedBranchId);
    }

    [Fact]
    public void TheDigestSettlesWhatTheCommitterCannot()
    {
        var a = Branch("a", committer: 0x01, digest: 0x20);
        var b = Branch("b", committer: 0x01, digest: 0x10);

        var trace = BranchSelectionAudit.SelectCanonicalTraced(2, [a, b], Policy);

        Assert.Equal("tip_digest", trace.RuleTrace.Single(r => r.Decisive).RuleName);
        Assert.Equal("b", trace.SelectedBranchId);
    }

    [Fact]
    public void AnOrdinaryTipOutranksAPrivilegedOneAtEqualDepth()
    {
        // Counter-intuitive, and upstream's: tip_priority compares reversed like
        // the other two tie-breaks. Pinned because getting it backwards is
        // invisible until two members disagree on a real fork.
        var privileged = Branch("a", priority: CommitOrderingPriority.Privileged);
        var ordinary = Branch("b", priority: CommitOrderingPriority.Ordinary);

        var trace = BranchSelectionAudit.SelectCanonicalTraced(
            2, [privileged, ordinary], Policy);

        Assert.Equal("tip_priority", trace.RuleTrace.Single(r => r.Decisive).RuleName);
        Assert.Equal("b", trace.SelectedBranchId);
    }

    [Fact]
    public void TheRulesAreTriedInTheDocumentedOrder()
    {
        var trace = BranchSelectionAudit.SelectCanonicalTraced(
            2, [Branch("a", committer: 0x01), Branch("b", committer: 0x02)], Policy);

        Assert.Equal(BranchSelectionAudit.RuleNames, trace.RuleTrace.Select(r => r.RuleName));
    }

    // ---- Witness counting ----

    [Fact]
    public void OneMemberTalkingTwiceIsOneWitness()
    {
        // Otherwise a branch could be made to look broadly observed by a single
        // member talking to themselves, which is exactly the influence the
        // witness score is supposed to measure the absence of.
        var repeated = new[] { new AppWitness(2, Key(1)), new AppWitness(2, Key(1)) };

        Assert.False(BranchSelection.WitnessQuorumMet(repeated, Policy));
        Assert.Equal(1, BranchSelection.AppWitnessScore(repeated, Policy));
    }

    [Fact]
    public void OneBusyEpochCannotOutscoreSeveralQuietOnes()
    {
        // Each epoch contributes at most the quorum, so the score measures how
        // broadly a branch was seen rather than how much traffic it carried.
        var crowded = new[]
        {
            new AppWitness(2, Key(1)), new AppWitness(2, Key(2)),
            new AppWitness(2, Key(3)), new AppWitness(2, Key(4)),
        };

        Assert.Equal(2, BranchSelection.AppWitnessScore(crowded, Policy));
    }

    [Fact]
    public void AQuorumOfZeroMeansUnachievableRatherThanAutomatic()
    {
        // Reading it the other way would hand the depth boost to every branch
        // and stop the rule being a tie-break at all.
        var policy = ConvergencePolicy.V1 with { WitnessQuorumSendersPerEpoch = 0 };

        Assert.False(
            BranchSelection.WitnessQuorumMet([new AppWitness(2, Key(1))], policy));

        var byEpochs = ConvergencePolicy.V1 with { WitnessQuorumEpochs = 0 };

        Assert.False(
            BranchSelection.WitnessQuorumMet([new AppWitness(2, Key(1))], byEpochs));
    }

    [Fact]
    public void WitnessesInDifferentEpochsDoNotCombineIntoAQuorum()
    {
        // Quorum is per epoch: two members speaking in different epochs is not
        // the same evidence as two speaking in one.
        var spread = new[] { new AppWitness(2, Key(1)), new AppWitness(3, Key(2)) };

        Assert.False(BranchSelection.WitnessQuorumMet(spread, Policy));
    }

    // ---- Order independence ----

    [Fact]
    public void TheResultDoesNotDependOnCandidateOrder()
    {
        // The property the whole thing rests on. A selector that is even
        // slightly order-dependent gives a group that never reconverges,
        // because each member keeps the branch it happened to see first.
        var candidates = new[]
        {
            Branch("a", tipEpoch: 3, committer: 0x03, digest: 0x30),
            Branch("b", tipEpoch: 3, committer: 0x01, digest: 0x10),
            Branch("c", tipEpoch: 3, committer: 0x02, digest: 0x20),
        };

        string? expected = BranchSelection.SelectCanonical(3, candidates, Policy)!.Id;

        foreach (var permutation in Permutations(candidates))
        {
            Assert.Equal(
                expected, BranchSelection.SelectCanonical(3, permutation, Policy)!.Id);
        }
    }

    [Fact]
    public void TheTraceDoesNotDependOnCandidateOrderEither()
    {
        // The trace is compared against upstream's vectors, so it has to be a
        // pure function of the candidate set rather than merely agreeing about
        // the winner.
        var candidates = new[]
        {
            Branch("a", tipEpoch: 3, committer: 0x03),
            Branch("b", tipEpoch: 3, committer: 0x01),
            Branch("c", tipEpoch: 3, committer: 0x02),
        };

        BranchSelectionTrace expected =
            BranchSelectionAudit.SelectCanonicalTraced(3, candidates, Policy);

        foreach (var permutation in Permutations(candidates))
        {
            BranchSelectionTrace actual =
                BranchSelectionAudit.SelectCanonicalTraced(3, permutation, Policy);

            Assert.Equal(expected.SelectedBranchId, actual.SelectedBranchId);
            Assert.Equal(expected.LosingBranchIds, actual.LosingBranchIds);
            Assert.Equal(
                expected.RuleTrace.Select(r => (r.RuleName, r.Decisive)),
                actual.RuleTrace.Select(r => (r.RuleName, r.Decisive)));
            Assert.Equal(
                expected.Candidates.Select(c => c.BranchId),
                actual.Candidates.Select(c => c.BranchId));
        }
    }

    [Fact]
    public void WitnessOrderDoesNotChangeAScore()
    {
        var forward = new[]
        {
            new AppWitness(2, Key(1)), new AppWitness(2, Key(2)), new AppWitness(3, Key(1)),
        };

        Assert.Equal(
            BranchSelection.AppWitnessScore(forward, Policy),
            BranchSelection.AppWitnessScore([.. forward.Reverse()], Policy));
    }

    // ---- The trace itself ----

    [Fact]
    public void ASingleCandidateHasNoDecisiveRule()
    {
        // Naming the first rule would claim a comparison that never happened.
        var trace = BranchSelectionAudit.SelectCanonicalTraced(2, [Branch("a")], Policy);

        Assert.Equal("a", trace.SelectedBranchId);
        Assert.Empty(trace.RuleTrace);
        Assert.Empty(trace.LosingBranchIds);
    }

    [Fact]
    public void AtMostOneRuleIsDecisive()
    {
        var trace = BranchSelectionAudit.SelectCanonicalTraced(
            3,
            [Branch("a", tipEpoch: 3), Branch("b", tipEpoch: 2, committer: 0x01)],
            Policy);

        Assert.Single(trace.RuleTrace, r => r.Decisive);
        Assert.Equal("effective_commit_depth", trace.RuleTrace.First(r => r.Decisive).RuleName);
    }

    [Fact]
    public void EveryLoserIsNamed()
    {
        var trace = BranchSelectionAudit.SelectCanonicalTraced(
            3,
            [Branch("a", tipEpoch: 3), Branch("b", tipEpoch: 2), Branch("c", tipEpoch: 2)],
            Policy);

        Assert.Equal("a", trace.SelectedBranchId);
        Assert.Equal(["b", "c"], trace.LosingBranchIds);
    }

    private static IEnumerable<BranchCandidate[]> Permutations(BranchCandidate[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (int i = 0; i < items.Length; i++)
        {
            BranchCandidate head = items[i];
            BranchCandidate[] rest = [.. items[..i], .. items[(i + 1)..]];

            foreach (BranchCandidate[] tail in Permutations(rest))
                yield return [head, .. tail];
        }
    }
}
