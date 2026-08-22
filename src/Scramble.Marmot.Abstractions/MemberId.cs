namespace Scramble.Marmot;

/// <summary>
/// A member's account identity — the 32-byte x-only secp256k1 public key
/// carried in the MLS BasicCredential.
/// </summary>
public readonly record struct MemberId(byte[] Value)
{
    public bool Equals(MemberId other) =>
        Value.AsSpan().SequenceEqual(other.Value.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Value);
        return hash.ToHashCode();
    }

    public override string ToString() => Convert.ToHexString(Value).ToLowerInvariant();
}
