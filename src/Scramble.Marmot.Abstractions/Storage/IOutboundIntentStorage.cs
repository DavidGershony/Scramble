namespace Scramble.Marmot.Storage;

/// <summary>Durable queue of outbound operations awaiting a settled group.</summary>
public interface IOutboundIntentStorage
{
    Task PutIntentAsync(QueuedOutboundIntent intent, CancellationToken ct = default);

    Task<IReadOnlyList<QueuedOutboundIntent>> ListIntentsAsync(
        GroupId groupId,
        CancellationToken ct = default);

    Task DeleteIntentAsync(MessageId id, CancellationToken ct = default);

    /// <summary>
    /// Drops every queued intent for a group. Used when the local member is
    /// evicted: queued sends must never be drained into a group we have left.
    /// </summary>
    Task ClearIntentsAsync(GroupId groupId, CancellationToken ct = default);
}
