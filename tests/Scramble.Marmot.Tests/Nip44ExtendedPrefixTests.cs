using System.Security.Cryptography;
using System.Text;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The NIP-44 extended length prefix (spec amendment of 2026-06-28).
/// </summary>
/// <remarks>
/// Plaintexts of 65536 bytes or more use a 6-byte prefix (two zero bytes plus
/// a big-endian u32) instead of the 2-byte u16 form. These vectors are inline
/// in <c>44.md</c> and are deliberately NOT in <c>nip44.vectors.json</c>, which
/// has not been regenerated since the amendment — so pinning only to the vector
/// file passes cleanly while the gap remains.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class Nip44ExtendedPrefixTests
{
    private static readonly byte[] ConversationKey = Convert.FromHexString(
        "c41c775356fd92eadc63ff5a0dc1da211b268cbea22316767095b2871ea1412d");

    private static readonly byte[] Nonce = Convert.FromHexString(
        "0000000000000000000000000000000000000000000000000000000000000001");

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [Theory]
    [InlineData(65535,
        "6e1bebca6a8229364a162a72ef064826c4cd7457bf54f190ef782bd9deff3e42",
        "6d8c2810d1e870fbaa1f0a0937126cca837a15f9260e27060c331d70a3c0bc84")]
    [InlineData(65536,
        "bf718b6f653bebc184e1479f1935b8da974d701b893afcf49e701f3e2f9f9c5a",
        "b7b4edb36ba92e267d322d56d9aebc22e7fa96ff52e3c12adc07f07a43cbc616")]
    [InlineData(65537,
        "008ffc88d3c96a9f307524eb361e47c5222a887fc45fa0c1fb8d429c5c23b430",
        "eeb7c7c5373894ea2c1547cfd3ccb15d5a0b2d619da852e5c79df792dcc9e435")]
    public void ThePrefixBoundaryMatchesTheSpecVectors(
        int plaintextLength, string plaintextSha256, string payloadSha256)
    {
        string plaintext = new string('a', plaintextLength);
        Assert.Equal(plaintextSha256, Sha256Hex(plaintext));

        string payload = Nip44.Encrypt(plaintext, ConversationKey, Nonce);

        Assert.Equal(payloadSha256, Sha256Hex(payload));
    }

    [Theory]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(100000)]
    public void LargePlaintextsRoundTrip(int plaintextLength)
    {
        string plaintext = new string('b', plaintextLength);

        string payload = Nip44.Encrypt(plaintext, ConversationKey, Nonce);

        Assert.Equal(
            plaintext,
            Nip44.Decrypt(payload, ConversationKey, maxPayloadLength: int.MaxValue));
    }

    [Fact]
    public void TheShortFormIsStillUsedBelowTheThreshold()
    {
        byte[] padded = Nip44.Pad(Encoding.UTF8.GetBytes(new string('a', 65535)));

        // 2-byte prefix: the first two bytes are the length, not zeros.
        Assert.Equal(2 + 65536, padded.Length);
        Assert.NotEqual(0, padded[0] | padded[1]);
    }

    [Fact]
    public void TheExtendedFormIsFlaggedByTwoZeroBytes()
    {
        byte[] padded = Nip44.Pad(Encoding.UTF8.GetBytes(new string('a', 65536)));

        Assert.Equal(6 + 65536, padded.Length);
        Assert.Equal(0, padded[0]);
        Assert.Equal(0, padded[1]);
        Assert.Equal(
            65536u,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(2, 4)));
    }

    [Fact]
    public void AnExtendedPrefixCarryingAShortLengthIsRejected()
    {
        // Two encodings of one value would let a forger reshape the plaintext.
        var padded = new byte[6 + 32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(padded.AsSpan(2, 4), 10);

        Assert.Throws<CryptographicException>(() => Nip44.Unpad(padded));
    }

    [Fact]
    public void AnExtendedPrefixTruncatedBelowSixBytesIsRejected()
    {
        Assert.Throws<CryptographicException>(() => Nip44.Unpad(new byte[] { 0, 0, 0, 1 }));
    }

    [Fact]
    public void ShortPayloadsAreRejectedBeforeDecoding()
    {
        Assert.Throws<CryptographicException>(
            () => Nip44.Decrypt(new string('A', 131), ConversationKey));
    }

    [Fact]
    public void ACallerSuppliedCeilingIsEnforced()
    {
        string payload = Nip44.Encrypt(new string('a', 70000), ConversationKey, Nonce);

        var ex = Assert.Throws<CryptographicException>(
            () => Nip44.Decrypt(payload, ConversationKey, maxPayloadLength: 1000));

        Assert.Contains("size limit", ex.Message);
    }

    [Fact]
    public void ThePrimitiveImposesNoCeilingOfItsOwn()
    {
        // Capping here would reject the large messages the amendment allows;
        // the bound belongs at the untrusted transport boundary.
        string payload = Nip44.Encrypt(new string('a', 200000), ConversationKey, Nonce);

        Assert.Equal(200000, Nip44.Decrypt(payload, ConversationKey).Length);
    }
}
