using System.Security.Cryptography;
using System.Text.Json;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The kind-445 wire codec and the peeler seam.
/// </summary>
/// <remarks>
/// The tag-shape cases are the important ones. A current peer rejects a
/// non-conformant kind-445 event at the envelope, before any MLS processing, so
/// getting this wrong means every message silently fails to arrive.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class NostrGroupPeelerTests
{
    private static readonly byte[] RoutingId = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] ExporterSecret = Enumerable.Repeat((byte)7, 32).ToArray();

    private static string RoutingIdHex => Convert.ToHexString(RoutingId).ToLowerInvariant();

    /// <summary>
    /// Builds a properly signed kind-445 envelope.
    /// </summary>
    /// <remarks>
    /// Signed because the peeler verifies the id and signature before trusting
    /// any field. An unsigned fixture would only ever exercise the rejection
    /// path — which is how the earlier version of these tests came to assert
    /// that an unauthenticated transport id was passed through unchanged.
    /// </remarks>
    private static string Envelope(
        string content, string[][] tags, string? overrideId = null, string? overrideSig = null)
    {
        var (secret, publicKey) = Bip340.GenerateKeyPair();
        var template = new NostrEventTemplate(
            Convert.ToHexString(publicKey).ToLowerInvariant(),
            1700000000,
            445,
            tags,
            content);

        byte[] id = template.ComputeId();
        string sig = overrideSig
            ?? Convert.ToHexString(Bip340.Sign(secret, id)).ToLowerInvariant();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", overrideId ?? Convert.ToHexString(id).ToLowerInvariant());
            writer.WriteString("pubkey", template.PublicKeyHex);
            writer.WriteNumber("created_at", template.CreatedAt);
            writer.WriteNumber("kind", 445);
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in tags)
            {
                writer.WriteStartArray();
                foreach (string value in tag)
                    writer.WriteStringValue(value);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteString("content", content);
            writer.WriteString("sig", sig);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ExpectedId(string envelope)
    {
        using var document = JsonDocument.Parse(envelope);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static Func<byte[], byte[]?> SecretFor(byte[]? secret = null) =>
        _ => secret ?? ExporterSecret;

    // -- Round trip --

    [Fact]
    public void WrappedMessageRoundTripsThroughThePeeler()
    {
        var peeler = new NostrGroupPeeler();
        byte[] mls = "an MLS message"u8.ToArray();

        string envelope = peeler.WrapGroupMessage(mls, RoutingId, ExporterSecret);
        var peeled = peeler.Peel(envelope, SecretFor());

        Assert.Equal(PeeledContentKind.GroupMessage, peeled.Kind);
        Assert.Equal(RoutingId, peeled.TransportGroupId);
        Assert.Equal(mls, peeled.MlsBytes);
    }

    [Fact]
    public void WrappedMessageCarriesOnlyTheRoutingTag()
    {
        var peeler = new NostrGroupPeeler();

        string envelope = peeler.WrapGroupMessage("x"u8.ToArray(), RoutingId, ExporterSecret);

        using var document = JsonDocument.Parse(envelope);
        var tags = GroupMessageEvent.ReadTags(document.RootElement);

        // Notably no 'encoding' tag: the previous implementation emitted one,
        // and a current peer rejects the whole event because of it.
        Assert.Single(tags);
        Assert.Equal("h", tags[0][0]);
        Assert.Equal(RoutingIdHex, tags[0][1]);
    }

    [Fact]
    public void ExpirationIsIncludedWhenRequested()
    {
        var peeler = new NostrGroupPeeler();

        string envelope = peeler.WrapGroupMessage(
            "x"u8.ToArray(), RoutingId, ExporterSecret, expiresAt: 1700000060);

        using var document = JsonDocument.Parse(envelope);
        var tags = GroupMessageEvent.ReadTags(document.RootElement);

        Assert.Equal(2, tags.Count);
        Assert.Equal(new[] { "expiration", "1700000060" }, tags[1]);
    }

    [Fact]
    public void TwoWrapsOfTheSameMessageDifferByNonce()
    {
        var peeler = new NostrGroupPeeler();
        byte[] mls = "same"u8.ToArray();

        string first = peeler.WrapGroupMessage(mls, RoutingId, ExporterSecret);
        string second = peeler.WrapGroupMessage(mls, RoutingId, ExporterSecret);

        Assert.NotEqual(first, second);
    }

    // -- Tag shape: the conformance gate --

    [Fact]
    public void RoutingIdIsReadFromAConformantTagSet()
    {
        var tags = new IReadOnlyList<string>[] { new[] { "h", RoutingIdHex } };

        Assert.Equal(RoutingId, GroupMessageEvent.ReadTransportGroupId(tags));
    }

    [Fact]
    public void ExpirationTagIsAccepted()
    {
        var tags = new IReadOnlyList<string>[]
        {
            new[] { "h", RoutingIdHex },
            new[] { "expiration", "1700000060" },
        };

        Assert.Equal(RoutingId, GroupMessageEvent.ReadTransportGroupId(tags));
    }

    [Fact]
    public void TagOrderIsNotSignificant()
    {
        var tags = new IReadOnlyList<string>[]
        {
            new[] { "expiration", "1" },
            new[] { "h", RoutingIdHex },
        };

        Assert.Equal(RoutingId, GroupMessageEvent.ReadTransportGroupId(tags));
    }

    [Fact]
    public void AnyOtherTagIsRejected()
    {
        // This is the regression that matters: the previous implementation
        // emitted an 'encoding' tag on every event.
        var tags = new IReadOnlyList<string>[]
        {
            new[] { "h", RoutingIdHex },
            new[] { "encoding", "base64" },
        };

        var ex = Assert.Throws<PeelFailedException>(
            () => GroupMessageEvent.ReadTransportGroupId(tags));
        Assert.Contains("encoding", ex.Message);
    }

    [Fact]
    public void ADuplicateRoutingTagIsRejected()
    {
        var tags = new IReadOnlyList<string>[]
        {
            new[] { "h", RoutingIdHex },
            new[] { "h", RoutingIdHex },
        };

        Assert.Throws<PeelFailedException>(() => GroupMessageEvent.ReadTransportGroupId(tags));
    }

    [Fact]
    public void ADuplicateExpirationTagIsRejected()
    {
        var tags = new IReadOnlyList<string>[]
        {
            new[] { "h", RoutingIdHex },
            new[] { "expiration", "1" },
            new[] { "expiration", "2" },
        };

        Assert.Throws<PeelFailedException>(() => GroupMessageEvent.ReadTransportGroupId(tags));
    }

    [Fact]
    public void AMissingRoutingTagIsRejected()
    {
        var tags = new IReadOnlyList<string>[] { new[] { "expiration", "1" } };

        Assert.Throws<PeelFailedException>(() => GroupMessageEvent.ReadTransportGroupId(tags));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abcd")]
    [InlineData("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF")]
    public void ARoutingIdThatIsNot32BytesOfLowercaseHexIsRejected(string value)
    {
        // Uppercase decodes to the same bytes but changes the event id, so it
        // is not interchangeable on the wire.
        var tags = new IReadOnlyList<string>[] { new[] { "h", value } };

        Assert.Throws<PeelFailedException>(() => GroupMessageEvent.ReadTransportGroupId(tags));
    }

    [Fact]
    public void ARoutingTagWithExtraElementsIsRejected()
    {
        var tags = new IReadOnlyList<string>[] { new[] { "h", RoutingIdHex, "extra" } };

        Assert.Throws<PeelFailedException>(() => GroupMessageEvent.ReadTransportGroupId(tags));
    }

    [Fact]
    public void ANonNumericExpirationIsRejected()
    {
        var tags = new IReadOnlyList<string>[]
        {
            new[] { "h", RoutingIdHex },
            new[] { "expiration", "soon" },
        };

        Assert.Throws<PeelFailedException>(() => GroupMessageEvent.ReadTransportGroupId(tags));
    }

    [Fact]
    public void AnExpiredTimestampIsStillAccepted()
    {
        // Expiration is relay deletion metadata, not a validity window;
        // treating it as one would drop messages over clock skew.
        var tags = new IReadOnlyList<string>[]
        {
            new[] { "h", RoutingIdHex },
            new[] { "expiration", "1" },
        };

        Assert.Equal(RoutingId, GroupMessageEvent.ReadTransportGroupId(tags));
    }

    [Fact]
    public void BuildingRejectsARoutingIdOfTheWrongLength()
    {
        Assert.Throws<ArgumentException>(() => GroupMessageEvent.BuildTags(new byte[31]));
    }

    // -- Peel failures --

    [Fact]
    public void AnUnknownRoutingIdDefersRatherThanFailing()
    {
        var peeler = new NostrGroupPeeler();
        string envelope = Envelope("irrelevant", new[] { new[] { "h", RoutingIdHex } });

        var ex = Assert.Throws<PeelFailedException>(() => peeler.Peel(envelope, _ => null));

        // The commit carrying this epoch's secret may not have arrived yet.
        Assert.True(ex.Retryable);
    }

    [Fact]
    public void AWrongKeyDefersRatherThanFailingTerminally()
    {
        var peeler = new NostrGroupPeeler();
        string envelope = peeler.WrapGroupMessage("x"u8.ToArray(), RoutingId, ExporterSecret);
        byte[] wrongKey = Enumerable.Repeat((byte)9, 32).ToArray();

        var ex = Assert.Throws<PeelFailedException>(() => peeler.Peel(envelope, SecretFor(wrongKey)));

        // It may open under a different retained epoch secret.
        Assert.True(ex.Retryable);
    }

    [Fact]
    public void AMalformedTagShapeIsTerminalNotRetryable()
    {
        var peeler = new NostrGroupPeeler();
        string envelope = Envelope("x",
            new[] { new[] { "h", RoutingIdHex }, new[] { "encoding", "base64" } });

        var ex = Assert.Throws<PeelFailedException>(() => peeler.Peel(envelope, SecretFor()));

        // Retrying a structurally invalid envelope forever just fills a queue.
        Assert.False(ex.Retryable);
    }

    [Fact]
    public void AnUnsupportedKindIsRejected()
    {
        var peeler = new NostrGroupPeeler();
        string envelope = """{"kind":1,"content":"hi","tags":[]}""";

        Assert.Throws<PeelFailedException>(() => peeler.Peel(envelope, SecretFor()));
    }

    [Fact]
    public void InvalidJsonIsRejectedCleanly()
    {
        var peeler = new NostrGroupPeeler();

        Assert.Throws<PeelFailedException>(() => peeler.Peel("{not json", SecretFor()));
    }

    [Fact]
    public void TheTransportIdIsBoundToTheVerifiedEventHash()
    {
        // Previously this test asserted the SELF-REPORTED id was passed through
        // unchanged, which pinned a defect: the transport id keys deduplication,
        // so an attacker who can post to a subscribed relay could pre-poison it
        // and have a legitimate message dropped as a duplicate.
        var peeler = new NostrGroupPeeler();
        string content = ChaCha20Poly1305Envelope.Seal("x"u8.ToArray(), ExporterSecret, out _);
        string envelope = Envelope(content, new[] { new[] { "h", RoutingIdHex } });

        var peeled = peeler.Peel(envelope, SecretFor());

        Assert.Equal(ExpectedId(envelope), peeled.TransportId);
        Assert.Equal(64, peeled.TransportId!.Length);
    }

    [Fact]
    public void AnEventWhoseIdDoesNotMatchItsContentIsRejected()
    {
        var peeler = new NostrGroupPeeler();
        string content = ChaCha20Poly1305Envelope.Seal("x"u8.ToArray(), ExporterSecret, out _);
        string envelope = Envelope(
            content, new[] { new[] { "h", RoutingIdHex } }, overrideId: new string('a', 64));

        var ex = Assert.Throws<PeelFailedException>(() => peeler.Peel(envelope, SecretFor()));

        Assert.Contains("id", ex.Message);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public void AnEventWithAnInvalidSignatureIsRejectedBeforeDecryption()
    {
        var peeler = new NostrGroupPeeler();
        string content = ChaCha20Poly1305Envelope.Seal("x"u8.ToArray(), ExporterSecret, out _);
        string envelope = Envelope(
            content, new[] { new[] { "h", RoutingIdHex } }, overrideSig: new string('0', 128));

        // The spec makes verification a MUST *before* attempting to decrypt.
        var ex = Assert.Throws<PeelFailedException>(() => peeler.Peel(envelope, _ =>
            throw new InvalidOperationException("must not reach the exporter lookup")));

        Assert.Contains("signature", ex.Message);
    }

    [Theory]
    [InlineData("{\"kind\":99999999999999,\"id\":\"x\",\"pubkey\":\"x\",\"created_at\":1,\"tags\":[],\"content\":\"\",\"sig\":\"x\"}")]
    [InlineData("{\"kind\":1.5,\"id\":\"x\",\"pubkey\":\"x\",\"created_at\":1,\"tags\":[],\"content\":\"\",\"sig\":\"x\"}")]
    [InlineData("[]")]
    [InlineData("\"hi\"")]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("{\"kind\":445,\"id\":null,\"pubkey\":\"x\",\"created_at\":1,\"tags\":[],\"content\":\"\",\"sig\":\"x\"}")]
    [InlineData("{\"kind\":445,\"id\":\"x\",\"pubkey\":\"x\",\"created_at\":1e400,\"tags\":[],\"content\":\"\",\"sig\":\"x\"}")]
    [InlineData("{\"kind\":445,\"id\":\"x\",\"pubkey\":\"x\",\"created_at\":1,\"tags\":\"nope\",\"content\":\"\",\"sig\":\"x\"}")]
    public void MalformedEnvelopesFailAsPeelFailuresNotArbitraryExceptions(string envelope)
    {
        // The engine branches on PeelFailedException.Retryable. Anything else
        // escaping bypasses that classification entirely.
        var peeler = new NostrGroupPeeler();

        Assert.Throws<PeelFailedException>(() => peeler.Peel(envelope, SecretFor()));
    }

    // -- Envelope crypto --

    [Fact]
    public void SealedEnvelopeOpensToTheOriginalBytes()
    {
        byte[] plaintext = "hello marmot"u8.ToArray();

        string sealedContent = ChaCha20Poly1305Envelope.Seal(plaintext, ExporterSecret, out byte[] nonce);

        Assert.Equal(12, nonce.Length);
        Assert.Equal(plaintext, ChaCha20Poly1305Envelope.Open(sealedContent, ExporterSecret));
    }

    [Fact]
    public void TamperingWithTheCiphertextFailsAuthentication()
    {
        string sealedContent = ChaCha20Poly1305Envelope.Seal("x"u8.ToArray(), ExporterSecret, out _);
        byte[] raw = Convert.FromBase64String(sealedContent);
        raw[^1] ^= 0xFF;

        Assert.Throws<CryptographicException>(
            () => ChaCha20Poly1305Envelope.Open(Convert.ToBase64String(raw), ExporterSecret));
    }

    [Fact]
    public void AnEnvelopeShorterThanNoncePlusTagIsRejected()
    {
        string tooShort = Convert.ToBase64String(new byte[27]);

        Assert.Throws<CryptographicException>(
            () => ChaCha20Poly1305Envelope.Open(tooShort, ExporterSecret));
    }

    [Fact]
    public void NonBase64ContentIsRejected()
    {
        Assert.Throws<CryptographicException>(
            () => ChaCha20Poly1305Envelope.Open("not base64!!", ExporterSecret));
    }

    [Fact]
    public void TheExporterContractMatchesTheProtocol()
    {
        // Label, context and length are shared with every other implementation;
        // a mismatch means nobody can read our messages.
        Assert.Equal("marmot", NostrGroupPeeler.ExporterLabel);
        Assert.Equal("group-event"u8.ToArray(), NostrGroupPeeler.ExporterContext);
        Assert.Equal(32, NostrGroupPeeler.ExporterLength);
    }
}
