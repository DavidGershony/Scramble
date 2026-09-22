using Scramble.Marmot.AppComponents;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Who may commit what, and how a same-epoch race is ordered.
/// </summary>
/// <remarks>
/// The classification is inverted on purpose — it recognises the two shapes a
/// non-admin may commit and treats everything else as admin-requiring — so the
/// tests that matter most are the ones proving an <i>unrecognised</i> shape
/// lands on the restrictive side.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class CommitAuthorizationTests
{
    private static byte[] Key(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static StagedCommitView Commit(
        bool hasUpdatePath = true, params StagedProposal[] proposals) =>
        new(proposals, hasUpdatePath);

    private static StagedProposal Proposal(CommitProposalKind kind) => new(kind);

    // -- The two permitted non-admin shapes --

    [Fact]
    public void ASelfUpdateNeedsNoAdmin()
    {
        // Shape (a): the committer's own path and nothing else.
        var commit = Commit(hasUpdatePath: true);

        Assert.True(CommitAuthorization.IsAllowedNonAdminCommit(commit));
        Assert.False(CommitAuthorization.RequiresAdmin(commit));
    }

    [Fact]
    public void ASelfRemoveNeedsNoAdmin()
    {
        // Shape (b). A non-admin member may always leave under its own steam.
        var commit = Commit(true, Proposal(CommitProposalKind.SelfRemove));

        Assert.True(CommitAuthorization.IsAllowedNonAdminCommit(commit));
    }

    [Fact]
    public void SeveralSelfRemovesTogetherStillNeedNoAdmin()
    {
        var commit = Commit(
            true,
            Proposal(CommitProposalKind.SelfRemove),
            Proposal(CommitProposalKind.SelfRemove));

        Assert.True(CommitAuthorization.IsAllowedNonAdminCommit(commit));
    }

    [Fact]
    public void ASelfRemoveCommitWithoutAnUpdatePathIsStillPermitted()
    {
        // The path is expected on this shape but is not what classifies it —
        // the proposals are.
        var commit = Commit(false, Proposal(CommitProposalKind.SelfRemove));

        Assert.True(CommitAuthorization.IsAllowedNonAdminCommit(commit));
    }

    // -- Everything else --

    [Theory]
    [InlineData(CommitProposalKind.Add)]
    [InlineData(CommitProposalKind.Remove)]
    [InlineData(CommitProposalKind.Update)]
    [InlineData(CommitProposalKind.PreSharedKey)]
    [InlineData(CommitProposalKind.ReInit)]
    [InlineData(CommitProposalKind.ExternalInit)]
    [InlineData(CommitProposalKind.GroupContextExtensions)]
    [InlineData(CommitProposalKind.AppDataUpdate)]
    [InlineData(CommitProposalKind.AppEphemeral)]
    [InlineData(CommitProposalKind.Other)]
    public void EveryOtherProposalRequiresAnAdmin(CommitProposalKind kind)
    {
        Assert.True(CommitAuthorization.RequiresAdmin(Commit(true, Proposal(kind))));
    }

    [Fact]
    public void AnUnrecognisedProposalLandsOnTheRestrictiveSide()
    {
        // The property the inverted classification exists for: registering a
        // new component must not silently make its updates ungoverned.
        Assert.True(CommitAuthorization.RequiresAdmin(
            Commit(true, Proposal(CommitProposalKind.Other))));
    }

    [Fact]
    public void ASelfRemoveMixedWithAnythingElseRequiresAnAdmin()
    {
        // Neither shape holds once a foreign proposal joins the commit, so
        // smuggling an Add alongside a SelfRemove does not inherit the
        // SelfRemove's permission.
        var commit = Commit(
            true,
            Proposal(CommitProposalKind.SelfRemove),
            Proposal(CommitProposalKind.Add));

        Assert.True(CommitAuthorization.RequiresAdmin(commit));
    }

    [Fact]
    public void AnEmptyCommitWithNoUpdatePathRequiresAnAdmin()
    {
        // What the exclusive-or is actually for. The two shapes cannot overlap,
        // so the xor is not guarding against both being true — it rejects the
        // case where neither is: a commit that advances the epoch while doing
        // nothing at all.
        Assert.True(CommitAuthorization.RequiresAdmin(Commit(hasUpdatePath: false)));
    }

    // -- Ordering priority --

    [Fact]
    public void AnAdminRequiringCommitOutranksAnOrdinaryOne()
    {
        // So a governance change is not lost to a routine self-update that
        // happened to race it in the same epoch.
        Assert.Equal(
            CommitOrderingPriority.Privileged,
            CommitAuthorization.OrderingPriority(Commit(true, Proposal(CommitProposalKind.Add))));

        Assert.Equal(
            CommitOrderingPriority.Ordinary,
            CommitAuthorization.OrderingPriority(Commit(hasUpdatePath: true)));
    }

    [Fact]
    public void PriorityTracksTheAdminRequirementExactly()
    {
        foreach (var kind in Enum.GetValues<CommitProposalKind>())
        {
            var commit = Commit(true, Proposal(kind));
            var expected = CommitAuthorization.RequiresAdmin(commit)
                ? CommitOrderingPriority.Privileged
                : CommitOrderingPriority.Ordinary;

            Assert.Equal(expected, CommitAuthorization.OrderingPriority(commit));
        }
    }

    // -- Admins may not self-remove --

    private static readonly AdminPolicy Policy = AdminPolicy.Create([Key(0x01), Key(0x02)]);

    [Fact]
    public void ANonAdminMaySelfRemove()
    {
        var commit = Commit(true, new StagedProposal(CommitProposalKind.SelfRemove, Key(0x09)));

        CommitAuthorization.RequireNoAdminSelfRemove(commit, Policy);
    }

    [Fact]
    public void AnAdminMayNotSelfRemove()
    {
        // It must first commit an admin-policy update removing itself, which is
        // valid only while another active admin remains. That ordering is what
        // stops a group losing its last admin in one step — something v1 has no
        // way to recover from.
        var commit = Commit(true, new StagedProposal(CommitProposalKind.SelfRemove, Key(0x02)));

        var ex = Assert.Throws<AppComponentException>(
            () => CommitAuthorization.RequireNoAdminSelfRemove(commit, Policy));
        Assert.Contains("admin-policy update", ex.Message);
    }

    [Fact]
    public void ASelfRemoveWithAnUnresolvableSenderIsRejected()
    {
        // Fails closed: "we could not tell who sent this" is not evidence that
        // they were not an admin.
        var commit = Commit(true, new StagedProposal(CommitProposalKind.SelfRemove, null));

        Assert.Throws<AppComponentException>(
            () => CommitAuthorization.RequireNoAdminSelfRemove(commit, Policy));
    }

    [Fact]
    public void TheGuardComparesRawBytesRatherThanValidatedKeys()
    {
        // The admin list is 32 raw bytes and is never checked as a curve point.
        // A key that is not a valid secp256k1 x-coordinate but matches a listed
        // admin byte for byte must still be caught — resolving the sender
        // through a validating path instead would yield nothing here and skip
        // the guard, letting exactly this admin self-remove.
        var policy = AdminPolicy.Create([Enumerable.Repeat((byte)0xff, 32).ToArray()]);
        var commit = Commit(
            true,
            new StagedProposal(
                CommitProposalKind.SelfRemove, Enumerable.Repeat((byte)0xff, 32).ToArray()));

        Assert.Throws<AppComponentException>(
            () => CommitAuthorization.RequireNoAdminSelfRemove(commit, policy));
    }

    [Fact]
    public void OneAdminSelfRemoveAmongSeveralProposalsIsCaught()
    {
        var commit = Commit(
            true,
            new StagedProposal(CommitProposalKind.SelfRemove, Key(0x09)),
            new StagedProposal(CommitProposalKind.SelfRemove, Key(0x01)));

        Assert.Throws<AppComponentException>(
            () => CommitAuthorization.RequireNoAdminSelfRemove(commit, Policy));
    }

    [Fact]
    public void AGroupWithNoAdminPolicyHasNoAdminToProtect()
    {
        var commit = Commit(true, new StagedProposal(CommitProposalKind.SelfRemove, Key(0x01)));

        CommitAuthorization.RequireNoAdminSelfRemove(commit, null);
    }

    [Fact]
    public void NonSelfRemoveProposalsAreNotSubjectToTheGuard()
    {
        // An admin removing someone else is ordinary admin business.
        var commit = Commit(true, new StagedProposal(CommitProposalKind.Remove, Key(0x01)));

        CommitAuthorization.RequireNoAdminSelfRemove(commit, Policy);
    }
}
