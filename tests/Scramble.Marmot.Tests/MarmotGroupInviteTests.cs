using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Adding members to a Marmot group.
/// </summary>
/// <remarks>
/// Two things carry the weight here. The MLS library validates almost nothing
/// about an added leaf — it has a capabilities check for RFC 9420 §12.1.1 and
/// never calls it, and knows nothing of app components — so every gate is ours.
/// And the commit is staged rather than applied: a commit applied locally and
/// never published forks the committer into an epoch nobody else can reach.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class MarmotGroupInviteTests
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

    private Task<CreatedGroup> NewGroupAsync() =>
        MarmotGroupBuilder.CreateAsync(_cs, new LocalSigner(), "Rakes", "", Now);

    private Task<MarmotKeyPackageBundle> NewInviteeAsync(
        IReadOnlySet<ushort>? supported = null) =>
        MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now, supported);

    // ---- The happy path ----

    [Fact]
    public async Task AConformantInviteeIsAccepted()
    {
        var group = await NewGroupAsync();
        var invitee = await NewInviteeAsync();

        byte[] account = MarmotGroupInvite.ValidateInvitee(group.Group, _cs, invitee.KeyPackage);

        var credential = Assert.IsType<BasicCredential>(invitee.KeyPackage.LeafNode.Credential);
        Assert.Equal(credential.Identity, account);
    }

    [Fact]
    public async Task AddingProducesACommitAndAWelcomeWithoutAdvancingTheGroup()
    {
        var group = await NewGroupAsync();
        var invitee = await NewInviteeAsync();

        var staged = MarmotGroupInvite.Add(group.Group, _cs, [invitee.KeyPackage]);

        Assert.NotNull(staged.Commit);
        Assert.NotNull(staged.Welcome);
        Assert.Single(staged.AddedAccounts);

        // Still at the old epoch. A commit applied before it is published forks
        // the committer into an epoch nobody else can reach.
        Assert.Equal(0UL, group.Group.Epoch);
    }

    [Fact]
    public async Task ApplyingTheCommitAdvancesTheEpochAndKeepsTheGroupValid()
    {
        var group = await NewGroupAsync();
        var invitee = await NewInviteeAsync();

        var staged = MarmotGroupInvite.Add(group.Group, _cs, [invitee.KeyPackage]);
        staged.Applied();

        Assert.Equal(1UL, group.Group.Epoch);

        // The Current-profile invariants must still hold in the resulting epoch,
        // not merely at creation: an Add that quietly dropped the dictionary
        // would leave a group every peer refuses.
        IReadOnlySet<ushort> required = MarmotGroupBuilder.ValidateCreated(group.Group);
        Assert.Equal(group.Required, required);
    }

    [Fact]
    public async Task DiscardingLeavesTheGroupWhereItWas()
    {
        var group = await NewGroupAsync();
        var invitee = await NewInviteeAsync();

        MarmotGroupInvite.Add(group.Group, _cs, [invitee.KeyPackage]).Discard();

        Assert.Equal(0UL, group.Group.Epoch);

        // And the group is still usable: a discarded commit must not leave a
        // pending one behind, or the next invite cannot be staged.
        var second = await NewInviteeAsync();
        MarmotGroupInvite.Add(group.Group, _cs, [second.KeyPackage]).Applied();
        Assert.Equal(1UL, group.Group.Epoch);
    }

    [Fact]
    public async Task SeveralInviteesGoInOneCommit()
    {
        var group = await NewGroupAsync();
        var first = await NewInviteeAsync();
        var second = await NewInviteeAsync();

        var staged = MarmotGroupInvite.Add(group.Group, _cs, [first.KeyPackage, second.KeyPackage]);
        staged.Applied();

        Assert.Equal(2, staged.AddedAccounts.Count);
        Assert.Equal(1UL, group.Group.Epoch);
    }

    // ---- What is refused, and why ----

    [Fact]
    public async Task AnInviteeMissingARequiredComponentIsRefused()
    {
        var group = await NewGroupAsync();

        // Supports everything except the admin policy the group requires. MLS
        // sees nothing wrong with this leaf — the component set is invisible to
        // it — so without our check the member joins and then cannot honour
        // state everyone else treats as mandatory.
        var narrow = new HashSet<ushort>(MarmotLeaf.DefaultSupportedComponents);
        narrow.Remove(AppComponent.GroupAdminPolicy);

        var invitee = await NewInviteeAsync(narrow);

        var ex = Assert.Throws<AppComponentException>(
            () => MarmotGroupInvite.ValidateInvitee(group.Group, _cs, invitee.KeyPackage));

        Assert.Contains($"0x{AppComponent.GroupAdminPolicy:x4}", ex.Message);
        Assert.Contains("requires", ex.Message);
    }

    [Fact]
    public async Task AnInviteeNotAdvertisingTheRequiredProposalIsRefused()
    {
        var group = await NewGroupAsync();

        // Built without app_data_update and correctly SIGNED that way, rather
        // than by editing a valid leaf. Editing one breaks the leaf signature,
        // so the KeyPackage would be refused as malformed and this gate would
        // never run — a test that passes while asserting nothing about the rule
        // it names.
        KeyPackage invitee = await NonConformantKeyPackageAsync(proposalTypes: []);

        var ex = Assert.Throws<AppComponentException>(
            () => MarmotGroupInvite.ValidateInvitee(group.Group, _cs, invitee));

        Assert.Contains($"required proposal 0x{MarmotLeaf.AppDataUpdateProposalType:x4}", ex.Message);
    }

    [Fact]
    public async Task ALeafCarryingTheDictionaryAlwaysAdvertisesIt()
    {
        // There is no negative test for the required-EXTENSION gate, and this
        // is why: asking for a leaf that omits 0x0006 does not produce one.
        // CreateKeyPackage unions a carried leaf extension's type into the
        // advertised set — RFC 9420 §7.2 makes a leaf that carries an extension
        // it does not advertise invalid — and a Marmot leaf always carries the
        // app_data_dictionary. So the gate is unreachable for our own required
        // set rather than untested, and it stays because a group may one day
        // require an extension we do not carry.
        KeyPackage keyPackage = await NonConformantKeyPackageAsync(
            extensionTypes: [MarmotLeaf.RequiredCapabilitiesExtensionType]);

        Assert.Contains(
            Scramble.Marmot.AppComponents.AppDataDictionary.ExtensionType,
            keyPackage.LeafNode.Capabilities.Extensions);

        MarmotGroupInvite.ValidateInvitee((await NewGroupAsync()).Group, _cs, keyPackage);
    }

    /// <summary>
    /// A properly signed KeyPackage whose leaf advertises less than Marmot
    /// requires.
    /// </summary>
    /// <remarks>
    /// Goes through <c>MlsGroup.CreateKeyPackage</c> rather than the Marmot
    /// builder, which hardcodes the conformant sets — the point is a KeyPackage
    /// that is MLS-valid and Marmot-invalid, which is the only shape that
    /// reaches the capability gates.
    /// </remarks>
    private async Task<KeyPackage> NonConformantKeyPackageAsync(
        ushort[]? extensionTypes = null, ushort[]? proposalTypes = null)
    {
        var signer = new LocalSigner();
        var (sigPriv, sigPub) = _cs.GenerateSignatureKeyPair();

        AccountIdentityProof proof = await AccountIdentityProofSigning.CreateAsync(
            signer, _cs.Id, MarmotKeyPackageBuilder.Ed25519SignatureScheme, sigPub, Now);

        return MlsGroup.CreateKeyPackage(
            _cs,
            signer.AccountPublicKey.ToArray(),
            sigPriv,
            sigPub,
            out _,
            out _,
            supportedExtensionTypes: extensionTypes ?? [.. MarmotLeaf.ExtensionTypes],
            supportedProposalTypes: proposalTypes ?? [.. MarmotLeaf.ProposalTypes],
            leafExtensions: [MarmotLeaf.ToExtension(
                MarmotLeaf.BuildDictionary(MarmotLeaf.DefaultSupportedComponents, proof))],
            lifetime: KeyPackageLifetimePolicy.Create(Now));
    }

    [Fact]
    public async Task AnInviteeWhoseKeyPackageSignatureIsBrokenIsRefused()
    {
        var group = await NewGroupAsync();
        var invitee = await NewInviteeAsync();

        // The attack the MLS validation closes: a valid leaf, someone else's
        // init_key. Without it, the substituter receives the Welcome.
        var (_, otherInitKey) = _cs.GenerateHpkeKeyPair();
        invitee.KeyPackage.InitKey = otherInitKey;

        var ex = Assert.Throws<AppComponentException>(
            () => MarmotGroupInvite.ValidateInvitee(group.Group, _cs, invitee.KeyPackage));

        Assert.Contains("KeyPackage is invalid", ex.Message);
    }

    [Fact]
    public async Task TheSameAccountTwiceInOneCommitIsRefused()
    {
        var group = await NewGroupAsync();
        var signer = new LocalSigner();

        var first = await MarmotKeyPackageBuilder.CreateAsync(_cs, signer, Now);
        var second = await MarmotKeyPackageBuilder.CreateAsync(_cs, signer, Now + 1);

        // Two leaves for one account is a duplicate, not a second device:
        // multi-device is a separate mechanism whose draft says its bytes must
        // not be implemented for interop yet.
        var ex = Assert.Throws<ArgumentException>(
            () => MarmotGroupInvite.Add(group.Group, _cs, [first.KeyPackage, second.KeyPackage]));

        Assert.Contains("twice", ex.Message);
    }

    [Fact]
    public async Task ABadInviteeLeavesTheGroupCompletelyUntouched()
    {
        var group = await NewGroupAsync();
        var good = await NewInviteeAsync();

        var narrow = new HashSet<ushort>(MarmotLeaf.DefaultSupportedComponents);
        narrow.Remove(AppComponent.GroupLifecycle);
        var bad = await NewInviteeAsync(narrow);

        Assert.Throws<AppComponentException>(
            () => MarmotGroupInvite.Add(group.Group, _cs, [good.KeyPackage, bad.KeyPackage]));

        // The epoch alone proves nothing here — CommitPublic stages rather than
        // advances, so a commit built before validation would leave the epoch at
        // 0 too. What distinguishes the two is the PENDING commit: if one is
        // left behind, a later MergePendingCommit applies a commit adding
        // somebody this group refused.
        Assert.False(group.Group.HasPendingCommit);
        Assert.Equal(0UL, group.Group.Epoch);

        MarmotGroupInvite.Add(group.Group, _cs, [good.KeyPackage]).Applied();
        Assert.Equal(1UL, group.Group.Epoch);
    }

    [Fact]
    public async Task StagingLeavesAPendingCommitAndFinishingItClearsIt()
    {
        var group = await NewGroupAsync();
        var invitee = await NewInviteeAsync();

        var staged = MarmotGroupInvite.Add(group.Group, _cs, [invitee.KeyPackage]);
        Assert.True(group.Group.HasPendingCommit);

        staged.Applied();
        Assert.False(group.Group.HasPendingCommit);

        // Discard is the other way to finish one. Leaving it unfinished is what
        // blocks the next invite.
        var second = await NewInviteeAsync();
        MarmotGroupInvite.Add(group.Group, _cs, [second.KeyPackage]).Discard();
        Assert.False(group.Group.HasPendingCommit);
    }

    [Fact]
    public async Task AddingNobodyIsRefused()
    {
        var group = await NewGroupAsync();

        Assert.Throws<ArgumentException>(() => MarmotGroupInvite.Add(group.Group, _cs, []));
    }

    // ---- The invitee actually joins ----

    [Fact]
    public async Task TheInviteeCanProcessTheWelcomeAndReachTheSameEpoch()
    {
        var group = await NewGroupAsync();
        var invitee = await NewInviteeAsync();

        var staged = MarmotGroupInvite.Add(group.Group, _cs, [invitee.KeyPackage]);
        staged.Applied();

        // The end-to-end check the rest of this file only approaches: the
        // Welcome is openable with the private material the KeyPackage builder
        // retained, which is what proves the bundle and the published bytes
        // belong to each other.
        var joined = MlsGroup.ProcessWelcome(
            _cs,
            staged.Welcome,
            invitee.KeyPackage,
            invitee.PrivateMaterial.InitPrivateKey,
            invitee.PrivateMaterial.LeafPrivateKey,
            invitee.PrivateMaterial.SignaturePrivateKey);

        Assert.Equal(group.Group.Epoch, joined.Epoch);
        Assert.Equal(group.GroupId, joined.GroupId);

        // And the joiner sees the same Marmot state, which is what makes it one
        // group rather than two that happen to share an id.
        Assert.Equal(group.Required, MarmotGroupBuilder.ValidateCreated(joined, "joined group"));
    }
}
