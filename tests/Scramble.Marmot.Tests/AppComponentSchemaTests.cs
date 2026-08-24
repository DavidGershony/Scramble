using Scramble.Marmot.AppComponents;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The four v1 component schemas.
/// </summary>
/// <remarks>
/// Each schema is tested for the same three things: the exact bytes it
/// produces, that it round-trips, and that a decoder <b>rejects</b> a
/// non-canonical encoding rather than normalising it. The third is the one that
/// matters — this is signed group state, so a member that quietly repairs what
/// it was given has forked its view of the group from everyone else's.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class AppComponentSchemaTests
{
    private static byte[] Key(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static readonly byte[] RoutingId = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    // -- Nostr routing (0x8004) --

    [Fact]
    public void RoutingEncodesTheIdThenALengthPrefixedRelayVector()
    {
        var routing = NostrRouting.Create(RoutingId, ["wss://a.example"]);

        byte[] encoded = routing.Encode();

        Assert.Equal(RoutingId, encoded[..32]);
        Assert.Equal(16, encoded[32]);          // relay vector byte length
        Assert.Equal(15, encoded[33]);          // first relay URL length
        Assert.Equal("wss://a.example"u8.ToArray(), encoded[34..]);
    }

    [Fact]
    public void RoutingRoundTrips()
    {
        var routing = NostrRouting.Create(RoutingId, ["wss://a.example", "wss://b.example"]);

        var decoded = NostrRouting.Decode(routing.Encode());

        Assert.Equal(RoutingId, decoded.TransportGroupId);
        Assert.Equal(["wss://a.example", "wss://b.example"], decoded.Relays);
    }

    [Fact]
    public void CreatingARoutingStateSortsAndDeduplicatesTheRelays()
    {
        // Safe here and only here: the producer has not committed to bytes yet.
        var routing = NostrRouting.Create(
            RoutingId, ["wss://c.example", "wss://a.example", "wss://c.example"]);

        Assert.Equal(["wss://a.example", "wss://c.example"], routing.Relays);
    }

    [Fact]
    public void ADecodedRelayListThatIsNotSortedIsRejectedRatherThanSorted()
    {
        // The heart of it. Sorting on the way in would leave this member
        // holding a different canonical list from every other member.
        byte[] encoded = Encoded(RoutingId, ["wss://c.example", "wss://a.example"]);

        var ex = Assert.Throws<AppComponentException>(() => NostrRouting.Decode(encoded));
        Assert.Contains("sorted", ex.Message);
    }

    [Fact]
    public void ADecodedRelayListWithDuplicatesIsRejected()
    {
        byte[] encoded = Encoded(RoutingId, ["wss://a.example", "wss://a.example"]);

        Assert.Throws<AppComponentException>(() => NostrRouting.Decode(encoded));
    }

    [Fact]
    public void ARoutingStateWithNoRelaysIsRejected()
    {
        Assert.Throws<AppComponentException>(() => NostrRouting.Decode(Encoded(RoutingId, [])));
        Assert.Throws<AppComponentException>(() => NostrRouting.Create(RoutingId, []));
    }

    [Fact]
    public void MoreThanSixteenRelaysAreRejected()
    {
        string[] relays = Enumerable.Range(0, 17).Select(i => $"wss://r{i:00}.example").ToArray();

        Assert.Throws<AppComponentException>(() => NostrRouting.Create(RoutingId, relays));
        Assert.Throws<AppComponentException>(() => NostrRouting.Decode(Encoded(RoutingId, relays)));
    }

    [Fact]
    public void SeventeenRelaysThatDeduplicateToSixteenAreAccepted()
    {
        // The count is re-checked after canonicalisation, not before: a list
        // over the limit only because of a repeat is within it once collapsed.
        var relays = Enumerable.Range(0, 16).Select(i => $"wss://r{i:00}.example").ToList();
        relays.Add(relays[0]);

        Assert.Equal(16, NostrRouting.Create(RoutingId, relays).Relays.Count);
    }

    [Fact]
    public void ARoutingIdThatIsNotThirtyTwoBytesIsRejected()
    {
        Assert.Throws<AppComponentException>(
            () => NostrRouting.Create(new byte[31], ["wss://a.example"]));
        Assert.Throws<AppComponentException>(() => NostrRouting.Decode(new byte[20]));
    }

    [Theory]
    [InlineData("http://a.example")]
    [InlineData("wss://user:pw@a.example")]
    [InlineData("wss://a.example#frag")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void ARelayUrlOutsideTheProfileIsRejected(string relay)
    {
        Assert.Throws<AppComponentException>(() => NostrRouting.Create(RoutingId, [relay]));
    }

    [Fact]
    public void TrailingBytesAfterTheRelayVectorAreRejected()
    {
        byte[] encoded = [.. Encoded(RoutingId, ["wss://a.example"]), 0xff];

        Assert.Throws<AppComponentException>(() => NostrRouting.Decode(encoded));
    }

    [Fact]
    public void ARelayUrlThatIsNotUtf8IsRejectedRatherThanSubstituted()
    {
        // A replacement character would re-encode to different bytes from the
        // ones the group signed.
        var relayEntries = new List<byte>();
        ComponentCodec.WriteVarBytes([0x77, 0x73, 0x73, 0xff, 0xfe], relayEntries);

        var output = new List<byte>(RoutingId);
        ComponentCodec.WriteVarint((ulong)relayEntries.Count, output);
        output.AddRange(relayEntries);

        Assert.Throws<AppComponentException>(() => NostrRouting.Decode(output.ToArray()));
    }

    /// <summary>Encodes a routing state without canonicalising, for rejection cases.</summary>
    private static byte[] Encoded(byte[] routingId, IReadOnlyList<string> relays)
    {
        var relayEntries = new List<byte>();
        foreach (string relay in relays)
            ComponentCodec.WriteVarBytes(System.Text.Encoding.UTF8.GetBytes(relay), relayEntries);

        var output = new List<byte>(routingId);
        ComponentCodec.WriteVarint((ulong)relayEntries.Count, output);
        output.AddRange(relayEntries);
        return output.ToArray();
    }

    // -- Admin policy (0x8003) --

    /// <summary>
    /// Builds an admin payload verbatim, for rejection cases.
    /// </summary>
    /// <remarks>
    /// The length prefix goes through <see cref="ComponentCodec.WriteVarint"/>
    /// rather than being written as a literal byte. Two 32-byte keys are 64
    /// bytes, and 64 is the first value that needs a two-byte varint — a
    /// literal <c>64</c> decodes as the one-byte value 0, so these fixtures
    /// would be rejected for having an empty list rather than for the flaw each
    /// one is actually about.
    /// </remarks>
    private static byte[] AdminPayload(params byte[][] keys)
    {
        var keyBytes = new List<byte>();
        foreach (byte[] key in keys)
            keyBytes.AddRange(key);

        var output = new List<byte>();
        ComponentCodec.WriteVarint((ulong)keyBytes.Count, output);
        output.AddRange(keyBytes);
        return output.ToArray();
    }

    [Fact]
    public void AnAdminPolicyEncodesAsAByteLengthThenKeys()
    {
        byte[] encoded = AdminPolicy.Create([Key(0x01), Key(0x02)]).Encode();

        // 64 payload bytes, which is exactly where the varint widens to two.
        Assert.Equal([0x40, 0x40], encoded[..2]);
        Assert.Equal(66, encoded.Length);
        Assert.Equal(Key(0x01), encoded[2..34]);
        Assert.Equal(Key(0x02), encoded[34..]);
    }

    [Fact]
    public void ASingleAdminStillUsesAOneByteLengthPrefix()
    {
        byte[] encoded = AdminPolicy.Create([Key(0x01)]).Encode();

        Assert.Equal(32, encoded[0]);
        Assert.Equal(33, encoded.Length);
    }

    [Fact]
    public void AnAdminPolicyRoundTrips()
    {
        var policy = AdminPolicy.Create([Key(0x03), Key(0x01)]);

        var decoded = AdminPolicy.Decode(policy.Encode());

        Assert.Equal(2, decoded.Admins.Count);
        Assert.Equal(Key(0x01), decoded.Admins[0]);
        Assert.Equal(Key(0x03), decoded.Admins[1]);
    }

    [Fact]
    public void ADecodedAdminListThatIsNotSortedIsRejected()
    {
        // The worst place to normalise: two members would disagree about who
        // governs the group while both believed their state was valid.
        var ex = Assert.Throws<AppComponentException>(
            () => AdminPolicy.Decode(AdminPayload(Key(0x02), Key(0x01))));
        Assert.Contains("sorted", ex.Message);
    }

    [Fact]
    public void ADecodedAdminListWithDuplicatesIsRejected()
    {
        var ex = Assert.Throws<AppComponentException>(
            () => AdminPolicy.Decode(AdminPayload(Key(0x01), Key(0x01))));
        Assert.Contains("unique", ex.Message);
    }

    [Fact]
    public void AnEmptyAdminListIsRejected()
    {
        Assert.Throws<AppComponentException>(() => AdminPolicy.Decode([0x00]));
        Assert.Throws<AppComponentException>(() => AdminPolicy.Create([]));
    }

    [Fact]
    public void AnAdminListWhoseLengthIsNotAMultipleOfThirtyTwoIsRejected()
    {
        var output = new List<byte>();
        ComponentCodec.WriteVarint(33, output);
        output.AddRange(Key(0x01));
        output.Add(0x01);

        Assert.Throws<AppComponentException>(() => AdminPolicy.Decode(output.ToArray()));
    }

    [Fact]
    public void AnAdminKeyThatIsNotThirtyTwoBytesIsRejected()
    {
        Assert.Throws<AppComponentException>(() => AdminPolicy.Create([new byte[31]]));
    }

    [Fact]
    public void AnAdminPolicyWithTrailingBytesIsRejected()
    {
        byte[] output = [.. AdminPayload(Key(0x01)), 0xff];

        Assert.Throws<AppComponentException>(() => AdminPolicy.Decode(output));
    }

    // -- Admin authority --

    [Fact]
    public void BeingListedIsNotEnoughToBeAnActiveAdmin()
    {
        // The distinction the whole authorization model turns on: an admin
        // whose last leaf is gone has no authority, however the list reads.
        var policy = AdminPolicy.Create([Key(0x01), Key(0x02)]);

        Assert.True(policy.IsListed(Key(0x02)));
        Assert.False(policy.IsActiveAdmin(Key(0x02), [Key(0x01)]));
        Assert.True(policy.IsActiveAdmin(Key(0x02), [Key(0x01), Key(0x02)]));
    }

    [Fact]
    public void AnUnlistedAccountIsNeverAnActiveAdminHoweverManyLeavesItHas()
    {
        var policy = AdminPolicy.Create([Key(0x01)]);

        Assert.False(policy.IsActiveAdmin(Key(0x09), [Key(0x01), Key(0x09)]));
    }

    [Fact]
    public void OneAdminEntryCoversEveryLeafOfAMultiDeviceAccount()
    {
        // An admin key is an account identity, not a leaf key, so a second
        // device does not need a second entry.
        var policy = AdminPolicy.Create([Key(0x01)]);

        Assert.True(policy.IsActiveAdmin(Key(0x01), [Key(0x01), Key(0x01)]));
    }

    [Fact]
    public void AnAdminWithNoMemberLeafMakesTheResultingEpochInvalid()
    {
        // The coupling rule: a commit removing an account's last leaf must
        // remove its admin key in the same commit.
        var policy = AdminPolicy.Create([Key(0x01), Key(0x02)]);

        Assert.True(policy.EveryAdminHasAMemberLeaf([Key(0x01), Key(0x02), Key(0x07)]));
        Assert.False(policy.EveryAdminHasAMemberLeaf([Key(0x01)]));
    }

    // -- Group profile (0x8001) --

    [Fact]
    public void AProfileEncodesAsTwoLengthPrefixedStrings()
    {
        byte[] encoded = new GroupProfile("hi", "there").Encode();

        Assert.Equal([0x02, (byte)'h', (byte)'i', 0x05, (byte)'t', (byte)'h', (byte)'e', (byte)'r', (byte)'e'], encoded);
    }

    [Fact]
    public void AProfileRoundTrips()
    {
        var profile = new GroupProfile("Team", "A group for the team");

        Assert.Equal(profile, GroupProfile.Decode(profile.Encode()));
    }

    [Fact]
    public void AnEmptyDescriptionIsAValueNotAnAbsence()
    {
        var profile = new GroupProfile("Team", "");

        Assert.Equal("", GroupProfile.Decode(profile.Encode()).Description);
    }

    [Fact]
    public void ProfileTextSurvivesBeyondTheBasicMultilingualPlane()
    {
        var profile = new GroupProfile("🎉 party", "emoji ☕ and 中文");

        Assert.Equal(profile, GroupProfile.Decode(profile.Encode()));
    }

    [Fact]
    public void ProfileLengthLimitsCountBytesNotCharacters()
    {
        // One emoji is four bytes. A character-based limit would let an
        // over-long profile onto the wire to be rejected by every peer.
        var justUnder = new GroupProfile(new string('a', 256), "");
        var justOver = new GroupProfile(new string('a', 257), "");
        var emojiOver = new GroupProfile(string.Concat(Enumerable.Repeat("🎉", 65)), "");

        _ = justUnder.Encode();
        Assert.Throws<AppComponentException>(() => justOver.Encode());
        Assert.Throws<AppComponentException>(() => emojiOver.Encode());
    }

    [Fact]
    public void AProfileWithTrailingBytesIsRejected()
    {
        byte[] encoded = [.. new GroupProfile("a", "b").Encode(), 0xff];

        Assert.Throws<AppComponentException>(() => GroupProfile.Decode(encoded));
    }

    [Fact]
    public void ProfileTextThatIsNotUtf8IsRejectedRatherThanSubstituted()
    {
        Assert.Throws<AppComponentException>(
            () => GroupProfile.Decode([0x02, 0xff, 0xfe, 0x00]));
    }

    // -- Message retention (0x8005) --

    [Fact]
    public void RetentionIsEightBigEndianBytes()
    {
        Assert.Equal([0, 0, 0, 0, 0, 0, 0x03, 0x84], new MessageRetention(900).Encode());
    }

    [Fact]
    public void RetentionRoundTrips()
    {
        foreach (ulong seconds in new ulong[] { 0, 1, 900, 86_400, ulong.MaxValue })
            Assert.Equal(seconds, MessageRetention.Decode(new MessageRetention(seconds).Encode()).Seconds);
    }

    [Fact]
    public void ZeroSecondsMeansDisabled()
    {
        Assert.False(MessageRetention.Disabled.IsEnabled);
        Assert.True(new MessageRetention(1).IsEnabled);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(0)]
    public void ARetentionComponentOfTheWrongLengthIsRejected(int length)
    {
        Assert.Throws<AppComponentException>(() => MessageRetention.Decode(new byte[length]));
    }

    [Fact]
    public void EveryUnsignedSixtyFourBitValueIsValidRetentionState()
    {
        // v1 defines no protocol maximum. A local UI cap is not signed group
        // state and must not invalidate what the group actually agreed.
        Assert.Equal(ulong.MaxValue, MessageRetention.Decode(Enumerable.Repeat((byte)0xff, 8).ToArray()).Seconds);
    }

    // -- The expiry calculation --

    [Fact]
    public void ExpiryIsTheSenderTimestampPlusTheDuration()
    {
        Assert.Equal(1_700_000_900UL, new MessageRetention(900).ExpiryFor(1_700_000_000));
    }

    [Fact]
    public void DisabledRetentionProducesNoExpiry()
    {
        Assert.Null(MessageRetention.Disabled.ExpiryFor(1_700_000_000));
    }

    [Fact]
    public void AnOverflowingExpiryIsOmittedRatherThanWrappedOrSaturated()
    {
        // Both alternatives produce a plausible-looking wrong timestamp that
        // the sender would then attach as fact. Omitting the tag leaves the
        // component state and the message valid.
        Assert.Null(new MessageRetention(ulong.MaxValue).ExpiryFor(1));
        Assert.Null(new MessageRetention(ulong.MaxValue - 4).ExpiryFor(5));

        // long.MaxValue + 2 is NOT an overflow: it fits in a ulong with room
        // to spare, and the exact answer is the right one to return.
        Assert.Equal((ulong)long.MaxValue + 2, new MessageRetention(2).ExpiryFor(long.MaxValue));
    }

    [Fact]
    public void ANegativeSenderTimestampProducesNoExpiry()
    {
        Assert.Null(new MessageRetention(900).ExpiryFor(-1));
    }

    [Fact]
    public void AnExpiryExactlyAtTheUnsignedCeilingIsStillDefined()
    {
        Assert.Equal(ulong.MaxValue, new MessageRetention(ulong.MaxValue - 5).ExpiryFor(5));
    }
}
