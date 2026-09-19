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
    /// What the authorization rules need to read off a commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inline and referenced proposals are both included, and deliberately: the
    /// rule is about what a commit <i>does</i>, and a proposal carried inline
    /// does exactly what the same proposal cited by reference would. Counting
    /// only the references would let any commit become ordinary by inlining
    /// what it applies.
    /// </para>
    /// <para>
    /// <b>One builder, for ordering and for admission both.</b>
    /// <see cref="Messages.CommitAdmission"/> decides whether to accept a commit
    /// from the same view this decides how to rank it. A second builder would be
    /// a second opinion about what a commit contains, and the two disagreeing
    /// means a commit refused by one rule and ranked by the other — so the
    /// richer fields are filled here rather than resolved again elsewhere.
    /// </para>
    /// </remarks>
    /// <param name="senderLeafIndex">
    /// The leaf that framed the commit, which is the proposer of every inline
    /// proposal it carries. Null when the caller is only ranking — ordering
    /// reads neither the proposer nor the operation — and passing null leaves
    /// those fields unresolved rather than guessed.
    /// </param>
    internal static StagedCommitView ViewOf(
        MlsGroup group, Commit commit, uint? senderLeafIndex = null)
    {
        var proposals = new List<StagedProposal>(commit.Proposals.Length);

        // A proposer is only resolved when the caller asked for the rich view.
        // Which leaf that is differs by entry shape -- an inline proposal is
        // proposed by whoever framed the commit, a referenced one by whoever
        // sent it -- so the argument gates the work and the cache supplies the
        // leaf.
        bool resolveProposers = senderLeafIndex is not null;

        foreach (ProposalOrRef entry in commit.Proposals)
        {
            switch (entry)
            {
                case InlineProposal inline:
                    proposals.Add(new StagedProposal(
                        KindOf(inline.Proposal.ProposalType),
                        resolveProposers ? IdentityOf(group, senderLeafIndex!.Value) : null,
                        UpdateOf(inline.Proposal)));
                    break;

                case ProposalReference reference when Resolve(group, reference) is { } cached:
                    proposals.Add(new StagedProposal(
                        KindOf(cached.Proposal.ProposalType),
                        resolveProposers ? IdentityOf(group, cached.SenderLeafIndex) : null,
                        UpdateOf(cached.Proposal)));
                    break;

                default:
                    // A reference that resolves to nothing, or an entry shape
                    // this build does not know. Other is admin-requiring by the
                    // rule's fail-closed design, and the proposer stays null --
                    // which the rules that read it treat as "not evidence of
                    // anything" rather than as an absent proposer.
                    proposals.Add(new StagedProposal(CommitProposalKind.Other));
                    break;
            }
        }

        return new StagedCommitView(proposals, commit.Path is not null);
    }

    /// <summary>
    /// The operation an <c>AppDataUpdate</c> proposal carries, or null.
    /// </summary>
    /// <remarks>
    /// Read off the proposal rather than left for a caller to fill, because
    /// <see cref="AppComponentIntegrity"/> fails closed on a proposal classified
    /// as an <c>AppDataUpdate</c> whose operation is missing — and it is right
    /// to: an operation nobody read is a dictionary change nobody accounted for.
    /// </remarks>
    private static AppDataUpdate? UpdateOf(Proposal proposal) =>
        proposal is not AppDataUpdateProposal update
            ? null
            : update.Operation == AppDataUpdateOperationType.Remove
                ? AppDataUpdate.Remove(update.ComponentId)
                : AppDataUpdate.Update(update.ComponentId, update.Data);

    /// <summary>
    /// The account identity at a leaf, or null when no member holds it.
    /// </summary>
    /// <remarks>
    /// Null rather than a throw: a commit naming a leaf nobody occupies will not
    /// apply, and the rules that read this field fail closed on null anyway. A
    /// throw here would turn a classification into a crash on the ingest path.
    /// </remarks>
    private static byte[]? IdentityOf(MlsGroup group, uint leafIndex)
    {
        foreach ((uint index, byte[] identity) in group.GetMembers())
        {
            if (index == leafIndex)
                return identity;
        }

        return null;
    }

    private static MlsGroup.CachedProposal? Resolve(MlsGroup group, ProposalReference reference)
    {
        foreach (MlsGroup.CachedProposal cached in group.CachedProposals)
        {
            if (cached.Reference.AsSpan().SequenceEqual(reference.Reference))
                return cached;
        }

        return null;
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
