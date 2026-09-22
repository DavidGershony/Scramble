using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using BcChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

namespace Scramble.Nostr.Crypto;

/// <summary>
/// A ChaCha20-Poly1305 envelope carried as base64 of
/// <c>nonce (12 bytes) || ciphertext || tag (16 bytes)</c>, with empty AAD.
/// </summary>
/// <remarks>
/// Generic AEAD framing, deliberately not in a Marmot namespace. The key is
/// supplied by the caller; which secret to use, and what the plaintext means,
/// is the protocol layer's business.
/// </remarks>
public static class ChaCha20Poly1305Envelope
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>
    /// Smallest structurally possible envelope: nonce + tag + one byte of
    /// ciphertext. Anything shorter cannot be a sealed message.
    /// </summary>
    public const int MinimumSealedLength = NonceSize + TagSize + 1;

    private const int TagSizeBits = TagSize * 8;

    /// <summary>Seals <paramref name="plaintext"/> under a fresh random nonce.</summary>
    /// <param name="nonce">The nonce that was used, so callers can enforce uniqueness.</param>
    public static string Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, out byte[] nonce)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));
        if (plaintext.Length == 0)
            throw new ArgumentException("Plaintext must not be empty.", nameof(plaintext));

        // A failure here must propagate: falling back to anything deterministic
        // would reuse a nonce, which is catastrophic for this construction.
        nonce = RandomNumberGenerator.GetBytes(NonceSize);

        var cipher = new BcChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(
            new KeyParameter(key.ToArray()), TagSizeBits, nonce, Array.Empty<byte>()));

        byte[] plain = plaintext.ToArray();
        var sealedBytes = new byte[cipher.GetOutputSize(plain.Length)];
        int written = cipher.ProcessBytes(plain, 0, plain.Length, sealedBytes, 0);
        written += cipher.DoFinal(sealedBytes, written);

        var combined = new byte[NonceSize + written];
        nonce.CopyTo(combined, 0);
        sealedBytes.AsSpan(0, written).CopyTo(combined.AsSpan(NonceSize));
        return Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Opens a sealed envelope.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The content is not valid base64, is too short, or fails authentication.
    /// </exception>
    public static byte[] Open(string sealedContent, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(sealedContent);
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));

        byte[] combined;
        try
        {
            combined = Convert.FromBase64String(sealedContent);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Envelope is not valid base64.", ex);
        }

        if (combined.Length < MinimumSealedLength)
            throw new CryptographicException("Envelope is too short to contain a nonce and ciphertext.");

        var cipher = new BcChaCha20Poly1305();
        cipher.Init(false, new AeadParameters(
            new KeyParameter(key.ToArray()), TagSizeBits, combined.AsSpan(0, NonceSize).ToArray(),
            Array.Empty<byte>()));

        int sealedLength = combined.Length - NonceSize;
        var plaintext = new byte[cipher.GetOutputSize(sealedLength)];
        int written;
        try
        {
            written = cipher.ProcessBytes(combined, NonceSize, sealedLength, plaintext, 0);
            written += cipher.DoFinal(plaintext, written);
        }
        catch (InvalidCipherTextException ex)
        {
            throw new CryptographicException(
                "Envelope failed authentication: wrong key or tampered ciphertext.", ex);
        }

        return plaintext.AsSpan(0, written).ToArray();
    }
}
