namespace Scramble.Marmot;

/// <summary>
/// An MLS group identifier.
/// </summary>
/// <remarks>
/// Distinct from the transport routing id: Dark Matter carries the Nostr
/// routing id in an app component, and that id can rotate independently of
/// this one. Never use one where the other is meant.
/// </remarks>
public readonly record struct GroupId(byte[] Value)
{
    public bool Equals(GroupId other) =>
        Value.AsSpan().SequenceEqual(other.Value.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Value);
        return hash.ToHashCode();
    }

    public override string ToString() => Convert.ToHexString(Value).ToLowerInvariant();
}
