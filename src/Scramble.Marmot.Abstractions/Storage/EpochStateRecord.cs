namespace Scramble.Marmot.Storage;

/// <summary>
/// Which epoch state a durable row describes.
/// </summary>
/// <remarks>
/// <para>
/// Three of the six <see cref="EpochState"/> variants, and the absent ones are
/// a decision rather than an omission — see <see cref="EpochStateRecord"/>.
/// </para>
/// <para>
/// The numbers are written into the database, so they are pinned here rather
/// than left to declaration order. Renumbering one silently re-labels every row
/// already stored.
/// </para>
/// </remarks>
public enum EpochStateKind
{
    /// <summary>A commit is staged and its fate is unknown.</summary>
    PendingPublish = 1,

    /// <summary>State is frozen until a verified repair runs.</summary>
    Unrecoverable = 2,

    /// <summary>The group is terminally disbanded.</summary>
    Disbanded = 3,
}

/// <summary>
/// One group's epoch state, in a form that survives a restart.
/// </summary>
/// <remarks>
/// <para>
/// <b>A row is a reason not to treat a group as ordinary.</b> Only the three
/// states a process restart cannot otherwise recover are stored, and each of
/// them carries a refusal: a staged commit whose fate is unknown, a frozen
/// group, a dead one. Without the row the engine comes back believing a group
/// it may have half-committed is simply settled — <c>CanIngest</c> answers true,
/// and the commit that is possibly already on a relay is forgotten by the only
/// party that could reconcile it.
/// </para>
/// <para>
/// <b>Stable has no row, deliberately.</b> Stable is the absence of a reason to
/// stop, which is exactly what an absent row already means to
/// <c>EpochManager</c> — a group it has never heard of is ingestible. The only
/// thing a Stable row could add is the epoch, and the epoch is already durable
/// twice over: on the group record, and authoritatively in the MLS state
/// itself. A third copy is a third thing to disagree after a crash between the
/// merge and the write, and it would have to be rewritten on every commit the
/// group ever ingests to say something no reader needed.
/// </para>
/// <para>
/// <b>Merging has no row because coming back into it means nothing.</b> Merging
/// says a confirmed commit is being applied to local MLS state, and whether
/// that apply landed is a question only the MLS state can answer — a row
/// written beforehand cannot. A restored Merging could leave only through
/// <c>MergeToStable</c>, which would assert an epoch we have no evidence the
/// group reached. It is also unobservable in practice:
/// <c>EpochManager.ConfirmPublish</c> passes through Merging to Stable in one
/// step, so no crash can find a group resting there.
/// </para>
/// <para>
/// <b>Recovering has no row because it is derived, and because it refuses
/// nothing.</b> Its payload — the buffered message ids — is already durable as
/// message rows, and a second copy is a copy that goes stale; the fork itself
/// is re-derivable from the stored commits by the next convergence pass. It is
/// the one non-Stable state that still ingests, so a group that comes back
/// Stable instead of Recovering does nothing it would otherwise have refused.
/// That is what makes leaving it unstored fail-safe, and it is why
/// <c>EpochManager</c> has a <c>RestorePending</c>, a
/// <c>RestoreUnrecoverable</c> and a <c>RestoreDisbanded</c>, and no
/// <c>RestoreRecovering</c>.
/// </para>
/// <para>
/// <b>Built through factories, never through a public constructor.</b> A
/// pending row is four fields that are meaningless apart, and the reasoning
/// that makes <c>StagedCommit</c> a class rather than a positional record
/// applies here too: a record's primary constructor is public, and a row built
/// through it carrying an epoch but no staged commit would read back as a
/// describable pending publish with the handle invented. The schema carries the
/// same rule as a CHECK, for a writer that is not this type.
/// </para>
/// </remarks>
public sealed class EpochStateRecord
{
    private EpochStateRecord(
        GroupId groupId, EpochStateKind kind, EpochId epoch, DateTimeOffset updatedAt)
    {
        GroupId = groupId;
        Kind = kind;
        Epoch = epoch;
        UpdatedAt = updatedAt;
    }

    public GroupId GroupId { get; }

    public EpochStateKind Kind { get; }

    /// <summary>
    /// The epoch this state is anchored to: the one a pending publish reaches
    /// on confirmation, the last stable one for a frozen group, the final one
    /// for a disbanded group.
    /// </summary>
    public EpochId Epoch { get; }

    /// <summary>
    /// The epoch to roll back to, for a pending publish. Null otherwise.
    /// </summary>
    /// <remarks>
    /// Not derivable from <see cref="Epoch"/> by subtracting one. A discarded
    /// commit returns the group to where it actually stood, and the engine has
    /// to name that epoch rather than guess at it.
    /// </remarks>
    public EpochId? PriorEpoch { get; private init; }

    /// <summary>The staged, unmerged commit. Null unless pending.</summary>
    public StagedCommitHandle? StagedCommit { get; private init; }

    /// <summary>
    /// The in-flight publish's reference. Null unless pending.
    /// </summary>
    /// <remarks>
    /// Restored rather than reallocated: whoever confirms or rolls the publish
    /// back names it by this reference, and a restart that handed out a fresh
    /// one would leave the caller holding an id the engine has never seen.
    /// </remarks>
    public PendingStateRef? Reference { get; private init; }

    /// <summary>What kind of operation the pending publish was. Null unless pending.</summary>
    public PendingKind? PendingOperation { get; private init; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>A staged commit whose fate is unknown.</summary>
    public static EpochStateRecord Pending(
        GroupId groupId,
        EpochId newEpoch,
        EpochId priorEpoch,
        StagedCommitHandle stagedCommit,
        PendingStateRef reference,
        PendingKind pendingKind,
        DateTimeOffset updatedAt)
    {
        if (stagedCommit.Value is null)
        {
            throw new ArgumentException(
                "A pending publish without its staged commit cannot be reconciled after a "
                + "restart: there is nothing left to publish, confirm or discard.",
                nameof(stagedCommit));
        }

        return new EpochStateRecord(groupId, EpochStateKind.PendingPublish, newEpoch, updatedAt)
        {
            PriorEpoch = priorEpoch,
            StagedCommit = stagedCommit,
            Reference = reference,
            PendingOperation = pendingKind,
        };
    }

    /// <summary>A group frozen until a verified repair runs.</summary>
    public static EpochStateRecord Unrecoverable(
        GroupId groupId, EpochId lastStableEpoch, DateTimeOffset updatedAt) =>
        new(groupId, EpochStateKind.Unrecoverable, lastStableEpoch, updatedAt);

    /// <summary>A group that is terminally gone.</summary>
    public static EpochStateRecord Disbanded(
        GroupId groupId, EpochId epoch, DateTimeOffset updatedAt) =>
        new(groupId, EpochStateKind.Disbanded, epoch, updatedAt);
}
