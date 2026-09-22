using Scramble.Marmot.Engine.Convergence;
using Xunit;

namespace Scramble.Marmot.Tests.Convergence;

/// <summary>
/// Upstream's convergence scenarios, run against our engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the only convergence tests here that we did not write.</b>
/// Everything else can confirm at most that the code does what its author
/// expected, which is worth very little for a consensus rule: the failure mode
/// is a rule reproduced backwards from a correct reading of upstream's source,
/// and such a rule looks right, passes every test its author thinks to write,
/// and diverges only on the cases it exists for.
/// </para>
/// <para>
/// The vectors are copied verbatim from <c>mdk@wn-agent-v0.9.17</c>. <b>One that
/// starts failing after a pin bump is the signal it exists to give — refresh it
/// from the new tag deliberately, never edit one to make it pass.</b>
/// </para>
/// </remarks>
[Trait("Category", "ConformanceVector")]
public class ConvergenceScenarioTests
{
    [Theory]
    [InlineData("convergence-committer-selected.v1.json")]
    [InlineData("convergence-witness-selected.v1.json")]
    public void TheVectorIsTheOneUpstreamWrote(string fileName)
    {
        // Cheap, and it catches the thing that would quietly hollow out
        // everything below: a vector edited to fit rather than a build fixed to
        // pass. The version is pinned to the peer the interop suite runs.
        ScenarioVector vector = ScenarioVector.Load(fileName);

        Assert.Equal("0.9.17", vector.ConformanceVersion);
        Assert.NotEmpty(vector.Steps);
        Assert.NotEmpty(vector.ExpectedOutcomes);
        Assert.Equal(
            ["alice", "bob", "carol", "david", "eve"], vector.Clients);
    }

    [Theory]
    [InlineData("convergence-committer-selected.v1.json")]
    [InlineData("convergence-witness-selected.v1.json")]
    public async Task TheScenarioReachesTheOutcomeUpstreamRecorded(string fileName)
    {
        ScenarioVector vector = ScenarioVector.Load(fileName);
        var runner = new ScenarioRunner();

        await runner.RunAsync(vector);

        foreach (ExpectedOutcome outcome in vector.ExpectedOutcomes)
        {
            switch (outcome.Type)
            {
                case "convergence_decision":
                    AssertConvergence(runner, outcome);
                    break;

                case "client_state":
                    AssertClientState(runner, outcome);
                    break;

                case "pending_resolution":
                    // Resolution is driven by acknowledge_outbound, and a
                    // scenario that reached its end applied every one it named:
                    // an unresolved publication throws there rather than
                    // surviving to be checked here.
                    break;

                default:
                    Assert.Fail(
                        $"The vector asserts '{outcome.Type}', which this harness does not "
                        + $"check. Skipping it silently would report a pass for a claim "
                        + $"nothing verified.\n{runner.Log}");
                    break;
            }
        }
    }

    private static void AssertConvergence(ScenarioRunner runner, ExpectedOutcome outcome)
    {
        ScenarioClient client = runner.Client(outcome.String("client"));

        Assert.True(
            client.LastDecision is not null,
            $"{client.Name} never made a convergence decision.\n{runner.Log}");

        if (outcome.UInt64OrNull("selected_tip_epoch") is { } tipEpoch)
        {
            Assert.True(
                client.LastSelectedTipEpoch == tipEpoch,
                $"{client.Name} selected a branch tipped at "
                + $"{client.LastSelectedTipEpoch}, not {tipEpoch}.\n{runner.Log}");
        }

        if (outcome.StringOrNull("decisive_rule") is { } decisiveRule)
        {
            RuleEvaluation? decisive =
                client.LastDecision!.RuleTrace.FirstOrDefault(r => r.Decisive);

            Assert.True(
                decisive is not null,
                $"{client.Name} decided without any rule being decisive, but the vector "
                + $"expects '{decisiveRule}'.\n{runner.Log}");

            Assert.True(
                string.Equals(decisive!.RuleName, decisiveRule, StringComparison.Ordinal),
                $"{client.Name} decided on '{decisive.RuleName}', not "
                + $"'{decisiveRule}'.\n{runner.Log}");
        }

        if (outcome.BoolOrNull("witness_quorum_met") is { } quorum)
        {
            BranchSelectionTrace trace = client.LastDecision!;
            CandidateEvaluation selected = trace.Candidates.Single(
                c => string.Equals(c.BranchId, trace.SelectedBranchId, StringComparison.Ordinal));

            Assert.True(
                selected.Score.WitnessQuorumMet == quorum,
                $"{client.Name} selected a branch whose witness quorum was "
                + $"{selected.Score.WitnessQuorumMet}, not {quorum}.\n{runner.Log}");
        }

        if (outcome.UInt64OrNull("min_app_witness_score") is { } minScore)
        {
            BranchSelectionTrace trace = client.LastDecision!;
            CandidateEvaluation selected = trace.Candidates.Single(
                c => string.Equals(c.BranchId, trace.SelectedBranchId, StringComparison.Ordinal));

            Assert.True(
                (ulong)selected.Score.AppWitnessScore >= minScore,
                $"{client.Name} selected a branch scoring "
                + $"{selected.Score.AppWitnessScore}, below the required "
                + $"{minScore}.\n{runner.Log}");
        }
    }

    private static void AssertClientState(ScenarioRunner runner, ExpectedOutcome outcome)
    {
        ScenarioClient client = runner.Client(outcome.String("client"));

        Assert.True(client.Group is not null, $"{client.Name} has no group.\n{runner.Log}");

        if (outcome.UInt64OrNull("epoch") is { } epoch)
        {
            Assert.True(
                client.Group!.Epoch == epoch,
                $"{client.Name} is at epoch {client.Group.Epoch}, not {epoch}.\n{runner.Log}");
        }

        if (outcome.UInt64OrNull("member_count") is { } memberCount)
        {
            int actual = client.Group!.GetMembers().Count;

            Assert.True(
                (ulong)actual == memberCount,
                $"{client.Name} sees {actual} members, not {memberCount}.\n{runner.Log}");
        }

        // Order is not asserted: the vector records what was received, and a
        // transport with no ordering guarantee may deliver them either way
        // round. Membership is the claim; sequence would be a claim about the
        // harness's delivery loop.
        var expected = outcome.Strings("received_payloads");

        Assert.True(
            expected.OrderBy(p => p, StringComparer.Ordinal)
                .SequenceEqual(client.ReceivedPayloads.OrderBy(p => p, StringComparer.Ordinal)),
            $"{client.Name} received [{string.Join(", ", client.ReceivedPayloads)}], "
            + $"not [{string.Join(", ", expected)}].\n{runner.Log}");
    }

    [Theory]
    [InlineData("convergence-committer-selected.v1.json")]
    [InlineData("convergence-witness-selected.v1.json")]
    public async Task EveryoneStillInTheGroupAgreesOnItsHistory(string fileName)
    {
        // Ours, not upstream's, and it is the property convergence exists for.
        // The vectors stop as soon as they have shown what they came to show,
        // which leaves members holding commits they never looked at -- so they
        // assert where *carol* landed and never that anybody agrees with her.
        //
        // The distinction this draws is the whole point. A member invited on
        // the branch that loses is not a divergence: that branch never
        // happened, so they were never added, and they are left holding a group
        // the group does not have. They have to be re-invited. A member who IS
        // in the surviving history and disagrees about it is the real failure --
        // an order-dependent selector, or a materializer that builds a
        // different branch depending on who is asking.
        ScenarioVector vector = ScenarioVector.Load(fileName);
        var runner = new ScenarioRunner();

        await runner.RunAsync(vector);

        var settled = runner.SettleAll();
        string report = string.Join(
            NEWLINE, settled.Select(s => $"  {s.Name}: epoch {s.Epoch} members {s.Members}"));

        // The surviving history is the one most members hold. With a single
        // fork at most one invitee can be stranded, so this is never a tie.
        var canonical = settled
            .GroupBy(s => s.Members, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .First();

        Assert.True(
            canonical.Count() > settled.Count - canonical.Count(),
            $"No history is held by a majority, so there is no convergence to speak of."
            + NEWLINE + report + NEWLINE + runner.Log);

        var members = canonical.Key.Split(',');

        foreach (var client in settled.Where(s => !canonical.Contains(s)))
        {
            ScenarioClient stranded = runner.Client(client.Name);

            Assert.True(
                !members.Contains(stranded.Hex, StringComparer.Ordinal),
                $"{client.Name} disagrees about a history it is a member of, which is a "
                + $"divergence rather than a stranded invite."
                + NEWLINE + report + NEWLINE + runner.Log);
        }

        // And everyone in the surviving history agrees about its epoch too.
        Assert.True(
            canonical.Select(s => s.Epoch).Distinct().Count() == 1,
            "Members of one history disagree about its epoch." + NEWLINE + report);
    }

    private const string NEWLINE = "\n";
}
