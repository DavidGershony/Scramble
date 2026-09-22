using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// NIP-44 v2 against the official vectors from the nostr-protocol/nips
/// repository.
/// </summary>
/// <remarks>
/// These matter beyond ordinary coverage: the ECDH here is a fresh BouncyCastle
/// implementation rather than the library call the previous code used, and the
/// conversation-key vectors are what prove the two agree. A mismatch would
/// produce keys that look fine and decrypt nothing.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class Nip44VectorTests
{
    private static readonly JsonDocument Vectors = LoadVectors();

    private static JsonDocument LoadVectors()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "vectors", "nip44.vectors.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static JsonElement Valid => Vectors.RootElement.GetProperty("v2").GetProperty("valid");

    private static JsonElement Invalid => Vectors.RootElement.GetProperty("v2").GetProperty("invalid");

    private static byte[] Hex(string value) => Convert.FromHexString(value);

    public static TheoryData<string, string, string> ConversationKeyCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var c in Valid.GetProperty("get_conversation_key").EnumerateArray())
        {
            data.Add(
                c.GetProperty("sec1").GetString()!,
                c.GetProperty("pub2").GetString()!,
                c.GetProperty("conversation_key").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ConversationKeyCases))]
    public void ConversationKeyMatchesTheVector(string sec1, string pub2, string expected)
    {
        byte[] key = Nip44.DeriveConversationKey(Hex(sec1), Hex(pub2));

        Assert.Equal(expected, Convert.ToHexString(key).ToLowerInvariant());
    }

    public static TheoryData<string, string, string, string, string> MessageKeyCases()
    {
        var data = new TheoryData<string, string, string, string, string>();
        string conversationKey = Valid.GetProperty("get_message_keys")
            .GetProperty("conversation_key").GetString()!;

        foreach (var c in Valid.GetProperty("get_message_keys").GetProperty("keys").EnumerateArray())
        {
            data.Add(
                conversationKey,
                c.GetProperty("nonce").GetString()!,
                c.GetProperty("chacha_key").GetString()!,
                c.GetProperty("chacha_nonce").GetString()!,
                c.GetProperty("hmac_key").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MessageKeyCases))]
    public void MessageKeysMatchTheVector(
        string conversationKey, string nonce, string chachaKey, string chachaNonce, string hmacKey)
    {
        var keys = Nip44.MessageKeys.Derive(Hex(conversationKey), Hex(nonce));

        Assert.Equal(chachaKey, Convert.ToHexString(keys.ChaChaKey).ToLowerInvariant());
        Assert.Equal(chachaNonce, Convert.ToHexString(keys.ChaChaNonce).ToLowerInvariant());
        Assert.Equal(hmacKey, Convert.ToHexString(keys.HmacKey).ToLowerInvariant());
    }

    public static TheoryData<int, int> PaddedLengthCases()
    {
        var data = new TheoryData<int, int>();
        foreach (var pair in Valid.GetProperty("calc_padded_len").EnumerateArray())
        {
            var values = pair.EnumerateArray().ToArray();
            data.Add(values[0].GetInt32(), values[1].GetInt32());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PaddedLengthCases))]
    public void PaddedLengthMatchesTheVector(int messageLength, int expected)
    {
        Assert.Equal(expected, Nip44.CalculatePaddedLength(messageLength));
    }

    public static TheoryData<string, string, string, string> EncryptDecryptCases()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var c in Valid.GetProperty("encrypt_decrypt").EnumerateArray())
        {
            data.Add(
                c.GetProperty("conversation_key").GetString()!,
                c.GetProperty("nonce").GetString()!,
                c.GetProperty("plaintext").GetString()!,
                c.GetProperty("payload").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EncryptDecryptCases))]
    public void EncryptingWithTheVectorNonceReproducesThePayload(
        string conversationKey, string nonce, string plaintext, string payload)
    {
        Assert.Equal(payload, Nip44.Encrypt(plaintext, Hex(conversationKey), Hex(nonce)));
    }

    [Theory]
    [MemberData(nameof(EncryptDecryptCases))]
    public void DecryptingTheVectorPayloadReproducesThePlaintext(
        string conversationKey, string nonce, string plaintext, string payload)
    {
        Assert.NotNull(nonce);

        Assert.Equal(plaintext, Nip44.Decrypt(payload, Hex(conversationKey)));
    }

    [Theory]
    [MemberData(nameof(EncryptDecryptCases))]
    public void BothPartiesDeriveTheSameConversationKey(
        string conversationKey, string nonce, string plaintext, string payload)
    {
        Assert.NotNull(nonce);
        Assert.NotNull(plaintext);
        Assert.NotNull(payload);

        var pair = Valid.GetProperty("encrypt_decrypt").EnumerateArray()
            .First(c => c.GetProperty("conversation_key").GetString() == conversationKey);

        byte[] sec1 = Hex(pair.GetProperty("sec1").GetString()!);
        byte[] sec2 = Hex(pair.GetProperty("sec2").GetString()!);

        // ECDH is symmetric, so each side derives the same key from its own
        // secret and the other's public key.
        byte[] pub1 = PublicKeyOf(sec1);
        byte[] pub2 = PublicKeyOf(sec2);

        Assert.Equal(
            Convert.ToHexString(Nip44.DeriveConversationKey(sec1, pub2)),
            Convert.ToHexString(Nip44.DeriveConversationKey(sec2, pub1)));
    }

    public static TheoryData<string, string, string> InvalidDecryptCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var c in Invalid.GetProperty("decrypt").EnumerateArray())
        {
            data.Add(
                c.GetProperty("conversation_key").GetString()!,
                c.GetProperty("payload").GetString()!,
                c.GetProperty("note").GetString() ?? "");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidDecryptCases))]
    public void InvalidPayloadsAreRejected(string conversationKey, string payload, string note)
    {
        Assert.NotNull(note);

        Assert.ThrowsAny<Exception>(() => Nip44.Decrypt(payload, Hex(conversationKey)));
    }

    public static TheoryData<string, string, string> InvalidConversationKeyCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var c in Invalid.GetProperty("get_conversation_key").EnumerateArray())
        {
            data.Add(
                c.GetProperty("sec1").GetString()!,
                c.GetProperty("pub2").GetString()!,
                c.GetProperty("note").GetString() ?? "");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidConversationKeyCases))]
    public void InvalidKeyPairsAreRejected(string sec1, string pub2, string note)
    {
        Assert.NotNull(note);

        // Off-curve points and out-of-range scalars must fail loudly rather
        // than yielding a usable-looking key.
        Assert.ThrowsAny<Exception>(() => Nip44.DeriveConversationKey(Hex(sec1), Hex(pub2)));
    }

    [Fact]
    public void LongMessagesMatchTheVectorDigests()
    {
        foreach (var c in Valid.GetProperty("encrypt_decrypt_long_msg").EnumerateArray())
        {
            byte[] conversationKey = Hex(c.GetProperty("conversation_key").GetString()!);
            byte[] nonce = Hex(c.GetProperty("nonce").GetString()!);
            string pattern = c.GetProperty("pattern").GetString()!;
            int repeat = c.GetProperty("repeat").GetInt32();

            string plaintext = string.Concat(Enumerable.Repeat(pattern, repeat));

            Assert.Equal(
                c.GetProperty("plaintext_sha256").GetString(),
                Sha256Hex(Encoding.UTF8.GetBytes(plaintext)));

            string payload = Nip44.Encrypt(plaintext, conversationKey, nonce);
            Assert.Equal(
                c.GetProperty("payload_sha256").GetString(),
                Sha256Hex(Encoding.UTF8.GetBytes(payload)));
        }
    }

    [Fact]
    public void RoundTripWorksWithAGeneratedNonce()
    {
        byte[] conversationKey = Hex(
            Valid.GetProperty("encrypt_decrypt").EnumerateArray().First()
                .GetProperty("conversation_key").GetString()!);

        string payload = Nip44.Encrypt("round trip", conversationKey);

        Assert.Equal("round trip", Nip44.Decrypt(payload, conversationKey));
    }

    [Fact]
    public void TamperingWithThePayloadFailsTheMac()
    {
        byte[] conversationKey = Hex(
            Valid.GetProperty("encrypt_decrypt").EnumerateArray().First()
                .GetProperty("conversation_key").GetString()!);
        byte[] raw = Convert.FromBase64String(Nip44.Encrypt("tamper me", conversationKey));
        raw[40] ^= 0xFF;

        Assert.Throws<CryptographicException>(
            () => Nip44.Decrypt(Convert.ToBase64String(raw), conversationKey));
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Derives the x-only public key for a secret, for the symmetry check.</summary>
    private static byte[] PublicKeyOf(byte[] secret)
    {
        var curve = Org.BouncyCastle.Asn1.X9.ECNamedCurveTable.GetByName("secp256k1");
        var point = curve.G.Multiply(new Org.BouncyCastle.Math.BigInteger(1, secret)).Normalize();
        byte[] x = point.AffineXCoord.ToBigInteger().ToByteArrayUnsigned();
        var padded = new byte[32];
        x.CopyTo(padded.AsSpan(32 - x.Length));
        return padded;
    }
}
