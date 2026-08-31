using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Types;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Wire.Nostr;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Engine.KeyPackages;

/// <summary>Why a KeyPackage publication was refused.</summary>
public sealed class KeyPackagePublicationException(string message) : Exception(message);

/// <summary>
/// A kind-30443 publication whose contents have been checked against its tags.
/// </summary>
/// <param name="Publication">The event it came from.</param>
/// <param name="KeyPackage">The decoded KeyPackage.</param>
/// <param name="Proof">The account-identity proof out of the leaf.</param>
/// <param name="CredentialIdentity">
/// The leaf's credential identity: the member's Nostr account key, x-only.
/// </param>
public sealed record ValidatedKeyPackage(
    KeyPackagePublication Publication,
    KeyPackage KeyPackage,
    AccountIdentityProof Proof,
    byte[] CredentialIdentity);

/// <summary>
/// The checks a kind-30443 event needs that its codec cannot perform.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="KeyPackageEvent"/> decodes no MLS, so it verifies the Nostr
/// envelope and the tag shape and stops. Two checks are mandatory and were left
/// for whoever decodes the payload, which is here:
/// </para>
/// <list type="number">
/// <item>
/// The <c>i</c> tag must equal the KeyPackageRef of the KeyPackage actually
/// carried. It is the id a Welcome names, so a publication whose tag points at
/// a different KeyPackage is one an inviter cannot address.
/// </item>
/// <item>
/// The event's author must equal the leaf's credential identity. Without it
/// anyone can republish someone else's KeyPackage under their own key, and the
/// directory lookup — "which KeyPackage belongs to this npub" — answers with an
/// attacker's choice.
/// </item>
/// </list>
/// <para>
/// <b>Known gap, deliberately not papered over.</b> This does not verify the
/// KeyPackage or LeafNode <i>signatures</i>: <c>dotnet-mls</c> signs both and
/// exposes no way to verify either. What is checked still binds the account key
/// to the leaf signature key — that is exactly what the account-identity proof
/// is, and it is BIP-340-verified below. What is <b>not</b> yet checked is
/// possession of the leaf private key, so a party who copies a valid leaf and
/// substitutes their own <c>init_key</c> would receive the Welcome. That closes
/// with a KeyPackage-signature verification API in the MLS library, and it must
/// close before the invite path treats a fetched KeyPackage as trustworthy.
/// </para>
/// </remarks>
public static class KeyPackagePublicationValidator
{
    /// <summary>
    /// Validates a publication against the KeyPackage it carries.
    /// </summary>
    /// <param name="publication">A parsed kind-30443 event.</param>
    /// <param name="cs">
    /// The ciphersuite to hash the KeyPackageRef under. Must be the one the
    /// KeyPackage names: a ref computed under a different suite is a different
    /// hash function and never matches.
    /// </param>
    /// <param name="now">
    /// Unix seconds. When given, the leaf's lifetime must contain it. Pass null
    /// to check only that the window is one a peer would accept — which is what
    /// validating a publication we made ourselves wants.
    /// </param>
    /// <exception cref="KeyPackagePublicationException">Any check fails.</exception>
    public static ValidatedKeyPackage Validate(
        KeyPackagePublication publication, ICipherSuite cs, ulong? now = null)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(cs);

        KeyPackage keyPackage = DecodePublishedBytes(publication.KeyPackageBytes);

        if (keyPackage.CipherSuite != cs.Id)
        {
            throw new KeyPackagePublicationException(
                $"The KeyPackage names ciphersuite 0x{keyPackage.CipherSuite:x4}, " +
                $"but it is being validated under 0x{cs.Id:x4}.");
        }

        // (1) The tag must name the KeyPackage that is here. Recomputed over the
        // inner KeyPackage, never over the MLSMessage that frames it: the
        // envelope adds a version and a wire format, so hashing it yields a
        // reference no other implementation computes.
        byte[] keyPackageBytes = TlsCodec.Serialize(keyPackage.WriteTo);
        string computed = Convert.ToHexString(
            KeyPackageRef.Compute(cs, keyPackageBytes).Value).ToLowerInvariant();

        if (!string.Equals(computed, publication.KeyPackageRefHex, StringComparison.Ordinal))
        {
            throw new KeyPackagePublicationException(
                $"The i tag says {publication.KeyPackageRefHex}, but the KeyPackage hashes to {computed}.");
        }

        byte[] identity = CredentialIdentityOf(keyPackage.LeafNode);

        // (2) The author must be the member. Compared as bytes rather than as
        // hex strings so a case difference cannot decide it.
        byte[] author = ParseAuthor(publication.AuthorPublicKeyHex);
        if (!author.AsSpan().SequenceEqual(identity))
        {
            throw new KeyPackagePublicationException(
                "The event author is not the KeyPackage's credential identity.");
        }

        AccountIdentityProof proof = ReadProof(keyPackage.LeafNode);

        // Validated against the KeyPackage's own ciphersuite, not a default
        // (mdk#747): the suite is one of the proof's signed inputs, so checking
        // under the wrong one rejects a valid proof and — worse, once a second
        // suite exists — could accept one issued for another.
        var result = proof.Validate(
            identity,
            keyPackage.CipherSuite,
            MarmotKeyPackageBuilder.SignatureSchemeOf(keyPackage.CipherSuite),
            keyPackage.LeafNode.SignatureKey);

        if (result != AccountIdentityProofResult.Valid)
            throw new KeyPackagePublicationException($"The account-identity proof is {result}.");

        Lifetime lifetime = keyPackage.LeafNode.Lifetime
            ?? throw new KeyPackagePublicationException(
                "The leaf carries no lifetime, which RFC 9420 §10 requires of a KeyPackage leaf.");

        if (!KeyPackageLifetimePolicy.IsAcceptableRange(lifetime))
        {
            throw new KeyPackagePublicationException(
                $"The lifetime spans {lifetime.NotAfter - lifetime.NotBefore}s, over the " +
                $"{KeyPackageLifetimePolicy.MaxRangeSeconds}s a peer accepts.");
        }

        if (now is { } instant && !KeyPackageLifetimePolicy.IsValidAt(lifetime, instant))
            throw new KeyPackagePublicationException("The KeyPackage is expired or not yet valid.");

        return new ValidatedKeyPackage(publication, keyPackage, proof, identity);
    }

    private static KeyPackage DecodePublishedBytes(byte[] published)
    {
        MlsMessage message;
        try
        {
            message = MlsMessage.ReadFrom(new TlsReader(published));
        }
        catch (Exception ex) when (ex is TlsDecodingException or ArgumentException or InvalidOperationException)
        {
            throw new KeyPackagePublicationException(
                $"The event content is not a decodable MLSMessage: {ex.Message}");
        }

        if (message.WireFormat != WireFormat.MlsKeyPackage || message.Body is not KeyPackage keyPackage)
        {
            throw new KeyPackagePublicationException(
                $"The MLSMessage carries {message.WireFormat}, not a KeyPackage.");
        }

        return keyPackage;
    }

    private static byte[] CredentialIdentityOf(LeafNode leaf)
    {
        if (leaf.Credential is not BasicCredential credential)
        {
            throw new KeyPackagePublicationException(
                "The leaf credential is not a BasicCredential, which is the only kind Marmot defines.");
        }

        byte[] identity = credential.Identity;

        // Same rule as at construction: exactly 32 bytes and on the curve. A
        // credential that is not a usable Nostr key is not a member id, and
        // every downstream lookup would key on nonsense.
        if (identity.Length != 32 || !Scramble.Nostr.Crypto.Bip340.IsValidXOnlyPublicKey(identity))
        {
            throw new KeyPackagePublicationException(
                "The credential identity is not a valid x-only secp256k1 public key.");
        }

        return identity;
    }

    private static AccountIdentityProof ReadProof(LeafNode leaf)
    {
        MarmotDictionary dictionary;
        try
        {
            dictionary = MarmotLeaf.ReadDictionary(leaf)
                ?? throw new KeyPackagePublicationException(
                    "The leaf carries no app_data_dictionary, so it carries no account-identity proof.");
        }
        catch (Scramble.Marmot.AppComponents.AppComponentException ex)
        {
            throw new KeyPackagePublicationException(
                $"The leaf app_data_dictionary is malformed: {ex.Message}");
        }

        byte[] encoded = dictionary.Get(AccountIdentityProof.ComponentId)
            ?? throw new KeyPackagePublicationException(
                "The leaf app_data_dictionary has no 0x8009 entry.");

        if (!AccountIdentityProof.TryDecode(encoded, out AccountIdentityProof? proof))
            throw new KeyPackagePublicationException("The account-identity proof is malformed.");

        return proof!;
    }

    private static byte[] ParseAuthor(string authorPublicKeyHex)
    {
        try
        {
            return Convert.FromHexString(authorPublicKeyHex);
        }
        catch (FormatException)
        {
            throw new KeyPackagePublicationException("The event author is not hex.");
        }
    }
}
