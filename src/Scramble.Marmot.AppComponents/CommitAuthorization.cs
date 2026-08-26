namespace Scramble.Marmot.AppComponents;

/// <summary>
/// The kinds of by-reference proposal a commit can carry.
/// </summary>
/// <remarks>
/// <see cref="Other"/> covers anything unrecognised, including custom proposal
/// types. It exists so an unknown proposal is <i>classifiable</i> rather than
/// unrepresentable — and everything below treats it as admin-requiring, which
/// is the safe direction.
/// </remarks>
public enum CommitProposalKind
{
    Add,
    Remove,
    Update,
    PreSharedKey,
    ReInit,
    ExternalInit,
    GroupContextExtensions,
    AppDataUpdate,
    AppEphemeral,
    SelfRemove,
    Other,
}

/// <summary>One by-reference proposal in a staged commit.</summary>
/// <param name="SenderAccountKey">
/// The proposer's MLS-authenticated account identity, or null when it could not
/// be resolved to a current member.
/// </param>
/// <param name="Update">
/// The operation, for an <see cref="CommitProposalKind.AppDataUpdate"/>
/// proposal, and null for every other kind.
/// </param>
/// <remarks>
/// The operation hangs off the proposal rather than sitting in a second list
/// beside it, so there is no way to fill one and forget the other — a commit
/// whose component changes were read but whose proposals were not would look
/// like a commit that changed state nobody proposed.
/// </remarks>
public sealed record StagedProposal(
    CommitProposalKind Kind,
    byte[]? SenderAccountKey = null,
    AppDataUpdate? Update = null);

/// <summary>
/// What the engine reads off a staged commit in order to authorize it.
/// </summary>
/// <param name="HasUpdatePathLeaf">
/// Whether the commit carries the committer's own update-path leaf node.
/// </param>
public sealed record StagedCommitView(
    IReadOnlyList<StagedProposal> Proposals,
    bool HasUpdatePathLeaf);

/// <summary>Branch-ordering class for same-epoch fork recovery.</summary>
public enum CommitOrderingPriority
{
    /// <summary>A commit any member may make.</summary>
    Ordinary,

    /// <summary>A commit only an active admin may make.</summary>
    /// <remarks>
    /// Ranks above <see cref="Ordinary"/> when two commits race in one epoch,
    /// so a governance change is not lost to a routine self-update that
    /// happened to arrive alongside it.
    /// </remarks>
    Privileged,
}

/// <summary>
/// Who is allowed to commit what.
/// </summary>
/// <remarks>
/// <para>
/// Every v1 group-level component update requires an active admin to commit,
/// as do invites, removing another member, changing the required-component
/// list, and changing <c>required_capabilities</c>. The rule follows the
/// component <i>class</i> rather than an enumerated list, so registering a new
/// component does not silently make its updates ungoverned.
/// </para>
/// <para>
/// The classification is therefore inverted and <b>fails closed</b>: rather
/// than listing what needs an admin, it recognises the two narrow shapes a
/// non-admin may commit and treats everything else — including any shape it
/// does not recognise — as admin-requiring.
/// </para>
/// </remarks>
public static class CommitAuthorization
{
    /// <summary>
    /// Whether only an active admin may commit this.
    /// </summary>
    public static bool RequiresAdmin(StagedCommitView commit) => !IsAllowedNonAdminCommit(commit);

    /// <summary>The branch-ordering class for same-epoch fork recovery.</summary>
    public static CommitOrderingPriority OrderingPriority(StagedCommitView commit) =>
        RequiresAdmin(commit) ? CommitOrderingPriority.Privileged : CommitOrderingPriority.Ordinary;

    /// <summary>
    /// Whether this is one of the two shapes a non-admin member may commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The update path is never a disqualifier. MLS requires a fresh path on a
    /// Remove or SelfRemove commit, and a self-update <i>is</i> a path — so
    /// classification is driven entirely by the by-reference proposals, and the
    /// path only distinguishes a real self-update from an empty commit.
    /// </para>
    /// <para>
    /// The two shapes are combined with exclusive-or rather than or. They are
    /// already mutually exclusive by proposal count, so the xor is not about
    /// them overlapping — it is what rejects the case where <i>neither</i>
    /// holds, namely a commit with no proposals and no update path. Such a
    /// no-op advances the epoch while doing nothing, and it is not one of the
    /// two things a non-admin is permitted to do.
    /// </para>
    /// </remarks>
    public static bool IsAllowedNonAdminCommit(StagedCommitView commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        int proposalCount = 0;
        int selfRemoveCount = 0;

        foreach (StagedProposal proposal in commit.Proposals)
        {
            proposalCount++;

            // Any by-reference proposal other than SelfRemove disqualifies both
            // shapes outright — Add, Remove, Update, PreSharedKey, ReInit,
            // ExternalInit, GroupContextExtensions, AppDataUpdate, AppEphemeral,
            // and anything unrecognised.
            if (proposal.Kind != CommitProposalKind.SelfRemove)
                return false;

            selfRemoveCount++;
        }

        // Shape (a): a self-update — the committer's own path, nothing else.
        bool isSelfUpdateOnly = proposalCount == 0 && commit.HasUpdatePathLeaf;

        // Shape (b): SelfRemove only — at least one, and nothing but. The
        // committer's update path is expected here; it re-keys their own leaf.
        bool isSelfRemoveOnly = selfRemoveCount > 0 && selfRemoveCount == proposalCount;

        return isSelfUpdateOnly ^ isSelfRemoveOnly;
    }

    /// <summary>
    /// Rejects a commit carrying a SelfRemove from a listed admin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A departing admin must first commit an admin-policy update removing
    /// itself — valid only while another active admin remains — and only then
    /// self-remove. That ordering is what keeps a group from losing its last
    /// admin in a single step, which v1 has no way to recover from.
    /// </para>
    /// <para>
    /// <b>Fails closed on an unresolvable sender.</b> A SelfRemove whose
    /// proposer cannot be tied to a current member is rejected rather than
    /// waved through, because "we could not tell who sent this" is not evidence
    /// that they were not an admin.
    /// </para>
    /// <para>
    /// <b>Compares on the same basis the admin list uses.</b> The admin set is
    /// 32 raw bytes, never checked as a curve point. If this guard resolved the
    /// sender through a validating path instead, a leaf whose 32-byte identity
    /// equalled a listed admin key but failed secp256k1 validation would
    /// resolve to nothing under that stricter check and skip the guard
    /// entirely — letting exactly the admin this protects against self-remove.
    /// Both sides of the comparison must be the same kind of value.
    /// </para>
    /// </remarks>
    /// <param name="policy">
    /// The group's admin policy, or null when the group has none — in which
    /// case there is no admin to protect and nothing to reject.
    /// </param>
    /// <exception cref="AppComponentException">An admin tried to self-remove.</exception>
    public static void RequireNoAdminSelfRemove(StagedCommitView commit, AdminPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(commit);

        if (policy is null)
            return;

        foreach (StagedProposal proposal in commit.Proposals)
        {
            if (proposal.Kind != CommitProposalKind.SelfRemove)
                continue;

            if (proposal.SenderAccountKey is null)
            {
                throw new AppComponentException(
                    "A SelfRemove proposal whose sender cannot be resolved to a member is rejected.");
            }

            // Listed rather than active, and the two coincide here: the sender
            // of a SelfRemove is by definition a member, so it holds a leaf.
            if (policy.IsListed(proposal.SenderAccountKey))
            {
                throw new AppComponentException(
                    "An active admin cannot self-remove; it must first commit an " +
                    "admin-policy update removing itself.");
            }
        }
    }
}
