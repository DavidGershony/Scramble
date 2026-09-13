namespace Scramble.Marmot;

/// <summary>
/// An opaque reference to a staged, unmerged MLS commit.
/// </summary>
public readonly record struct StagedCommitHandle(byte[] Value)
{
    public bool Equals(StagedCommitHandle other) =>
        Value.AsSpan().SequenceEqual(other.Value.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Value);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Identifies one in-flight publish so its confirmation or failure can be
/// matched back to the group it belongs to.
/// </summary>
public readonly record struct PendingStateRef(ulong Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>
/// What kind of operation a pending publish represents.
/// </summary>
/// <remarks>
/// Beside the state machine rather than with the manager that uses it, because
/// it is part of what a pending publish has to be restored from: a commit
/// recovered after a restart has to be reconciled as the operation it was, and
/// a disband cannot be retried as a group evolution.
/// </remarks>
public enum PendingKind
{
    CreateGroup,
    GroupEvolution,
    Disband,
}

/// <summary>
/// Raised when a caller attempts a transition the state machine forbids.
/// </summary>
public sealed class InvalidEpochTransitionException : InvalidOperationException
{
    public InvalidEpochTransitionException(string from, string to, string reason)
        : base($"Cannot move from {from} to {to}: {reason}.")
    {
        From = from;
        To = to;
        Reason = reason;
    }

    public string From { get; }

    public string To { get; }

    public string Reason { get; }
}

/// <summary>
/// Where a single group sits in the publish-before-apply lifecycle.
/// </summary>
/// <remarks>
/// Dark Matter never applies a commit before the transport confirms it, so a
/// group is not simply "at epoch N" — it may be holding a staged commit whose
/// fate is unknown. This type makes that explicit, and makes the illegal moves
/// unrepresentable rather than merely discouraged.
/// </remarks>
public abstract record EpochState
{
    private EpochState()
    {
    }

    /// <summary>Settled at an epoch. The only state a new commit may be staged from.</summary>
    public sealed record Stable(EpochId Epoch) : EpochState;

    /// <summary>
    /// A commit is staged locally and published, but not yet confirmed.
    /// <paramref name="Epoch"/> is the epoch the group reaches once confirmed.
    /// </summary>
    public sealed record PendingPublish(
        EpochId Epoch,
        StagedCommitHandle StagedCommit,
        PendingStateRef Reference) : EpochState;

    /// <summary>Publish confirmed; the commit is being applied to local MLS state.</summary>
    public sealed record Merging(EpochId Epoch) : EpochState;

    /// <summary>
    /// A fork was detected. Canonical state is ambiguous, so the current epoch
    /// is undefined and <paramref name="LastStableEpoch"/> is what we can still
    /// assert. Ingest continues, buffering what arrives, because convergence
    /// needs those inputs to choose a branch.
    /// </summary>
    public sealed record Recovering(
        EpochId LastStableEpoch,
        IReadOnlyList<MessageId> Buffered) : EpochState;

    /// <summary>
    /// No branch can be validated from retained material. State is frozen and
    /// the engine MUST stop applying and ingesting group-state changes until a
    /// verified repair path runs.
    /// </summary>
    public sealed record Unrecoverable(EpochId LastStableEpoch) : EpochState;

    /// <summary>The group was terminally disbanded. There is no way out.</summary>
    public sealed record Disbanded(EpochId Epoch) : EpochState;

    /// <summary>
    /// The epoch this state can assert. For <see cref="Recovering"/> and
    /// <see cref="Unrecoverable"/> this is the last stable epoch, because the
    /// current one is ambiguous by definition.
    /// </summary>
    public EpochId CurrentEpoch => this switch
    {
        Stable s => s.Epoch,
        PendingPublish p => p.Epoch,
        Merging m => m.Epoch,
        Recovering r => r.LastStableEpoch,
        Unrecoverable u => u.LastStableEpoch,
        Disbanded d => d.Epoch,
        _ => throw new InvalidOperationException($"Unhandled epoch state {GetType().Name}."),
    };

    /// <summary>
    /// Whether inbound messages may be ingested now.
    /// </summary>
    /// <remarks>
    /// <see cref="PendingPublish"/> and <see cref="Merging"/> buffer instead,
    /// because applying an inbound commit mid-publish would race our own.
    /// <see cref="Recovering"/> accepts: convergence needs the inputs.
    /// <see cref="Unrecoverable"/> and <see cref="Disbanded"/> reject.
    /// </remarks>
    public bool CanIngest => this is Stable or Recovering;

    public bool IsStable => this is Stable;

    public bool IsUnrecoverable => this is Unrecoverable;

    public bool IsDisbanded => this is Disbanded;

    /// <summary>Short name, used in transition errors and logs.</summary>
    public string Name => this switch
    {
        Stable => nameof(Stable),
        PendingPublish => nameof(PendingPublish),
        Merging => nameof(Merging),
        Recovering => nameof(Recovering),
        Unrecoverable => nameof(Unrecoverable),
        Disbanded => nameof(Disbanded),
        _ => GetType().Name,
    };

    // -- Transitions --

    /// <summary>Stable to PendingPublish. Legal only from Stable.</summary>
    public EpochState BeginPending(
        EpochId newEpoch,
        StagedCommitHandle stagedCommit,
        PendingStateRef reference) =>
        this is Stable
            ? new PendingPublish(newEpoch, stagedCommit, reference)
            : throw Illegal(nameof(PendingPublish), "staging a commit requires Stable");

    /// <summary>PendingPublish to Merging, on transport confirmation.</summary>
    public EpochState ConfirmPublish() =>
        this is PendingPublish p
            ? new Merging(p.Epoch)
            : throw Illegal(nameof(Merging), "confirming a publish requires PendingPublish");

    /// <summary>
    /// PendingPublish back to Stable at the prior epoch, when publishing fails
    /// and the staged commit is discarded.
    /// </summary>
    public EpochState RollbackPending(EpochId priorEpoch) =>
        this is PendingPublish
            ? new Stable(priorEpoch)
            : throw Illegal(nameof(Stable), "rolling back a publish requires PendingPublish");

    /// <summary>Merging to Stable, once the commit is applied locally.</summary>
    public EpochState MergeToStable(EpochId nextEpoch) =>
        this is Merging
            ? new Stable(nextEpoch)
            : throw Illegal(nameof(Stable), "merging requires Merging");

    /// <summary>
    /// To Recovering, from any state the group is still running in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fork can be discovered at any time, and from a live group this is
    /// always the right move: <see cref="Recovering"/> keeps ingesting, because
    /// convergence needs the inputs to choose a branch.
    /// </para>
    /// <para>
    /// <b>Refused from <see cref="Unrecoverable"/> and <see cref="Disbanded"/>,
    /// which is what those two states are for.</b> Recovering ingests — so
    /// allowing this would let inbound traffic restart a group frozen pending a
    /// verified repair, or resurrect one that is terminally gone.
    /// <see cref="RepairToStable"/> is documented as the only way out of the
    /// first and the second has no way out at all, and a transition that walks
    /// around both is not a smaller mistake for being a quiet one.
    /// </para>
    /// <para>
    /// Nothing is lost by refusing. A frozen group froze precisely because no
    /// branch could be validated from what it retained, so being told that
    /// there is a fork is not news to it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidEpochTransitionException">
    /// The group is frozen or disbanded.
    /// </exception>
    public EpochState DetectFork(IReadOnlyList<MessageId> buffered) =>
        this is Unrecoverable or Disbanded
            ? throw Illegal(
                nameof(Recovering),
                "a frozen or disbanded group does not reopen on ordinary traffic")
            : new Recovering(CurrentEpoch, buffered);

    /// <summary>To Unrecoverable. Always legal; freezes the current epoch.</summary>
    public EpochState ToUnrecoverable() => new Unrecoverable(CurrentEpoch);

    /// <summary>
    /// Unrecoverable to Stable. The only way out, and only after a verified
    /// repair — never as a side effect of ordinary traffic.
    /// </summary>
    public EpochState RepairToStable(EpochId epoch) =>
        this is Unrecoverable
            ? new Stable(epoch)
            : throw Illegal(nameof(Stable), "repair requires Unrecoverable");

    /// <summary>
    /// Recovering to Disbanded. Terminalisation is legal only once a bounded
    /// convergence pass has actually selected a disband branch.
    /// </summary>
    public EpochState SettleToDisbanded(EpochId epoch) =>
        this is Recovering
            ? new Disbanded(epoch)
            : throw Illegal(nameof(Disbanded), "disbanding requires Recovering");

    private InvalidEpochTransitionException Illegal(string to, string reason) =>
        new(Name, to, reason);
}
