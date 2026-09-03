using DotnetMls.Group;
using DotnetMls.Types;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>
/// Leaving a group, and committing someone else's departure.
/// </summary>
/// <remarks>
/// <para>
/// <b>A member cannot remove themselves.</b> RFC 9420 §12.2 requires the
/// committer to remain a member, because they derive the new epoch's secrets.
/// So leaving is a two-party operation: the leaver publishes a
/// <c>self_remove</c> proposal and some <i>other</i> member commits it. Until
/// one does, the leaver is still in the group and still receiving its traffic.
/// </para>
/// <para>
/// <b>The proposal is epoch-bound and the intent is not.</b> A proposal is
/// framed against one epoch, and every member drops it when the epoch advances
/// — so a leave request that is overtaken by anyone else's commit simply
/// vanishes from the group's view. The durable
/// <see cref="Scramble.Marmot.Storage.LeaveRequest"/> is what survives that:
/// the intent is re-proposed against each new epoch until a commit actually
/// removes the member. Treating the proposal as the record of intent is how a
/// member ends up silently still in a group they asked to leave.
/// </para>
/// <para>
/// Nothing here schedules or jitters the commit. Deciding <i>which</i> remaining
/// member commits a departure, and after how long, is a policy question for the
/// layer that can see the clock and the other members; this provides the two
/// mechanisms that policy composes.
/// </para>
/// </remarks>
public static class MarmotGroupLeave
{
    /// <summary>
    /// Builds this member's request to leave, framed for publication.
    /// </summary>
    /// <remarks>
    /// Sending it changes nothing locally, deliberately: the member is still
    /// present, still holds the group's keys, and still reads its messages until
    /// somebody commits the proposal. A client that hides the group at this
    /// point is showing an outcome that has not happened.
    /// </remarks>
    /// <remarks>
    /// <b>The proposal is cached on our own group before it is returned</b>, and
    /// that is not bookkeeping. The commit that grants the request cites the
    /// proposal <i>by hash</i>, so a leaver who did not keep a copy cannot
    /// resolve what that commit does — the one message telling them they are out
    /// fails with "unknown proposal reference" instead. Committing it remains
    /// impossible for us, and <see cref="CommitDepartures"/> skips it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The group has only one member, so nobody could ever commit the request.
    /// </exception>
    public static PublicMessage Request(MlsGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (group.GetMembers().Count < 2)
        {
            // There is no second member to commit it, so the proposal would sit
            // unresolved forever. Refused here rather than published, because a
            // request that cannot be granted is worse than an error: it looks
            // like progress.
            throw new InvalidOperationException(
                "A sole member cannot leave; nobody remains to commit the request.");
        }

        PublicMessage proposal = group.ProposePublic(new SelfRemoveProposal());
        group.CacheProposal(proposal);

        return proposal;
    }

    /// <summary>
    /// Commits departures other members have requested.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Publish-before-apply, like every other commit: the returned
    /// <see cref="StagedInvite"/> leaves the group at its current epoch until
    /// <see cref="StagedInvite.Applied"/> is called.
    /// </para>
    /// <para>
    /// Our own cached request is skipped rather than refused. It is always there
    /// when we are leaving — <see cref="Request"/> caches it deliberately — and
    /// committing it is the one thing RFC 9420 forbids, so treating its presence
    /// as an error would make our own pending departure block everyone else's.
    /// </para>
    /// </remarks>
    /// <param name="group">The group to commit against.</param>
    /// <returns>The staged commit, or null when nobody has asked to leave.</returns>
    public static StagedInvite? CommitDepartures(MlsGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var references = new List<byte[]>();
        var departing = new List<byte[]>();

        foreach (MlsGroup.CachedProposal cached in group.CachedProposals)
        {
            if (cached.Proposal is not SelfRemoveProposal)
                continue;

            if (cached.SenderLeafIndex == group.MyLeafIndex)
                continue;

            references.Add(cached.Reference);
            departing.Add(IdentityOfLeaf(group, cached.SenderLeafIndex));
        }

        if (references.Count == 0)
            return null;

        // Nothing here refuses to commit the last other member's departure. A
        // group of one is an ordinary outcome -- it is what a two-member
        // conversation becomes when the other person leaves, which is the most
        // common leave there is -- and the remaining member can still invite
        // someone or abandon it. The committer always stays, so a commit built
        // here can never empty the group.
        var (commit, welcome) = group.CommitPublic(referencedProposals: references);

        return new StagedInvite(group, commit, welcome, departing);
    }

    /// <summary>
    /// Drops any cached departure request from one account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For removing outright a member who has already asked to leave. Both
    /// resolve to the same leaf, and a commit carrying both removes it twice,
    /// which the library refuses — so an admin evicting someone mid-departure
    /// would find every commit blocked until the request was dropped.
    /// </para>
    /// <para>
    /// Deliberately narrow: it does not sweep requests from members who are
    /// already gone, because there are none to sweep. A proposal is only valid
    /// in the epoch it was framed against, and the cache empties on every
    /// commit — applied or received — so a request can never outlive the
    /// removal that overtook it.
    /// </para>
    /// </remarks>
    /// <returns>How many requests were dropped.</returns>
    public static int DropRequestsFrom(MlsGroup group, ReadOnlySpan<byte> account)
    {
        ArgumentNullException.ThrowIfNull(group);

        var leaves = new HashSet<uint>();
        foreach (var (index, identity) in group.GetMembers())
        {
            if (identity.AsSpan().SequenceEqual(account))
                leaves.Add(index);
        }

        int dropped = 0;
        foreach (MlsGroup.CachedProposal cached in group.CachedProposals)
        {
            if (cached.Proposal is SelfRemoveProposal
                && leaves.Contains(cached.SenderLeafIndex)
                && group.RemoveCachedProposal(cached.Reference))
            {
                dropped++;
            }
        }

        return dropped;
    }

    private static byte[] IdentityOfLeaf(MlsGroup group, uint leafIndex)
    {
        foreach (var (index, identity) in group.GetMembers())
        {
            if (index == leafIndex)
                return identity;
        }

        throw new InvalidOperationException(
            $"Leaf {leafIndex} asked to leave but is not a member.");
    }
}
