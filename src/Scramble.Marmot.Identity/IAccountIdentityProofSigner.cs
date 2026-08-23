using Scramble.Nostr.Crypto;

namespace Scramble.Marmot.Identity;

/// <summary>
/// Produces account-identity proofs by signing the kind:450 template.
/// </summary>
/// <remarks>
/// <para>
/// Asynchronous on purpose. The Rust reference signs synchronously because it
/// holds the key, but Scramble's common case is an external signer — Amber or
/// a NIP-46 remote — where signing involves a round trip and a human tapping
/// approve. Proof signing happens at KeyPackage creation and at every leaf
/// replacement, both of which can occur when the signer is not immediately
/// reachable, so this must be a first-class async step rather than a call that
/// blocks a lock.
/// </para>
/// <para>
/// Implementations MUST verify what an external signer returns before trusting
/// it: see <see cref="AccountIdentityProofSigning.VerifySignedTemplate"/>.
/// </para>
/// </remarks>
public interface IAccountIdentityProofSigner
{
    /// <summary>The account key proofs will be issued under, x-only, 32 bytes.</summary>
    ReadOnlyMemory<byte> AccountPublicKey { get; }

    /// <summary>
    /// Signs the id of <paramref name="template"/> and returns the 64-byte
    /// BIP-340 signature.
    /// </summary>
    /// <remarks>
    /// The template is passed whole rather than pre-hashed so a signer UI can
    /// show the user what they are approving.
    /// </remarks>
    Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default);
}

/// <summary>Building proofs through a signer.</summary>
public static class AccountIdentityProofSigning
{
    /// <summary>
    /// Builds a proof for a leaf key, signing through <paramref name="signer"/>.
    /// </summary>
    /// <param name="createdAt">
    /// Unix seconds. Defaults to now. A proof is reusable only while every
    /// signed input is byte-identical, so a new leaf key, ciphersuite, scheme
    /// or account requires a fresh proof with a fresh timestamp.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The signer returned something that does not verify as its own.
    /// </exception>
    public static async Task<AccountIdentityProof> CreateAsync(
        IAccountIdentityProofSigner signer,
        ushort cipherSuite,
        ushort signatureScheme,
        ReadOnlyMemory<byte> mlsSignatureKey,
        ulong? createdAt = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);

        byte[] accountKey = signer.AccountPublicKey.ToArray();
        ulong timestamp = createdAt ?? (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var template = AccountIdentityProof.BuildSigningEvent(
            accountKey, timestamp, cipherSuite, signatureScheme, mlsSignatureKey.Span);

        byte[] signature = await signer.SignAsync(template, ct).ConfigureAwait(false);

        // Never trust a signer's response blindly: a remote signer can return a
        // signature over a different template, or a stale one from an earlier
        // request. An unverified proof would be rejected by every peer, and the
        // failure would surface far from its cause.
        if (!VerifySignedTemplate(accountKey, template, signature))
            throw new InvalidOperationException(
                "The signer returned a signature that does not verify over the requested proof template.");

        return new AccountIdentityProof(accountKey, timestamp, signature);
    }

    /// <summary>
    /// Whether <paramref name="signature"/> is a valid BIP-340 signature over
    /// <paramref name="template"/>'s id under <paramref name="accountPublicKey"/>.
    /// </summary>
    public static bool VerifySignedTemplate(
        ReadOnlySpan<byte> accountPublicKey,
        NostrEventTemplate template,
        ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(template);

        return signature.Length == 64
            && Bip340.Verify(accountPublicKey, template.ComputeId(), signature);
    }
}
