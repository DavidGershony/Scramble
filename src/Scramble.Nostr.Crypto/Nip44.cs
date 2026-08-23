using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Scramble.Nostr.Crypto;

/// <summary>
/// NIP-44 v2: secp256k1 ECDH, HKDF, ChaCha20 and HMAC-SHA256.
/// </summary>
/// <remarks>
/// <para>
/// Payload layout is <c>base64(version || nonce[32] || ciphertext || mac[32])</c>
/// with version <c>0x02</c>. The conversation key is
/// <c>HKDF-Extract(salt="nip44-v2", ikm=shared_x)</c>, and per-message keys come
/// from <c>HKDF-Expand(conversation_key, nonce, 76)</c>.
/// </para>
/// <para>
/// Generic Nostr cryptography, deliberately not in a Marmot namespace.
/// </para>
/// </remarks>
public static class Nip44
{
    public const byte Version = 0x02;

    /// <summary>version(1) + nonce(32) + smallest ciphertext(34) + mac(32).</summary>
    public const int MinPayloadLength = 99;

    private static readonly byte[] HkdfSalt = "nip44-v2"u8.ToArray();

    /// <summary>Per-message keys expanded from the conversation key and nonce.</summary>
    public readonly record struct MessageKeys(byte[] ChaChaKey, byte[] ChaChaNonce, byte[] HmacKey)
    {
        /// <summary>
        /// Expands 76 bytes and splits them into a 32-byte cipher key, a
        /// 12-byte cipher nonce, and a 32-byte MAC key.
        /// </summary>
        public static MessageKeys Derive(ReadOnlySpan<byte> conversationKey, ReadOnlySpan<byte> nonce)
        {
            if (conversationKey.Length != 32)
                throw new ArgumentException("Conversation key must be 32 bytes.", nameof(conversationKey));
            if (nonce.Length != 32)
                throw new ArgumentException("Nonce must be 32 bytes.", nameof(nonce));

            var expanded = new byte[76];
            HKDF.Expand(HashAlgorithmName.SHA256, conversationKey.ToArray(), expanded, nonce.ToArray());

            return new MessageKeys(
                expanded.AsSpan(0, 32).ToArray(),
                expanded.AsSpan(32, 12).ToArray(),
                expanded.AsSpan(44, 32).ToArray());
        }
    }

    /// <summary>
    /// Derives the conversation key shared by a private key and a counterparty's
    /// x-only public key. Symmetric: both sides compute the same value.
    /// </summary>
    public static byte[] DeriveConversationKey(
        ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> xOnlyPublicKey)
    {
        byte[] sharedX = Secp256k1.SharedSecretX(privateKey, xOnlyPublicKey);
        return HKDF.Extract(HashAlgorithmName.SHA256, sharedX, HkdfSalt);
    }

    /// <summary>Encrypts under a fresh random nonce.</summary>
    public static string Encrypt(string plaintext, ReadOnlySpan<byte> conversationKey)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Encrypt(plaintext, conversationKey, RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>
    /// Encrypts under a caller-supplied nonce.
    /// </summary>
    /// <remarks>
    /// Exposed so the official test vectors, which fix the nonce, can be
    /// checked byte for byte. Production callers should use the overload that
    /// generates one.
    /// </remarks>
    public static string Encrypt(
        string plaintext, ReadOnlySpan<byte> conversationKey, ReadOnlySpan<byte> nonce)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        byte[] message = Encoding.UTF8.GetBytes(plaintext);
        if (message.Length is 0 or > 65535)
            throw new ArgumentException(
                "Plaintext must be 1 to 65535 bytes once UTF-8 encoded.", nameof(plaintext));

        var keys = MessageKeys.Derive(conversationKey, nonce);
        byte[] ciphertext = ChaCha20(keys.ChaChaKey, keys.ChaChaNonce, Pad(message));
        byte[] mac = ComputeMac(keys.HmacKey, nonce, ciphertext);

        var payload = new byte[1 + 32 + ciphertext.Length + 32];
        payload[0] = Version;
        nonce.CopyTo(payload.AsSpan(1));
        ciphertext.CopyTo(payload.AsSpan(33));
        mac.CopyTo(payload.AsSpan(33 + ciphertext.Length));
        return Convert.ToBase64String(payload);
    }

    /// <summary>
    /// Decrypts a payload.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The payload is malformed, the version is unsupported, or the MAC does
    /// not verify.
    /// </exception>
    public static string Decrypt(string payload, ReadOnlySpan<byte> conversationKey)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Length > 0 && payload[0] == '#')
            throw new CryptographicException("Unsupported NIP-44 encoding.");

        byte[] data;
        try
        {
            data = Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Payload is not valid base64.", ex);
        }

        if (data.Length < MinPayloadLength)
            throw new CryptographicException("Payload is too short.");
        if (data[0] != Version)
            throw new CryptographicException($"Unsupported NIP-44 version {data[0]}.");

        var nonce = data.AsSpan(1, 32);
        var ciphertext = data.AsSpan(33, data.Length - 65);
        var mac = data.AsSpan(data.Length - 32, 32);

        var keys = MessageKeys.Derive(conversationKey, nonce);

        // Constant-time: a timing-variable compare here leaks whether a
        // forgery was close, one byte at a time.
        byte[] expected = ComputeMac(keys.HmacKey, nonce, ciphertext);
        if (!CryptographicOperations.FixedTimeEquals(mac, expected))
            throw new CryptographicException("MAC verification failed.");

        byte[] padded = ChaCha20(keys.ChaChaKey, keys.ChaChaNonce, ciphertext.ToArray());
        return Encoding.UTF8.GetString(Unpad(padded));
    }

    /// <summary>
    /// The padded length for a message, excluding the two-byte length prefix.
    /// </summary>
    /// <remarks>
    /// Padding to a coarse ladder rather than the exact length is what stops
    /// the ciphertext size from revealing the message size.
    /// </remarks>
    public static int CalculatePaddedLength(int messageLength)
    {
        if (messageLength < 1)
            throw new ArgumentOutOfRangeException(nameof(messageLength));
        if (messageLength <= 32)
            return 32;

        int nextPowerOfTwo = 1;
        while (nextPowerOfTwo < messageLength)
            nextPowerOfTwo <<= 1;

        int chunk = Math.Max(32, nextPowerOfTwo / 8);
        return chunk * ((messageLength - 1) / chunk + 1);
    }

    internal static byte[] Pad(byte[] message)
    {
        if (message.Length is < 1 or > 65535)
            throw new ArgumentException("Message length must be 1 to 65535.", nameof(message));

        var padded = new byte[2 + CalculatePaddedLength(message.Length)];
        padded[0] = (byte)(message.Length >> 8);
        padded[1] = (byte)(message.Length & 0xFF);
        message.CopyTo(padded.AsSpan(2));
        return padded;
    }

    internal static byte[] Unpad(byte[] padded)
    {
        if (padded.Length < 2)
            throw new CryptographicException("Padded plaintext is too short.");

        int length = (padded[0] << 8) | padded[1];
        if (length < 1 || length > padded.Length - 2)
            throw new CryptographicException("Declared plaintext length is out of range.");

        // The padded length must be exactly what the ladder prescribes;
        // accepting anything else would let a forger reshape the plaintext.
        if (padded.Length != 2 + CalculatePaddedLength(length))
            throw new CryptographicException("Padding does not match the declared length.");

        return padded.AsSpan(2, length).ToArray();
    }

    private static byte[] ComputeMac(
        ReadOnlySpan<byte> hmacKey, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext)
    {
        var input = new byte[nonce.Length + ciphertext.Length];
        nonce.CopyTo(input);
        ciphertext.CopyTo(input.AsSpan(nonce.Length));
        return HMACSHA256.HashData(hmacKey.ToArray(), input);
    }

    private static byte[] ChaCha20(byte[] key, byte[] nonce, byte[] input)
    {
        // RFC 8439 ChaCha20 with a 96-bit nonce, used as a stream cipher, so
        // the same call both encrypts and decrypts.
        var engine = new ChaCha7539Engine();
        engine.Init(true, new ParametersWithIV(new KeyParameter(key), nonce));
        var output = new byte[input.Length];
        engine.ProcessBytes(input, 0, input.Length, output, 0);
        return output;
    }
}
