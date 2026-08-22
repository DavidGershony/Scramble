namespace Scramble.Marmot.Storage;

/// <summary>
/// Lifecycle of a stored inbound or outbound message record.
/// </summary>
public enum MessageRecordState
{
    /// <summary>Persisted on receipt, not yet processed.</summary>
    Created,

    /// <summary>Processing failed in a way that may succeed later; eligible for retry.</summary>
    Retryable,

    /// <summary>
    /// Could not be unwrapped by the transport peeler yet — typically the
    /// epoch's exporter secret is not available. Retried under a bounded
    /// budget before becoming <see cref="Failed"/>.
    /// </summary>
    PeelDeferred,

    /// <summary>Applied to group state. Terminal for inbound messages.</summary>
    Processed,

    /// <summary>Terminally rejected. Never retried.</summary>
    Failed,

    /// <summary>
    /// Was processed, but the epoch it belonged to lost a fork and was rolled
    /// back. Retained for audit rather than deleted, so the UI can explain a
    /// disappearing message.
    /// </summary>
    EpochInvalidated,

    /// <summary>Locally originated and handed to the transport. Terminal for outbound.</summary>
    Sent,
}
