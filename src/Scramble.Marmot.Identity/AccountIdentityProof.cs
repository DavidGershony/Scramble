using System.Buffers.Binary;
using Scramble.Nostr.Crypto;

namespace Scramble.Marmot.Identity;

/// <summary>
/// The account-identity proof that binds an MLS leaf key to a Nostr account.
/// </summary>
/// <remarks>
/// <para>
/// Carried as app component <c>0x8009</c> in a LeafNode's app-data dictionary,
/// as exactly 104 bytes:
/// </para>
/// <code>
/// struct {
///   opaque signer_pubkey[32];   // x-only secp256k1 account key
///   uint64 created_at;          // unsigned, BIG-endian, unix seconds
///   opaque signature[64];       // BIP-340 Schnorr
/// } MarmotAuthorizationProof;
/// </code>
/// <para>
/// The signature is over the id of a kind:450 Nostr event that is never
/// published — it exists so an external signer that can only sign Nostr events
/// (Amber, NIP-46) can produce the proof without exposing raw key access.
/// Producer and verifier reconstruct that event identically; a single byte of
/// difference means universal rejection, so the construction below is exact
/// and deliberately not "cleaned up".
/// </para>
/// </remarks>
public sealed record AccountIdentityProof(
    byte[] SignerPublicKey,
    ulong CreatedAt,
    byte[] Signature)
{
    /// <summary>App component id carrying this proof.</summary>
    public const ushort ComponentId = 0x8009;

    /// <summary>Exact encoded size. Anything else is invalid.</summary>
    public const int EncodedLength = 104;

    /// <summary>Kind of the signing event. Never published to a relay.</summary>
    public const int SigningEventKind = 450;

    /// <summary>Content of the signing event, shown on an external signer's consent screen.</summary>
    public const string SigningEventContent = "Authorize this MLS leaf key for my Marmot account";

    private const string DTagValue = "marmot.account-identity-proof.v2";

    /// <summary>Largest valid <c>created_at</c>: 2^53 - 1, so it survives a JSON number.</summary>
    public const ulong MaxCreatedAt = 9007199254740991;

    /// <summary>Encodes the proof to its 104-byte component form.</summary>
    public byte[] Encode()
    {
        if (SignerPublicKey.Length != 32)
            throw new ArgumentException("Signer public key must be 32 bytes.", nameof(SignerPublicKey));
        if (Signature.Length != 64)
            throw new ArgumentException("Signature must be 64 bytes.", nameof(Signature));

        var buffer = new byte[EncodedLength];
        SignerPublicKey.CopyTo(buffer, 0);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(32, 8), CreatedAt);
        Signature.CopyTo(buffer, 40);
        return buffer;
    }

    /// <summary>
    /// Decodes the 104-byte component form.
    /// </summary>
    /// <remarks>
    /// Trailing bytes are rejected rather than ignored: the field is
    /// fixed-width, so anything longer is a different structure.
    /// </remarks>
    public static bool TryDecode(ReadOnlySpan<byte> data, out AccountIdentityProof? proof)
    {
        proof = null;
        if (data.Length != EncodedLength)
            return false;

        proof = new AccountIdentityProof(
            data[..32].ToArray(),
            BinaryPrimitives.ReadUInt64BigEndian(data.Slice(32, 8)),
            data.Slice(40, 64).ToArray());
        return true;
    }

    /// <summary>
    /// Rebuilds the kind:450 event whose id this proof signs.
    /// </summary>
    /// <remarks>
    /// The tags are exactly these five, in exactly this order, with no others.
    /// Hex is lowercase throughout; ciphersuite and signature scheme are
    /// <c>0x</c> followed by exactly four lowercase hex digits, while the leaf
    /// signature key is bare lowercase hex with no prefix and no length prefix.
    /// </remarks>
    public static NostrEventTemplate BuildSigningEvent(
        ReadOnlySpan<byte> signerPublicKey,
        ulong createdAt,
        ushort cipherSuite,
        ushort signatureScheme,
        ReadOnlySpan<byte> mlsSignatureKey) =>
        new(
            PublicKeyHex: Hex(signerPublicKey),
            CreatedAt: checked((long)createdAt),
            Kind: SigningEventKind,
            Tags: new[]
            {
                new[] { "d", DTagValue },
                new[] { "component", Hex16(ComponentId) },
                new[] { "ciphersuite", Hex16(cipherSuite) },
                new[] { "signature_scheme", Hex16(signatureScheme) },
                new[] { "mls_signature_key", Hex(mlsSignatureKey) },
            },
            Content: SigningEventContent);

    /// <summary>
    /// Verifies this proof against the leaf it is supposed to authorise.
    /// </summary>
    /// <param name="credentialIdentity">
    /// The identity from the leaf's BasicCredential. Must equal the proof's
    /// signer key, or the proof authorises a different account than the leaf
    /// claims.
    /// </param>
    /// <param name="cipherSuite">The ciphersuite of the validated context.</param>
    /// <param name="signatureScheme">The signature scheme of the validated context.</param>
    /// <param name="mlsSignatureKey">The leaf's signature key, unprefixed.</param>
    /// <remarks>
    /// Deliberately performs no wall-clock check on <c>created_at</c>. A proof
    /// stays valid as long as its signed inputs are unchanged, and comparing
    /// against local time would reject valid members over clock skew.
    /// </remarks>
    public AccountIdentityProofResult Validate(
        ReadOnlySpan<byte> credentialIdentity,
        ushort cipherSuite,
        ushort signatureScheme,
        ReadOnlySpan<byte> mlsSignatureKey)
    {
        if (SignerPublicKey.Length != 32 || Signature.Length != 64)
            return AccountIdentityProofResult.Malformed;

        if (credentialIdentity.Length != 32 || !credentialIdentity.SequenceEqual(SignerPublicKey))
            return AccountIdentityProofResult.IdentityMismatch;

        if (!Bip340.IsValidXOnlyPublicKey(SignerPublicKey))
            return AccountIdentityProofResult.InvalidSignerKey;

        // Zero is what distinguishes this from the superseded construction.
        if (CreatedAt == 0 || CreatedAt > MaxCreatedAt)
            return AccountIdentityProofResult.CreatedAtOutOfRange;

        var template = BuildSigningEvent(
            SignerPublicKey, CreatedAt, cipherSuite, signatureScheme, mlsSignatureKey);

        return Bip340.Verify(SignerPublicKey, template.ComputeId(), Signature)
            ? AccountIdentityProofResult.Valid
            : AccountIdentityProofResult.BadSignature;
    }

    internal static string Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(value).ToLowerInvariant();

    private static string Hex16(ushort value) => "0x" + value.ToString("x4");
}

/// <summary>Why a proof was accepted or rejected.</summary>
public enum AccountIdentityProofResult
{
    Valid,

    /// <summary>Wrong length, or a field that cannot be a key or signature.</summary>
    Malformed,

    /// <summary>The signer key does not match the leaf's credential identity.</summary>
    IdentityMismatch,

    /// <summary>The signer key is not a point on the curve.</summary>
    InvalidSignerKey,

    /// <summary>Zero, or beyond what a JSON number can carry exactly.</summary>
    CreatedAtOutOfRange,

    /// <summary>The Schnorr signature does not verify over the reconstructed event id.</summary>
    BadSignature,
}
