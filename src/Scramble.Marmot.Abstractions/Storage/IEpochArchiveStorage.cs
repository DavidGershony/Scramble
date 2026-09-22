namespace Scramble.Marmot.Storage;

/// <summary>
/// Retained MLS group state, one checkpoint per epoch.
/// </summary>
/// <remarks>
/// <para>
/// <b>An MLS group cannot rewind.</b> Applying a commit is a one-way key
/// derivation, so evaluating a branch that forks two epochs back means
/// restoring a copy of the group as it was there and replaying forward. Without
/// an archive there is nothing to restore from, and every competing commit is
/// simply unprocessable — which is what convergence looks like when it is not
/// wired up: refusals that read as transport noise.
/// </para>
/// <para>
/// <b>Bounded by the rewind horizon, not by age or count.</b> A branch forking
/// further back than <c>MaxRewindCommits</c> is refused however good it looks,
/// so retaining more than that buys nothing and keeps key material alive for no
/// reason. Retaining less silently narrows the horizon: a branch the policy says
/// is eligible becomes one this member alone cannot evaluate, and a member that
/// cannot evaluate a branch cannot agree about it either.
/// </para>
/// </remarks>
public interface IEpochArchiveStorage
{
    /// <summary>
    /// Archives the group's state at an epoch. Re-archiving an epoch replaces it.
    /// </summary>
    /// <remarks>
    /// Replacement rather than refusal because the same epoch can legitimately
    /// be reached twice — once on a branch that loses and again after a reorg
    /// onto the branch that wins. The later state is the one a further branch
    /// would fork from.
    /// </remarks>
    Task PutEpochCheckpointAsync(EpochCheckpoint checkpoint, CancellationToken ct = default);

    /// <summary>The checkpoint for an epoch, or null if it is out of horizon.</summary>
    Task<EpochCheckpoint?> GetEpochCheckpointAsync(
        GroupId groupId, EpochId epoch, CancellationToken ct = default);

    /// <summary>
    /// Every checkpoint from <paramref name="oldestEpoch"/> up to and including
    /// the group's newest, oldest first.
    /// </summary>
    /// <remarks>
    /// A convergence pass reads the whole retained window in one call rather
    /// than an epoch at a time: the branch that needs restoring is not known
    /// until the candidates have been built, and building them is what needs
    /// the states.
    /// </remarks>
    Task<IReadOnlyList<EpochCheckpoint>> ListEpochCheckpointsAsync(
        GroupId groupId, EpochId oldestEpoch, CancellationToken ct = default);

    /// <summary>
    /// Drops checkpoints anchored before <paramref name="oldestRetainedEpoch"/>.
    /// </summary>
    /// <returns>How many were dropped.</returns>
    Task<int> PruneEpochCheckpointsBeforeAsync(
        GroupId groupId, EpochId oldestRetainedEpoch, CancellationToken ct = default);
}
