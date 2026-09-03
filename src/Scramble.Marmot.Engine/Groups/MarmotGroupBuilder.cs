using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage;
using Scramble.Nostr.Crypto;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>
/// A newly created group and the material its creator holds.
/// </summary>
/// <param name="Group">The live MLS group, at epoch 0.</param>
/// <param name="GroupId">The MLS group id.</param>
/// <param name="Required">The negotiated required-component set.</param>
/// <param name="Admins">The initial admin account keys.</param>
/// <param name="Routing">
/// The group's transport address: the <c>nostr_group_id</c> its kind-445
/// messages are published under, and the relays they go to.
/// </param>
/// <param name="SignaturePrivateKey">
/// The creator's leaf signature private key. Held by the caller: the group
/// cannot sign anything without it, and it is not recoverable from MLS state.
/// </param>
public sealed record CreatedGroup(
    MlsGroup Group,
    byte[] GroupId,
    IReadOnlySet<ushort> Required,
    IReadOnlyList<byte[]> Admins,
    NostrRouting Routing,
    byte[] SignaturePrivateKey)
{
    /// <summary>Builds the durable Marmot-layer record beside the MLS state.</summary>
    public GroupRecord ToRecord(DateTimeOffset createdAt) =>
        new(
            new GroupId(GroupId),
            new EpochId(0),
            ProtocolProfile.Current,
            createdAt,
            createdAt)
        {
            // The creator is present from epoch 0, so nothing before it is
            // missing. A join sets this to the epoch the Welcome admitted us at.
            JoinEpoch = new EpochId(0),

            // One leaf, and we just built its proof. Nothing to re-verify.
            ValidatedTree = true,
        };
}

/// <summary>
/// Creates Current-profile Marmot groups.
/// </summary>
/// <remarks>
/// <para>
/// Group creation is where the app components stop being a library and become
/// state a group is governed by. Three things go into the initial GroupContext
/// and all three must be right at epoch 0, because none of them can be repaired
/// later without an authorised commit: <c>required_capabilities</c>, the
/// <c>app_data_dictionary</c>'s requirement list, and the state of every
/// component that list names.
/// </para>
/// <para>
/// <b>This slice creates a group containing only its creator.</b> Adding
/// invitees needs a fetched KeyPackage to be trustworthy, and it is not yet —
/// <c>dotnet-mls</c> exposes no way to verify a KeyPackage or LeafNode
/// signature (see <see cref="KeyPackagePublicationValidator"/>). Shipping
/// invite before that closes would mean trusting a leaf we cannot check, so the
/// negotiation and admin-coupling rules are built and tested here against
/// member component sets, ready for the caller that will supply real invitees.
/// </para>
/// </remarks>
public static class MarmotGroupBuilder
{
    /// <summary>
    /// Creates a group with the caller as its only member and first admin.
    /// </summary>
    /// <param name="cs">The ciphersuite.</param>
    /// <param name="signer">Signs the creator's account-identity proof.</param>
    /// <param name="name">Group name.</param>
    /// <param name="description">Group description.</param>
    /// <param name="now">Unix seconds, for the creator's proof.</param>
    /// <param name="additionalAdmins">
    /// Co-admins beyond the creator. Each must be a valid x-only secp256k1
    /// account key <b>and</b> a member of the group being created — which, with
    /// no invitees, means this must be empty or contain only the creator.
    /// </param>
    /// <param name="supportedComponents">
    /// What this client can honour. Defaults to
    /// <see cref="MarmotLeaf.DefaultSupportedComponents"/>.
    /// </param>
    /// <param name="relays">
    /// Relays this group's messages live on. Required: a group with no relays
    /// cannot be reached, and the routing component has nowhere to point.
    /// </param>
    /// <param name="groupId">A specific group id, or null to generate one.</param>
    /// <param name="transportGroupId">
    /// The 32-byte <c>nostr_group_id</c>, or null to generate one. It is public
    /// and must be random — deriving it from the MLS group id would let a relay
    /// link the two.
    /// </param>
    /// <exception cref="AppComponentException">
    /// The resulting group would not satisfy the Current-profile invariants.
    /// </exception>
    public static async Task<CreatedGroup> CreateAsync(
        ICipherSuite cs,
        IAccountIdentityProofSigner signer,
        string name,
        string description,
        ulong now,
        IReadOnlyList<string>? relays = null,
        IEnumerable<byte[]>? additionalAdmins = null,
        IReadOnlySet<ushort>? supportedComponents = null,
        byte[]? groupId = null,
        byte[]? transportGroupId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);

        byte[] identity = signer.AccountPublicKey.ToArray();
        RequireAccountKey(identity, "creator");

        IReadOnlySet<ushort> supported = supportedComponents ?? MarmotLeaf.DefaultSupportedComponents;
        IReadOnlySet<ushort> creatorAdvertised = MarmotLeaf.AdvertisedComponents(supported);

        // Only the creator is a member, so the negotiation has one input. It
        // still runs: the mandatory-component guard is what catches a client
        // configured with a support set too narrow to create a usable group,
        // and finding that here beats finding it when nobody can join.
        IReadOnlySet<ushort> required = MarmotGroupProfile.Negotiate(
            MarmotGroupProfile.DefaultComponents,
            [new MarmotGroupProfile.MemberComponents("creator", creatorAdvertised)]);

        NostrRouting routing = NostrRouting.Create(
            transportGroupId ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(
                NostrRouting.TransportGroupIdLength),
            relays ?? throw new ArgumentNullException(
                nameof(relays), "A group must name at least one relay."));

        IReadOnlyList<byte[]> admins = BuildAdminSet(identity, additionalAdmins);

        // Every admin must be an account of a member of the group. Without this
        // a group can be created naming an admin who is not in it — a phantom
        // that becomes active the moment a matching leaf appears, with no commit
        // any member observed granting it. Checked against the projected member
        // set, because no group exists yet to check against.
        RequireAdminsAreMembers(admins, [identity]);

        ushort signatureScheme = MarmotKeyPackageBuilder.SignatureSchemeOf(cs.Id);
        var (signaturePrivateKey, signaturePublicKey) = cs.GenerateSignatureKeyPair();

        AccountIdentityProof proof = await AccountIdentityProofSigning.CreateAsync(
            signer, cs.Id, signatureScheme, signaturePublicKey, now, ct).ConfigureAwait(false);

        MarmotDictionary leafDictionary = MarmotLeaf.BuildDictionary(supported, proof);
        MarmotDictionary groupDictionary = MarmotGroupProfile.BuildDictionary(
            required, name, description, admins, routing);

        // Validated before the group exists, not after. Everything below is
        // irreversible from the caller's point of view — MLS state gets written
        // — so an invariant violation must surface while nothing has happened.
        CurrentProfile.Validate(ViewOf(groupDictionary), "new group");

        var groupExtensions = new[]
        {
            MarmotGroupProfile.BuildRequiredCapabilities().ToExtension(),
            new Extension(MarmotDictionary.ExtensionType, groupDictionary.Encode()),
        };

        MlsGroup group = MlsGroup.CreateGroup(
            cs,
            identity,
            signaturePrivateKey,
            signaturePublicKey,
            groupId,
            groupExtensions,
            config: MarmotGroupSettings.Create(),
            leafExtensions: [MarmotLeaf.ToExtension(leafDictionary)],
            supportedExtensionTypes: MarmotLeaf.ExtensionTypes.ToArray(),
            supportedProposalTypes: MarmotLeaf.ProposalTypes.ToArray());

        // Re-read off the group we actually built rather than trusting what we
        // asked for. The library unions extension types into leaf capabilities
        // and could in principle reshape the context; validating the request and
        // assuming the result is how a group that fails at every peer gets
        // created locally without complaint.
        ValidateCreated(group, "created group");

        return new CreatedGroup(
            group,
            group.GroupId.ToArray(),
            required,
            admins,
            routing,
            signaturePrivateKey);
    }

    /// <summary>
    /// Validates a live group's GroupContext against the Current-profile rules.
    /// </summary>
    /// <exception cref="AppComponentException">An invariant does not hold.</exception>
    public static IReadOnlySet<ushort> ValidateCreated(MlsGroup group, string what = "group")
    {
        ArgumentNullException.ThrowIfNull(group);

        Extension[] extensions = group.GroupContext.Extensions;

        RequiredCapabilities capabilities = RequiredCapabilities.FromExtensions(extensions)
            ?? throw new AppComponentException(
                $"Invalid Current-profile {what}: no required_capabilities extension.");

        MarmotDictionary? dictionary = null;
        foreach (var extension in extensions)
        {
            if (extension.ExtensionType == MarmotDictionary.ExtensionType)
                dictionary = MarmotDictionary.Decode(extension.ExtensionData);
        }

        if (dictionary is null)
        {
            throw new AppComponentException(
                $"Invalid Current-profile {what}: no app_data_dictionary extension.");
        }

        return CurrentProfile.Validate(
            new GroupContextView(
                new HashSet<ushort>(capabilities.ExtensionTypes),
                new HashSet<ushort>(capabilities.ProposalTypes),
                dictionary),
            what);
    }

    private static GroupContextView ViewOf(MarmotDictionary dictionary)
    {
        RequiredCapabilities capabilities = MarmotGroupProfile.BuildRequiredCapabilities();
        return new GroupContextView(
            new HashSet<ushort>(capabilities.ExtensionTypes),
            new HashSet<ushort>(capabilities.ProposalTypes),
            dictionary);
    }

    private static IReadOnlyList<byte[]> BuildAdminSet(
        byte[] creator, IEnumerable<byte[]>? additionalAdmins)
    {
        // The creator is an admin by construction. A group whose creator is not
        // an admin cannot grant anyone else admin, so it is frozen at birth.
        var admins = new List<byte[]> { creator };

        foreach (byte[] admin in additionalAdmins ?? [])
        {
            ArgumentNullException.ThrowIfNull(admin);

            // Validated as a real curve point, not merely 32 bytes. An admin
            // entry that is not a usable account key can never authorise
            // anything, and it would sit in signed group state forever.
            RequireAccountKey(admin, "admin");

            if (!admins.Any(existing => existing.AsSpan().SequenceEqual(admin)))
                admins.Add(admin);
        }

        return admins;
    }

    private static void RequireAdminsAreMembers(
        IReadOnlyList<byte[]> admins, IReadOnlyList<byte[]> memberAccounts)
    {
        foreach (byte[] admin in admins)
        {
            if (!memberAccounts.Any(member => member.AsSpan().SequenceEqual(admin)))
            {
                throw new AppComponentException(
                    $"Admin {Convert.ToHexString(admin).ToLowerInvariant()} is not a member of " +
                    "the group being created, so it would be a phantom admin no commit granted.");
            }
        }
    }

    private static void RequireAccountKey(byte[] key, string what)
    {
        if (key.Length != 32 || !Bip340.IsValidXOnlyPublicKey(key))
        {
            throw new ArgumentException(
                $"The {what} key is not a valid x-only secp256k1 account key.", nameof(key));
        }
    }
}
