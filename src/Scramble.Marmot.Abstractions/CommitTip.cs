using Scramble.Marmot.AppComponents;

namespace Scramble.Marmot;

/// <summary>
/// The commit that produced an epoch, as branch selection sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three facts that are learned at one moment and are useless apart.</b> A
/// branch is ranked by the commit that ends it, and the last three tie-breaks
/// read this commit's class, its committer and its digest in that order. All
/// three can only be taken as the commit is applied: the class resolves against
/// a proposal cache the apply clears, and the committer is a leaf index that
/// afterwards may belong to somebody else entirely. Keeping them together is
/// what stops two of them being remembered and the third being guessed.
/// </para>
/// <para>
/// <b>The digest and the message id are the same value</b>, and deliberately:
/// both are SHA-256 over the commit's MLS bytes, so the branch a member names
/// and the record it stores cannot drift apart.
/// </para>
/// </remarks>
/// <param name="Priority">Whether the commit needed admin authority.</param>
/// <param name="Commit">Its content id, which is also its branch digest.</param>
/// <param name="Committer">The committing member's account key.</param>
public sealed record CommitTip(
    CommitOrderingPriority Priority,
    MessageId Commit,
    byte[] Committer)
{
    /// <summary>The branch identifier this tip ends — its digest in lowercase hex.</summary>
    public string BranchId => Commit.ToString();
}
