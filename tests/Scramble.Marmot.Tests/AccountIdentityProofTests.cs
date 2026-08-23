using Scramble.Marmot.Identity;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The account-identity proof construction, anchored on the official spec
/// vector.
/// </summary>
/// <remarks>
/// This is a byte-exactness problem, not a logic problem: a single wrong byte
/// anywhere in the signing template changes the event id, and every peer
/// rejects every KeyPackage and commit we produce. The vector below is the
/// arbiter.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class AccountIdentityProofTests
{
    private const ushort CipherSuite = 0x0001;
    private const ushort SignatureScheme = 0x0807;

    private static readonly byte[] VectorSignerPublicKey = Convert.FromHexString(
        "f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9");

    private const ulong VectorCreatedAt = 1700000000;

    private static readonly byte[] VectorMlsSignatureKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private const string VectorSerialization =
        """[0,"f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9",1700000000,450,[["d","marmot.account-identity-proof.v2"],["component","0x8009"],["ciphersuite","0x0001"],["signature_scheme","0x0807"],["mls_signature_key","000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"]],"Authorize this MLS leaf key for my Marmot account"]""";

    private const string VectorEventId =
        "b7e9a15dd85990fb0f49c33db3cc9875f73986207b038404ceb6b7fec4e0af6b";

    private const string VectorSignature =
        "c5315d3c85b9d4907cb03395a2a97b3ba2eab393f8e45b13a5d5233acedac60a" +
        "51d2a295e1b1b5ee372d18a49bdb8041a7dba9dedce722c7c6f712f78bbdfb5d";

    private const string VectorComponentData =
        "f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9" +
        "000000006553f100" +
        "c5315d3c85b9d4907cb03395a2a97b3ba2eab393f8e45b13a5d5233acedac60a" +
        "51d2a295e1b1b5ee372d18a49bdb8041a7dba9dedce722c7c6f712f78bbdfb5d";

    private static NostrEventTemplate VectorTemplate() =>
        AccountIdentityProof.BuildSigningEvent(
            VectorSignerPublicKey, VectorCreatedAt, CipherSuite, SignatureScheme, VectorMlsSignatureKey);

    private static AccountIdentityProof VectorProof() =>
        new(VectorSignerPublicKey, VectorCreatedAt, Convert.FromHexString(VectorSignature));

    // -- The official vector --

    [Fact]
    public void SigningEventMatchesTheSpecSerializationExactly()
    {
        Assert.Equal(VectorSerialization, VectorTemplate().Serialize());
    }

    [Fact]
    public void SigningEventIdMatchesTheSpecVector()
    {
        Assert.Equal(VectorEventId, Convert.ToHexString(VectorTemplate().ComputeId()).ToLowerInvariant());
    }

    [Fact]
    public void SpecVectorSignatureVerifies()
    {
        Assert.True(Bip340.Verify(
            VectorSignerPublicKey,
            VectorTemplate().ComputeId(),
            Convert.FromHexString(VectorSignature)));
    }

    [Fact]
    public void ComponentEncodingMatchesTheSpecVector()
    {
        Assert.Equal(VectorComponentData, Convert.ToHexString(VectorProof().Encode()).ToLowerInvariant());
    }

    [Fact]
    public void SpecVectorValidatesEndToEnd()
    {
        var result = VectorProof().Validate(
            VectorSignerPublicKey, CipherSuite, SignatureScheme, VectorMlsSignatureKey);

        Assert.Equal(AccountIdentityProofResult.Valid, result);
    }

    [Fact]
    public void ComponentEncodingIsExactly104Bytes()
    {
        Assert.Equal(AccountIdentityProof.EncodedLength, VectorProof().Encode().Length);
        Assert.Equal(104, VectorProof().Encode().Length);
    }

    [Fact]
    public void CreatedAtIsBigEndian()
    {
        byte[] encoded = VectorProof().Encode();

        // 1700000000 == 0x6553F100, big-endian in the middle eight bytes.
        Assert.Equal(
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x65, 0x53, 0xf1, 0x00 },
            encoded[32..40]);
    }

    [Fact]
    public void ComponentIdIsTheCurrentProfileOne()
    {
        Assert.Equal(0x8009, AccountIdentityProof.ComponentId);
    }

    // -- Codec --

    [Fact]
    public void EncodeDecodeRoundTrips()
    {
        Assert.True(AccountIdentityProof.TryDecode(VectorProof().Encode(), out var decoded));

        Assert.Equal(VectorSignerPublicKey, decoded!.SignerPublicKey);
        Assert.Equal(VectorCreatedAt, decoded.CreatedAt);
        Assert.Equal(Convert.FromHexString(VectorSignature), decoded.Signature);
    }

    [Theory]
    [InlineData(103)]
    [InlineData(105)]
    [InlineData(0)]
    public void DecodeRejectsAnyLengthButExactly104(int length)
    {
        // Fixed-width means trailing bytes are a different structure, not extra.
        Assert.False(AccountIdentityProof.TryDecode(new byte[length], out _));
    }

    // -- Validation --

    [Fact]
    public void ValidationRejectsAnIdentityThatIsNotTheSigner()
    {
        var otherIdentity = new byte[32];
        otherIdentity[0] = 0xAB;

        var result = VectorProof().Validate(
            otherIdentity, CipherSuite, SignatureScheme, VectorMlsSignatureKey);

        Assert.Equal(AccountIdentityProofResult.IdentityMismatch, result);
    }

    [Fact]
    public void ValidationRejectsADifferentLeafKey()
    {
        // The proof authorises one specific leaf key; reusing it for another
        // is exactly the substitution this construction exists to stop.
        var otherLeafKey = new byte[32];

        var result = VectorProof().Validate(
            VectorSignerPublicKey, CipherSuite, SignatureScheme, otherLeafKey);

        Assert.Equal(AccountIdentityProofResult.BadSignature, result);
    }

    [Fact]
    public void ValidationRejectsADifferentCipherSuite()
    {
        var result = VectorProof().Validate(
            VectorSignerPublicKey, 0x0002, SignatureScheme, VectorMlsSignatureKey);

        Assert.Equal(AccountIdentityProofResult.BadSignature, result);
    }

    [Fact]
    public void ValidationRejectsADifferentSignatureScheme()
    {
        var result = VectorProof().Validate(
            VectorSignerPublicKey, CipherSuite, 0x0403, VectorMlsSignatureKey);

        Assert.Equal(AccountIdentityProofResult.BadSignature, result);
    }

    [Fact]
    public void ValidationRejectsATamperedSignature()
    {
        byte[] tampered = Convert.FromHexString(VectorSignature);
        tampered[0] ^= 0xFF;

        var result = new AccountIdentityProof(VectorSignerPublicKey, VectorCreatedAt, tampered)
            .Validate(VectorSignerPublicKey, CipherSuite, SignatureScheme, VectorMlsSignatureKey);

        Assert.Equal(AccountIdentityProofResult.BadSignature, result);
    }

    [Fact]
    public void ValidationRejectsZeroCreatedAt()
    {
        // Zero is what separates this construction from the superseded one.
        var result = new AccountIdentityProof(
                VectorSignerPublicKey, 0, Convert.FromHexString(VectorSignature))
            .Validate(VectorSignerPublicKey, CipherSuite, SignatureScheme, VectorMlsSignatureKey);

        Assert.Equal(AccountIdentityProofResult.CreatedAtOutOfRange, result);
    }

    [Fact]
    public void ValidationRejectsCreatedAtBeyondExactJsonRange()
    {
        var result = new AccountIdentityProof(
                VectorSignerPublicKey,
                AccountIdentityProof.MaxCreatedAt + 1,
                Convert.FromHexString(VectorSignature))
            .Validate(VectorSignerPublicKey, CipherSuite, SignatureScheme, VectorMlsSignatureKey);

        Assert.Equal(AccountIdentityProofResult.CreatedAtOutOfRange, result);
    }

    [Fact]
    public void ValidationRejectsASignerKeyThatIsNotOnTheCurve()
    {
        var offCurve = Enumerable.Repeat((byte)0xFF, 32).ToArray();

        var result = new AccountIdentityProof(offCurve, VectorCreatedAt, new byte[64])
            .Validate(offCurve, CipherSuite, SignatureScheme, VectorMlsSignatureKey);

        Assert.Equal(AccountIdentityProofResult.InvalidSignerKey, result);
    }

    [Fact]
    public void ValidationRejectsMalformedFieldLengths()
    {
        var result = new AccountIdentityProof(new byte[31], VectorCreatedAt, new byte[64])
            .Validate(new byte[31], CipherSuite, SignatureScheme, VectorMlsSignatureKey);

        Assert.Equal(AccountIdentityProofResult.Malformed, result);
    }

    [Fact]
    public void ValidationDoesNotApplyAFreshnessWindow()
    {
        // A very old proof stays valid: its inputs are unchanged, and rejecting
        // on wall-clock skew would evict legitimate members.
        var template = AccountIdentityProof.BuildSigningEvent(
            VectorSignerPublicKey, 1, CipherSuite, SignatureScheme, VectorMlsSignatureKey);

        // Only the signature is unavailable here, so assert on the reason: an
        // ancient timestamp must fail signature checking, never a time check.
        var result = new AccountIdentityProof(VectorSignerPublicKey, 1, new byte[64])
            .Validate(VectorSignerPublicKey, CipherSuite, SignatureScheme, VectorMlsSignatureKey);

        Assert.NotNull(template);
        Assert.Equal(AccountIdentityProofResult.BadSignature, result);
    }

    // -- Tag shape --

    [Fact]
    public void SigningEventCarriesExactlyTheFiveRequiredTagsInOrder()
    {
        var tags = VectorTemplate().Tags;

        Assert.Equal(5, tags.Count);
        Assert.Equal(new[] { "d", "marmot.account-identity-proof.v2" }, tags[0]);
        Assert.Equal(new[] { "component", "0x8009" }, tags[1]);
        Assert.Equal(new[] { "ciphersuite", "0x0001" }, tags[2]);
        Assert.Equal(new[] { "signature_scheme", "0x0807" }, tags[3]);
        Assert.Equal("mls_signature_key", tags[4][0]);
    }

    [Fact]
    public void NumericTagsAreLowercaseHexWithAnExactWidth()
    {
        // The superseded construction encoded these as decimal. Getting this
        // wrong produces a valid-looking event that no peer accepts.
        var tags = AccountIdentityProof.BuildSigningEvent(
            VectorSignerPublicKey, VectorCreatedAt, 0x000a, 0x080f, VectorMlsSignatureKey).Tags;

        Assert.Equal("0x000a", tags[2][1]);
        Assert.Equal("0x080f", tags[3][1]);
    }

    [Fact]
    public void LeafKeyTagIsBareLowercaseHexWithNoPrefix()
    {
        string value = VectorTemplate().Tags[4][1];

        Assert.DoesNotContain("0x", value);
        Assert.Equal(value.ToLowerInvariant(), value);
        Assert.Equal(64, value.Length);
    }

    [Fact]
    public void SigningEventIsKind450WithTheConsentContent()
    {
        var template = VectorTemplate();

        Assert.Equal(450, template.Kind);
        Assert.Equal("Authorize this MLS leaf key for my Marmot account", template.Content);
    }
}
