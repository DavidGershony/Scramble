using Scramble.Marmot.AppComponents;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The QUIC-varint and var-bytes primitives underneath every component schema.
/// </summary>
/// <remarks>
/// These run against the boundary values RFC 9000 §16 names, because that is
/// where an off-by-one changes the encoded width and so the bytes inside signed
/// group state. Canonicality is the property under test throughout: one value,
/// one encoding, everything else rejected.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class ComponentCodecTests
{
    /// <summary>
    /// Reads one var-bytes field from a whole buffer.
    /// </summary>
    /// <remarks>
    /// A wrapper because a <c>ref</c> span local cannot be captured by the
    /// lambda <c>Assert.Throws</c> needs.
    /// </remarks>
    private static byte[] ReadVarBytes(byte[] buffer, int maxLength, string label)
    {
        ReadOnlySpan<byte> cursor = buffer;
        return ComponentCodec.ReadVarBytes(ref cursor, maxLength, label);
    }

    private static byte[] Varint(ulong value)
    {
        var output = new List<byte>();
        ComponentCodec.WriteVarint(value, output);
        return output.ToArray();
    }

    // -- Varint widths --

    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(63UL, 1)]
    [InlineData(64UL, 2)]
    [InlineData(16_383UL, 2)]
    [InlineData(16_384UL, 4)]
    [InlineData(1_073_741_823UL, 4)]
    [InlineData(1_073_741_824UL, 8)]
    public void EachWidthBoundaryEncodesToTheExpectedLength(ulong value, int expectedLength)
    {
        Assert.Equal(expectedLength, Varint(value).Length);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(63UL)]
    [InlineData(64UL)]
    [InlineData(16_383UL)]
    [InlineData(16_384UL)]
    [InlineData(1_073_741_823UL)]
    [InlineData(1_073_741_824UL)]
    [InlineData(ComponentCodec.MaxVarint)]
    public void EveryVarintRoundTrips(ulong value)
    {
        byte[] encoded = Varint(value);

        var (decoded, length) = ComponentCodec.ReadVarint(encoded);

        Assert.Equal(value, decoded);
        Assert.Equal(encoded.Length, length);
    }

    [Fact]
    public void TheFirstTwoBitsCarryTheWidth()
    {
        // The wire property everything else depends on, pinned against the
        // literal bytes rather than against our own decoder.
        Assert.Equal([0x00], Varint(0));
        Assert.Equal([0x3f], Varint(63));
        Assert.Equal([0x40, 0x40], Varint(64));
        Assert.Equal([0x7f, 0xff], Varint(16_383));
        Assert.Equal([0x80, 0x00, 0x40, 0x00], Varint(16_384));
        Assert.Equal([0xc0, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00], Varint(1_073_741_824));
    }

    [Fact]
    public void AValueBeyondSixtyTwoBitsIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ComponentCodec.WriteVarint(ComponentCodec.MaxVarint + 1, []));
    }

    // -- Varint canonicality --

    [Theory]
    [InlineData(new byte[] { 0x40, 0x00 })]                                     // 0 in two bytes
    [InlineData(new byte[] { 0x80, 0x00, 0x00, 0x3f })]                         // 63 in four
    [InlineData(new byte[] { 0xc0, 0, 0, 0, 0, 0, 0x40, 0x00 })]                // 16_384 in eight
    public void ANonMinimalVarintIsRejected(byte[] bytes)
    {
        // A padded varint is a second encoding of one value. Accepting it and
        // re-emitting minimally would mean two members hold different bytes for
        // the same group state.
        Assert.Throws<AppComponentException>(() => ComponentCodec.ReadVarint(bytes));
    }

    [Fact]
    public void AnEmptyInputIsRejected()
    {
        Assert.Throws<AppComponentException>(() => ComponentCodec.ReadVarint([]));
    }

    [Theory]
    [InlineData(new byte[] { 0x40 })]
    [InlineData(new byte[] { 0x80, 0x00 })]
    [InlineData(new byte[] { 0xc0, 0x00, 0x00 })]
    public void ATruncatedVarintIsRejected(byte[] bytes)
    {
        Assert.Throws<AppComponentException>(() => ComponentCodec.ReadVarint(bytes));
    }

    // -- Var bytes --

    [Fact]
    public void VarBytesRoundTripAndAdvanceTheCursor()
    {
        var output = new List<byte>();
        ComponentCodec.WriteVarBytes("first"u8, output);
        ComponentCodec.WriteVarBytes("second"u8, output);

        ReadOnlySpan<byte> cursor = output.ToArray();
        Assert.Equal("first"u8.ToArray(), ComponentCodec.ReadVarBytes(ref cursor, 16, "a"));
        Assert.Equal("second"u8.ToArray(), ComponentCodec.ReadVarBytes(ref cursor, 16, "b"));
        Assert.True(cursor.IsEmpty);
    }

    [Fact]
    public void AnEmptyVarBytesFieldIsAValidValue()
    {
        // Distinct from an absent field. Group profiles routinely carry an
        // empty description, and it must survive the round trip as one.
        var output = new List<byte>();
        ComponentCodec.WriteVarBytes([], output);

        ReadOnlySpan<byte> cursor = output.ToArray();
        Assert.Empty(ComponentCodec.ReadVarBytes(ref cursor, 16, "a"));
    }

    [Fact]
    public void AVarBytesFieldOverItsSchemaBoundIsRejectedBeforeAllocating()
    {
        // The length prefix is attacker-controlled, so the bound is checked
        // against the prefix rather than against what was actually delivered.
        var output = new List<byte>();
        ComponentCodec.WriteVarint(4096, output);

        var ex = Assert.Throws<AppComponentException>(
            () => ReadVarBytes(output.ToArray(), 16, "name"));
        Assert.Contains("name", ex.Message);
    }

    [Fact]
    public void ATruncatedVarBytesFieldIsRejected()
    {
        var output = new List<byte>();
        ComponentCodec.WriteVarint(8, output);
        output.AddRange("short"u8.ToArray());

        Assert.Throws<AppComponentException>(
            () => ReadVarBytes(output.ToArray(), 16, "name"));
    }

    [Fact]
    public void TrailingBytesAreRejectedRatherThanIgnored()
    {
        // Ignoring them lets one member read a field a stricter member does
        // not, so the same signed state means two things.
        Assert.Throws<AppComponentException>(
            () => ComponentCodec.RequireSpent(new byte[] { 0x00 }, "test"));

        ComponentCodec.RequireSpent([], "test");
    }

    // -- Component vectors --

    [Fact]
    public void VectorsEncodeAsConsecutiveLengthPrefixedFields()
    {
        byte[] encoded = ComponentCodec.EncodeVectors("ab"u8.ToArray(), "cde"u8.ToArray());

        Assert.Equal([0x02, (byte)'a', (byte)'b', 0x03, (byte)'c', (byte)'d', (byte)'e'], encoded);
    }

    [Fact]
    public void NoCountPrefixPrecedesTheVectors()
    {
        // The field count comes from the schema, not the wire: a decoder reads
        // what it expects and then requires the input to be spent.
        Assert.Equal([0x01, (byte)'x'], ComponentCodec.EncodeVectors("x"u8.ToArray()));
    }

    // -- Components list --

    [Fact]
    public void AComponentsListEncodesAsAByteLengthThenBigEndianIds()
    {
        byte[] encoded = ComponentCodec.EncodeComponentsList(
            new SortedSet<ushort> { 0x8001, 0x8009 });

        Assert.Equal([0x04, 0x80, 0x01, 0x80, 0x09], encoded);
    }

    [Fact]
    public void AComponentsListRoundTrips()
    {
        var ids = new SortedSet<ushort> { 0x0001, 0x8003, 0x8004, 0x8009 };

        Assert.Equal(ids, ComponentCodec.DecodeComponentsList(
            ComponentCodec.EncodeComponentsList(ids)));
    }

    [Fact]
    public void IdsAreEmittedAscendingWhateverOrderTheyArriveIn()
    {
        // The list is compared for equality across members, so one set must
        // have one encoding.
        var scrambled = new HashSet<ushort> { 0x8009, 0x0001, 0x8004 };

        Assert.Equal(
            ComponentCodec.EncodeComponentsList(new SortedSet<ushort> { 0x0001, 0x8004, 0x8009 }),
            ComponentCodec.EncodeComponentsList(scrambled));
    }

    [Fact]
    public void AnEmptyComponentsListIsValid()
    {
        Assert.Empty(ComponentCodec.DecodeComponentsList(
            ComponentCodec.EncodeComponentsList(new SortedSet<ushort>())));
    }

    [Fact]
    public void AnUnsortedComponentsListIsRejected()
    {
        Assert.Throws<AppComponentException>(
            () => ComponentCodec.DecodeComponentsList([0x04, 0x80, 0x09, 0x80, 0x01]));
    }

    [Fact]
    public void AComponentsListWithDuplicateIdsIsRejected()
    {
        Assert.Throws<AppComponentException>(
            () => ComponentCodec.DecodeComponentsList([0x04, 0x80, 0x01, 0x80, 0x01]));
    }

    [Fact]
    public void AComponentsListWithAnOddByteLengthIsRejected()
    {
        Assert.Throws<AppComponentException>(
            () => ComponentCodec.DecodeComponentsList([0x03, 0x80, 0x01, 0x80]));
    }

    [Fact]
    public void AComponentsListWithTrailingBytesIsRejected()
    {
        Assert.Throws<AppComponentException>(
            () => ComponentCodec.DecodeComponentsList([0x02, 0x80, 0x01, 0xff]));
    }

    [Fact]
    public void ATruncatedComponentsListIsRejected()
    {
        Assert.Throws<AppComponentException>(
            () => ComponentCodec.DecodeComponentsList([0x04, 0x80, 0x01]));
    }

    // -- The id registry --

    [Fact]
    public void EveryV1ComponentMapsToItsSchemaName()
    {
        Assert.Equal("marmot.group.profile.v1", AppComponent.SchemaOf(AppComponent.GroupProfile));
        Assert.Equal("marmot.group.admin-policy.v1", AppComponent.SchemaOf(AppComponent.GroupAdminPolicy));
        Assert.Equal("marmot.transport.nostr.routing.v1", AppComponent.SchemaOf(AppComponent.NostrRouting));
        Assert.Equal("marmot.group.message-retention.v1", AppComponent.SchemaOf(AppComponent.MessageRetention));
        Assert.Equal("marmot.member.account-identity-proof.v2", AppComponent.SchemaOf(AppComponent.AccountIdentityProof));
    }

    [Theory]
    [InlineData((ushort)0x8002)] // blossom image
    [InlineData((ushort)0x8006)] // agent text stream over QUIC
    [InlineData((ushort)0x8007)] // avatar url
    [InlineData((ushort)0x8008)] // encrypted media v1
    [InlineData((ushort)0x800b)] // encrypted media v2
    [InlineData((ushort)0x800c)] // lifecycle
    public void ADeferredComponentHasNoSchemaName(ushort id)
    {
        // Deliberately unknown. Naming a component we have no codec for would
        // be a step towards advertising support we do not have.
        Assert.Null(AppComponent.SchemaOf(id));
    }

    [Theory]
    [InlineData((ushort)0x8000, true)]
    [InlineData((ushort)0x8001, true)]
    [InlineData((ushort)0x7fff, false)]
    [InlineData((ushort)0x0001, false)]
    public void ThePrivateUseBoundarySitsAt0x8000(ushort id, bool expected)
    {
        Assert.Equal(expected, AppComponent.IsPrivateUse(id));
    }
}
