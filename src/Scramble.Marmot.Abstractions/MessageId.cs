namespace Scramble.Marmot;

/// <summary>
/// A durable identifier for a message the engine has seen.
/// </summary>
/// <remarks>
/// For group messages this is <b>content-derived</b> — SHA-256 over the MLS
/// message bytes — and NOT the transport event id. Dark Matter requires
/// deduplication to survive the same MLS message arriving under different
/// transport envelopes, so a transport id can only ever be a cheap pre-filter.
/// Using a Nostr event id here is a conformance bug, and it is the exact
/// mistake the previous engine made.
/// </remarks>
public readonly record struct MessageId(byte[] Value)
{
    public bool Equals(MessageId other) =>
        Value.AsSpan().SequenceEqual(other.Value.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Value);
        return hash.ToHashCode();
    }

    public override string ToString() => Convert.ToHexString(Value).ToLowerInvariant();

    /// <summary>Derives the content id of an MLS message from its wire bytes.</summary>
    public static MessageId FromMlsBytes(ReadOnlySpan<byte> mlsBytes) =>
        new(System.Security.Cryptography.SHA256.HashData(mlsBytes));
}
