using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The kind-444 Welcome rumor, and the peeler's Welcome path.
/// </summary>
[Trait("Category", "MarmotEngine")]
public class WelcomeEventTests
{
    private static readonly byte[] KeyPackageEventId =
        Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();

    private static string KeyPackageEventIdHex =>
        Convert.ToHexString(KeyPackageEventId).ToLowerInvariant();

    private static readonly byte[] WelcomeBytes = "an MLS welcome"u8.ToArray();

    private static Rumor MakeRumor(
        IReadOnlyList<IReadOnlyList<string>>? tags = null,
        string? content = null,
        int kind = 444) =>
        new(
            "aa".PadRight(64, 'b'),
            1700000000,
            kind,
            tags ?? new IReadOnlyList<string>[]
            {
                new[] { "e", KeyPackageEventIdHex },
                new[] { "relays", "wss://relay.example" },
            },
            content ?? Convert.ToBase64String(WelcomeBytes));

    // -- Happy path --

    [Fact]
    public void AConformantRumorYieldsTheWelcome()
    {
        var welcome = WelcomeEvent.Read(MakeRumor());

        Assert.Equal(KeyPackageEventId, welcome.KeyPackageEventId);
        Assert.Equal(new[] { "wss://relay.example" }, welcome.Relays);
        Assert.Equal(WelcomeBytes, welcome.WelcomeBytes);
    }

    [Fact]
    public void MultipleRelayValuesOnTheOneTagAreKept()
    {
        // The tag appears once; its values stay multiple.
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex },
            new[] { "relays", "wss://one.example", "wss://two.example" },
        });

        Assert.Equal(2, WelcomeEvent.Read(rumor).Relays.Count);
    }

    [Fact]
    public void TheInviterComesFromTheRumorAuthor()
    {
        Assert.Equal(MakeRumor().PublicKeyHex, WelcomeEvent.Read(MakeRumor()).SenderPublicKeyHex);
    }

    // -- Tag cardinality --

    [Fact]
    public void ADuplicateKeyPackageTagIsRejected()
    {
        // Rejected rather than resolved by taking the first: "take the first"
        // lets an attacker prepend a tag and steer the join.
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex },
            new[] { "e", KeyPackageEventIdHex },
            new[] { "relays", "wss://relay.example" },
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    [Fact]
    public void ADuplicateRelaysTagIsRejected()
    {
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex },
            new[] { "relays", "wss://one.example" },
            new[] { "relays", "wss://two.example" },
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    [Fact]
    public void AMissingKeyPackageTagIsRejected()
    {
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "relays", "wss://relay.example" },
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    [Fact]
    public void AMissingRelaysTagIsRejected()
    {
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex },
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    // -- Field validation --

    [Theory]
    [InlineData("")]
    [InlineData("abcd")]
    [InlineData("zz112233445566778899aabbccddeeff00112233445566778899aabbccddeeff")]
    public void AMalformedKeyPackageEventIdIsRejected(string value)
    {
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", value },
            new[] { "relays", "wss://relay.example" },
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    [Fact]
    public void AnEmptyRelayListIsRejected()
    {
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex },
            new[] { "relays" },
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    [Fact]
    public void TooManyRelaysAreRejected()
    {
        var relays = new List<string> { "relays" };
        relays.AddRange(Enumerable.Range(0, 17).Select(i => $"wss://relay{i}.example"));

        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex },
            relays,
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    [Fact]
    public void AnOverlongRelayUrlIsRejected()
    {
        string huge = "wss://" + new string('a', 600);
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex },
            new[] { "relays", huge },
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    [Theory]
    [InlineData("https://relay.example")]
    [InlineData("not a url")]
    [InlineData("wss://")]
    public void ANonWebsocketRelayIsRejected(string relay)
    {
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex },
            new[] { "relays", relay },
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    [Fact]
    public void NonBase64ContentIsRejected()
    {
        Assert.Throws<PeelFailedException>(
            () => WelcomeEvent.Read(MakeRumor(content: "not base64!!")));
    }

    [Fact]
    public void EmptyWelcomeBytesAreRejected()
    {
        Assert.Throws<PeelFailedException>(
            () => WelcomeEvent.Read(MakeRumor(content: Convert.ToBase64String(Array.Empty<byte>()))));
    }

    [Fact]
    public void AWrongRumorKindIsRejected()
    {
        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(MakeRumor(kind: 1)));
    }

    // -- Building --

    [Fact]
    public void BuiltTagsRoundTripThroughRead()
    {
        var tags = WelcomeEvent.BuildTags(KeyPackageEventId, new[] { "wss://relay.example" });

        var welcome = WelcomeEvent.Read(MakeRumor(tags));

        Assert.Equal(KeyPackageEventId, welcome.KeyPackageEventId);
        Assert.Equal(new[] { "wss://relay.example" }, welcome.Relays);
    }

    [Fact]
    public void BuildingRejectsAWrongLengthKeyPackageEventId()
    {
        Assert.Throws<ArgumentException>(
            () => WelcomeEvent.BuildTags(new byte[31], new[] { "wss://relay.example" }));
    }

    [Theory]
    [InlineData("wss://user:pass@relay.example")]
    [InlineData("wss://user@relay.example")]
    public void ARelayCarryingCredentialsIsRejected(string relay)
    {
        // The list comes from whoever invited us, before any trust decision;
        // embedded credentials would be handed to whatever connects.
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex },
            new[] { "relays", relay },
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    [Fact]
    public void ARelayCarryingAFragmentIsRejected()
    {
        // Compared as an exact string downstream, so a fragment makes a second
        // distinct entry for the same relay.
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex },
            new[] { "relays", "wss://relay.example/#frag" },
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    [Theory]
    [InlineData(512, false)]
    [InlineData(513, true)]
    public void TheRelayUrlLengthLimitSitsAtFiveHundredAndTwelveBytes(int length, bool rejected)
    {
        // Both sides of the boundary, so an off-by-one is caught. The bound is
        // on encoded bytes and is shared with the routing component: the
        // Welcome tag and the group's own signed relay list describe the same
        // relays, so a URL one accepted and the other refused would make the
        // group reachable through its invite but not through its state.
        // Padded in the path rather than the host: a 500-character hostname
        // trips .NET's own host-length limit first, which would make this
        // assert something other than the byte bound it is about. A path is
        // within the profile — only userinfo and fragments are forbidden.
        const string prefix = "wss://relay.example/";
        string url = prefix + new string('a', length - prefix.Length);
        Assert.Equal(length, System.Text.Encoding.UTF8.GetByteCount(url));

        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex },
            new[] { "relays", url },
        });

        if (rejected)
            Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
        else
            Assert.Single(WelcomeEvent.Read(rumor).Relays);
    }

    [Fact]
    public void ADuplicateRelayValueIsRejected()
    {
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex },
            new[] { "relays", "wss://a.example", "wss://a.example" },
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    [Fact]
    public void ExtraValuesOnTheKeyPackageTagAreRejected()
    {
        // Ignoring them would let an attacker append a value that another
        // implementation reads instead of the first.
        var rumor = MakeRumor(new IReadOnlyList<string>[]
        {
            new[] { "e", KeyPackageEventIdHex, "wss://hint.example" },
            new[] { "relays", "wss://relay.example" },
        });

        Assert.Throws<PeelFailedException>(() => WelcomeEvent.Read(rumor));
    }

    [Fact]
    public void BuildingRejectsAnInvalidRelay()
    {
        Assert.Throws<PeelFailedException>(
            () => WelcomeEvent.BuildTags(KeyPackageEventId, new[] { "https://nope.example" }));
    }
}
