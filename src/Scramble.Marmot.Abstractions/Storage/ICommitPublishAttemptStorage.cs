namespace Scramble.Marmot.Storage;

/// <summary>
/// Publish intent for commits, made to survive a restart.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not <see cref="IOutboundIntentStorage"/>.</b> That is an
/// outbox: work not yet started, queued while a group is unsettled, drained
/// when it settles, retried under <c>Attempts</c>, and dropped wholesale by
/// <c>ClearIntentsAsync</c> when the local member is evicted. Every one of
/// those properties is wrong here. This row is created at the moment work
/// becomes <i>irrevocable</i> rather than before it starts; it cannot be
/// retried, because MLS will not let a member re-stage a commit it has already
/// made; and clearing it on eviction would erase it in precisely the case that
/// needs it most, since the commit that evicts us is one of the commits this
/// records. A queue row also has only two observable states, present and
/// absent, which cannot express the difference between a transport that
/// refused and a transport that did not answer — the distinction the whole
/// mechanism turns on.
/// </para>
/// <para>
/// <b>At most one row per group, and only while a commit is unresolved.</b> An
/// absent row is the positive statement that no commit of ours has left this
/// device, so the table is empty in the ordinary case. That is also why there
/// is no index beyond the primary key, and why the recovery path reads the
/// whole table rather than asking group by group: it cannot know which groups
/// need anything until it has looked.
/// </para>
/// <para>
/// <b>The order the writes go in is the point.</b> The row is written before
/// the bytes reach a transport and the outcome is written before the local
/// apply or discard. Both windows then err the same way — towards believing a
/// commit may be out there — because that costs a relay query, while the
/// opposite forgets a commit the rest of the group has already applied.
/// </para>
/// </remarks>
public interface ICommitPublishAttemptStorage
{
    /// <summary>
    /// Records an attempt, replacing whatever the group had.
    /// </summary>
    /// <remarks>
    /// Replacing rather than refusing: recording the transport's answer is a
    /// write to the same row, and two rows for one group would make which of
    /// them describes it a coin flip at session open.
    /// </remarks>
    Task PutCommitPublishAttemptAsync(
        CommitPublishAttempt attempt, CancellationToken ct = default);

    /// <summary>The group's unresolved attempt, or null when it has none.</summary>
    Task<CommitPublishAttempt?> GetCommitPublishAttemptAsync(
        GroupId groupId, CancellationToken ct = default);

    /// <summary>Every recorded attempt, for session-open classification.</summary>
    Task<IReadOnlyList<CommitPublishAttempt>> ListCommitPublishAttemptsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Drops a group's attempt, once the commit has actually been applied or
    /// discarded locally.
    /// </summary>
    /// <remarks>
    /// Cleared last, never first. The row is what says a commit may be out
    /// there; it has to outlive the local move it authorised, or a crash
    /// between the clear and the move comes back with no record of a commit
    /// whose fate we knew and whose application we cannot vouch for.
    /// </remarks>
    /// <returns>False when there was nothing to clear.</returns>
    Task<bool> ClearCommitPublishAttemptAsync(GroupId groupId, CancellationToken ct = default);
}
