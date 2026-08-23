namespace Scramble.Marmot.Engine;

/// <summary>
/// What kind of operation a pending publish represents.
/// </summary>
public enum PendingKind
{
    CreateGroup,
    GroupEvolution,
    Disband,
}

/// <summary>
/// Owns the epoch state of every group, and the bookkeeping that lets the
/// engine tell its own commits apart from other members'.
/// </summary>
/// <remarks>
/// <para>
/// In-memory and single-owner by design: it holds no I/O and performs no MLS
/// work, so it can be exercised exhaustively in tests. Callers are responsible
/// for the durable side (MLS state, storage snapshots) and for serialising
/// access per group.
/// </para>
/// <para>
/// Every mutation is atomic in the sense that matters here: a transition that
/// throws leaves every map untouched, so a rejected move cannot half-advance a
/// group. The fallible step is always taken before anything is written.
/// </para>
/// </remarks>
public sealed class EpochManager
{
    private readonly Dictionary<GroupId, EpochState> _states = new();
    private readonly Dictionary<PendingStateRef, PendingMeta> _pending = new();
    private readonly Dictionary<GroupId, SortedSet<EpochId>> _committedFrom = new();
    private ulong _pendingCounter;

    /// <param name="OwnsCommittedFrom">
    /// Whether this pending was the one that inserted its prior epoch into
    /// <see cref="_committedFrom"/>. Only the owner may remove it on rollback;
    /// otherwise a rollback would erase an incumbent entry recorded by an
    /// earlier confirmed commit, and fork detection would stop recognising our
    /// own commits at that epoch.
    /// </param>
    private sealed record PendingMeta(
        GroupId GroupId,
        EpochId PriorEpoch,
        PendingKind Kind,
        bool OwnsCommittedFrom);

    // -- Queries --

    public EpochState? GetState(GroupId groupId) =>
        _states.TryGetValue(groupId, out var state) ? state : null;

    public EpochId? GetEpoch(GroupId groupId) => GetState(groupId)?.CurrentEpoch;

    /// <summary>
    /// Whether inbound messages may be ingested for this group.
    /// </summary>
    /// <remarks>
    /// A group with no recorded state is ingestible: a Welcome necessarily
    /// arrives before we have any state for the group it admits us to.
    /// </remarks>
    public bool CanIngest(GroupId groupId) =>
        !_states.TryGetValue(groupId, out var state) || state.CanIngest;

    public bool IsUnrecoverable(GroupId groupId) => GetState(groupId)?.IsUnrecoverable ?? false;

    public bool IsDisbanded(GroupId groupId) => GetState(groupId)?.IsDisbanded ?? false;

    /// <summary>
    /// Whether we ourselves committed from this epoch.
    /// </summary>
    /// <remarks>
    /// This is what separates a genuine fork from a benign late-arriving
    /// commit: a competing commit at an epoch we never committed from is
    /// simply someone else's work reaching us out of order.
    /// </remarks>
    public bool WeCommittedFrom(GroupId groupId, EpochId epoch) =>
        _committedFrom.TryGetValue(groupId, out var epochs) && epochs.Contains(epoch);

    public GroupId? GroupForPending(PendingStateRef pending) =>
        _pending.TryGetValue(pending, out var meta) ? meta.GroupId : null;

    public PendingKind? KindForPending(PendingStateRef pending) =>
        _pending.TryGetValue(pending, out var meta) ? meta.Kind : null;

    // -- Pending-reference allocation --

    public PendingStateRef NextPendingRef() => new(++_pendingCounter);

    // -- Transitions --

    /// <summary>
    /// Sets a group to Stable. Used when joining from a Welcome, where there is
    /// no prior state to transition from.
    /// </summary>
    /// <remarks>
    /// Refuses to overwrite Unrecoverable or Disbanded. Those states exist
    /// precisely to stop ordinary traffic from resuming, so letting a routine
    /// write clear them would defeat them; leaving Unrecoverable requires
    /// <see cref="RepairToStable"/>, and Disbanded is terminal.
    /// </remarks>
    /// <returns>True if the state was set; false if it was refused.</returns>
    public bool SetStable(GroupId groupId, EpochId epoch)
    {
        if (IsUnrecoverable(groupId) || IsDisbanded(groupId))
            return false;

        _states[groupId] = new EpochState.Stable(epoch);
        return true;
    }

    /// <summary>
    /// Stages a commit: Stable to PendingPublish, recording the epoch we
    /// committed from so a later competing commit can be recognised as a fork.
    /// </summary>
    /// <exception cref="InvalidEpochTransitionException">The group is not Stable.</exception>
    public void BeginPending(
        GroupId groupId,
        EpochId priorEpoch,
        EpochId newEpoch,
        StagedCommitHandle stagedCommit,
        PendingStateRef reference,
        PendingKind kind)
    {
        var previous = GetState(groupId) ?? new EpochState.Stable(priorEpoch);

        // Fallible step first: if this throws, nothing below runs.
        var next = previous.BeginPending(newEpoch, stagedCommit, reference);

        if (!_committedFrom.TryGetValue(groupId, out var epochs))
        {
            epochs = new SortedSet<EpochId>();
            _committedFrom[groupId] = epochs;
        }

        bool ownsCommittedFrom = epochs.Add(priorEpoch);

        _states[groupId] = next;
        _pending[reference] = new PendingMeta(groupId, priorEpoch, kind, ownsCommittedFrom);
    }

    /// <summary>
    /// Recreates a pending slot after a restart, from durable state.
    /// </summary>
    /// <remarks>
    /// Also advances the allocator past the restored reference, so a new
    /// operation in this session cannot be handed an id that is already in use.
    /// </remarks>
    public void RestorePending(
        GroupId groupId,
        EpochId priorEpoch,
        EpochId newEpoch,
        StagedCommitHandle stagedCommit,
        PendingStateRef reference,
        PendingKind kind)
    {
        _pendingCounter = Math.Max(_pendingCounter, reference.Value);
        BeginPending(groupId, priorEpoch, newEpoch, stagedCommit, reference, kind);
    }

    /// <summary>
    /// Publish confirmed: PendingPublish through Merging to Stable at the new
    /// epoch, as one step.
    /// </summary>
    /// <returns>The group and the epoch it now sits at.</returns>
    /// <exception cref="KeyNotFoundException">The pending reference is unknown.</exception>
    public (GroupId GroupId, EpochId Epoch) ConfirmPublish(PendingStateRef pending)
    {
        var meta = RequirePending(pending);
        var previous = RequireState(meta.GroupId);

        // Both transitions are computed before either map is written, so a
        // rejected merge cannot leave the group stranded in Merging.
        var merging = previous.ConfirmPublish();
        var stable = merging.MergeToStable(merging.CurrentEpoch);

        _pending.Remove(pending);
        _states[meta.GroupId] = stable;
        return (meta.GroupId, merging.CurrentEpoch);
    }

    /// <summary>
    /// Publish failed: PendingPublish back to Stable at the prior epoch.
    /// </summary>
    /// <remarks>
    /// Also drops the provisional committed-from entry this pending added. The
    /// commit never reached anyone, so a later commit at the same epoch is a
    /// benign race rather than a fork — leaving the entry behind would make the
    /// engine chase a fork that never happened. An entry owned by an earlier
    /// confirmed commit is left alone.
    /// </remarks>
    /// <returns>The group and the epoch it has returned to.</returns>
    /// <exception cref="KeyNotFoundException">The pending reference is unknown.</exception>
    public (GroupId GroupId, EpochId PriorEpoch) RollbackPublish(PendingStateRef pending)
    {
        var meta = RequirePending(pending);
        var previous = RequireState(meta.GroupId);

        var stable = previous.RollbackPending(meta.PriorEpoch);

        _pending.Remove(pending);

        if (meta.OwnsCommittedFrom && _committedFrom.TryGetValue(meta.GroupId, out var epochs))
        {
            epochs.Remove(meta.PriorEpoch);
            if (epochs.Count == 0)
                _committedFrom.Remove(meta.GroupId);
        }

        _states[meta.GroupId] = stable;
        return (meta.GroupId, meta.PriorEpoch);
    }

    /// <summary>Moves a group into Recovering. Legal from any state.</summary>
    public void DetectFork(GroupId groupId, IReadOnlyList<MessageId> buffered)
    {
        var previous = GetState(groupId) ?? new EpochState.Stable(new EpochId(0));
        _states[groupId] = previous.DetectFork(buffered);
    }

    /// <summary>
    /// Freezes a group as Unrecoverable. Legal from any state.
    /// </summary>
    /// <remarks>
    /// Called when no candidate branch can be validated from retained material
    /// — the fail-closed outcome, chosen over guessing at canonical state.
    /// </remarks>
    public void MarkUnrecoverable(GroupId groupId)
    {
        var previous = GetState(groupId) ?? new EpochState.Stable(new EpochId(0));
        _states[groupId] = previous.ToUnrecoverable();
    }

    /// <summary>Restores a persisted Unrecoverable marker at session open.</summary>
    public void RestoreUnrecoverable(GroupId groupId, EpochId lastStableEpoch) =>
        _states[groupId] = new EpochState.Unrecoverable(lastStableEpoch);

    /// <summary>
    /// Unrecoverable back to Stable, after a verified repair.
    /// </summary>
    /// <exception cref="InvalidEpochTransitionException">The group is not Unrecoverable.</exception>
    public void RepairToStable(GroupId groupId, EpochId epoch)
    {
        var previous = GetState(groupId) ?? new EpochState.Stable(epoch).ToUnrecoverable();
        _states[groupId] = previous.RepairToStable(epoch);
    }

    /// <summary>
    /// Terminalises a group as Disbanded.
    /// </summary>
    /// <remarks>
    /// Routed through Recovering because disbanding is only legal once a
    /// convergence pass has selected a disband branch, and that is the state
    /// such a pass runs from.
    /// </remarks>
    public void MarkDisbanded(GroupId groupId, EpochId epoch)
    {
        var previous = GetState(groupId) ?? new EpochState.Stable(epoch);
        _states[groupId] = previous
            .DetectFork(Array.Empty<MessageId>())
            .SettleToDisbanded(epoch);
    }

    /// <summary>Restores a persisted Disbanded marker at session open.</summary>
    public void RestoreDisbanded(GroupId groupId, EpochId epoch) =>
        _states[groupId] = new EpochState.Disbanded(epoch);

    /// <summary>
    /// Forgets committed-from epochs older than the rewind horizon, which is
    /// the only thing bounding this set's growth.
    /// </summary>
    public void PruneCommittedFromBefore(GroupId groupId, EpochId oldestRetainedEpoch)
    {
        if (!_committedFrom.TryGetValue(groupId, out var epochs))
            return;

        epochs.RemoveWhere(e => e < oldestRetainedEpoch);
        if (epochs.Count == 0)
            _committedFrom.Remove(groupId);
    }

    private PendingMeta RequirePending(PendingStateRef pending) =>
        _pending.TryGetValue(pending, out var meta)
            ? meta
            : throw new KeyNotFoundException($"Unknown pending publish {pending}.");

    private EpochState RequireState(GroupId groupId) =>
        GetState(groupId) ?? throw new KeyNotFoundException($"Unknown group {groupId}.");
}
