namespace Scramble.Marmot.Storage;

/// <summary>
/// A durable record of one message the engine has seen, keyed by its
/// content-derived <see cref="MessageId"/>.
/// </summary>
/// <param name="Id">Content-derived id — see <see cref="MessageId"/>.</param>
/// <param name="TransportId">
/// The transport envelope id (e.g. a Nostr event id), when known. A cheap
/// pre-filter only: never the deduplication key.
/// </param>
/// <param name="SourceEpoch">The epoch the message was produced in.</param>
/// <param name="Wire">The MLS message bytes, retained for replay and audit.</param>
public sealed record MessageRecord(
    MessageId Id,
    GroupId GroupId,
    string? TransportId,
    EpochId SourceEpoch,
    MessageRecordState State,
    byte[] Wire,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>How many times processing has been attempted. Diagnostic only.</summary>
    /// <remarks>
    /// Not a budget. What bounds retries is
    /// <see cref="LastAttemptEpoch"/> together with the delivery window — see
    /// there — and a count would give up on a message still perfectly
    /// deliverable while retrying one whose keys are long gone.
    /// </remarks>
    public int Attempts { get; init; }

    /// <summary>
    /// The group epoch we last tried to process this at, if ever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What makes a flood of undecryptable messages free.</b> A message held
    /// because its keys were unreachable can only become readable when the
    /// group's state moves; trying it again at the same epoch asks a question
    /// already answered. Without this, a peer can leave a quiet group with any
    /// number of such records and have every later pass re-attempt all of them.
    /// </para>
    /// <para>
    /// It also bounds the total attempts any one message can ever cost, without
    /// a count: at most one per epoch, and the delivery window retires it a few
    /// epochs on. Null means never attempted, which is not the same as attempted
    /// at epoch zero.
    /// </para>
    /// </remarks>
    public EpochId? LastAttemptEpoch { get; init; }

    /// <summary>Why the record is in its current state, for diagnostics. Never load-bearing.</summary>
    public string? Reason { get; init; }
}
