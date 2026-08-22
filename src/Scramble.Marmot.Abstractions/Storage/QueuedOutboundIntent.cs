namespace Scramble.Marmot.Storage;

/// <summary>
/// An outbound operation persisted before it is sent, so it survives a crash
/// between the caller's request and the transport handing it off.
/// </summary>
/// <remarks>
/// Dark Matter queues sends while a group is not in a settled state and drains
/// the queue once it settles, so this is durable state rather than an in-memory
/// buffer. The payload is opaque here: the engine owns its interpretation.
/// </remarks>
public sealed record QueuedOutboundIntent(
    MessageId Id,
    GroupId GroupId,
    string IntentKind,
    byte[] Payload,
    DateTimeOffset CreatedAt)
{
    /// <summary>Attempts made to drain this intent, for bounded retry.</summary>
    public int Attempts { get; init; }
}
