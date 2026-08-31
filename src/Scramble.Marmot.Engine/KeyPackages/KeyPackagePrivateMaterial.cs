using Scramble.Marmot.AppComponents;

namespace Scramble.Marmot.Engine.KeyPackages;

/// <summary>
/// The three private keys a Welcome needs to open a KeyPackage.
/// </summary>
/// <remarks>
/// <para>
/// All three, because <c>ProcessWelcome</c> takes all three. The
/// <c>init_key</c> decrypts the Welcome's group secrets, the leaf HPKE key
/// decrypts the path secret addressed to our leaf, and the signature key signs
/// everything we send afterwards. A bundle missing one is a KeyPackage that can
/// be published and never joined against — the exact failure the previous
/// implementation shipped.
/// </para>
/// <para>
/// This is device-local storage, never wire format. It is nonetheless
/// length-prefixed and versioned rather than concatenated: key sizes differ by
/// ciphersuite, so a fixed-offset reader would silently mis-split a bundle
/// written under a different suite.
/// </para>
/// </remarks>
/// <param name="InitPrivateKey">HPKE private key for the KeyPackage's init key.</param>
/// <param name="LeafPrivateKey">HPKE private key for the leaf's encryption key.</param>
/// <param name="SignaturePrivateKey">
/// The leaf's MLS signature private key. Distinct from the Nostr account key:
/// the account-identity proof exists precisely to bind these two together
/// without making them the same key.
/// </param>
public sealed record KeyPackagePrivateMaterial(
    byte[] InitPrivateKey,
    byte[] LeafPrivateKey,
    byte[] SignaturePrivateKey)
{
    /// <summary>The only format version.</summary>
    public const byte Version = 1;

    /// <summary>
    /// Generous per-key bound. Present so a corrupt length prefix cannot make a
    /// decoder allocate; no MLS private key is anywhere near it.
    /// </summary>
    private const int MaxKeyLength = 1024;

    /// <summary>Serializes the bundle for <c>KeyPackageRecord.PrivateMaterial</c>.</summary>
    public byte[] Encode()
    {
        var output = new List<byte> { Version };
        ComponentCodec.WriteVarBytes(InitPrivateKey, output);
        ComponentCodec.WriteVarBytes(LeafPrivateKey, output);
        ComponentCodec.WriteVarBytes(SignaturePrivateKey, output);
        return output.ToArray();
    }

    /// <summary>Reads a bundle back.</summary>
    /// <exception cref="AppComponentException">
    /// Unknown version, truncated, or with bytes left over.
    /// </exception>
    public static KeyPackagePrivateMaterial Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            throw new AppComponentException("The private material bundle is empty.");

        if (bytes[0] != Version)
            throw new AppComponentException(
                $"Unknown private material version {bytes[0]}.");

        ReadOnlySpan<byte> cursor = bytes[1..];
        byte[] init = ComponentCodec.ReadVarBytes(ref cursor, MaxKeyLength, "init private key");
        byte[] leaf = ComponentCodec.ReadVarBytes(ref cursor, MaxKeyLength, "leaf private key");
        byte[] signature = ComponentCodec.ReadVarBytes(ref cursor, MaxKeyLength, "signature private key");

        ComponentCodec.RequireSpent(cursor, "private material bundle");
        return new KeyPackagePrivateMaterial(init, leaf, signature);
    }
}
