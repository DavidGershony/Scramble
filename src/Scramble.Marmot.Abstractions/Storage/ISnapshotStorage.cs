namespace Scramble.Marmot.Storage;

/// <summary>
/// Epoch-anchored snapshots of a group's stored state.
/// </summary>
/// <remarks>
/// <para>
/// Snapshots are anchored to the epoch they were taken at — not merely counted
/// — because a caller needs to find "the snapshot for epoch N", and pruning is
/// bounded by the convergence rewind horizon rather than by a fixed number of
/// retained snapshots.
/// </para>
/// <para>
/// <b>This was written as the primitive fork recovery and convergence replay
/// would be built on, and neither was.</b> A convergence pass restores a branch
/// from <see cref="IEpochArchiveStorage"/> and invalidates the message records
/// the branch it left had delivered; it never rolls a table back. So nothing in
/// production calls any of these today. The Sqlite implementation carries the
/// reasoning, including why four later tables are deliberately outside a
/// snapshot — read it before adding a fifth.
/// </para>
/// </remarks>
public interface ISnapshotStorage
{
    /// <summary>
    /// Snapshots the group's state as of <paramref name="epoch"/> and returns
    /// the anchor name. Taking a snapshot for an epoch that already has one
    /// replaces it.
    /// </summary>
    Task<string> CreateSnapshotAsync(GroupId groupId, EpochId epoch, CancellationToken ct = default);

    /// <summary>Restores the group to a previously captured snapshot.</summary>
    Task RollbackToSnapshotAsync(string snapshotName, CancellationToken ct = default);

    /// <summary>Drops a snapshot, keeping current state. Safe to call twice.</summary>
    Task ReleaseSnapshotAsync(string snapshotName, CancellationToken ct = default);

    /// <summary>The retained anchor for an epoch, or null if it is out of horizon.</summary>
    Task<string?> GetSnapshotAsync(GroupId groupId, EpochId epoch, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListSnapshotsAsync(GroupId groupId, CancellationToken ct = default);

    /// <summary>
    /// Drops snapshots anchored before <paramref name="oldestRetainedEpoch"/>,
    /// which the caller derives from the convergence rewind horizon.
    /// </summary>
    Task PruneSnapshotsBeforeAsync(
        GroupId groupId,
        EpochId oldestRetainedEpoch,
        CancellationToken ct = default);
}
