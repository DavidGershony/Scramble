namespace Scramble.Marmot.Storage;

/// <summary>
/// How far a commit got towards a transport, in a form that survives a restart.
/// </summary>
/// <remarks>
/// <para>
/// The numbers are written into the database, so they are pinned here rather
/// than left to declaration order. Renumbering one silently re-labels every row
/// already stored.
/// </para>
/// </remarks>
public enum CommitPublishState
{
    /// <summary>
    /// The bytes were handed to a transport and no answer has come back.
    /// </summary>
    /// <remarks>
    /// Written <b>before</b> the handoff, so a crash anywhere in the handoff
    /// leaves this. It deliberately over-claims: a crash between the write and
    /// the send records a commit that never left the device. That costs one
    /// relay query to settle, where the opposite ordering costs the fact that
    /// the commit exists at all.
    /// </remarks>
    HandedToTransport = 1,

    /// <summary>The transport confirmed it. The commit is live.</summary>
    /// <remarks>
    /// Written before the local apply, because the window between "the relay
    /// has it" and "we have applied it" is the one a crash must not resolve as
    /// abandonment — the rest of the group has already moved.
    /// </remarks>
    Accepted = 2,

    /// <summary>The transport definitively refused it. No copy exists anywhere.</summary>
    /// <remarks>
    /// The only state that authorises abandoning the commit. Report it only
    /// when the commit provably never landed.
    /// </remarks>
    Rejected = 3,

    /// <summary>The attempt neither succeeded nor provably failed.</summary>
    /// <remarks>
    /// A timeout, a dropped socket, a throwing transport. The commit may be
    /// live. Distinct from <see cref="HandedToTransport"/> not because the
    /// verdict differs — it does not — but because one is an answer and the
    /// other is the absence of one, and a retry policy that cannot tell them
    /// apart will re-send a commit a relay already told us it could not judge.
    /// </remarks>
    Indeterminate = 4,
}

/// <summary>
/// What a restart should do with a commit it finds staged and unresolved.
/// </summary>
/// <remarks>
/// Three answers, and the two that look adjacent want opposite moves. Getting
/// either wrong forks the group: abandon a commit a relay holds and we stay
/// behind alone while everyone else advances; adopt one that never left and we
/// advance into an epoch nobody can reach.
/// </remarks>
public enum StrandedCommitVerdict
{
    /// <summary>
    /// Nobody else can have seen it. Discard the commit and stay where we are.
    /// </summary>
    Abandon,

    /// <summary>
    /// A transport may hold it. Ask the relay before deciding anything.
    /// </summary>
    Reconcile,

    /// <summary>
    /// A transport confirmed it and we died before applying it. The group has
    /// moved without us; catch up.
    /// </summary>
    Adopt,
}

/// <summary>
/// One group's in-flight commit publish, written down.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this exists for.</b> <c>StagedCommit.Publishing()</c> draws the one
/// line that matters for crash recovery — before it the commit is ours alone
/// and clearing it is free, after it a relay may hold it and clearing it
/// locally is the single move that guarantees a fork. That line is a private
/// field in an in-memory object, so across a restart nothing says which side of
/// it we were on. This row says.
/// </para>
/// <para>
/// <b>It is not the same fact as the pending epoch state.</b>
/// <see cref="EpochStateRecord"/> records that a commit was <i>staged</i>, and
/// is written before the group moves. This records that its bytes were
/// <i>handed to a transport</i>, and is written before the send. Two moments,
/// two orderings, two rows — folding them would mean one clear erasing the
/// other's fact at exactly the instant it is needed, since confirming a publish
/// clears the epoch-state row.
/// </para>
/// <para>
/// <b>Keyed by the group.</b> A group holds at most one staged commit — MLS
/// refuses a second — so a second row for one group could only describe a
/// commit that no longer exists, and which of the two described the group would
/// be a coin flip at exactly the moment, session open after a crash, when
/// nobody is there to arbitrate.
/// </para>
/// <para>
/// <b>Built through factories, never a public constructor.</b> Same reasoning
/// as <see cref="EpochStateRecord"/> and <c>StagedCommit</c>: a positional
/// record's primary constructor is public, and a row built through it could
/// name a state the transition rules forbid — an attempt that went straight to
/// <see cref="CommitPublishState.Accepted"/> without ever recording that the
/// bytes left, which is the one shape the row exists to make impossible.
/// </para>
/// </remarks>
public sealed class CommitPublishAttempt
{
    private CommitPublishAttempt(
        GroupId groupId,
        MessageId commitId,
        EpochId newEpoch,
        CommitPublishState state,
        DateTimeOffset handedOffAt,
        DateTimeOffset updatedAt)
    {
        GroupId = groupId;
        CommitId = commitId;
        NewEpoch = newEpoch;
        State = state;
        HandedOffAt = handedOffAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>The group whose commit this is.</summary>
    public GroupId GroupId { get; }

    /// <summary>
    /// The commit's content id — SHA-256 over its MLS bytes.
    /// </summary>
    /// <remarks>
    /// The whole point of storing it is that a reconciling session can ask a
    /// relay "do you have this?" and compare the answer. That only works if the
    /// digest is over the MLS message, which every peer computes the same way,
    /// and not over the transport envelope, which a relay is free to
    /// re-serialise — the same mistake that once made our own commit come back
    /// looking like a competitor's.
    /// </remarks>
    public MessageId CommitId { get; }

    /// <summary>The epoch the group reaches if this commit landed.</summary>
    /// <remarks>
    /// Stored so a recovering session can tell an unresolved row apart from a
    /// row it already acted on: if live state is already at this epoch, the
    /// apply happened and only the clear was lost.
    /// </remarks>
    public EpochId NewEpoch { get; }

    public CommitPublishState State { get; }

    /// <summary>When the bytes were handed off. Never changes.</summary>
    public DateTimeOffset HandedOffAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>
    /// What a restart should do with the commit this row describes.
    /// </summary>
    public StrandedCommitVerdict Verdict => State switch
    {
        // Rejected is the only state that authorises abandoning a commit, and
        // it is the same asymmetry the KeyPackage publisher turns on: a
        // timeout is not a rejection. Reading Indeterminate as failure here
        // would discard a commit a relay may be serving to every other member.
        CommitPublishState.Rejected => StrandedCommitVerdict.Abandon,
        CommitPublishState.Accepted => StrandedCommitVerdict.Adopt,
        CommitPublishState.HandedToTransport or CommitPublishState.Indeterminate =>
            StrandedCommitVerdict.Reconcile,

        // The exhaustiveness arm. NOT LOAD-BEARING: it survives mutation,
        // because the factories and the storage reader between them make a row
        // with an unknown state unbuildable, so no test can reach it. Kept
        // because Reconcile is the only safe answer if one ever does — Abandon
        // risks the fork we cannot come back from, and Adopt asserts an epoch
        // nothing has evidence the group reached.
        _ => StrandedCommitVerdict.Reconcile,
    };

    /// <summary>
    /// What to do about a commit found staged, given whatever row it has.
    /// </summary>
    /// <remarks>
    /// <b>No row means Abandon, and that is the load-bearing half.</b> A commit
    /// staged and never handed to a transport leaves nothing behind, so the
    /// absence of a row is the positive statement "nobody else can have seen
    /// this" — which is exactly the case where clearing it is free and keeping
    /// it advances us alone.
    /// </remarks>
    public static StrandedCommitVerdict VerdictFor(CommitPublishAttempt? attempt) =>
        attempt?.Verdict ?? StrandedCommitVerdict.Abandon;

    /// <summary>
    /// The bytes are about to go to a transport.
    /// </summary>
    /// <remarks>
    /// Write this and wait for it before sending anything. Everything after the
    /// send has to reason about a row that already exists.
    /// </remarks>
    public static CommitPublishAttempt HandedToTransport(
        GroupId groupId, MessageId commitId, EpochId newEpoch, DateTimeOffset at)
    {
        if (commitId.Value is null || commitId.Value.Length == 0)
        {
            throw new ArgumentException(
                "A publish attempt with no commit id cannot be reconciled: there is nothing "
                + "to ask a relay about.",
                nameof(commitId));
        }

        return new CommitPublishAttempt(
            groupId,
            commitId,
            newEpoch,
            CommitPublishState.HandedToTransport,
            handedOffAt: at,
            updatedAt: at);
    }

    /// <summary>
    /// The transport answered. Records what it said, keeping the handoff time.
    /// </summary>
    /// <remarks>
    /// Refuses a move back to <see cref="CommitPublishState.HandedToTransport"/>
    /// and a second resolution. Both would rewrite history a recovering session
    /// reads as fact — the first turning a relay's definite "no" back into "ask
    /// it again", the second letting a retry's rejection overwrite an earlier
    /// acceptance and authorise discarding a commit that is live.
    /// </remarks>
    public CommitPublishAttempt Resolved(CommitPublishState state, DateTimeOffset at)
    {
        if (state == CommitPublishState.HandedToTransport)
        {
            throw new ArgumentException(
                "HandedToTransport is not an answer a transport can give; it is what the row "
                + "says before one arrives.",
                nameof(state));
        }

        if (State != CommitPublishState.HandedToTransport)
        {
            throw new InvalidOperationException(
                $"This publish attempt is already resolved as {State}; a second answer would "
                + "overwrite what the transport actually said.");
        }

        return new CommitPublishAttempt(
            GroupId, CommitId, NewEpoch, state, HandedOffAt, updatedAt: at);
    }

    /// <summary>Rebuilds a row read back from storage.</summary>
    /// <remarks>
    /// The one way to construct a resolved attempt without going through
    /// <see cref="Resolved"/>, and it exists only for the storage layer, which
    /// is replaying a transition that already happened rather than making one.
    /// </remarks>
    public static CommitPublishAttempt FromStorage(
        GroupId groupId,
        MessageId commitId,
        EpochId newEpoch,
        CommitPublishState state,
        DateTimeOffset handedOffAt,
        DateTimeOffset updatedAt) =>
        new(groupId, commitId, newEpoch, state, handedOffAt, updatedAt);
}
