using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Math;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// A Welcome travelling the whole inbound path: gift wrap, seal, kind-444
/// rumor, out as MLS bytes plus the metadata the join needs.
/// </summary>
[Trait("Category", "MarmotEngine")]
public class PeelWelcomeTests
{
    private static readonly X9ECParameters Curve = ECNamedCurveTable.GetByName("secp256k1");

    private static readonly byte[] KeyPackageEventId =
        Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();

    private static readonly byte[] WelcomeBytes = "an MLS welcome message"u8.ToArray();

    private sealed record Identity(byte[] Secret, byte[] PublicKey)
    {
        public string PublicKeyHex => Convert.ToHexString(PublicKey).ToLowerInvariant();
    }

    private static Identity NewIdentity(byte seed)
    {
        byte[] secret = Enumerable.Repeat(seed, 32).ToArray();
        var point = Curve.G.Multiply(new BigInteger(1, secret)).Normalize();

        if (point.AffineYCoord.ToBigInteger().TestBit(0))
        {
            secret = Pad32(Curve.N.Subtract(new BigInteger(1, secret)).ToByteArrayUnsigned());
            point = Curve.G.Multiply(new BigInteger(1, secret)).Normalize();
        }

        return new Identity(secret, Pad32(point.AffineXCoord.ToBigInteger().ToByteArrayUnsigned()));
    }

    private static byte[] Pad32(byte[] value)
    {
        var padded = new byte[32];
        value.CopyTo(padded.AsSpan(32 - value.Length));
        return padded;
    }

    /// <summary>BIP-340 signing with zero aux randomness.</summary>
    private static byte[] Sign(byte[] secret, byte[] message)
    {
        var n = Curve.N;
        var d0 = new BigInteger(1, secret);
        var d = Curve.G.Multiply(d0).Normalize().AffineYCoord.ToBigInteger().TestBit(0)
            ? n.Subtract(d0)
            : d0;
        byte[] px = Pad32(Curve.G.Multiply(d).Normalize().AffineXCoord.ToBigInteger().ToByteArrayUnsigned());

        byte[] t = Pad32(d.ToByteArrayUnsigned());
        byte[] aux = Bip340.TaggedHash("BIP0340/aux", new byte[32]);
        for (int i = 0; i < 32; i++)
            t[i] ^= aux[i];

        var nonceInput = new byte[96];
        t.CopyTo(nonceInput, 0);
        px.CopyTo(nonceInput, 32);
        message.CopyTo(nonceInput, 64);
        var k0 = new BigInteger(1, Bip340.TaggedHash("BIP0340/nonce", nonceInput)).Mod(n);

        var r = Curve.G.Multiply(k0).Normalize();
        var k = r.AffineYCoord.ToBigInteger().TestBit(0) ? n.Subtract(k0) : k0;
        byte[] rx = Pad32(r.AffineXCoord.ToBigInteger().ToByteArrayUnsigned());

        var challengeInput = new byte[96];
        rx.CopyTo(challengeInput, 0);
        px.CopyTo(challengeInput, 32);
        message.CopyTo(challengeInput, 64);
        var e = new BigInteger(1, Bip340.TaggedHash("BIP0340/challenge", challengeInput)).Mod(n);

        var signature = new byte[64];
        rx.CopyTo(signature, 0);
        Pad32(k.Add(e.Multiply(d)).Mod(n).ToByteArrayUnsigned()).CopyTo(signature, 32);
        return signature;
    }

    private static string WrapWelcome(
        Identity inviter,
        Identity invitee,
        IReadOnlyList<string>? relays = null,
        byte[]? welcomeBytes = null)
    {
        var ephemeral = NewIdentity(0x99);
        var rumor = new Rumor(
            inviter.PublicKeyHex,
            1700000000,
            WelcomeEvent.Kind,
            WelcomeEvent.BuildTags(KeyPackageEventId, relays ?? new[] { "wss://relay.example" }),
            Convert.ToBase64String(welcomeBytes ?? WelcomeBytes));

        return Nip59GiftWrap.Wrap(
            rumor, inviter.Secret, inviter.PublicKey, invitee.PublicKey,
            ephemeral.Secret, ephemeral.PublicKey, Sign);
    }

    [Fact]
    public void AGiftWrappedWelcomeIsPeeledToItsMlsBytes()
    {
        var inviter = NewIdentity(0x11);
        var invitee = NewIdentity(0x22);
        var peeler = new NostrGroupPeeler(invitee.Secret);

        var peeled = peeler.Peel(WrapWelcome(inviter, invitee), _ => null);

        Assert.Equal(PeeledContentKind.Welcome, peeled.Kind);
        Assert.Equal(WelcomeBytes, peeled.MlsBytes);
        Assert.Null(peeled.TransportGroupId);
    }

    [Fact]
    public void TheJoinMetadataTravelsWithTheMessage()
    {
        var inviter = NewIdentity(0x11);
        var invitee = NewIdentity(0x22);
        var peeler = new NostrGroupPeeler(invitee.Secret);

        var peeled = peeler.Peel(
            WrapWelcome(inviter, invitee, new[] { "wss://a.example", "wss://b.example" }),
            _ => null);

        Assert.NotNull(peeled.Welcome);
        Assert.Equal(KeyPackageEventId, peeled.Welcome!.KeyPackageEventId);
        Assert.Equal(new[] { "wss://a.example", "wss://b.example" }, peeled.Welcome.Relays);

        // The inviter comes from the verified seal, not an unsigned field.
        Assert.Equal(inviter.PublicKeyHex, peeled.Welcome.SenderPublicKeyHex);
    }

    [Fact]
    public void AWelcomeForSomeoneElseIsRejected()
    {
        var inviter = NewIdentity(0x11);
        var invitee = NewIdentity(0x22);
        var bystander = NewIdentity(0x44);
        var peeler = new NostrGroupPeeler(bystander.Secret);

        Assert.Throws<PeelFailedException>(
            () => peeler.Peel(WrapWelcome(inviter, invitee), _ => null));
    }

    [Fact]
    public void AWelcomeFailureIsTerminalNotRetryable()
    {
        var inviter = NewIdentity(0x11);
        var invitee = NewIdentity(0x22);
        var peeler = new NostrGroupPeeler(NewIdentity(0x44).Secret);

        var ex = Assert.Throws<PeelFailedException>(
            () => peeler.Peel(WrapWelcome(inviter, invitee), _ => null));

        // A wrap that will not open for us is not ours or is forged; waiting
        // does not change that, unlike a group message awaiting its epoch key.
        Assert.False(ex.Retryable);
    }

    [Fact]
    public void APeelerWithoutAnAccountSecretRejectsWelcomesExplicitly()
    {
        var inviter = NewIdentity(0x11);
        var invitee = NewIdentity(0x22);
        var peeler = new NostrGroupPeeler();

        var ex = Assert.Throws<PeelFailedException>(
            () => peeler.Peel(WrapWelcome(inviter, invitee), _ => null));

        // Silently ignoring the envelope would look like a lost invite.
        Assert.Contains("account secret", ex.Message);
    }

    [Fact]
    public void AGroupMessagePeelerStillWorksWithAnAccountSecretPresent()
    {
        var invitee = NewIdentity(0x22);
        var peeler = new NostrGroupPeeler(invitee.Secret);
        byte[] routingId = Enumerable.Repeat((byte)5, 32).ToArray();
        byte[] exporter = Enumerable.Repeat((byte)7, 32).ToArray();

        string envelope = peeler.WrapGroupMessage("hello"u8.ToArray(), routingId, exporter);
        var peeled = peeler.Peel(envelope, _ => exporter);

        Assert.Equal(PeeledContentKind.GroupMessage, peeled.Kind);
        Assert.Null(peeled.Welcome);
    }

    [Fact]
    public void ANonConformantWelcomeRumorIsRejected()
    {
        var inviter = NewIdentity(0x11);
        var invitee = NewIdentity(0x22);
        var peeler = new NostrGroupPeeler(invitee.Secret);

        Assert.Throws<PeelFailedException>(() => peeler.Peel(
            WrapWelcome(inviter, invitee, new[] { "https://not-a-relay.example" }), _ => null));
    }

    [Fact]
    public void AnEmptyWelcomeIsRejected()
    {
        var inviter = NewIdentity(0x11);
        var invitee = NewIdentity(0x22);
        var peeler = new NostrGroupPeeler(invitee.Secret);

        Assert.Throws<PeelFailedException>(
            () => peeler.Peel(WrapWelcome(inviter, invitee, welcomeBytes: Array.Empty<byte>()), _ => null));
    }

    [Fact]
    public void AnAccountSecretOfTheWrongLengthIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new NostrGroupPeeler(new byte[16]));
    }
}
