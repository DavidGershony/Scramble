using Scramble.Marmot.AppComponents;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The two components that turned out to be required for being invited.
/// </summary>
/// <remarks>
/// Both were deferred on the assumption they were optional. They are optional
/// for <i>creating</i> a group and mandatory for <i>being invited to one</i> by
/// the reference client, which is a different statement — so supporting them is
/// not a feature choice, it is the price of being a full participant.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class MediaAndStreamPolicyTests
{
    // ---- Agent text stream, 0x8006 ----

    [Fact]
    public void TheStreamPolicyIsExactlyTwelveBytesBigEndian()
    {
        var policy = new AgentTextStreamPolicy(
            AgentTextStreamRoles.Receive,
            AgentTextStreamRoles.Receive | AgentTextStreamRoles.Send,
            MaxPlaintextFrameLength: 4096,
            ReplayTtlSeconds: 60,
            PaddingBucketBytes: 256);

        byte[] encoded = policy.Encode();

        Assert.Equal(AgentTextStreamPolicy.EncodedLength, encoded.Length);
        Assert.Equal(
            new byte[] { 0x01, 0x03, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x3C, 0x01, 0x00 },
            encoded);

        Assert.Equal(policy, AgentTextStreamPolicy.Decode(encoded));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    public void AStreamPolicyOfTheWrongLengthIsRefused(int length)
    {
        var ex = Assert.Throws<AppComponentException>(
            () => AgentTextStreamPolicy.Decode(new byte[length]));

        Assert.Contains("12 bytes", ex.Message);
    }

    [Fact]
    public void AnUnknownRoleBitIsRefused()
    {
        // A newer peer requiring a role we cannot even name. Treating the bit as
        // "no role" would have us believe we satisfy a requirement we do not
        // understand, which is the worst of the available answers.
        byte[] encoded = new AgentTextStreamPolicy(
            AgentTextStreamRoles.Receive, AgentTextStreamRoles.Receive, 4096, 0, 0).Encode();

        encoded[0] = 0x08;
        encoded[1] = 0x08;

        var ex = Assert.Throws<AppComponentException>(() => AgentTextStreamPolicy.Decode(encoded));
        Assert.Contains("unknown bits", ex.Message);
    }

    [Fact]
    public void RequiredRolesMustBeASubsetOfAllowed()
    {
        var policy = new AgentTextStreamPolicy(
            AgentTextStreamRoles.Send, AgentTextStreamRoles.Receive, 4096, 0, 0);

        var ex = Assert.Throws<AppComponentException>(() => policy.Encode());
        Assert.Contains("subset", ex.Message);
    }

    [Fact]
    public void EmptyRequiredRolesAreRefused()
    {
        var policy = new AgentTextStreamPolicy(
            AgentTextStreamRoles.None, AgentTextStreamRoles.Receive, 4096, 0, 0);

        Assert.Throws<AppComponentException>(() => policy.Encode());
    }

    [Theory]
    [InlineData(0u, 0u, (ushort)0)]                 // zero frame limit
    [InlineData(65520u, 0u, (ushort)0)]             // frame limit over the cap
    [InlineData(4096u, 301u, (ushort)0)]            // replay TTL over the cap
    [InlineData(4096u, 0u, (ushort)4097)]           // padding bucket over the cap
    public void StreamPolicyBoundsAreEnforced(uint frame, uint ttl, ushort padding)
    {
        var policy = new AgentTextStreamPolicy(
            AgentTextStreamRoles.Receive, AgentTextStreamRoles.Receive, frame, ttl, padding);

        Assert.Throws<AppComponentException>(() => policy.Encode());
    }

    [Fact]
    public void TheStreamComponentAndAllThreeOfItsRolesAreAdvertised()
    {
        // The component says we can read and honour the policy; the three role
        // capabilities are separate MLS extension types that say we understand
        // the roles. Upstream advertises all three from its feature registry
        // "regardless of level", and requires `receive` of every invitee, so
        // this is the shape of a client that can be invited at all.
        Assert.Contains(AppComponent.AgentTextStreamQuic, CurrentProfile.KnownGroupComponents);

        Assert.Equal(
            [(ushort)0xf2d1, (ushort)0xf2d2, (ushort)0xf2d4],
            Scramble.Marmot.Engine.KeyPackages.MarmotLeaf.AgentTextStreamRoleExtensionTypes);

        Assert.All(
            Scramble.Marmot.Engine.KeyPackages.MarmotLeaf.AgentTextStreamRoleExtensionTypes,
            role => Assert.Contains(role, Scramble.Marmot.Engine.KeyPackages.MarmotLeaf.ExtensionTypes));
    }

    [Fact]
    public void TheRoleCapabilitiesAreExtensionTypesAndNotComponents()
    {
        // The two sets are read from different places in a leaf - the extension
        // list and the component dictionary - so a role id appearing among the
        // components would be advertised where nothing looks for it, and the
        // requirement would still be unmet.
        Assert.All(
            Scramble.Marmot.Engine.KeyPackages.MarmotLeaf.AgentTextStreamRoleExtensionTypes,
            role => Assert.DoesNotContain(role, CurrentProfile.KnownGroupComponents));
    }

    // ---- Encrypted media v2, 0x800b ----

    [Fact]
    public void TheMediaPolicyRoundTrips()
    {
        var policy = EncryptedMediaPolicy.BlossomDefault(["https://blossom.example.com"]);

        EncryptedMediaPolicy decoded = EncryptedMediaPolicy.Decode(policy.Encode());

        Assert.Equal(EncryptedMediaPolicy.FormatV2, decoded.MediaFormat);
        Assert.Equal([EncryptedMediaPolicy.BlossomLocatorKind], decoded.AllowedLocatorKinds);
        Assert.Equal(
            EncryptedMediaPolicy.BlossomLocatorKind,
            Assert.Single(decoded.DefaultBlobEndpoints).LocatorKind);
    }

    [Fact]
    public void OnlyTheV2FormatIsAccepted()
    {
        var ex = Assert.Throws<AppComponentException>(() => EncryptedMediaPolicy.Create(
            "encrypted-media-v1", ["blossom-v1"],
            [new BlobStoreEndpoint("blossom-v1", "https://blossom.example.com")]));

        Assert.Contains(EncryptedMediaPolicy.FormatV2, ex.Message);
    }

    [Fact]
    public void ProducersCanonicaliseAndDecodersRefuse()
    {
        // Create trims, lowercases and deduplicates because nothing is committed
        // yet. Decode refuses the same input, because repairing signed group
        // state leaves us holding a canonical form nobody else has.
        var policy = EncryptedMediaPolicy.Create(
            "  encrypted-media-v2  ", ["BLOSSOM-V1", "blossom-v1"],
            [new BlobStoreEndpoint(" Blossom-V1 ", " https://blossom.example.com ")]);

        Assert.Equal([EncryptedMediaPolicy.BlossomLocatorKind], policy.AllowedLocatorKinds);

        var uncanonical = new EncryptedMediaPolicy(
            EncryptedMediaPolicy.FormatV2, ["BLOSSOM-V1"],
            [new BlobStoreEndpoint("BLOSSOM-V1", "https://blossom.example.com/")]);

        Assert.Throws<AppComponentException>(
            () => EncryptedMediaPolicy.Decode(uncanonical.Encode()));
    }

    [Theory]
    [InlineData("ftp://blossom.example.com")]        // wrong scheme
    [InlineData("https://user:pw@blossom.example")]  // credentials
    [InlineData("https://blossom.example?a=b")]      // query
    [InlineData("https://blossom.example#frag")]     // fragment
    public void AnEndpointUrlOutsideTheProfileIsRefused(string url)
    {
        Assert.Throws<AppComponentException>(() => EncryptedMediaPolicy.Create(
            EncryptedMediaPolicy.FormatV2, ["blossom-v1"],
            [new BlobStoreEndpoint("blossom-v1", url)]));
    }

    [Fact]
    public void AnEndpointWhoseLocatorKindIsNotAllowedIsRefused()
    {
        var ex = Assert.Throws<AppComponentException>(() => EncryptedMediaPolicy.Create(
            EncryptedMediaPolicy.FormatV2, ["blossom-v1"],
            [new BlobStoreEndpoint("other-v1", "https://blossom.example.com")]));

        Assert.Contains("not in the allowed set", ex.Message);
    }

    [Fact]
    public void APolicyWithNoEndpointsIsRefused()
    {
        Assert.Throws<AppComponentException>(() => EncryptedMediaPolicy.Create(
            EncryptedMediaPolicy.FormatV2, ["blossom-v1"], []));
    }

    [Fact]
    public void MediaV1StaysFrozenWhileV2IsLive()
    {
        // Not two versions of one supported thing: v1 may neither be required
        // nor carried by a Current-profile group, and v2 is ordinary state.
        Assert.Contains(AppComponent.EncryptedMediaV2, CurrentProfile.KnownGroupComponents);
        Assert.DoesNotContain(AppComponent.EncryptedMediaV1Frozen, CurrentProfile.KnownGroupComponents);
    }

    [Fact]
    public void MediaPolicyBytesWithTrailingDataAreRefused()
    {
        byte[] encoded = EncryptedMediaPolicy.BlossomDefault(["https://blossom.example.com"]).Encode();

        Assert.Throws<AppComponentException>(
            () => EncryptedMediaPolicy.Decode([.. encoded, 0x00]));
    }
}
