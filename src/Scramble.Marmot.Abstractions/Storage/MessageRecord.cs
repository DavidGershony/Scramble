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
    /// <summary>How many times processing has been attempted, for the retry budget.</summary>
    public int Attempts { get; init; }

    /// <summary>Why the record is in its current state, for diagnostics. Never load-bearing.</summary>
    public string? Reason { get; init; }
}
