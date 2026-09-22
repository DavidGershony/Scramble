using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage;
using Scramble.Nostr.Crypto;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Engine.KeyPackages;

/// <summary>
/// A freshly built KeyPackage, everything needed to publish it, and the
/// private material that must be stored before it is.
/// </summary>
/// <param name="KeyPackage">The decoded KeyPackage.</param>
/// <param name="KeyPackageRefHex">
/// The RFC 9420 KeyPackageRef, lowercase hex — computed over the
/// <see cref="KeyPackage"/>, <b>not</b> over <see cref="PublishedBytes"/>. The
/// two differ by the MLSMessage framing, and hashing the wrong one produces a
/// reference nobody else computes.
/// </param>
/// <param name="PublishedBytes">
/// What goes in the kind-30443 event content: an <c>MLSMessage</c> whose wire
/// format is <c>mls_key_package</c>.
/// </param>
/// <param name="PrivateMaterial">The bundle a Welcome will need.</param>
/// <param name="CipherSuites">For the <c>mls_ciphersuite</c> tag.</param>
/// <param name="MlsExtensions">For the <c>mls_extensions</c> tag.</param>
/// <param name="MlsProposals">For the <c>mls_proposals</c> tag.</param>
/// <param name="AppComponents">For the <c>app_components</c> tag.</param>
public sealed record MarmotKeyPackageBundle(
    KeyPackage KeyPackage,
    string KeyPackageRefHex,
    byte[] PublishedBytes,
    KeyPackagePrivateMaterial PrivateMaterial,
    IReadOnlyList<ushort> CipherSuites,
    IReadOnlyList<ushort> MlsExtensions,
    IReadOnlyList<ushort> MlsProposals,
    IReadOnlyList<ushort> AppComponents)
{
    /// <summary>The leaf's validity window.</summary>
    public Lifetime Lifetime => KeyPackage.LeafNode.Lifetime!;

    /// <summary>
    /// Builds the durable record for this bundle.
    /// </summary>
    /// <remarks>
    /// Always <see cref="KeyPackageRecordState.Created"/>. The last-resort flag
    /// is read off the KeyPackage rather than passed in, so the record and the
    /// bytes on the wire cannot disagree about whether the private material may
    /// outlive its first Welcome.
    /// </remarks>
    public KeyPackageRecord ToRecord(string slotId, DateTimeOffset createdAt) =>
        new(
            KeyPackageRefHex,
            slotId,
            PublishedBytes,
            PrivateMaterial.Encode(),
            LastResort: MarmotLeaf.IsLastResort(KeyPackage),
            NotBefore: checked((long)Lifetime.NotBefore),
            NotAfter: checked((long)Lifetime.NotAfter),
            KeyPackageRecordState.Created,
            createdAt);
}

/// <summary>
/// Builds Current-profile Marmot KeyPackages.
/// </summary>
/// <remarks>
/// <para>
/// A Marmot KeyPackage is an RFC 9420 KeyPackage plus three things the MLS
/// library knows nothing about: a credential identity that is a valid x-only
/// secp256k1 key, a leaf <c>app_data_dictionary</c> carrying the
/// account-identity proof, and leaf capabilities naming
/// <c>app_data_dictionary</c> and <c>app_data_update</c>. This is where those
/// meet, and it is the only place in the engine that mints a leaf signature
/// key.
/// </para>
/// <para>
/// <b>The leaf signature key is fresh, and is not the Nostr account key.</b>
/// That separation is the whole reason the account-identity proof exists: the
/// proof is a Nostr-signed statement that this account authorises this MLS leaf
/// key, so making them one key would be both impossible (different signature
/// schemes) and pointless.
/// </para>
/// <para>
/// <b>KeyPackages are marked last-resort by default</b>, matching the reference
/// implementation. Kind 30443 is addressable — one live event per slot — so
/// several people can invite us off one publication, and an unmarked KeyPackage
/// says only the first of those Welcomes may be opened. The marker is a
/// KeyPackage-level component, not a leaf extension and not the obsolete
/// <c>0x000a</c> extension; see <see cref="MarmotLeaf.LastResortComponentId"/>,
/// where getting it wrong is easy.
/// </para>
/// </remarks>
public static class MarmotKeyPackageBuilder
{
    /// <summary>
    /// Ed25519, the signature scheme of every ciphersuite Marmot uses.
    /// </summary>
    /// <remarks>
    /// Both suites in play — <c>0x0001</c>
    /// (<c>MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519</c>) and <c>0x0003</c>
    /// (the ChaCha20-Poly1305 variant) — sign with Ed25519. The value is
    /// written into the proof's signing template, where a wrong one produces a
    /// proof that verifies for us and for nobody else, so it is derived from
    /// the suite rather than defaulted.
    /// </remarks>
    public const ushort Ed25519SignatureScheme = 0x0807;

    /// <summary>
    /// The IANA signature scheme of an MLS ciphersuite.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// A ciphersuite whose signature scheme this engine has not been told.
    /// Guessing would produce proofs no peer accepts.
    /// </exception>
    public static ushort SignatureSchemeOf(ushort cipherSuite) => cipherSuite switch
    {
        0x0001 or 0x0003 => Ed25519SignatureScheme,
        _ => throw new NotSupportedException(
            $"No signature scheme is known for ciphersuite 0x{cipherSuite:x4}."),
    };

    /// <summary>
    /// Builds a KeyPackage and the account-identity proof inside it.
    /// </summary>
    /// <param name="cs">The ciphersuite. Its id goes into the proof's template.</param>
    /// <param name="signer">Signs the proof under the Nostr account key.</param>
    /// <param name="now">Unix seconds, for the lifetime and the proof timestamp.</param>
    /// <param name="supportedComponents">
    /// App components this client honours. Defaults to
    /// <see cref="MarmotLeaf.DefaultSupportedComponents"/>.
    /// </param>
    /// <param name="validitySeconds">
    /// Lifetime ahead of <paramref name="now"/>; see
    /// <see cref="KeyPackageLifetimePolicy"/>.
    /// </param>
    /// <param name="lastResort">
    /// Whether the KeyPackage may serve more than one Welcome. Defaults to true,
    /// which is what a published KeyPackage wants: pass false only for one
    /// addressed to a single known inviter.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The signer's account key is not a valid Marmot credential identity.
    /// </exception>
    public static async Task<MarmotKeyPackageBundle> CreateAsync(
        ICipherSuite cs,
        IAccountIdentityProofSigner signer,
        ulong now,
        IReadOnlySet<ushort>? supportedComponents = null,
        ulong? validitySeconds = null,
        bool lastResort = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(signer);

        byte[] identity = signer.AccountPublicKey.ToArray();

        // foundation/identity.md: a member-leaf credential identity is exactly
        // 32 bytes AND a point on secp256k1. Checked rather than trusted,
        // because a leaf that fails it is refused by every peer at the moment
        // of being added — a long way from the signer that produced it.
        if (identity.Length != 32 || !Bip340.IsValidXOnlyPublicKey(identity))
        {
            throw new ArgumentException(
                "The account public key is not a valid x-only secp256k1 credential identity.",
                nameof(signer));
        }

        ushort signatureScheme = SignatureSchemeOf(cs.Id);
        Lifetime lifetime = KeyPackageLifetimePolicy.Create(now, validitySeconds);

        var (signaturePrivateKey, signaturePublicKey) = cs.GenerateSignatureKeyPair();

        // Before the KeyPackage, because it goes inside it — and because this
        // is the step that can block on a human approving on an external
        // signer. Nothing is persisted until the caller holds the whole bundle,
        // so failing here leaves no key material behind.
        AccountIdentityProof proof = await AccountIdentityProofSigning.CreateAsync(
            signer, cs.Id, signatureScheme, signaturePublicKey, now, ct).ConfigureAwait(false);

        IReadOnlySet<ushort> supported = supportedComponents ?? MarmotLeaf.DefaultSupportedComponents;
        MarmotDictionary dictionary = MarmotLeaf.BuildDictionary(supported, proof);

        KeyPackage keyPackage = MlsGroup.CreateKeyPackage(
            cs,
            identity,
            signaturePrivateKey,
            signaturePublicKey,
            out byte[] initPrivateKey,
            out byte[] leafPrivateKey,
            supportedExtensionTypes: MarmotLeaf.ExtensionTypes.ToArray(),
            supportedProposalTypes: MarmotLeaf.ProposalTypes.ToArray(),
            leafExtensions: new[] { MarmotLeaf.ToExtension(dictionary) },
            lifetime: lifetime,
            keyPackageExtensions: lastResort ? [MarmotLeaf.LastResortExtension()] : null);

        byte[] keyPackageBytes = TlsCodec.Serialize(keyPackage.WriteTo);
        var keyPackageRef = KeyPackageRef.Compute(cs, keyPackageBytes);

        byte[] publishedBytes = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsKeyPackage, keyPackage).WriteTo);

        return new MarmotKeyPackageBundle(
            keyPackage,
            Convert.ToHexString(keyPackageRef.Value).ToLowerInvariant(),
            publishedBytes,
            new KeyPackagePrivateMaterial(initPrivateKey, leafPrivateKey, signaturePrivateKey),
            CipherSuites: new[] { cs.Id },
            MlsExtensions: keyPackage.LeafNode.Capabilities.Extensions.ToArray(),
            MlsProposals: keyPackage.LeafNode.Capabilities.Proposals.ToArray(),
            AppComponents: MarmotLeaf.TaggedComponents(
                MarmotLeaf.AdvertisedComponents(supported)).ToArray());
    }
}
