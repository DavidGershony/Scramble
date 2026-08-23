namespace Scramble.Marmot;

/// <summary>What a peeled transport envelope turned out to contain.</summary>
public enum PeeledContentKind
{
    /// <summary>An MLS handshake or application message for a known group.</summary>
    GroupMessage,

    /// <summary>An MLS Welcome admitting us to a group.</summary>
    Welcome,
}

/// <summary>
/// An unwrapped transport envelope, ready for the engine.
/// </summary>
/// <param name="TransportGroupId">
/// The routing id the envelope was addressed to. Not the MLS group id: it is
/// carried in an app component and can rotate independently, so the engine
/// resolves it through an index rather than assuming equality.
/// </param>
/// <param name="TransportId">The envelope's own id, used only as a dedup pre-filter.</param>
/// <param name="MlsBytes">The MLS message, which is what actually gets deduplicated and applied.</param>
public sealed record PeeledMessage(
    PeeledContentKind Kind,
    byte[]? TransportGroupId,
    string? TransportId,
    byte[] MlsBytes)
{
    /// <summary>
    /// Present only on a <see cref="PeeledContentKind.Welcome"/>.
    /// </summary>
    /// <remarks>
    /// Joining needs more than the MLS bytes: which KeyPackage was consumed,
    /// where the group's relays are, and who invited us. Carried here rather
    /// than through peeler state so a peeler stays safe to use concurrently.
    /// </remarks>
    public WelcomeDetails? Welcome { get; init; }
}

/// <summary>
/// The parts of a Welcome the join path needs beyond the MLS message.
/// </summary>
/// <param name="KeyPackageEventId">
/// The KeyPackage event consumed by this Welcome. Its private material may be
/// used exactly once, so the join path must be able to identify it.
/// </param>
/// <param name="Relays">Group relays the new member should use from now on.</param>
/// <param name="SenderPublicKeyHex">
/// The inviter, taken from the verified seal rather than from any unsigned
/// field.
/// </param>
public sealed record WelcomeDetails(
    byte[] KeyPackageEventId,
    IReadOnlyList<string> Relays,
    string SenderPublicKeyHex);

/// <summary>
/// Raised when an envelope cannot be unwrapped.
/// </summary>
/// <param name="Retryable">
/// Whether unwrapping could succeed later. A missing epoch secret is retryable
/// — the commit that produces it may not have arrived yet. A malformed
/// envelope is not, and retrying it forever is how a queue fills with garbage.
/// </param>
public sealed class PeelFailedException(string message, bool retryable = false)
    : Exception(message)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Converts between transport envelopes and MLS bytes.
/// </summary>
/// <remarks>
/// <para>
/// The engine is transport-agnostic: it never sees a Nostr event, only the MLS
/// bytes behind one. Everything about kinds, tags and event ids lives behind
/// this seam, which is what would let a second transport exist without the
/// engine knowing.
/// </para>
/// <para>
/// Implementations MUST fail cleanly rather than throw arbitrary exceptions,
/// and MUST distinguish retryable from terminal failure — the engine uses that
/// to decide between deferring a message and dropping it.
/// </para>
/// </remarks>
public interface ITransportPeeler
{
    /// <summary>
    /// Unwraps an inbound envelope.
    /// </summary>
    /// <param name="exporterSecretFor">
    /// Supplies the group's exporter secret for a routing id, or null when the
    /// group or its epoch secret is unknown.
    /// </param>
    /// <exception cref="PeelFailedException">The envelope could not be unwrapped.</exception>
    PeeledMessage Peel(string envelope, Func<byte[], byte[]?> exporterSecretFor);

    /// <summary>Wraps outbound MLS bytes for a group.</summary>
    /// <remarks>
    /// Deterministic given the same inputs, apart from the nonce and any
    /// timestamp, so the same message does not become two distinct events.
    /// </remarks>
    string WrapGroupMessage(
        ReadOnlySpan<byte> mlsBytes,
        ReadOnlySpan<byte> transportGroupId,
        ReadOnlySpan<byte> exporterSecret,
        long? expiresAt = null);
}
