using DotnetMls.Group;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>
/// Rotating this member's own leaf key material.
/// </summary>
/// <remarks>
/// <para>
/// The one membership-neutral commit. Nobody joins, nobody leaves, and the
/// member list is identical afterwards — what changes is that this member's
/// leaf carries fresh key material and the group advances an epoch, which
/// retires every secret derived from the old one.
/// </para>
/// <para>
/// <b>That retirement is the whole point.</b> MLS forward secrecy is a property
/// of moving forward: a group that never commits keeps the same key material
/// indefinitely, so a device compromised today can read everything sent since
/// the last commit. A member with nothing to say still has something to do.
/// </para>
/// <para>
/// <b>It deliberately commits no pending proposals.</b> Upstream builds this
/// with <c>consume_proposal_store(false)</c>, and the reason is worth stating:
/// someone else's <c>self_remove</c> may be sitting in the cache, and a routine
/// key rotation that silently also evicted a member would be a governance
/// action disguised as maintenance. Committing a departure is
/// <see cref="MarmotGroupLeave.CommitDepartures"/>'s job, where it is the
/// caller's stated intent.
/// </para>
/// <para>
/// Nothing here schedules it. How often to rotate, and with what jitter so a
/// group does not stampede, needs the clock and a view of the other members.
/// </para>
/// </remarks>
public static class MarmotSelfUpdate
{
    /// <summary>
    /// Stages a commit that rotates this member's leaf and nothing else.
    /// </summary>
    /// <remarks>
    /// Publish-before-apply like every other commit: the group stays at its
    /// current epoch until <see cref="StagedCommit.Applied"/>. A rotation
    /// applied locally and never published is the worst of both — the member
    /// leaves the epoch everyone else is in, having gained no forward secrecy
    /// the group agrees about.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A commit is already staged on this group. Two pending commits cannot
    /// both be published, and picking one silently would discard the other.
    /// </exception>
    public static StagedCommit Stage(MlsGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (group.HasPendingCommit)
        {
            throw new InvalidOperationException(
                "This group already has a staged commit; apply or discard it before "
                + "rotating the leaf.");
        }

        // No proposals and no references: an empty commit still carries an
        // UpdatePath, which is what rotates the leaf. Passing the cached
        // proposals here is exactly the mistake the class comment describes.
        var (commit, welcome) = group.CommitPublic();

        // Never a Welcome -- nobody is being admitted. Asserted rather than
        // assumed, because a Welcome produced here would mean the commit
        // carried an Add nobody asked for.
        if (welcome is not null)
        {
            throw new InvalidOperationException(
                "A self-update produced a Welcome, so it added a member. Refusing it: "
                + "the commit is not what it claims to be.");
        }

        return new StagedCommit(group, commit, welcome, []);
    }
}
