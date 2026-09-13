namespace Scramble.Marmot.Storage;

/// <summary>
/// The epoch state machine, made to survive a restart.
/// </summary>
/// <remarks>
/// <para>
/// <b>At most one row per group, and only for the states that refuse
/// something.</b> The absence of a row means the group is ordinary, which is
/// already how <c>EpochManager</c> reads a group it holds no state for. See
/// <see cref="EpochStateRecord"/> for why Stable, Merging and Recovering are
/// not stored.
/// </para>
/// <para>
/// <b>The order the writes go in is the point.</b> Entering a stored state is
/// recorded before the in-memory move; leaving one is recorded after it. Both
/// windows therefore err the same way — towards believing a group is still
/// pending, still frozen, still gone — because that costs a reconciliation
/// query, while the opposite forgets a commit that may already be on a relay.
/// </para>
/// </remarks>
public interface IEpochStateStorage
{
    /// <summary>
    /// Records a group's epoch state, replacing whatever it had.
    /// </summary>
    /// <remarks>
    /// Replacing rather than refusing: a group moves between stored states
    /// directly — a pending publish can be abandoned into a frozen group — and
    /// two rows for one group would make which of them describes it a coin
    /// flip.
    /// </remarks>
    Task PutEpochStateAsync(EpochStateRecord record, CancellationToken ct = default);

    /// <summary>The group's stored state, or null when it has none.</summary>
    Task<EpochStateRecord?> GetEpochStateAsync(GroupId groupId, CancellationToken ct = default);

    /// <summary>
    /// Every stored state, for session-open restore.
    /// </summary>
    /// <remarks>
    /// Read whole rather than group by group, because the restore path's
    /// question is the other way round: it cannot know which groups need
    /// anything until it has looked. Bounded by the number of groups holding a
    /// refusal, which is normally none.
    /// </remarks>
    Task<IReadOnlyList<EpochStateRecord>> ListEpochStatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops a group's stored state, returning it to ordinary.
    /// </summary>
    /// <returns>False when there was nothing to clear.</returns>
    Task<bool> ClearEpochStateAsync(GroupId groupId, CancellationToken ct = default);
}
