namespace Scramble.Marmot.Storage;

/// <summary>
/// The state a commit of ours produced, written down before it is published.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one thing a crash between publish and apply cannot reconstruct.</b>
/// MLS refuses to let a member process a commit it authored, so our own bytes
/// coming back off a relay are no help; and an export of the live group drops a
/// staged-but-unmerged commit, so the group's own durable copy describes the
/// epoch we were about to leave. Between handing a commit to a relay and
/// merging it locally, the epoch the group is actually moving to exists in
/// exactly one place — the process that is about to die. This row is that place
/// made durable.
/// </para>
/// <para>
/// <b>It is not evidence that anything was published.</b> The row is written
/// when the commit is staged, which is before anybody decides to send it, so
/// its presence says only "a commit of ours was prepared". What happened to it
/// afterwards is <see cref="CommitPublishAttempt"/>'s answer, and recovery must
/// ask that before adopting anything here — adopting a commit that never left
/// the device advances this member into an epoch nobody else can reach.
/// </para>
/// <para>
/// <b>Keyed by the group,</b> like the publish attempt and for the same reason:
/// MLS refuses a second staged commit, so a second row could only describe one
/// that no longer exists.
/// </para>
/// </remarks>
/// <param name="GroupState">
/// The exported MLS group as it stands <i>after</i> the commit. Carries private
/// key material, exactly like <see cref="EpochCheckpoint.GroupState"/>, and
/// belongs under the same protection.
/// </param>
/// <param name="Tip">
/// What branch selection will need to say about this commit, read while it
/// could still be read — see <see cref="CommitTip"/>.
/// </param>
public sealed record StagedCommitRecord(
    GroupId GroupId,
    EpochId NewEpoch,
    byte[] GroupState,
    CommitTip Tip,
    DateTimeOffset CreatedAt);

/// <summary>
/// Where the post-commit state of an unconfirmed commit is kept.
/// </summary>
/// <remarks>
/// Empty in the ordinary case — a row exists only while a commit of ours is
/// staged and unresolved — which is why there is no index beyond the primary
/// key and why recovery reads the whole table rather than asking group by
/// group: it cannot know which groups need anything until it has looked.
/// </remarks>
public interface IStagedCommitStorage
{
    /// <summary>Records the state a staged commit produces. Replaces any prior row.</summary>
    Task PutStagedCommitAsync(StagedCommitRecord record, CancellationToken ct = default);

    /// <summary>The staged commit for a group, or null when none is outstanding.</summary>
    Task<StagedCommitRecord?> GetStagedCommitAsync(
        GroupId groupId, CancellationToken ct = default);

    /// <summary>Every outstanding staged commit, oldest first.</summary>
    Task<IReadOnlyList<StagedCommitRecord>> ListStagedCommitsAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops a group's staged commit.
    /// </summary>
    /// <remarks>
    /// Called once the commit's fate is settled — applied, or provably never
    /// sent. Never called merely because a publish failed to answer: an
    /// unanswered publish is the case this row exists for.
    /// </remarks>
    Task ClearStagedCommitAsync(GroupId groupId, CancellationToken ct = default);
}
