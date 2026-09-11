using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;

namespace Scramble.Marmot.Engine.Convergence;

/// <summary>
/// The ordering class of a commit we did not make.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CommitAuthorization.OrderingPriority"/> is the rule; this is what
/// feeds it. It is a small piece with a large consequence: priority outranks
/// both the committer and the digest in <see cref="BranchSelection"/>, so a
/// member that classifies every candidate the same way has not made the
/// tie-break conservative — it has deleted a rule the group agrees on and
/// pushed every decision down to the one below it. Two members doing that
/// differently pick different branches from identical candidates.
/// </para>
/// <para>
/// <b>Read before the commit is applied, and that is forced rather than
/// chosen.</b> A commit may cite proposals by reference, and a proposal is only
/// cached for the epoch it was framed against — applying the commit clears the
/// cache that would have said what those references were. So the classification
/// has to happen on the way in.
/// </para>
/// <para>
/// A reference that resolves to nothing is classified as
/// <see cref="CommitProposalKind.Other"/>, which fails closed to
/// <see cref="CommitOrderingPriority.Privileged"/>. That case cannot survive:
/// a commit whose proposals we cannot resolve is a commit that will not apply,
/// so the branch is refused before its priority is ever compared. The
/// fail-closed answer exists so the gap between the two steps cannot be read as
/// "ordinary".
/// </para>
/// </remarks>
public static class CommitOrdering
{
    /// <summary>
    /// Classifies a commit for branch ordering.
    /// </summary>
    /// <param name="group">
    /// The group as it stands <i>before</i> the commit is applied, whose
    /// proposal cache resolves the commit's references.
    /// </param>
    /// <param name="commit">The commit to classify.</param>
    public static CommitOrderingPriority PriorityOf(MlsGroup group, Commit commit)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(commit);

        return CommitAuthorization.OrderingPriority(ViewOf(group, commit));
    }

    /// <summary>
    /// Classifies the commit carried by a handshake message, if it carries one.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> for a handshake that is not a commit, so a caller
    /// that meant to classify a proposal gets nothing rather than a default.
    /// </remarks>
    public static CommitOrderingPriority? PriorityOf(MlsGroup group, PublicMessage handshake)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(handshake);

        if (handshake.Content.ContentType != ContentType.Commit)
            return null;

        return PriorityOf(
            group,
            Commit.ReadFrom(new DotnetMls.Codec.TlsReader(handshake.Content.Content)));
    }

    /// <summary>
    /// What the authorization rule needs to read off a commit.
    /// </summary>
    /// <remarks>
    /// Inline and referenced proposals are both included, and deliberately: the
    /// rule is about what a commit <i>does</i>, and a proposal carried inline
    /// does exactly what the same proposal cited by reference would. Counting
    /// only the references would let any commit become ordinary by inlining
    /// what it applies.
    /// </remarks>
    private static StagedCommitView ViewOf(MlsGroup group, Commit commit)
    {
        var proposals = new List<StagedProposal>(commit.Proposals.Length);

        foreach (ProposalOrRef entry in commit.Proposals)
        {
            proposals.Add(
                new StagedProposal(
                    entry switch
                    {
                        InlineProposal inline => KindOf(inline.Proposal.ProposalType),
                        ProposalReference reference => KindOf(group, reference),
                        _ => CommitProposalKind.Other,
                    }));
        }

        return new StagedCommitView(proposals, commit.Path is not null);
    }

    private static CommitProposalKind KindOf(MlsGroup group, ProposalReference reference)
    {
        foreach (MlsGroup.CachedProposal cached in group.CachedProposals)
        {
            if (cached.Reference.AsSpan().SequenceEqual(reference.Reference))
                return KindOf(cached.Proposal.ProposalType);
        }

        return CommitProposalKind.Other;
    }

    private static CommitProposalKind KindOf(ProposalType type) => type switch
    {
        ProposalType.Add => CommitProposalKind.Add,
        ProposalType.Update => CommitProposalKind.Update,
        ProposalType.Remove => CommitProposalKind.Remove,
        ProposalType.PreSharedKey => CommitProposalKind.PreSharedKey,
        ProposalType.ReInit => CommitProposalKind.ReInit,
        ProposalType.ExternalInit => CommitProposalKind.ExternalInit,
        ProposalType.GroupContextExtensions => CommitProposalKind.GroupContextExtensions,
        ProposalType.AppDataUpdate => CommitProposalKind.AppDataUpdate,
        ProposalType.SelfRemove => CommitProposalKind.SelfRemove,

        // Not a hole: anything unrecognised is admin-requiring by the rule's own
        // fail-closed design, so a proposal type registered after this was
        // written ranks as privileged rather than as ordinary.
        _ => CommitProposalKind.Other,
    };
}
