using System.Text.Json;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Math;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// NIP-59 gift wrapping, including the checks that stop sender impersonation.
/// </summary>
[Trait("Category", "MarmotEngine")]
public class Nip59GiftWrapTests
{
    private static readonly X9ECParameters Curve = ECNamedCurveTable.GetByName("secp256k1");

    /// <summary>A keypair with an x-only public key, as Nostr uses.</summary>
    private sealed record Identity(byte[] Secret, byte[] PublicKey)
    {
        public string PublicKeyHex => Convert.ToHexString(PublicKey).ToLowerInvariant();

        public static Identity FromSecret(byte[] secret)
        {
            var point = Curve.G.Multiply(new BigInteger(1, secret)).Normalize();

            // BIP-340 keys are x-only with implicit even y, so a secret whose
            // point has odd y must be negated to match its own public key.
            byte[] effective = secret;
            if (point.AffineYCoord.ToBigInteger().TestBit(0))
            {
                effective = Pad32(Curve.N.Subtract(new BigInteger(1, secret)).ToByteArrayUnsigned());
                point = Curve.G.Multiply(new BigInteger(1, effective)).Normalize();
            }

            return new Identity(effective, Pad32(point.AffineXCoord.ToBigInteger().ToByteArrayUnsigned()));
        }

        private static byte[] Pad32(byte[] value)
        {
            var padded = new byte[32];
            value.CopyTo(padded.AsSpan(32 - value.Length));
            return padded;
        }
    }

    private static Identity NewIdentity(byte seed) =>
        Identity.FromSecret(Enumerable.Repeat(seed, 32).ToArray());

    /// <summary>
    /// BIP-340 signing, so the wrap's own signatures verify against
    /// <see cref="Bip340.Verify"/>.
    /// </summary>
    /// <remarks>
    /// Written out here rather than taken from a library because BouncyCastle
    /// 2.5.1 ships no Schnorr signer, and production signing goes through the
    /// signer abstraction rather than a raw key.
    /// </remarks>
    private static byte[] Sign(byte[] secret, byte[] message)
    {
        var n = Curve.N;
        var d0 = new BigInteger(1, secret);
        var point = Curve.G.Multiply(d0).Normalize();

        // The secret must correspond to the even-y form of its public key.
        var d = point.AffineYCoord.ToBigInteger().TestBit(0) ? n.Subtract(d0) : d0;
        byte[] px = Pad32(Curve.G.Multiply(d).Normalize().AffineXCoord.ToBigInteger().ToByteArrayUnsigned());

        // Deterministic nonce with zero aux randomness, which is what the spec
        // vectors use.
        byte[] t = Pad32(d.ToByteArrayUnsigned());
        byte[] auxHash = Bip340.TaggedHash("BIP0340/aux", new byte[32]);
        for (int i = 0; i < 32; i++)
            t[i] ^= auxHash[i];

        var nonceInput = new byte[96];
        t.CopyTo(nonceInput, 0);
        px.CopyTo(nonceInput, 32);
        message.CopyTo(nonceInput, 64);
        var k0 = new BigInteger(1, Bip340.TaggedHash("BIP0340/nonce", nonceInput)).Mod(n);
        if (k0.SignValue == 0)
            throw new InvalidOperationException("Nonce was zero; retry with different aux.");

        var r = Curve.G.Multiply(k0).Normalize();
        var k = r.AffineYCoord.ToBigInteger().TestBit(0) ? n.Subtract(k0) : k0;
        byte[] rx = Pad32(r.AffineXCoord.ToBigInteger().ToByteArrayUnsigned());

        var challengeInput = new byte[96];
        rx.CopyTo(challengeInput, 0);
        px.CopyTo(challengeInput, 32);
        message.CopyTo(challengeInput, 64);
        var e = new BigInteger(1, Bip340.TaggedHash("BIP0340/challenge", challengeInput)).Mod(n);

        byte[] s = Pad32(k.Add(e.Multiply(d)).Mod(n).ToByteArrayUnsigned());

        var signature = new byte[64];
        rx.CopyTo(signature, 0);
        s.CopyTo(signature, 32);
        return signature;
    }

    private static byte[] Pad32(byte[] value)
    {
        var padded = new byte[32];
        value.CopyTo(padded.AsSpan(32 - value.Length));
        return padded;
    }

    private static Rumor SampleRumor(Identity sender) =>
        new(sender.PublicKeyHex, 1700000000, 444, Array.Empty<IReadOnlyList<string>>(), "welcome-bytes");

    private static string WrapFor(Identity sender, Identity recipient, Rumor? rumor = null)
    {
        var ephemeral = NewIdentity(0x33);
        return Nip59GiftWrap.Wrap(
            rumor ?? SampleRumor(sender),
            sender.Secret,
            sender.PublicKey,
            recipient.PublicKey,
            ephemeral.Secret,
            ephemeral.PublicKey,
            Sign);
    }

    [Fact]
    public void ARumorRoundTripsThroughAGiftWrap()
    {
        var sender = NewIdentity(0x11);
        var recipient = NewIdentity(0x22);

        var opened = Nip59GiftWrap.Unwrap(WrapFor(sender, recipient), recipient.Secret);

        Assert.Equal(sender.PublicKeyHex, opened.PublicKeyHex);
        Assert.Equal(444, opened.Kind);
        Assert.Equal("welcome-bytes", opened.Content);
    }

    [Fact]
    public void TheOuterWrapIsSignedByAnEphemeralKeyNotTheSender()
    {
        var sender = NewIdentity(0x11);
        var recipient = NewIdentity(0x22);

        using var document = JsonDocument.Parse(WrapFor(sender, recipient));
        string wrapAuthor = document.RootElement.GetProperty("pubkey").GetString()!;

        // A relay must not be able to see who sent it.
        Assert.NotEqual(sender.PublicKeyHex, wrapAuthor);
        Assert.Equal(1059, document.RootElement.GetProperty("kind").GetInt32());
    }

    [Fact]
    public void TheWrapAddressesTheRecipient()
    {
        var recipient = NewIdentity(0x22);

        using var document = JsonDocument.Parse(WrapFor(NewIdentity(0x11), recipient));
        var tags = document.RootElement.GetProperty("tags");

        Assert.Equal("p", tags[0][0].GetString());
        Assert.Equal(recipient.PublicKeyHex, tags[0][1].GetString());
    }

    [Fact]
    public void TheSenderIsNotVisibleInTheWrappedBytes()
    {
        var sender = NewIdentity(0x11);
        string wrap = WrapFor(sender, NewIdentity(0x22));

        Assert.DoesNotContain(sender.PublicKeyHex, wrap, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnotherRecipientCannotOpenIt()
    {
        string wrap = WrapFor(NewIdentity(0x11), NewIdentity(0x22));

        Assert.Throws<GiftWrapException>(
            () => Nip59GiftWrap.Unwrap(wrap, NewIdentity(0x44).Secret));
    }

    [Fact]
    public void AForgedInnerSenderIsRejected()
    {
        // The attack the inner-sender check exists for: only the seal is
        // signed, so an attacker who seals honestly under their own key but
        // puts the victim's key on the rumor would otherwise impersonate them.
        // Built by hand because Wrap deliberately refuses to produce it.
        var attacker = NewIdentity(0x11);
        var victim = NewIdentity(0x55);
        var recipient = NewIdentity(0x22);

        string wrap = ForgeWrap(attacker, victim, recipient);

        var ex = Assert.Throws<GiftWrapException>(() => Nip59GiftWrap.Unwrap(wrap, recipient.Secret));
        Assert.Contains("forged", ex.Message);
    }

    [Fact]
    public void WrappingRefusesARumorAuthoredBySomeoneElse()
    {
        var sender = NewIdentity(0x11);
        var victim = NewIdentity(0x55);
        var recipient = NewIdentity(0x22);
        var ephemeral = NewIdentity(0x33);

        var notMine = new Rumor(
            victim.PublicKeyHex, 1700000000, 444,
            Array.Empty<IReadOnlyList<string>>(), "not mine to send");

        Assert.Throws<ArgumentException>(() => Nip59GiftWrap.Wrap(
            notMine, sender.Secret, sender.PublicKey, recipient.PublicKey,
            ephemeral.Secret, ephemeral.PublicKey, Sign));
    }

    /// <summary>
    /// Builds a wrap whose seal is validly signed by <paramref name="attacker"/>
    /// but whose rumor claims <paramref name="victim"/> as its author.
    /// </summary>
    private static string ForgeWrap(Identity attacker, Identity victim, Identity recipient)
    {
        var ephemeral = NewIdentity(0x33);

        string rumorJson = WriteEvent(
            victim.PublicKeyHex, 1700000000, 444, "i am the victim", null);

        byte[] sealKey = Nip44.DeriveConversationKey(attacker.Secret, recipient.PublicKey);
        string sealContent = Nip44.Encrypt(rumorJson, sealKey);
        var sealTemplate = new NostrEventTemplate(
            attacker.PublicKeyHex, 1700000000, 13,
            Array.Empty<IReadOnlyList<string>>(), sealContent);
        byte[] sealId = sealTemplate.ComputeId();
        string sealJson = WriteEvent(
            attacker.PublicKeyHex, 1700000000, 13, sealContent,
            Convert.ToHexString(Sign(attacker.Secret, sealId)).ToLowerInvariant(),
            Convert.ToHexString(sealId).ToLowerInvariant());

        byte[] wrapKey = Nip44.DeriveConversationKey(ephemeral.Secret, recipient.PublicKey);
        string wrapContent = Nip44.Encrypt(sealJson, wrapKey);
        var wrapTemplate = new NostrEventTemplate(
            ephemeral.PublicKeyHex, 1700000000, 1059,
            new[] { new[] { "p", recipient.PublicKeyHex } }, wrapContent);
        byte[] wrapId = wrapTemplate.ComputeId();

        return WriteEvent(
            ephemeral.PublicKeyHex, 1700000000, 1059, wrapContent,
            Convert.ToHexString(Sign(ephemeral.Secret, wrapId)).ToLowerInvariant(),
            Convert.ToHexString(wrapId).ToLowerInvariant(),
            new[] { new[] { "p", recipient.PublicKeyHex } });
    }

    private static string WriteEvent(
        string pubkey, long createdAt, int kind, string content, string? sig,
        string? id = null, string[][]? tags = null)
    {
        var template = new NostrEventTemplate(
            pubkey, createdAt, kind, tags ?? Array.Empty<IReadOnlyList<string>>(), content);
        string eventId = id ?? Convert.ToHexString(template.ComputeId()).ToLowerInvariant();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", eventId);
            writer.WriteString("pubkey", pubkey);
            writer.WriteNumber("created_at", createdAt);
            writer.WriteNumber("kind", kind);
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in tags ?? Array.Empty<string[]>())
            {
                writer.WriteStartArray();
                foreach (string value in tag)
                    writer.WriteStringValue(value);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteString("content", content);
            if (sig is not null)
                writer.WriteString("sig", sig);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void ATamperedWrapSignatureIsRejected()
    {
        string wrap = WrapFor(NewIdentity(0x11), NewIdentity(0x22));
        var recipient = NewIdentity(0x22);

        using var document = JsonDocument.Parse(wrap);
        string sig = document.RootElement.GetProperty("sig").GetString()!;
        string tampered = wrap.Replace(sig, new string('0', sig.Length));

        Assert.Throws<GiftWrapException>(() => Nip59GiftWrap.Unwrap(tampered, recipient.Secret));
    }

    [Fact]
    public void AWrapWhoseIdDoesNotMatchItsContentIsRejected()
    {
        string wrap = WrapFor(NewIdentity(0x11), NewIdentity(0x22));
        var recipient = NewIdentity(0x22);

        using var document = JsonDocument.Parse(wrap);
        string id = document.RootElement.GetProperty("id").GetString()!;
        string tampered = wrap.Replace(id, new string('1', id.Length));

        var ex = Assert.Throws<GiftWrapException>(() => Nip59GiftWrap.Unwrap(tampered, recipient.Secret));
        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public void AWrongOuterKindIsRejected()
    {
        var recipient = NewIdentity(0x22);
        string notAWrap = """{"id":"00","pubkey":"00","created_at":1,"kind":1,"tags":[],"content":"","sig":"00"}""";

        Assert.Throws<GiftWrapException>(() => Nip59GiftWrap.Unwrap(notAWrap, recipient.Secret));
    }

    [Fact]
    public void MalformedJsonIsRejectedCleanly()
    {
        var recipient = NewIdentity(0x22);

        Assert.Throws<GiftWrapException>(() => Nip59GiftWrap.Unwrap("{not json", recipient.Secret));
    }

    [Fact]
    public void TwoWrapsOfTheSameRumorAreUnlinkable()
    {
        var sender = NewIdentity(0x11);
        var recipient = NewIdentity(0x22);
        var rumor = SampleRumor(sender);

        var first = NewIdentity(0x66);
        var second = NewIdentity(0x77);
        string a = Nip59GiftWrap.Wrap(
            rumor, sender.Secret, sender.PublicKey, recipient.PublicKey,
            first.Secret, first.PublicKey, Sign);
        string b = Nip59GiftWrap.Wrap(
            rumor, sender.Secret, sender.PublicKey, recipient.PublicKey,
            second.Secret, second.PublicKey, Sign);

        using var docA = JsonDocument.Parse(a);
        using var docB = JsonDocument.Parse(b);

        Assert.NotEqual(
            docA.RootElement.GetProperty("pubkey").GetString(),
            docB.RootElement.GetProperty("pubkey").GetString());
        Assert.NotEqual(
            docA.RootElement.GetProperty("content").GetString(),
            docB.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void TimestampJitterMovesTimestampsBackwardsOnly()
    {
        var sender = NewIdentity(0x11);
        var recipient = NewIdentity(0x22);
        var ephemeral = NewIdentity(0x33);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        string wrap = Nip59GiftWrap.Wrap(
            SampleRumor(sender), sender.Secret, sender.PublicKey, recipient.PublicKey,
            ephemeral.Secret, ephemeral.PublicKey, Sign, TimeSpan.FromHours(2));

        using var document = JsonDocument.Parse(wrap);
        long createdAt = document.RootElement.GetProperty("created_at").GetInt64();

        // A future timestamp would stand out; the window only reaches back.
        Assert.InRange(createdAt, now - 7200, now + 1);
    }
}
