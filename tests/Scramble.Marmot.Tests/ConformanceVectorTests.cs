using System.Text.Json;
using Scramble.Marmot.AppComponents;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The app-component codecs against upstream's own byte fixtures.
/// </summary>
/// <remarks>
/// <para>
/// Every other test in this project was written by whoever wrote the code it
/// checks, so it can only ever confirm that the implementation does what its
/// author expected. These fixtures come from
/// <c>mdk@wn-agent-v0.9.10</c>'s <c>cgka-conformance-simulator</c>, copied
/// verbatim — they are the cheapest defence there is against agreeing with
/// ourselves and diverging from everyone else.
/// </para>
/// <para>
/// Deliberately the <b>byte</b> fixtures only. Upstream also ships scenario
/// vectors (<c>invite-member</c>, <c>convergence-*</c>, …) which drive a whole
/// engine through a step list; those become runnable at P6 and are not
/// approximated here. A test that pretended to run them would be worse than
/// their absence.
/// </para>
/// <para>
/// To refresh: re-copy from
/// <c>crates/cgka-conformance-simulator/vectors/byte-fixtures/</c> at the
/// pinned tag and re-run. A fixture that starts failing after a pin bump is the
/// signal it exists to give — do not edit the fixture to make it pass.
/// </para>
/// </remarks>
[Trait("Category", "ConformanceVector")]
public class ConformanceVectorTests
{
    private static JsonElement Load(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "vectors", "marmot", name);
        Assert.True(File.Exists(path), $"Missing conformance fixture: {path}");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value);

    // -- The fixture set itself --

    [Fact]
    public void EveryVendoredFixtureIsPinnedToOurReferenceTag()
    {
        // A fixture from a different tag than the one the code was written
        // against proves nothing about either.
        var profile = Load("current-profile-required-set.v1.json");

        Assert.Equal("0.9.10", profile.GetProperty("conformance_version").GetString());
    }

    [Theory]
    [InlineData("nostr-routing-v1-valid-state.v1.json")]
    [InlineData("nostr-routing-v1-valid-update.v1.json")]
    [InlineData("nostr-routing-v1-invalid-duplicate-relay.v1.json")]
    public void EveryByteFixtureDeclaresTheComponentWeThinkItDoes(string name)
    {
        var fixture = Load(name);

        Assert.Equal("1", fixture.GetProperty("fixture_version").GetString());
        Assert.Equal("0x8004", fixture.GetProperty("component").GetProperty("id").GetString());
        Assert.Equal(
            AppComponent.NostrRoutingSchema,
            fixture.GetProperty("component").GetProperty("name").GetString());
    }

    // -- Routing component state --

    [Theory]
    [InlineData("nostr-routing-v1-valid-state.v1.json")]
    [InlineData("nostr-routing-v1-valid-update.v1.json")]
    public void AValidRoutingFixtureDecodesToItsDeclaredFields(string name)
    {
        var fixture = Load(name);
        var expected = fixture.GetProperty("expected");
        Assert.True(expected.GetProperty("valid").GetBoolean());

        var routing = NostrRouting.Decode(Hex(fixture.GetProperty("bytes").GetProperty("hex").GetString()!));

        var fields = expected.GetProperty("fields");
        Assert.Equal(
            fields.GetProperty("nostr_group_id_hex").GetString(),
            Convert.ToHexString(routing.TransportGroupId).ToLowerInvariant());
        Assert.Equal(
            fields.GetProperty("relays").EnumerateArray().Select(r => r.GetString()).ToArray(),
            routing.Relays);
    }

    [Theory]
    [InlineData("nostr-routing-v1-valid-state.v1.json")]
    [InlineData("nostr-routing-v1-valid-update.v1.json")]
    public void OurEncoderReproducesTheFixtureBytesExactly(string name)
    {
        // The direction that catches divergence a decoder cannot: being
        // liberal enough to read upstream's bytes says nothing about emitting
        // bytes upstream will read.
        var fixture = Load(name);
        string hex = fixture.GetProperty("bytes").GetProperty("hex").GetString()!;

        var routing = NostrRouting.Decode(Hex(hex));

        Assert.Equal(hex, Convert.ToHexString(routing.Encode()).ToLowerInvariant());
    }

    [Fact]
    public void ARoutingStateBuiltFromTheFixtureFieldsProducesTheFixtureBytes()
    {
        // Round-tripping decoded bytes could pass while Create() canonicalised
        // differently from upstream. This starts from the semantic fields.
        var fixture = Load("nostr-routing-v1-valid-state.v1.json");
        var fields = fixture.GetProperty("expected").GetProperty("fields");

        var routing = NostrRouting.Create(
            Hex(fields.GetProperty("nostr_group_id_hex").GetString()!),
            fields.GetProperty("relays").EnumerateArray().Select(r => r.GetString()!));

        Assert.Equal(
            fixture.GetProperty("bytes").GetProperty("hex").GetString(),
            Convert.ToHexString(routing.Encode()).ToLowerInvariant());
    }

    [Fact]
    public void TheDuplicateRelayFixtureIsRejected()
    {
        var fixture = Load("nostr-routing-v1-invalid-duplicate-relay.v1.json");
        Assert.False(fixture.GetProperty("expected").GetProperty("valid").GetBoolean());
        Assert.Contains(
            "duplicate_relay",
            fixture.GetProperty("expected").GetProperty("errors")
                .EnumerateArray().Select(e => e.GetString()));

        var ex = Assert.Throws<AppComponentException>(
            () => NostrRouting.Decode(Hex(fixture.GetProperty("bytes").GetProperty("hex").GetString()!)));

        Assert.Contains("more than once", ex.Message);
    }

    // -- The dictionary entry encoding --

    [Fact]
    public void OurDictionaryEntryMatchesUpstreamsComponentDataBytes()
    {
        // The fixture's component_data_hex is an OpenMLS ComponentData entry:
        // the uint16 id, then the state as a variable-length byte vector. That
        // makes it an independent check on the dictionary's entry framing —
        // the one piece of this subsystem with no spec prose of its own, since
        // the Marmot documents defer it to the MLS extensions draft.
        var fixture = Load("nostr-routing-v1-valid-state.v1.json");
        string stateHex = fixture.GetProperty("bytes").GetProperty("hex").GetString()!;
        string entryHex = fixture.GetProperty("bytes").GetProperty("component_data_hex").GetString()!;

        var dictionary = new AppDataDictionary();
        dictionary.Set(AppComponent.NostrRouting, Hex(stateHex));

        // Encode() frames the whole dictionary, so strip its outer vector
        // length to compare the single entry upstream published.
        byte[] encoded = dictionary.Encode();
        var (declared, prefixLength) = ComponentCodec.ReadVarint(encoded);

        Assert.Equal((ulong)(encoded.Length - prefixLength), declared);
        Assert.Equal(entryHex, Convert.ToHexString(encoded[prefixLength..]).ToLowerInvariant());
    }

    [Fact]
    public void TheFixtureEntryLengthPrefixUsesTheTwoByteVarintForm()
    {
        // 77 payload bytes sits just past the one-byte varint ceiling of 63,
        // so upstream's own bytes pin the width boundary our encoder has to
        // agree on. `404d` is 0x4000 | 77.
        var fixture = Load("nostr-routing-v1-valid-state.v1.json");
        string entryHex = fixture.GetProperty("bytes").GetProperty("component_data_hex").GetString()!;

        Assert.StartsWith("8004404d", entryHex);

        var output = new List<byte>();
        ComponentCodec.WriteVarint(77, output);
        Assert.Equal([0x40, 0x4d], output);
    }

    // -- The Current-profile constants --

    [Fact]
    public void OurCurrentProfileConstantsMatchUpstreamsDeclaredContract()
    {
        // Upstream publishes these as an implementation-neutral hex contract
        // precisely so a second implementation can cross-check them without
        // running the scenario. Hardcoding them independently and never
        // comparing is how two implementations drift while both look correct.
        var profile = Load("current-profile-required-set.v1.json")
            .GetProperty("application_profile");

        Assert.Equal("current", profile.GetProperty("name").GetString());

        AssertIdSet(profile, "required_group_context_extensions", CurrentProfile.RequiredExtensionTypes);
        AssertIdSet(profile, "required_proposals", CurrentProfile.RequiredProposalTypes);
        AssertIdSet(profile, "required_app_components", CurrentProfile.RequiredComponents);
        AssertIdSet(
            profile,
            "required_group_context_state_components",
            CurrentProfile.RequiredGroupStateComponents);
        AssertIdSet(profile, "leaf_only_app_components", CurrentProfile.LeafOnlyComponents);
    }

    private static void AssertIdSet(JsonElement profile, string field, IReadOnlySet<ushort> ours)
    {
        var theirs = profile.GetProperty(field)
            .EnumerateArray()
            .Select(e => Convert.ToUInt16(e.GetString()![2..], 16))
            .ToHashSet();

        Assert.Equal(theirs.OrderBy(id => id), ours.OrderBy(id => id));
    }

    [Fact]
    public void OurIdFormattingMatchesTheSpelledFormUpstreamPublishes()
    {
        // Id-list values are compared as exact strings on the wire, so the
        // spelling is part of the contract, not presentation.
        var profile = Load("current-profile-required-set.v1.json")
            .GetProperty("application_profile");

        foreach (var entry in profile.GetProperty("required_app_components").EnumerateArray())
        {
            string spelled = entry.GetString()!;
            ushort id = Convert.ToUInt16(spelled[2..], 16);

            Assert.Equal(spelled, KeyPackageEventFormatting(id));
        }
    }

    private static string KeyPackageEventFormatting(ushort id) =>
        Scramble.Marmot.Wire.Nostr.KeyPackageEvent.FormatId(id);
}
