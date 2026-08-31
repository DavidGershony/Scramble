using System.Text;
using System.Text.Json;
using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Building a Current-profile Marmot KeyPackage, and publishing one.
/// </summary>
/// <remarks>
/// The shape these pin comes from <c>mdk</c> at <c>wn-agent-v0.9.15</c>, not
/// from spec prose: <c>leaf_capabilities</c> for the capability set,
/// <c>leaf_app_components_extension</c> for the three dictionary entries, and
/// OpenMLS at the revision mdk pins for the lifetime bound. A test written
/// against what our own builder happens to emit would agree with itself and
/// with nobody else.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class MarmotKeyPackageBuilderTests
{
    private readonly ICipherSuite _cs = new CipherSuite0x0001();

    private const ulong Now = 1_760_000_000;

    /// <summary>A signer holding a real Nostr key, so proofs actually verify.</summary>
    private sealed class LocalSigner : IAccountIdentityProofSigner
    {
        private readonly byte[] _secret;

        public LocalSigner()
        {
            var (secret, publicKey) = Bip340.GenerateKeyPair();
            _secret = secret;
            AccountPublicKey = publicKey;
        }

        public ReadOnlyMemory<byte> AccountPublicKey { get; }

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(_secret, template.ComputeId()));
    }

    private Task<MarmotKeyPackageBundle> BuildAsync(
        IAccountIdentityProofSigner? signer = null,
        IReadOnlySet<ushort>? supported = null) =>
        MarmotKeyPackageBuilder.CreateAsync(_cs, signer ?? new LocalSigner(), Now, supported);

    // ---- What the leaf advertises ----

    [Fact]
    public async Task TheLeafAdvertisesTheCurrentProfileCapabilities()
    {
        var bundle = await BuildAsync();
        var capabilities = bundle.KeyPackage.LeafNode.Capabilities;

        // required_capabilities and app_data_dictionary. 0x8009 must NOT be
        // here: it is an extension capability only in the Legacy profile, and
        // advertising it is a Legacy tell.
        Assert.Equal(new ushort[] { 0x0003, 0x0006 }, capabilities.Extensions.Order().ToArray());
        Assert.Equal(new ushort[] { 0x0008 }, capabilities.Proposals.ToArray());
        Assert.DoesNotContain((ushort)0x8009, capabilities.Extensions);
    }

    [Fact]
    public async Task TheLeafDictionaryCarriesTheThreeEntriesUpstreamEmits()
    {
        var bundle = await BuildAsync();
        MarmotDictionary dictionary = MarmotLeaf.ReadDictionary(bundle.KeyPackage.LeafNode)!;

        Assert.Equal(
            new ushort[] { AppComponent.AppComponents, AppComponent.SafeAad, AccountIdentityProof.ComponentId },
            dictionary.ComponentIds.ToArray());

        // safe_aad names the components whose messages this leaf protects with
        // safe AAD. Marmot v1 has none, so the list is empty — present and
        // empty, not absent.
        Assert.Empty(ComponentCodec.DecodeComponentsList(dictionary.Get(AppComponent.SafeAad)!));
    }

    [Fact]
    public async Task TheAdvertisedComponentListCarriesItsOwnIdAndTheProof()
    {
        var bundle = await BuildAsync(supported: new HashSet<ushort> { AppComponent.GroupProfile });

        Assert.Equal(
            new ushort[] { AppComponent.AppComponents, AppComponent.GroupProfile, AccountIdentityProof.ComponentId },
            bundle.AppComponents.ToArray());

        // The event tag and the leaf dictionary must say the same thing; a peer
        // reads the second and a relay filter reads the first.
        MarmotDictionary dictionary = MarmotLeaf.ReadDictionary(bundle.KeyPackage.LeafNode)!;
        Assert.Equal(bundle.AppComponents.ToArray(), dictionary.ComponentList()!.Order().ToArray());
    }

    [Fact]
    public async Task TheDefaultSupportedSetExcludesTheDeferredComponents()
    {
        var bundle = await BuildAsync();

        foreach (ushort deferred in new ushort[] { 0x8002, 0x8006, 0x8007, 0x8008, 0x800b, 0x800c })
            Assert.DoesNotContain(deferred, bundle.AppComponents);
    }

    // ---- The proof ----

    [Fact]
    public async Task TheProofVerifiesAgainstTheLeafItAuthorises()
    {
        var signer = new LocalSigner();
        var bundle = await BuildAsync(signer);

        MarmotDictionary dictionary = MarmotLeaf.ReadDictionary(bundle.KeyPackage.LeafNode)!;
        Assert.True(AccountIdentityProof.TryDecode(
            dictionary.Get(AccountIdentityProof.ComponentId), out var proof));

        Assert.Equal(
            AccountIdentityProofResult.Valid,
            proof!.Validate(
                signer.AccountPublicKey.Span,
                _cs.Id,
                MarmotKeyPackageBuilder.Ed25519SignatureScheme,
                bundle.KeyPackage.LeafNode.SignatureKey));
    }

    [Fact]
    public async Task TheLeafSignatureKeyIsFreshAndIsNotTheAccountKey()
    {
        var signer = new LocalSigner();
        var first = await BuildAsync(signer);
        var second = await BuildAsync(signer);

        Assert.NotEqual(
            signer.AccountPublicKey.ToArray(), first.KeyPackage.LeafNode.SignatureKey);
        Assert.NotEqual(
            first.KeyPackage.LeafNode.SignatureKey, second.KeyPackage.LeafNode.SignatureKey);

        // The credential, by contrast, IS the account key: that is what the
        // proof binds the leaf key to.
        var credential = Assert.IsType<BasicCredential>(first.KeyPackage.LeafNode.Credential);
        Assert.Equal(signer.AccountPublicKey.ToArray(), credential.Identity);
    }

    [Fact]
    public async Task ACredentialIdentityThatIsNotACurvePointIsRefused()
    {
        var signer = new StubSigner(new byte[32]);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => BuildAsync(signer));
        Assert.Contains("credential identity", ex.Message);
    }

    private sealed class StubSigner(byte[] accountKey) : IAccountIdentityProofSigner
    {
        public ReadOnlyMemory<byte> AccountPublicKey { get; } = accountKey;

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            throw new InvalidOperationException("The identity must be rejected before signing.");
    }

    // ---- The reference and the published bytes ----

    [Fact]
    public async Task TheRefIsOverTheKeyPackageAndNotOverTheMlsMessageThatFramesIt()
    {
        var bundle = await BuildAsync();

        byte[] inner = TlsCodec.Serialize(bundle.KeyPackage.WriteTo);
        string overInner = Hex(KeyPackageRef.Compute(_cs, inner).Value);
        string overEnvelope = Hex(KeyPackageRef.Compute(_cs, bundle.PublishedBytes).Value);

        Assert.Equal(overInner, bundle.KeyPackageRefHex);
        Assert.NotEqual(overEnvelope, bundle.KeyPackageRefHex);
    }

    [Fact]
    public async Task ThePublishedBytesAreAnMlsMessageWrappingTheKeyPackage()
    {
        var bundle = await BuildAsync();

        var message = MlsMessage.ReadFrom(new TlsReader(bundle.PublishedBytes));

        Assert.Equal(WireFormat.MlsKeyPackage, message.WireFormat);
        Assert.Equal(ProtocolVersion.Mls10, message.Version);
        var decoded = Assert.IsType<KeyPackage>(message.Body);
        Assert.Equal(
            TlsCodec.Serialize(bundle.KeyPackage.WriteTo), TlsCodec.Serialize(decoded.WriteTo));
    }

    // ---- The lifetime ----

    [Fact]
    public async Task TheLifetimeSitsInsideWhatAPeerAccepts()
    {
        var bundle = await BuildAsync();

        Assert.Equal(Now - KeyPackageLifetimePolicy.ClockSkewMarginSeconds, bundle.Lifetime.NotBefore);
        Assert.Equal(Now + KeyPackageLifetimePolicy.DefaultValiditySeconds, bundle.Lifetime.NotAfter);
        Assert.True(KeyPackageLifetimePolicy.IsAcceptableRange(bundle.Lifetime));
        Assert.True(KeyPackageLifetimePolicy.IsValidAt(bundle.Lifetime, Now));
    }

    [Fact]
    public void TheDefaultWindowIsExactlyTheMaximumAndNothingFitsAboveIt()
    {
        // Upstream's own default sits on the bound, and the comparison is <=.
        // Pinning the equality is the point: if a future edit adds "a little
        // headroom" to the validity, every KeyPackage we publish is refused.
        var lifetime = KeyPackageLifetimePolicy.Create(Now);
        Assert.Equal(
            KeyPackageLifetimePolicy.MaxRangeSeconds, lifetime.NotAfter - lifetime.NotBefore);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => KeyPackageLifetimePolicy.Create(Now, KeyPackageLifetimePolicy.DefaultValiditySeconds + 1));
    }

    [Fact]
    public void AnUnboundedWindowIsRefusedAsARange()
    {
        // What dotnet-mls produces when no lifetime is passed. It decodes and
        // verifies perfectly well, so it has to be caught as a policy failure
        // or not at all.
        Assert.False(KeyPackageLifetimePolicy.IsAcceptableRange(new Lifetime(0, ulong.MaxValue)));
    }

    [Fact]
    public void AClockThatIsNotSetIsRefusedRatherThanUnderflowed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyPackageLifetimePolicy.Create(60));
    }

    // ---- The private material ----

    [Fact]
    public async Task ThePrivateMaterialRoundTripsAllThreeKeys()
    {
        var bundle = await BuildAsync();
        var material = bundle.PrivateMaterial;

        var decoded = KeyPackagePrivateMaterial.Decode(material.Encode());

        Assert.Equal(material.InitPrivateKey, decoded.InitPrivateKey);
        Assert.Equal(material.LeafPrivateKey, decoded.LeafPrivateKey);
        Assert.Equal(material.SignaturePrivateKey, decoded.SignaturePrivateKey);

        // Not merely three blobs of the right shape: the stored signature key
        // must be the private half of the leaf's public one. A bundle that
        // round-trips but holds the wrong key publishes fine and then cannot
        // sign a single message in the group it joins.
        byte[] signature = _cs.SignWithLabel(decoded.SignaturePrivateKey, "LeafNodeTBS", [0xAB]);
        Assert.True(_cs.VerifyWithLabel(
            bundle.KeyPackage.LeafNode.SignatureKey, "LeafNodeTBS", [0xAB], signature));

        Assert.NotEmpty(decoded.InitPrivateKey);
    }

    [Fact]
    public void PrivateMaterialWithTrailingBytesIsRefused()
    {
        byte[] encoded = new KeyPackagePrivateMaterial([1], [2], [3]).Encode();

        Assert.Throws<AppComponentException>(
            () => KeyPackagePrivateMaterial.Decode([.. encoded, 0x00]));
        Assert.Throws<AppComponentException>(
            () => KeyPackagePrivateMaterial.Decode(encoded.AsSpan(0, encoded.Length - 1)));
    }

    [Fact]
    public void PrivateMaterialUnderAnUnknownVersionIsRefused()
    {
        byte[] encoded = new KeyPackagePrivateMaterial([1], [2], [3]).Encode();
        encoded[0] = 0xFF;

        Assert.Throws<AppComponentException>(() => KeyPackagePrivateMaterial.Decode(encoded));
    }

    // ---- The durable record ----

    [Fact]
    public async Task TheRecordIsCreatedNotPublishedAndCarriesTheWindow()
    {
        var bundle = await BuildAsync();
        string slot = KeyPackageEvent.NewSlotId();

        KeyPackageRecord record = bundle.ToRecord(slot, DateTimeOffset.UnixEpoch);

        Assert.Equal(KeyPackageRecordState.Created, record.State);
        Assert.Null(record.EventIdHex);
        Assert.True(record.CanConsume);
        Assert.Equal(slot, record.SlotId);
        Assert.Equal(bundle.PublishedBytes, record.PublicKeyPackage);
        Assert.Equal((long)bundle.Lifetime.NotAfter, record.NotAfter);

        // Not last-resort, deliberately: we erase private material on consume,
        // and dotnet-mls cannot set the extension in any case.
        Assert.False(record.LastResort);
    }

    // ---- Publishing, and the checks the codec left to the engine ----

    [Fact]
    public async Task APublicationWeBuiltPassesEveryCheckTheCodecLeftToTheEngine()
    {
        var signer = new LocalSigner();
        var bundle = await BuildAsync(signer);

        KeyPackagePublication publication = Publish(bundle, signer);
        var validated = KeyPackagePublicationValidator.Validate(publication, _cs, Now);

        Assert.Equal(bundle.KeyPackageRefHex, validated.Publication.KeyPackageRefHex);
        Assert.Equal(signer.AccountPublicKey.ToArray(), validated.CredentialIdentity);
    }

    [Fact]
    public async Task ARefTagThatNamesADifferentKeyPackageIsRefused()
    {
        var signer = new LocalSigner();
        var bundle = await BuildAsync(signer);
        var other = await BuildAsync(signer);

        // Same event, same author, same payload — only the i tag moved. The tag
        // is what a Welcome names, so this publication is unaddressable.
        var publication = Publish(bundle, signer) with { KeyPackageRefHex = other.KeyPackageRefHex };

        var ex = Assert.Throws<KeyPackagePublicationException>(
            () => KeyPackagePublicationValidator.Validate(publication, _cs, Now));
        Assert.Contains("hashes to", ex.Message);
    }

    [Fact]
    public async Task AKeyPackageRepublishedUnderAnotherAccountIsRefused()
    {
        var owner = new LocalSigner();
        var thief = new LocalSigner();
        var bundle = await BuildAsync(owner);

        // A valid, correctly self-consistent KeyPackage — signed onto the relay
        // by someone else. Without this check the directory answers "this npub's
        // KeyPackage" with a package belonging to another account.
        KeyPackagePublication publication = Publish(bundle, thief);

        var ex = Assert.Throws<KeyPackagePublicationException>(
            () => KeyPackagePublicationValidator.Validate(publication, _cs, Now));
        Assert.Contains("author", ex.Message);
    }

    [Fact]
    public async Task AnExpiredPublicationIsRefusedOnlyWhenATimeIsGiven()
    {
        var signer = new LocalSigner();
        var bundle = await BuildAsync(signer);
        KeyPackagePublication publication = Publish(bundle, signer);

        ulong after = bundle.Lifetime.NotAfter + 1;
        Assert.Throws<KeyPackagePublicationException>(
            () => KeyPackagePublicationValidator.Validate(publication, _cs, after));

        // Without a time it is still structurally valid: validating our own
        // fresh publication should not depend on the wall clock.
        KeyPackagePublicationValidator.Validate(publication, _cs);
    }

    [Fact]
    public async Task ContentThatIsNotAKeyPackageIsRefusedRatherThanMisread()
    {
        var signer = new LocalSigner();
        var bundle = await BuildAsync(signer);

        var publication = Publish(bundle, signer) with { KeyPackageBytes = [0x00, 0x01, 0x02] };

        Assert.Throws<KeyPackagePublicationException>(
            () => KeyPackagePublicationValidator.Validate(publication, _cs, Now));
    }

    // ---- Helpers ----

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    /// <summary>
    /// Signs and re-parses a bundle as a kind-30443 publication.
    /// </summary>
    /// <remarks>
    /// Round-tripped through <see cref="KeyPackageEvent.Parse"/> rather than
    /// constructed directly, so the tags this builder produces are ones the
    /// codec accepts. A hand-built publication would test the validator against
    /// a shape no relay would ever carry.
    /// </remarks>
    private static KeyPackagePublication Publish(MarmotKeyPackageBundle bundle, LocalSigner signer)
    {
        string accountHex = Hex(signer.AccountPublicKey.ToArray());

        var template = KeyPackageEvent.BuildTemplate(
            accountHex,
            bundle.PublishedBytes,
            KeyPackageEvent.NewSlotId(),
            bundle.KeyPackageRefHex,
            bundle.CipherSuites,
            bundle.MlsExtensions,
            bundle.MlsProposals,
            bundle.AppComponents,
            createdAt: (long)Now);

        byte[] id = template.ComputeId();
        byte[] signature = signer.SignAsync(template).GetAwaiter().GetResult();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", Hex(id));
            writer.WriteString("pubkey", accountHex);
            writer.WriteNumber("created_at", template.CreatedAt);
            writer.WriteNumber("kind", KeyPackageEvent.Kind);
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in template.Tags)
            {
                writer.WriteStartArray();
                foreach (string value in tag)
                    writer.WriteStringValue(value);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteString("content", template.Content);
            writer.WriteString("sig", Hex(signature));
            writer.WriteEndObject();
        }

        return KeyPackageEvent.Parse(Encoding.UTF8.GetString(stream.ToArray()));
    }
}
