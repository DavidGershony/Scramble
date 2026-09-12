namespace Scramble.Marmot.Storage;

/// <summary>Durable message records and deduplication.</summary>
public interface IMessageStorage
{
    Task PutMessageAsync(MessageRecord message, CancellationToken ct = default);

    /// <summary>Looks a record up by its content-derived id.</summary>
    Task<MessageRecord?> GetMessageAsync(MessageId id, CancellationToken ct = default);

    Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(
        GroupId groupId,
        CancellationToken ct = default);

    Task<IReadOnlyList<MessageRecord>> ListMessagesByStateAsync(
        GroupId groupId,
        MessageRecordState state,
        CancellationToken ct = default);

    /// <summary>
    /// Records a transport envelope id as seen. This is a pre-filter to avoid
    /// re-peeling a duplicate envelope; the authoritative dedup key is the
    /// content-derived <see cref="MessageId"/>.
    /// </summary>
    Task PutTransportSeenAsync(string transportId, CancellationToken ct = default);

    Task<bool> HasTransportSeenAsync(string transportId, CancellationToken ct = default);

    /// <summary>
    /// Marks every <b>delivered</b> record produced after <paramref name="epoch"/>
    /// as <see cref="MessageRecordState.EpochInvalidated"/> after a fork
    /// rollback. Records are retained, not deleted, so the loss stays
    /// explainable.
    /// </summary>
    /// <remarks>
    /// <see cref="MessageRecordState.Processed"/> only, and that is the whole
    /// rule. A record still waiting to be read was never part of our history, so
    /// there is nothing about it to invalidate — and after a reorg the messages
    /// the adopted branch carried are precisely the ones sitting undelivered,
    /// waiting for that branch to arrive. Sweeping by epoch alone would discard
    /// the history the reorg was performed to gain.
    /// </remarks>
    Task InvalidateAfterEpochAsync(GroupId groupId, EpochId epoch, CancellationToken ct = default);
}
