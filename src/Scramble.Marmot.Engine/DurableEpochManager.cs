using Scramble.Marmot.Storage;

namespace Scramble.Marmot.Engine;

/// <summary>
/// The epoch state machine with a durable record behind it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a separate class.</b> <see cref="EpochManager"/> holds no I/O
/// on purpose — it is a pure state machine that can be exercised exhaustively —
/// and giving it a storage dependency would end that. So the writes live here,
/// wrapped around the same transitions, and the manager stays the thing that
/// decides whether a move is legal.
/// </para>
/// <para>
/// <b>The ordering rule, and it is the substance of this class.</b> Entering a
/// stored state writes the row <i>before</i> the in-memory move; leaving one
/// clears it <i>after</i>. Both windows then fail the same way: a group is
/// believed to be still pending, still frozen, still gone when it is not. That
/// costs a reconciliation — ask the relay whether the commit landed, and confirm
/// or discard. The opposite ordering loses the fact that a commit was staged at
/// all, and a commit that may already be on a relay is one no later pass can
/// recognise as ours: <c>CanIngest</c> answers true, the group looks settled at
/// an epoch everyone else has left, and the next commit staged from there is a
/// fork this member cannot even detect. This is the same principle
/// <c>StagedCommit</c> states for publish-before-apply — whichever step is
/// unrecoverable goes second.
/// </para>
/// <para>
/// <b>Restore is a session-open operation and must run before any
/// transition.</b> It writes the stored states into the manager, and a manager
/// that has already moved a group on would either refuse the restore or be
/// rewound by it.
/// </para>
/// <para>
/// <b>Transitions go through here or the record drifts.</b> The manager is
/// exposed for the query side — <c>MessageIngest</c> and <c>ConvergencePass</c>
/// take one and read it — but a caller that mutates it directly gets a durable
/// record that describes a group the engine has already left.
/// </para>
/// </remarks>
public sealed class DurableEpochManager
{
    private readonly EpochManager _epochs;
    private readonly IEpochStateStorage _storage;
    private readonly Func<DateTimeOffset> _clock;

    public DurableEpochManager(
        EpochManager epochs, IEpochStateStorage storage, Func<DateTimeOffset> clock)
    {
        _epochs = epochs ?? throw new ArgumentNullException(nameof(epochs));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// The in-memory state machine, for queries.
    /// </summary>
    /// <remarks>
    /// Handed to the components that only read it. Mutating it from outside is
    /// what this class exists to prevent — see the type's remarks.
    /// </remarks>
    public EpochManager Epochs => _epochs;

    // -- Session open --

    /// <summary>
    /// Replays the stored states into the manager.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point of the whole subsystem: a group that was mid-publish when the
    /// process died comes back knowing it, rather than as a group with no state
    /// at all. A restored pending publish refuses ingest and holds the staged
    /// commit, so whoever owns reconciliation can ask the relay what happened
    /// and then confirm or roll back by the same reference the crashed session
    /// used.
    /// </para>
    /// <para>
    /// Groups with no row are left alone rather than set Stable. Absent is
    /// already how the manager reads an unknown group, and a Stable written here
    /// would assert an epoch this class does not know — the MLS state does.
    /// </para>
    /// </remarks>
    /// <returns>How many groups were restored.</returns>
    public async Task<int> RestoreAsync(CancellationToken ct = default)
    {
        IReadOnlyList<EpochStateRecord> records = await _storage.ListEpochStatesAsync(ct);

        foreach (EpochStateRecord record in records)
        {
            switch (record)
            {
                case
                {
                    Kind: EpochStateKind.PendingPublish,
                    PriorEpoch: { } prior,
                    StagedCommit: { } staged,
                    Reference: { } reference,
                    PendingOperation: { } pendingKind,
                }:
                    _epochs.RestorePending(
                        record.GroupId, prior, record.Epoch, staged, reference, pendingKind);
                    break;

                case { Kind: EpochStateKind.Unrecoverable }:
                    _epochs.RestoreUnrecoverable(record.GroupId, record.Epoch);
                    break;

                case { Kind: EpochStateKind.Disbanded }:
                    _epochs.RestoreDisbanded(record.GroupId, record.Epoch);
                    break;

                // The exhaustiveness arm, and it is not reachable today: the
                // factories and the schema between them make a half-written
                // pending row unbuildable. It is here for the day a fourth kind
                // is added, because the alternative to throwing is skipping --
                // and a stored refusal that is skipped restores the group as
                // ordinary, which is the one outcome the row existed to
                // prevent.
                default:
                    throw new InvalidOperationException(
                        $"Epoch state row for group {record.GroupId} is incomplete "
                        + $"({record.Kind}) and cannot be restored.");
            }
        }

        return records.Count;
    }

    // -- Transitions --

    /// <summary>
    /// Stages a commit, recording it before the group moves.
    /// </summary>
    /// <remarks>
    /// The row is written first because from the moment the caller publishes,
    /// the commit is outside our control: a record of it is the only thing that
    /// makes a crash recoverable. If the write fails the group never leaves
    /// Stable, and a commit that was never staged is nothing at all.
    /// </remarks>
    /// <returns>The reference to confirm or roll this publish back by.</returns>
    /// <exception cref="InvalidEpochTransitionException">The group is not Stable.</exception>
    public async Task<PendingStateRef> BeginPendingAsync(
        GroupId groupId,
        EpochId priorEpoch,
        EpochId newEpoch,
        StagedCommitHandle stagedCommit,
        PendingKind kind,
        CancellationToken ct = default)
    {
        PendingStateRef reference = _epochs.NextPendingRef();

        // Fallible step first, on a copy of the state rather than the map: the
        // pure transition throws exactly what the manager would, before
        // anything durable is written. Without it, a refused move would leave a
        // row describing a commit that was never staged -- and the next session
        // would try to reconcile it against a relay that has never seen it.
        EpochState current = _epochs.GetState(groupId) ?? new EpochState.Stable(priorEpoch);
        _ = current.BeginPending(newEpoch, stagedCommit, reference);

        await _storage.PutEpochStateAsync(
            EpochStateRecord.Pending(
                groupId, newEpoch, priorEpoch, stagedCommit, reference, kind, _clock()),
            ct);

        _epochs.BeginPending(groupId, priorEpoch, newEpoch, stagedCommit, reference, kind);
        return reference;
    }

    /// <summary>
    /// Publish confirmed: the group advances, and only then is the row cleared.
    /// </summary>
    /// <remarks>
    /// Clearing first would mean a crash in between comes back with no record
    /// of a commit that was confirmed but whose local merge we cannot vouch
    /// for. Clearing second means the worst case is a stale row for a publish
    /// that is already settled, which reconciliation resolves by observing the
    /// commit on the relay and confirming it again.
    /// </remarks>
    public async Task<(GroupId GroupId, EpochId Epoch)> ConfirmPublishAsync(
        PendingStateRef pending, CancellationToken ct = default)
    {
        (GroupId groupId, EpochId epoch) = _epochs.ConfirmPublish(pending);
        await _storage.ClearEpochStateAsync(groupId, ct);
        return (groupId, epoch);
    }

    /// <summary>
    /// Publish failed: the group returns to its prior epoch, and only then is
    /// the row cleared.
    /// </summary>
    /// <remarks>
    /// Same ordering and the same reason as
    /// <see cref="ConfirmPublishAsync"/>. The row is what says a commit may be
    /// out there; it survives until the group has actually stopped believing
    /// so.
    /// </remarks>
    public async Task<(GroupId GroupId, EpochId PriorEpoch)> RollbackPublishAsync(
        PendingStateRef pending, CancellationToken ct = default)
    {
        (GroupId groupId, EpochId priorEpoch) = _epochs.RollbackPublish(pending);
        await _storage.ClearEpochStateAsync(groupId, ct);
        return (groupId, priorEpoch);
    }

    /// <summary>
    /// Sets a group Stable, dropping any stored refusal it carried.
    /// </summary>
    /// <remarks>
    /// Stable has no row of its own, so this is a clear rather than a write —
    /// see <see cref="EpochStateRecord"/>. The manager refuses to overwrite
    /// Unrecoverable and Disbanded, and a refused move must leave the row
    /// alone, which is why the clear is conditional on the answer.
    /// </remarks>
    /// <returns>True if the state was set; false if the manager refused.</returns>
    public async Task<bool> SetStableAsync(
        GroupId groupId, EpochId epoch, CancellationToken ct = default)
    {
        if (!_epochs.SetStable(groupId, epoch))
            return false;

        await _storage.ClearEpochStateAsync(groupId, ct);
        return true;
    }

    /// <summary>
    /// Moves a group into Recovering, dropping any stored refusal it carried.
    /// </summary>
    /// <remarks>
    /// Recovering is not stored, so this clears. That is not a shortcut: the
    /// durable record has to track the in-memory one, and a group the manager
    /// has moved out of Unrecoverable must not come back frozen from a row
    /// nobody cleared.
    /// </remarks>
    public async Task<bool> DetectForkAsync(
        GroupId groupId, IReadOnlyList<MessageId> buffered, CancellationToken ct = default)
    {
        // Cleared only if the group actually moved. A refused detection that
        // cleared anyway would erase the very refusal that caused it, and the
        // group would come back ingesting -- the in-memory mistake made
        // permanent, and silent, because the state it should have kept is gone.
        if (!_epochs.DetectFork(groupId, buffered))
            return false;

        await _storage.ClearEpochStateAsync(groupId, ct);
        return true;
    }

    /// <summary>
    /// Freezes a group, recording it before the group stops.
    /// </summary>
    /// <remarks>
    /// Written first because this is a refusal: a crash between the two must
    /// leave the group frozen, not ingesting. Frozen has an exit —
    /// <see cref="RepairToStableAsync"/> — and ingesting on state we have
    /// already judged untrustworthy does not.
    /// </remarks>
    public async Task MarkUnrecoverableAsync(GroupId groupId, CancellationToken ct = default)
    {
        // The epoch a group with no state at all freezes at, matching the
        // manager's own fallback. A group we know nothing about still has to be
        // recordable as frozen -- that is precisely the case where freezing
        // matters.
        EpochId lastStable = _epochs.GetState(groupId)?.CurrentEpoch ?? new EpochId(0);

        await _storage.PutEpochStateAsync(
            EpochStateRecord.Unrecoverable(groupId, lastStable, _clock()), ct);

        _epochs.MarkUnrecoverable(groupId);
    }

    /// <summary>
    /// Unfreezes a group after a verified repair, clearing the row afterwards.
    /// </summary>
    /// <remarks>
    /// Cleared second, so a crash in between leaves the group frozen and the
    /// repair repeatable. Clearing first would hand back a group that ingests
    /// on state nothing ever repaired.
    /// </remarks>
    /// <exception cref="InvalidEpochTransitionException">The group is not Unrecoverable.</exception>
    public async Task RepairToStableAsync(
        GroupId groupId, EpochId epoch, CancellationToken ct = default)
    {
        _epochs.RepairToStable(groupId, epoch);
        await _storage.ClearEpochStateAsync(groupId, ct);
    }

    /// <summary>
    /// Terminalises a group, recording it before the group stops.
    /// </summary>
    /// <remarks>
    /// Written first, like <see cref="MarkUnrecoverableAsync"/>: a group
    /// decided to be disbanded that comes back ingesting is the failure that
    /// matters. The reverse — a row for a disband that the in-memory move then
    /// refused — cannot arise, because the composite transition through
    /// Recovering is legal from every state.
    /// </remarks>
    public async Task MarkDisbandedAsync(
        GroupId groupId, EpochId epoch, CancellationToken ct = default)
    {
        await _storage.PutEpochStateAsync(
            EpochStateRecord.Disbanded(groupId, epoch, _clock()), ct);

        _epochs.MarkDisbanded(groupId, epoch);
    }
}
