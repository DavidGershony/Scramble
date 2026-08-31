using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Wire.Nostr;
using Xunit;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// Our KeyPackage stack against a KeyPackage the reference implementation
/// actually published.
/// </summary>
/// <remarks>
/// <para>
/// This is the test P3 left open and P6 needed. Upstream ships byte fixtures
/// for the app components but none for kind 30443, so until now every rule in
/// the codec and the leaf reader was pinned to spec prose and to a reading of
/// <c>transport-nostr-adapter/src/key_package.rs</c> — that is, to our own
/// interpretation. A live <c>wn-agent</c> publishing to a relay is the only
/// oracle available, and these assertions are worth more than the rest of the
/// KeyPackage suite combined for exactly that reason: nothing here was written
/// by the author of the code it checks.
/// </para>
/// <para>
/// The direction is inbound only. Having the agent validate <i>our</i>
/// KeyPackage needs the invite path, which needs create-group; that arrives
/// with the next P6 slice. What this does cover is every step between the wire
/// and a validated member identity.
/// </para>
/// <para>
/// Requires <c>docker compose -f docker-compose.test.yml up -d nostr-relay
/// wn-agent</c>. When the agent is not running the tests skip rather than fail:
/// a missing container is an absent environment, not a regression, and a suite
/// that goes red on a developer laptop without Docker stops being read.
/// </para>
/// </remarks>
[Trait("Category", "DarkMatterInterop")]
public class KeyPackageInteropTests
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private readonly List<string> _log = [];

    private async Task<(KeyPackagePublication Publication, KeyPackage KeyPackage)?> FetchAgentKeyPackageAsync()
    {
        var agent = new WnAgentDockerClient(_log.Add);
        if (!await agent.IsReadyAsync())
            return null;

        AgentBootstrap bootstrap = await agent.BootstrapAsync();
        Assert.True(bootstrap.KeyPackagePublished, "The agent did not publish a KeyPackage.");

        var relay = new InteropRelayClient(InteropRelayClient.DefaultRelayUrl);
        var envelopes = await relay.FetchKeyPackagesAsync(bootstrap.AccountIdHex, FetchTimeout);

        Assert.NotEmpty(envelopes);

        // Kind 30443 is addressable, so a well-behaved relay keeps one event per
        // (author, kind, d). More than one here means several slots, which is
        // legitimate; the newest is the one to read.
        string envelope = envelopes[^1];

        // Parse verifies the id and the Nostr signature before reading a field,
        // so reaching the next line already proves the envelope is conformant.
        var publication = KeyPackageEvent.Parse(envelope);

        var message = MlsMessage.ReadFrom(new TlsReader(publication.KeyPackageBytes));
        return (publication, (KeyPackage)message.Body);
    }

    [Fact]
    public async Task OurStackValidatesAKeyPackageTheReferenceImplementationPublished()
    {
        var fetched = await FetchAgentKeyPackageAsync();
        Assert.SkipWhen(fetched is null, "The wn-agent interop peer is not running.");

        var (publication, _) = fetched!.Value;

        // Everything at once: MLSMessage framing, KeyPackageRef equality against
        // the i tag, author-to-credential binding, the BIP-340 account-identity
        // proof over the leaf signature key, and the lifetime policy.
        var validated = KeyPackagePublicationValidator.Validate(publication, _cs);

        Assert.Equal(publication.AuthorPublicKeyHex,
            Convert.ToHexString(validated.CredentialIdentity).ToLowerInvariant());
    }

    [Fact]
    public async Task OurEncoderReproducesUpstreamsKeyPackageBytesExactly()
    {
        var fetched = await FetchAgentKeyPackageAsync();
        Assert.SkipWhen(fetched is null, "The wn-agent interop peer is not running.");

        var (publication, keyPackage) = fetched!.Value;

        // Decoding proves little on its own — a decoder that drops a field it
        // does not understand still decodes. Re-encoding and hashing does: the
        // KeyPackageRef is taken over the whole struct, so if a single byte of
        // our round trip differs, this hash differs from the i tag upstream
        // computed over its own bytes.
        byte[] reEncoded = TlsCodec.Serialize(keyPackage.WriteTo);
        string ourRef = Convert.ToHexString(
            KeyPackageRef.Compute(_cs, reEncoded).Value).ToLowerInvariant();

        Assert.Equal(publication.KeyPackageRefHex, ourRef);
    }

    [Fact]
    public async Task TheLeafDictionaryHasTheThreeEntriesOurBuilderEmits()
    {
        var fetched = await FetchAgentKeyPackageAsync();
        Assert.SkipWhen(fetched is null, "The wn-agent interop peer is not running.");

        var (_, keyPackage) = fetched!.Value;

        MarmotDictionary dictionary = MarmotLeaf.ReadDictionary(keyPackage.LeafNode)
            ?? throw new InvalidOperationException("The reference leaf carries no app_data_dictionary.");

        Assert.Equal(
            new ushort[]
            {
                AppComponent.AppComponents,
                AppComponent.SafeAad,
                AccountIdentityProof.ComponentId,
            },
            dictionary.ComponentIds.ToArray());

        // safe_aad present and EMPTY in a leaf. The same component id is an
        // error as GroupContext state, and having the reference confirm the
        // asymmetry is worth a test of its own.
        Assert.Empty(ComponentCodec.DecodeComponentsList(dictionary.Get(AppComponent.SafeAad)!));

        // The advertised list unions in its own id and the proof's.
        IReadOnlySet<ushort> advertised = dictionary.ComponentList()!;
        Assert.Contains(AppComponent.AppComponents, advertised);
        Assert.Contains(AccountIdentityProof.ComponentId, advertised);
    }

    [Fact]
    public async Task TheLeafAdvertisesTheCapabilitiesWeRequireAndWeAdvertiseASubsetOfIt()
    {
        var fetched = await FetchAgentKeyPackageAsync();
        Assert.SkipWhen(fetched is null, "The wn-agent interop peer is not running.");

        var (_, keyPackage) = fetched!.Value;
        Capabilities capabilities = keyPackage.LeafNode.Capabilities;

        // The Current-profile floor, which is what we emit.
        Assert.Contains(MarmotLeaf.RequiredCapabilitiesExtensionType, capabilities.Extensions);
        Assert.Contains(MarmotDictionary.ExtensionType, capabilities.Extensions);
        Assert.Contains(MarmotLeaf.AppDataUpdateProposalType, capabilities.Proposals);

        // 0x8009 is a component, never an advertised extension type, in Current.
        // A peer carrying it here would be running the Legacy profile.
        Assert.DoesNotContain(AccountIdentityProof.ComponentId, capabilities.Extensions);

        // Everything we advertise, the reference advertises too. The converse
        // does not hold and is not asserted: the agent adds its feature-registry
        // extensions and the SelfRemove proposal, which we cannot yet encode.
        Assert.All(MarmotLeaf.ExtensionTypes, e => Assert.Contains(e, capabilities.Extensions));
        Assert.All(MarmotLeaf.ProposalTypes, p => Assert.Contains(p, capabilities.Proposals));
    }

    [Fact]
    public async Task TheReferenceLifetimeSitsInsideTheBoundWeEnforce()
    {
        var fetched = await FetchAgentKeyPackageAsync();
        Assert.SkipWhen(fetched is null, "The wn-agent interop peer is not running.");

        var (_, keyPackage) = fetched!.Value;
        Lifetime lifetime = keyPackage.LeafNode.Lifetime!;

        // The bound is OpenMLS's MAX_LEAF_NODE_LIFETIME_RANGE_SECONDS, which no
        // Marmot document restates — we derived it by reading the revision mdk
        // pins. This is that reading checked against the running peer, and it is
        // the assertion that would have caught the unbounded lifetime we used to
        // emit before the peer ever saw it.
        Assert.True(
            KeyPackageLifetimePolicy.IsAcceptableRange(lifetime),
            $"The reference published a {lifetime.NotAfter - lifetime.NotBefore}s window, " +
            $"but we refuse anything over {KeyPackageLifetimePolicy.MaxRangeSeconds}s.");

        Assert.Equal(
            KeyPackageLifetimePolicy.MaxRangeSeconds, lifetime.NotAfter - lifetime.NotBefore);
    }

    [Fact]
    public async Task TheReferenceMarksItsKeyPackageLastResortAndWeDoNot()
    {
        var fetched = await FetchAgentKeyPackageAsync();
        Assert.SkipWhen(fetched is null, "The wn-agent interop peer is not running.");

        var (_, keyPackage) = fetched!.Value;

        // Pins a known divergence rather than a shared rule, so it is visible
        // instead of forgotten. Last-resort in the Current profile is component
        // 0x0004 with EMPTY data inside the KEYPACKAGE-level app_data_dictionary
        // — not a leaf extension, and not the obsolete 0x000a extension
        // (openmls/src/key_packages/mod.rs, KeyPackage::last_resort). Non-empty
        // data there is explicitly malformed.
        Extension dictionaryExtension = Assert.Single(
            keyPackage.Extensions, e => e.ExtensionType == MarmotDictionary.ExtensionType);

        byte[]? marker = MarmotDictionary
            .Decode(dictionaryExtension.ExtensionData)
            .Get(LastResortComponentId);

        Assert.NotNull(marker);
        Assert.Empty(marker!);

        // Ours, built for real and compared. We emit no KeyPackage-level
        // extensions at all, because MlsGroup.CreateKeyPackage hardcodes an
        // empty set and offers no parameter. The cost is that only the first
        // Welcome sent to one of our publications can be opened. When the
        // library grows that parameter, this assertion fails and says so —
        // which is the point of pinning the divergence rather than a comment.
        var ours = await MarmotKeyPackageBuilder.CreateAsync(
            _cs, new EphemeralSigner(), (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        Assert.Empty(ours.KeyPackage.Extensions);
    }

    /// <summary>The last_resort_key_package component, draft-ietf-mls-extensions.</summary>
    private const ushort LastResortComponentId = 0x0004;

    /// <summary>A throwaway account key, for building one KeyPackage locally.</summary>
    private sealed class EphemeralSigner : IAccountIdentityProofSigner
    {
        private readonly byte[] _secret;

        public EphemeralSigner()
        {
            var (secret, publicKey) = Scramble.Nostr.Crypto.Bip340.GenerateKeyPair();
            _secret = secret;
            AccountPublicKey = publicKey;
        }

        public ReadOnlyMemory<byte> AccountPublicKey { get; }

        public Task<byte[]> SignAsync(
            Scramble.Nostr.Crypto.NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Scramble.Nostr.Crypto.Bip340.Sign(_secret, template.ComputeId()));
    }
}
