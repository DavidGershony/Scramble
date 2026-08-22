namespace Scramble.Marmot.Storage;

/// <summary>
/// Epoch-anchored snapshots of a group's stored state.
/// </summary>
/// <remarks>
/// This is the primitive both fork recovery and convergence replay are built
/// on: take a snapshot before applying a commit, and roll back to it if a
/// competing branch wins. Snapshots are anchored to the epoch they were taken
/// at — not merely counted — because recovery needs to find "the snapshot for
/// epoch N", and pruning is bounded by the convergence rewind horizon rather
/// than by a fixed number of retained snapshots.
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
