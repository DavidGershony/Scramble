using System.Text.Json;
using Scramble.Marmot;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The kind-30443 KeyPackage publication codec.
/// </summary>
/// <remarks>
/// The tag-shape cases carry the weight. A KeyPackage is how an account is
/// reachable for an invite at all, so a non-conformant publication does not
/// degrade anything visibly — it simply makes the account uninvitable, with no
/// error anywhere near the cause.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class KeyPackageEventTests
{
    private const string SlotId = "1f2e3d4c5b6a79880011223344556677889900aabbccddeeff00112233445566";
    private const string KeyPackageRefHex = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899";

    private static readonly byte[] KeyPackageBytes = "an MLS message wrapping a key package"u8.ToArray();

    private static readonly ushort[] CipherSuites = [0x0001];
    private static readonly ushort[] MlsExtensions = [0x0006, 0x000a];
    private static readonly ushort[] MlsProposals = [0x0008, 0x000a];
    private static readonly ushort[] AppComponents = [0x8001, 0x8003, 0x8004, 0x8009];

    private static IReadOnlyList<IReadOnlyList<string>> ConformantTags() =>
        KeyPackageEvent.BuildTags(
            SlotId, KeyPackageRefHex, CipherSuites, MlsExtensions, MlsProposals, AppComponents);

    private static string Content => Convert.ToBase64String(KeyPackageBytes);

    private static KeyPackagePublication ReadTags(
        IReadOnlyList<IReadOnlyList<string>> tags, string? content = null) =>
        KeyPackageEvent.Read(
            tags,
            content ?? Content,
            "aa".PadRight(64, 'b'),
            "cc".PadRight(64, 'd'),
            1700000000);

    private static string Value(IReadOnlyList<IReadOnlyList<string>> tags, string name) =>
        tags.Single(tag => tag[0] == name)[1];

    private static IReadOnlyList<string> Tag(
        IReadOnlyList<IReadOnlyList<string>> tags, string name) =>
        tags.Single(tag => tag[0] == name);

    // -- Building --

    [Fact]
    public void TheTagSetIsTheSevenTheSpecDefines()
    {
        var tags = ConformantTags();

        Assert.Equal(
            ["d", "mls_protocol_version", "i", "mls_ciphersuite", "mls_extensions", "mls_proposals", "app_components"],
            tags.Select(tag => tag[0]));
    }

    [Fact]
    public void NoEncodingTagIsEmitted()
    {
        // The trap this codec exists to avoid. The previous implementation
        // emitted ["encoding", "base64"] on 30443/444/445; a current peer
        // rejects such an event at the envelope, before any MLS processing.
        Assert.DoesNotContain(ConformantTags(), tag => tag[0] == "encoding");
    }

    [Fact]
    public void NoRelaysTagIsEmitted()
    {
        // KeyPackage relay discovery moved to the account's NIP-65 kind-10002
        // list. A KeyPackage event does not repeat those relays.
        Assert.DoesNotContain(ConformantTags(), tag => tag[0] == "relays");
    }

    [Fact]
    public void TheProtocolVersionIsOnePointZero()
    {
        Assert.Equal("1.0", Value(ConformantTags(), "mls_protocol_version"));
    }

    [Fact]
    public void IdListValuesArePrefixedAndZeroPaddedToFourDigits()
    {
        var tags = ConformantTags();

        Assert.Equal(["mls_extensions", "0x0006", "0x000a"], Tag(tags, "mls_extensions"));
        Assert.Equal(
            ["app_components", "0x8001", "0x8003", "0x8004", "0x8009"],
            Tag(tags, "app_components"));
    }

    [Fact]
    public void AllIdsOfOneListSitOnOneTag()
    {
        // A producer must not split one list across repeated tags, and a
        // consumer rejects an event that does.
        var tags = ConformantTags();

        Assert.Single(tags, tag => tag[0] == "app_components");
    }

    [Fact]
    public void BuildingWithoutTheAccountIdentityProofComponentIsRejected()
    {
        // Advertising 0x8009 is what makes this a Marmot KeyPackage.
        var ex = Assert.Throws<ArgumentException>(() => KeyPackageEvent.BuildTags(
            SlotId, KeyPackageRefHex, CipherSuites, MlsExtensions, MlsProposals, [0x8001, 0x8003]));

        Assert.Contains("0x8009", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1F2E3D4C5B6A79880011223344556677889900AABBCCDDEEFF00112233445566")]
    [InlineData("1f2e3d4c5b6a79880011223344556677889900aabbccddeeff0011223344556")]
    public void ASlotIdThatIsNotThirtyTwoBytesOfLowercaseHexIsRejected(string slotId)
    {
        Assert.Throws<ArgumentException>(() => KeyPackageEvent.BuildTags(
            slotId, KeyPackageRefHex, CipherSuites, MlsExtensions, MlsProposals, AppComponents));
    }

    [Fact]
    public void AnEmptyIdListIsRejected()
    {
        Assert.Throws<ArgumentException>(() => KeyPackageEvent.BuildTags(
            SlotId, KeyPackageRefHex, CipherSuites, [], MlsProposals, AppComponents));
    }

    [Fact]
    public void ARepeatedIdWithinOneListIsRejected()
    {
        Assert.Throws<ArgumentException>(() => KeyPackageEvent.BuildTags(
            SlotId, KeyPackageRefHex, CipherSuites, [0x0006, 0x0006], MlsProposals, AppComponents));
    }

    [Fact]
    public void ASlotIdIsThirtyTwoRandomBytesAndDiffersEachTime()
    {
        string first = KeyPackageEvent.NewSlotId();
        string second = KeyPackageEvent.NewSlotId();

        Assert.Equal(64, first.Length);
        Assert.Equal(first, first.ToLowerInvariant());
        Assert.NotEqual(first, second);
    }

    // -- The unsigned template --

    [Fact]
    public void TheTemplateIsAuthoredByTheAccountIdentity()
    {
        // Not an ephemeral key, unlike a kind-445 group message: publishing a
        // KeyPackage is the act of claiming the account owns that leaf.
        string account = "ab".PadRight(64, 'c');

        var template = KeyPackageEvent.BuildTemplate(
            account, KeyPackageBytes, SlotId, KeyPackageRefHex,
            CipherSuites, MlsExtensions, MlsProposals, AppComponents, 1700000000);

        Assert.Equal(account, template.PublicKeyHex);
        Assert.Equal(30443, template.Kind);
        Assert.Equal(Content, template.Content);
    }

    [Fact]
    public void TheTemplateIsReturnedUnsignedSoAnExternalSignerCanSignIt()
    {
        // NostrEventTemplate carries no signature by construction. This test
        // pins the shape of the seam rather than a behaviour: the account key
        // is usually in Amber or behind NIP-46, so this layer must not assume
        // it can sign.
        var template = KeyPackageEvent.BuildTemplate(
            "ab".PadRight(64, 'c'), KeyPackageBytes, SlotId, KeyPackageRefHex,
            CipherSuites, MlsExtensions, MlsProposals, AppComponents);

        Assert.Equal(32, template.ComputeId().Length);
    }

    [Fact]
    public void AnEmptyKeyPackageIsRejected()
    {
        Assert.Throws<ArgumentException>(() => KeyPackageEvent.BuildTemplate(
            "ab".PadRight(64, 'c'), [], SlotId, KeyPackageRefHex,
            CipherSuites, MlsExtensions, MlsProposals, AppComponents));
    }

    [Fact]
    public void AnAccountKeyThatIsNotThirtyTwoBytesOfLowercaseHexIsRejected()
    {
        Assert.Throws<ArgumentException>(() => KeyPackageEvent.BuildTemplate(
            "AB".PadRight(64, 'c'), KeyPackageBytes, SlotId, KeyPackageRefHex,
            CipherSuites, MlsExtensions, MlsProposals, AppComponents));
    }

    // -- Reading --

    [Fact]
    public void WhatWeBuildIsWhatWeRead()
    {
        var publication = ReadTags(ConformantTags());

        Assert.Equal(SlotId, publication.SlotId);
        Assert.Equal(KeyPackageRefHex, publication.KeyPackageRefHex);
        Assert.Equal(CipherSuites, publication.CipherSuites);
        Assert.Equal(MlsExtensions, publication.MlsExtensions);
        Assert.Equal(MlsProposals, publication.MlsProposals);
        Assert.Equal(AppComponents, publication.AppComponents);
        Assert.Equal(KeyPackageBytes, publication.KeyPackageBytes);
    }

    [Fact]
    public void TagOrderIsNotSignificant()
    {
        var reversed = ConformantTags().Reverse().ToArray();

        Assert.Equal(SlotId, ReadTags(reversed).SlotId);
    }

    [Fact]
    public void AnUnknownTagIsCarriedPastRatherThanRejected()
    {
        // Kind 445 has a closed tag set; kind 30443 does not. Rejecting here
        // would invent a rule the spec does not state and break against a peer
        // that adds a tag we have not seen.
        var tags = ConformantTags().Append<IReadOnlyList<string>>(["client", "scramble"]).ToArray();

        Assert.Equal(SlotId, ReadTags(tags).SlotId);
    }

    [Fact]
    public void AnEncodingTagDoesNotSwitchTheDecoder()
    {
        // A receiver must never choose a decoder from an encoding tag. Every
        // field is decoded by the rule that defines it, so the tag is inert.
        var tags = ConformantTags().Append<IReadOnlyList<string>>(["encoding", "hex"]).ToArray();

        Assert.Equal(KeyPackageBytes, ReadTags(tags).KeyPackageBytes);
    }

    [Theory]
    [InlineData("d")]
    [InlineData("mls_protocol_version")]
    [InlineData("i")]
    [InlineData("mls_ciphersuite")]
    [InlineData("mls_extensions")]
    [InlineData("mls_proposals")]
    [InlineData("app_components")]
    public void AMissingRequiredTagIsRejected(string name)
    {
        var tags = ConformantTags().Where(tag => tag[0] != name).ToArray();

        var ex = Assert.Throws<PeelFailedException>(() => ReadTags(tags));
        Assert.Contains(name, ex.Message);
        Assert.False(ex.Retryable);
    }

    [Theory]
    [InlineData("d")]
    [InlineData("mls_protocol_version")]
    [InlineData("i")]
    [InlineData("mls_ciphersuite")]
    [InlineData("mls_extensions")]
    [InlineData("mls_proposals")]
    [InlineData("app_components")]
    public void ARepeatedTagIsRejectedRatherThanResolvedByTakingTheFirst(string name)
    {
        // Explicitly a MUST NOT: reading the first occurrence lets an attacker
        // prepend a tag that another implementation ignores, so two peers
        // disagree about what the same signed event says.
        var tags = ConformantTags().ToList();
        tags.Insert(0, tags.Single(tag => tag[0] == name));

        var ex = Assert.Throws<PeelFailedException>(() => ReadTags(tags));
        Assert.Contains("exactly one", ex.Message);
    }

    [Theory]
    [InlineData("d")]
    [InlineData("mls_protocol_version")]
    [InlineData("i")]
    public void AnExtraValueOnASingletonTagIsRejected(string name)
    {
        var tags = ConformantTags()
            .Select(tag => tag[0] == name ? [.. tag, "extra"] : tag.ToArray())
            .ToArray();

        Assert.Throws<PeelFailedException>(() => ReadTags(tags));
    }

    [Fact]
    public void AnIdListSplitAcrossTwoTagsIsRejected()
    {
        // The rejection a producer's "exactly one tag" rule relies on: without
        // it, a split list would silently lose its second half.
        var tags = ConformantTags()
            .Where(tag => tag[0] != "app_components")
            .Append<IReadOnlyList<string>>(["app_components", "0x8001", "0x8009"])
            .Append<IReadOnlyList<string>>(["app_components", "0x8003"])
            .ToArray();

        Assert.Throws<PeelFailedException>(() => ReadTags(tags));
    }

    [Fact]
    public void AnEmptyIdListTagIsRejected()
    {
        var tags = ConformantTags()
            .Select(tag => tag[0] == "mls_extensions" ? ["mls_extensions"] : tag.ToArray())
            .ToArray();

        Assert.Throws<PeelFailedException>(() => ReadTags(tags));
    }

    [Fact]
    public void ARepeatedValueWithinOneIdListIsRejected()
    {
        var tags = ConformantTags()
            .Select(tag => tag[0] == "mls_proposals"
                ? ["mls_proposals", "0x0008", "0x0008"]
                : tag.ToArray())
            .ToArray();

        Assert.Throws<PeelFailedException>(() => ReadTags(tags));
    }

    [Theory]
    [InlineData("0x1")]
    [InlineData("0x00001")]
    [InlineData("0X0001")]
    [InlineData("0x000A")]
    [InlineData("1")]
    [InlineData("0xzzzz")]
    public void AnIdThatIsNotFourLowercaseHexDigitsIsRejected(string value)
    {
        // Consumers compare id-list values as exact strings, so a lenient
        // spelling is a different value, not the same one written differently.
        var tags = ConformantTags()
            .Select(tag => tag[0] == "mls_extensions" ? ["mls_extensions", value] : tag.ToArray())
            .ToArray();

        Assert.Throws<PeelFailedException>(() => ReadTags(tags));
    }

    [Fact]
    public void AProtocolVersionOtherThanOnePointZeroIsRejected()
    {
        var tags = ConformantTags()
            .Select(tag => tag[0] == "mls_protocol_version"
                ? ["mls_protocol_version", "1.1"]
                : tag.ToArray())
            .ToArray();

        Assert.Throws<PeelFailedException>(() => ReadTags(tags));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899")]
    [InlineData("aabbc")]
    public void AKeyPackageRefThatIsNotEvenLengthLowercaseHexIsRejected(string value)
    {
        var tags = ConformantTags()
            .Select(tag => tag[0] == "i" ? ["i", value] : tag.ToArray())
            .ToArray();

        Assert.Throws<PeelFailedException>(() => ReadTags(tags));
    }

    [Fact]
    public void AKeyPackageRefLongerThanTheLargestMlsHashIsRejected()
    {
        var tags = ConformantTags()
            .Select(tag => tag[0] == "i" ? ["i", new string('a', 130)] : tag.ToArray())
            .ToArray();

        Assert.Throws<PeelFailedException>(() => ReadTags(tags));
    }

    [Fact]
    public void AFortyEightByteKeyPackageRefIsAccepted()
    {
        // The ref is the ciphersuite's hash, so SHA-384 and SHA-512 sizes are
        // as valid as SHA-256's. Pinning 32 bytes would reject a valid peer
        // over a value the caller re-derives from the decoded KeyPackage anyway.
        var tags = ConformantTags()
            .Select(tag => tag[0] == "i" ? ["i", new string('a', 96)] : tag.ToArray())
            .ToArray();

        Assert.Equal(new string('a', 96), ReadTags(tags).KeyPackageRefHex);
    }

    [Fact]
    public void AnAppComponentListWithoutTheAccountIdentityProofIsRejected()
    {
        var tags = ConformantTags()
            .Select(tag => tag[0] == "app_components"
                ? ["app_components", "0x8001", "0x8003"]
                : tag.ToArray())
            .ToArray();

        var ex = Assert.Throws<PeelFailedException>(() => ReadTags(tags));
        Assert.Contains("0x8009", ex.Message);
    }

    [Fact]
    public void ContentThatIsNotBase64IsRejected()
    {
        Assert.Throws<PeelFailedException>(() => ReadTags(ConformantTags(), "not base64!"));
    }

    [Fact]
    public void EmptyContentIsRejected()
    {
        Assert.Throws<PeelFailedException>(() => ReadTags(ConformantTags(), ""));
    }

    // -- Parsing a whole event --

    [Fact]
    public void AConformantEventParses()
    {
        var (envelope, publicKeyHex, idHex) = SignedEnvelope();

        var publication = KeyPackageEvent.Parse(envelope);

        Assert.Equal(SlotId, publication.SlotId);
        Assert.Equal(KeyPackageBytes, publication.KeyPackageBytes);
        Assert.Equal(publicKeyHex, publication.AuthorPublicKeyHex);
        Assert.Equal(idHex, publication.EventIdHex);
        Assert.Equal(1700000000, publication.CreatedAt);
    }

    [Fact]
    public void TheEventIdIsTheOneWeComputedNotTheOneClaimed()
    {
        // Bound to the verified hash. A kind-444 Welcome names the consumed
        // KeyPackage by this id, and the private material behind it may be used
        // exactly once — so an attacker-chosen id would let a Welcome be
        // matched against the wrong package.
        var (envelope, _, idHex) = SignedEnvelope(overrideId: new string('f', 64));

        var ex = Assert.Throws<PeelFailedException>(() => KeyPackageEvent.Parse(envelope));
        Assert.Contains("does not match", ex.Message);
        Assert.NotEqual(new string('f', 64), idHex);
    }

    [Fact]
    public void AnEventWithAnInvalidSignatureIsRejectedBeforeAnyFieldIsRead()
    {
        var (envelope, _, _) = SignedEnvelope(overrideSig: new string('0', 128));

        Assert.Throws<PeelFailedException>(() => KeyPackageEvent.Parse(envelope));
    }

    [Fact]
    public void AnEventOfAnotherKindIsRejected()
    {
        var (envelope, _, _) = SignedEnvelope(kind: 445);

        var ex = Assert.Throws<PeelFailedException>(() => KeyPackageEvent.Parse(envelope));
        Assert.Contains("kind", ex.Message);
    }

    [Fact]
    public void MalformedJsonFailsAsAPeelFailureRatherThanAJsonException()
    {
        // The engine branches on PeelFailedException.Retryable. Any other
        // exception type escapes that classification entirely, so it cannot
        // tell "defer" from "drop".
        Assert.Throws<PeelFailedException>(() => KeyPackageEvent.Parse("{ not json"));
    }

    // -- Candidate ranking --

    [Fact]
    public void TheFresherPublicationRanksFirst()
    {
        var older = Publication(createdAt: 100);
        var newer = Publication(createdAt: 200);

        Assert.True(KeyPackageEvent.CompareCandidates(newer, older) < 0);
        Assert.True(KeyPackageEvent.CompareCandidates(older, newer) > 0);
    }

    [Fact]
    public void WithinOneSlotEqualTimestampsBreakOnTheLowerEventId()
    {
        var low = Publication(eventIdHex: new string('1', 64));
        var high = Publication(eventIdHex: new string('2', 64));

        Assert.True(KeyPackageEvent.CompareCandidates(low, high) < 0);
    }

    [Fact]
    public void AcrossSlotsEqualTimestampsBreakOnTheLowerKeyPackageRef()
    {
        var low = Publication(slotId: new string('a', 64), keyPackageRefHex: new string('1', 64));
        var high = Publication(slotId: new string('b', 64), keyPackageRefHex: new string('2', 64));

        Assert.True(KeyPackageEvent.CompareCandidates(low, high) < 0);
    }

    [Fact]
    public void KeyPackageRefsOfDifferentLengthsCompareAsBytesNotAsText()
    {
        // "0a…" as text sorts below "1…", but as bytes 0x0a sorts below 0x11
        // too — the case that separates the two orderings is a prefix: a
        // 32-byte ref against a 48-byte ref that starts with it.
        var shorter = Publication(slotId: new string('a', 64), keyPackageRefHex: new string('a', 64));
        var longer = Publication(slotId: new string('b', 64), keyPackageRefHex: new string('a', 96));

        Assert.True(KeyPackageEvent.CompareCandidates(shorter, longer) < 0);
    }

    private static KeyPackagePublication Publication(
        long createdAt = 100,
        string? slotId = null,
        string? eventIdHex = null,
        string? keyPackageRefHex = null) =>
        new(
            slotId ?? SlotId,
            keyPackageRefHex ?? KeyPackageRefHex,
            CipherSuites,
            MlsExtensions,
            MlsProposals,
            AppComponents,
            KeyPackageBytes,
            "aa".PadRight(64, 'b'),
            eventIdHex ?? "cc".PadRight(64, 'd'),
            createdAt);

    /// <summary>
    /// Builds a properly signed kind-30443 envelope.
    /// </summary>
    /// <remarks>
    /// Signed, because <see cref="KeyPackageEvent.Parse"/> verifies before
    /// reading anything. An unsigned fixture could only ever reach the
    /// rejection path, which is how an earlier round of these tests came to
    /// assert a defect was the behaviour.
    /// </remarks>
    private static (string Envelope, string PublicKeyHex, string IdHex) SignedEnvelope(
        int kind = 30443, string? overrideId = null, string? overrideSig = null)
    {
        var (secret, publicKey) = Bip340.GenerateKeyPair();
        string publicKeyHex = Convert.ToHexString(publicKey).ToLowerInvariant();
        var tags = ConformantTags();

        var template = new NostrEventTemplate(publicKeyHex, 1700000000, kind, tags, Content);
        byte[] id = template.ComputeId();
        string idHex = Convert.ToHexString(id).ToLowerInvariant();
        string sig = overrideSig ?? Convert.ToHexString(Bip340.Sign(secret, id)).ToLowerInvariant();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", overrideId ?? idHex);
            writer.WriteString("pubkey", publicKeyHex);
            writer.WriteNumber("created_at", template.CreatedAt);
            writer.WriteNumber("kind", kind);
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
            writer.WriteString("content", Content);
            writer.WriteString("sig", sig);
            writer.WriteEndObject();
        }

        return (System.Text.Encoding.UTF8.GetString(stream.ToArray()), publicKeyHex, idHex);
    }
}
