using DotnetMls.Crypto;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage;
using Scramble.Nostr.Crypto;
using Xunit;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Creating a Current-profile group.
/// </summary>
/// <remarks>
/// Epoch 0 is the only chance to get the GroupContext right: nothing in it can
/// be repaired later without an authorised commit, and some of it — an empty
/// admin set — cannot be repaired at all, because the commit that would fix it
/// is the one nobody is authorised to make. So these tests are about the shape
/// of the initial state far more than about the mechanics of creation.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class MarmotGroupBuilderTests
{
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;

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

    private Task<CreatedGroup> CreateAsync(
        IAccountIdentityProofSigner? signer = null,
        IEnumerable<byte[]>? admins = null,
        IReadOnlySet<ushort>? supported = null) =>
        MarmotGroupBuilder.CreateAsync(
            _cs, signer ?? new LocalSigner(), "Rakes", "For the raking of leaves",
            Now, admins, supported);

    private static MarmotDictionary DictionaryOf(CreatedGroup created)
    {
        foreach (var extension in created.Group.GroupContext.Extensions)
        {
            if (extension.ExtensionType == MarmotDictionary.ExtensionType)
                return MarmotDictionary.Decode(extension.ExtensionData);
        }

        throw new InvalidOperationException("The group carries no app_data_dictionary.");
    }

    // ---- The initial GroupContext ----

    [Fact]
    public async Task ANewGroupSatisfiesTheCurrentProfileInvariants()
    {
        var created = await CreateAsync();

        // Read off the group that was actually built, not off the request.
        IReadOnlySet<ushort> required = MarmotGroupBuilder.ValidateCreated(created.Group);

        Assert.Equal(created.Required, required);
        Assert.Equal(0UL, created.Group.Epoch);
    }

    [Fact]
    public async Task TheGroupRequiresTheFourDefaultComponents()
    {
        var created = await CreateAsync();

        Assert.Equal(
            new ushort[]
            {
                AppComponent.GroupProfile,
                AppComponent.GroupAdminPolicy,
                AccountIdentityProof.ComponentId,
                AppComponent.GroupLifecycle,
            }.Order().ToArray(),
            created.Required.Order().ToArray());
    }

    [Fact]
    public async Task TheDictionaryCarriesStateForEveryRequiredComponentExceptTheLeafOnlyOne()
    {
        var created = await CreateAsync();
        MarmotDictionary dictionary = DictionaryOf(created);

        Assert.Equal(
            new ushort[]
            {
                AppComponent.AppComponents,
                AppComponent.GroupProfile,
                AppComponent.GroupAdminPolicy,
                AppComponent.GroupLifecycle,
            },
            dictionary.ComponentIds.ToArray());

        // Required, and deliberately absent: the proof is LeafNode-only, and its
        // presence in a GroupContext dictionary is an error rather than
        // harmless duplication.
        Assert.Contains(AccountIdentityProof.ComponentId, created.Required);
        Assert.False(dictionary.Contains(AccountIdentityProof.ComponentId));
    }

    [Fact]
    public async Task TheGroupStartsActive()
    {
        var created = await CreateAsync();

        Assert.Equal(
            GroupLifecycleState.Active,
            GroupLifecycle.Decode(DictionaryOf(created).Get(AppComponent.GroupLifecycle)!));
    }

    [Fact]
    public async Task TheProfileCarriesTheNameAndDescription()
    {
        var created = await CreateAsync();

        var profile = GroupProfile.Decode(DictionaryOf(created).Get(AppComponent.GroupProfile)!);

        Assert.Equal("Rakes", profile.Name);
        Assert.Equal("For the raking of leaves", profile.Description);
    }

    [Fact]
    public async Task RequiredCapabilitiesNamesTheExtensionAndProposalButNoComponents()
    {
        var created = await CreateAsync();

        var capabilities = RequiredCapabilities.FromExtensions(created.Group.GroupContext.Extensions)!;

        Assert.Equal(new ushort[] { MarmotDictionary.ExtensionType }, capabilities.ExtensionTypes.ToArray());
        Assert.Equal(new ushort[] { 0x0008 }, capabilities.ProposalTypes.ToArray());

        // Empty on purpose. The profile fixes BasicCredential, so requiring it
        // would add a constraint upstream does not emit and make our groups
        // differ on the wire for nothing.
        Assert.Empty(capabilities.CredentialTypes);

        // Component ids never appear here — required_capabilities is MLS's
        // vocabulary. They live in the dictionary's requirement list.
        Assert.DoesNotContain(AppComponent.GroupAdminPolicy, capabilities.ExtensionTypes);
    }

    // ---- Admins ----

    [Fact]
    public async Task TheCreatorIsTheFirstAdmin()
    {
        var signer = new LocalSigner();
        var created = await CreateAsync(signer);

        byte[] only = Assert.Single(created.Admins);
        Assert.Equal(signer.AccountPublicKey.ToArray(), only);

        var policy = AdminPolicy.Decode(DictionaryOf(created).Get(AppComponent.GroupAdminPolicy)!);
        Assert.Equal(signer.AccountPublicKey.ToArray(), Assert.Single(policy.Admins));
    }

    [Fact]
    public async Task NamingTheCreatorAgainAsAnAdminDoesNotDuplicateThem()
    {
        var signer = new LocalSigner();

        var created = await CreateAsync(signer, admins: [signer.AccountPublicKey.ToArray()]);

        Assert.Single(created.Admins);
    }

    [Fact]
    public async Task AnAdminWhoIsNotAMemberIsRefused()
    {
        var stranger = new LocalSigner();

        // A phantom admin becomes active the instant a matching leaf appears,
        // with no commit any member observed granting it — bypassing the audit
        // trail every commit seam enforces.
        var ex = await Assert.ThrowsAsync<AppComponentException>(
            () => CreateAsync(admins: [stranger.AccountPublicKey.ToArray()]));

        Assert.Contains("phantom admin", ex.Message);
    }

    [Fact]
    public async Task AnAdminKeyThatIsNotACurvePointIsRefused()
    {
        // Length alone is not enough: an entry that cannot be a Nostr key can
        // never authorise anything, and it would sit in signed state forever.
        await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync(admins: [new byte[32]]));
    }

    // ---- Negotiation ----

    private static MarmotGroupProfile.MemberComponents Member(
        string label, IReadOnlySet<ushort> components) => new(label, components);

    private static IReadOnlySet<ushort> Everyone() =>
        MarmotLeaf.AdvertisedComponents(MarmotLeaf.DefaultSupportedComponents);

    [Fact]
    public void NegotiationIntersectsWhatEveryMemberAdvertises()
    {
        IReadOnlySet<ushort> everyone = Everyone();

        var thinner = new HashSet<ushort>(everyone);
        thinner.Remove(AppComponent.MessageRetention);

        IReadOnlySet<ushort> negotiated = MarmotGroupProfile.Negotiate(
            new SortedSet<ushort>(everyone),
            [Member("alice", everyone), Member("bob", thinner)]);

        Assert.DoesNotContain(AppComponent.MessageRetention, negotiated);
    }

    [Theory]
    [InlineData(AppComponent.GroupProfile)]
    [InlineData(AppComponent.GroupAdminPolicy)]
    [InlineData(AppComponent.GroupLifecycle)]
    [InlineData(AccountIdentityProof.ComponentId)]
    public void AMemberMissingAMandatoryComponentIsRefusedByNameRatherThanNegotiatedAround(
        ushort mandatory)
    {
        // The alternative is a group that cannot be repaired. Drop the admin
        // policy and the admin set is empty, so every admin-gated operation and
        // every later join fails closed — permanently, because the commit that
        // would fix it is the one nobody is authorised to make.
        IReadOnlySet<ushort> everyone = Everyone();

        var lacking = new HashSet<ushort>(everyone);
        lacking.Remove(mandatory);

        var ex = Assert.Throws<AppComponentException>(
            () => MarmotGroupProfile.Negotiate(
                everyone, [Member("alice", everyone), Member("bob", lacking)]));

        Assert.Contains($"0x{mandatory:x4}", ex.Message);

        // Naming the member is the point, and the reason this guard exists
        // beside the post-condition: the caller can drop bob and retry, which a
        // message naming only the component does not let them do. Without the
        // name, the intersection alone would refuse the same inputs and this
        // guard would be doing no work.
        Assert.Contains("bob", ex.Message);
        Assert.DoesNotContain("alice", ex.Message);
    }

    [Fact]
    public void ThePostConditionStillRefusesIfTheNamedGuardIsBypassed()
    {
        // The second half of the pair, reachable only when the desired set never
        // had the component to begin with. It says nothing about who is at
        // fault, which is exactly why it is not a substitute for the guard.
        var withoutAdminPolicy = new SortedSet<ushort>(MarmotGroupProfile.DefaultComponents);
        withoutAdminPolicy.Remove(AppComponent.GroupAdminPolicy);

        var ex = Assert.Throws<AppComponentException>(
            () => MarmotGroupProfile.Negotiate(withoutAdminPolicy, []));

        Assert.Contains("negotiated out", ex.Message);
    }

    [Fact]
    public async Task ACreatorWhoseSupportSetIsTooNarrowIsRefusedAtCreation()
    {
        // Better here than when nobody can join. The creator is a member like
        // any other, so the same guard applies to them.
        var ex = await Assert.ThrowsAsync<AppComponentException>(
            () => CreateAsync(supported: new HashSet<ushort> { AppComponent.GroupProfile }));

        Assert.Contains($"0x{AppComponent.GroupAdminPolicy:x4}", ex.Message);
    }

    // ---- The durable record ----

    [Fact]
    public async Task TheRecordSaysWeHaveBeenHereSinceEpochZero()
    {
        var created = await CreateAsync();

        GroupRecord record = created.ToRecord(DateTimeOffset.UnixEpoch);

        Assert.Equal(created.GroupId, record.Id.Value.ToArray());
        Assert.Equal(0UL, record.Epoch.Value);
        Assert.Equal(ProtocolProfile.Current, record.Profile);
        Assert.False(record.Removed);

        // Nothing precedes epoch 0 for a creator, so no message can be a
        // delivery failure for being older than our join.
        Assert.Equal(0UL, record.JoinEpoch!.Value.Value);

        // One leaf, whose proof we just built.
        Assert.True(record.ValidatedTree);
    }

    // ---- required_capabilities codec ----

    [Fact]
    public void RequiredCapabilitiesRoundTripsAndIsCanonicalisedOnCreateOnly()
    {
        var value = RequiredCapabilities.Create([0x0006, 0x0003, 0x0006], [0x0008]);

        Assert.Equal(new ushort[] { 0x0003, 0x0006 }, value.ExtensionTypes.ToArray());

        var decoded = RequiredCapabilities.Decode(value.Encode());
        Assert.Equal(value.ExtensionTypes, decoded.ExtensionTypes);
        Assert.Equal(value.ProposalTypes, decoded.ProposalTypes);
        Assert.Empty(decoded.CredentialTypes);
    }

    [Fact]
    public void AnUnsortedRequiredCapabilitiesIsAcceptedRatherThanRepaired()
    {
        // RFC 9420 states no ordering rule, so rejecting an unsorted list would
        // invent one and refuse a conformant peer. Repairing it would be worse:
        // this is signed group state, and a member that rewrites it holds a
        // canonical form nobody else has.
        byte[] encoded = new RequiredCapabilities([0x0006, 0x0003], [0x0008], []).Encode();

        var decoded = RequiredCapabilities.Decode(encoded);

        Assert.Equal(new ushort[] { 0x0006, 0x0003 }, decoded.ExtensionTypes.ToArray());
    }

    [Fact]
    public void RequiredCapabilitiesWithTrailingBytesIsRefused()
    {
        byte[] encoded = RequiredCapabilities.Create([0x0006], [0x0008]).Encode();

        Assert.Throws<DotnetMls.Codec.TlsDecodingException>(
            () => RequiredCapabilities.Decode([.. encoded, 0x00]));
    }
}
