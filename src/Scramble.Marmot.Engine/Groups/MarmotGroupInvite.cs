using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>
/// A commit that adds members, staged but not yet applied.
/// </summary>
/// <remarks>
/// <para>
/// <b>The group is still at the old epoch when this is returned, and that is the
/// point.</b> A commit applied locally and then never published forks the
/// committer from everyone else: they advance to an epoch nobody can reach, and
/// every message they send afterwards is undecryptable by the group they think
/// they are in. So the order is publish, then apply — the mirror image of the
/// KeyPackage rule, where the private material is persisted <i>before</i> the
/// publish, and for the same underlying reason. Whichever step is unrecoverable
/// goes second.
/// </para>
/// <para>
/// The caller must finish it: <see cref="Applied"/> once a relay has the commit,
/// or <see cref="Discard"/> if publishing failed for good. Leaving it unfinished
/// leaves a pending commit on the group, which blocks the next one.
/// </para>
/// </remarks>
public sealed class StagedInvite
{
    private readonly MlsGroup _group;

    internal StagedInvite(
        MlsGroup group,
        PublicMessage commit,
        Welcome welcome,
        IReadOnlyList<byte[]> addedAccounts)
    {
        // A class with an internal constructor rather than a positional record:
        // a record's primary constructor is public, and one built through it
        // would carry no group and throw from Applied() at the worst possible
        // moment — after the commit is already on a relay.
        _group = group;
        Commit = commit;
        Welcome = welcome;
        AddedAccounts = addedAccounts;
    }

    /// <summary>The commit, framed as a PublicMessage.</summary>
    public PublicMessage Commit { get; }

    /// <summary>The Welcome for the added members.</summary>
    public Welcome Welcome { get; }

    /// <summary>The account keys added, in the order given.</summary>
    public IReadOnlyList<byte[]> AddedAccounts { get; }

    /// <summary>
    /// Applies the commit, advancing the group to the new epoch.
    /// </summary>
    /// <remarks>Call only once the commit is durably published.</remarks>
    public void Applied() => _group.MergePendingCommit();

    /// <summary>
    /// Abandons the commit, leaving the group where it was.
    /// </summary>
    /// <remarks>
    /// For a publish that provably failed. If it may have succeeded, discarding
    /// is the wrong move: a peer that received it has advanced, and this group
    /// would be the one left behind.
    /// </remarks>
    public void Discard() => _group.ClearPendingCommit();
}

/// <summary>
/// Adding members to an existing Marmot group.
/// </summary>
/// <remarks>
/// <para>
/// The MLS library validates almost nothing about an added leaf. It has a
/// <c>ValidateAddLeafCapabilities</c> helper for RFC 9420 §12.1.1 and <b>never
/// calls it</b>, and it has no notion at all of the group's required app
/// components. So every check below has to happen here, before the Add proposal
/// is built — and the cost of skipping one is not a local error but a member who
/// joins and then cannot honour state the group considers mandatory.
/// </para>
/// <para>
/// Granting admin is deliberately not part of this. Upstream couples an
/// admin-policy <c>AppDataUpdate</c> into the same commit for
/// invite-with-admin-grant, which needs the proposal wired through
/// <see cref="AppComponentIntegrity"/>; that is its own slice, and doing it
/// badly means an admin set no member observed being granted.
/// </para>
/// </remarks>
public static class MarmotGroupInvite
{
    /// <summary>
    /// Checks one KeyPackage against what a group requires of a new member.
    /// </summary>
    /// <param name="group">The group being joined.</param>
    /// <param name="cs">The group's ciphersuite.</param>
    /// <param name="keyPackage">The invitee's KeyPackage.</param>
    /// <returns>The invitee's account key.</returns>
    /// <exception cref="AppComponentException">The invitee cannot join this group.</exception>
    public static byte[] ValidateInvitee(MlsGroup group, ICipherSuite cs, KeyPackage keyPackage)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(keyPackage);

        // MLS validity first. Everything below reads fields off the leaf, and
        // reading them from an unverified KeyPackage is reading attacker-chosen
        // values — including the credential this returns as a member identity.
        try
        {
            MlsGroup.ValidateKeyPackage(cs, keyPackage);
        }
        catch (InvalidOperationException ex)
        {
            throw new AppComponentException($"The invitee's KeyPackage is invalid: {ex.Message}");
        }

        LeafNode leaf = keyPackage.LeafNode;

        // RFC 9420 §12.1.1. The library defines this check and never runs it, so
        // a leaf advertising neither our version nor our ciphersuite would be
        // added and then be unable to process anything.
        if (!leaf.Capabilities.Versions.Contains(ProtocolVersion.Mls10))
            throw new AppComponentException("The invitee does not advertise MLS 1.0.");

        if (!leaf.Capabilities.CipherSuites.Contains(cs.Id))
        {
            throw new AppComponentException(
                $"The invitee does not advertise ciphersuite 0x{cs.Id:x4}.");
        }

        RequiredCapabilities required =
            RequiredCapabilities.FromExtensions(group.GroupContext.Extensions)
            ?? throw new AppComponentException("The group has no required_capabilities extension.");

        foreach (ushort extensionType in required.ExtensionTypes)
        {
            if (!leaf.Capabilities.Extensions.Contains(extensionType))
            {
                throw new AppComponentException(
                    $"The invitee does not advertise required extension 0x{extensionType:x4}.");
            }
        }

        foreach (ushort proposalType in required.ProposalTypes)
        {
            if (!leaf.Capabilities.Proposals.Contains(proposalType))
            {
                throw new AppComponentException(
                    $"The invitee does not advertise required proposal 0x{proposalType:x4}.");
            }
        }

        // The Marmot half, which MLS knows nothing about. A member who does not
        // advertise a component the group requires would join and then be unable
        // to honour state everyone else treats as mandatory — the group would
        // look healthy and behave inconsistently.
        IReadOnlySet<ushort> groupRequires = MarmotGroupBuilder.ValidateCreated(group, "group");
        IReadOnlySet<ushort> advertised = AdvertisedComponentsOf(leaf);

        foreach (ushort componentId in groupRequires)
        {
            if (!advertised.Contains(componentId))
            {
                throw new AppComponentException(
                    $"The invitee does not advertise app component 0x{componentId:x4}, " +
                    "which this group requires.");
            }
        }

        return CredentialIdentityOf(leaf);
    }

    /// <summary>
    /// Builds a commit adding <paramref name="invitees"/>, without applying it.
    /// </summary>
    /// <remarks>
    /// Every invitee is validated before any proposal is built, so a bad one in
    /// the list leaves the group entirely untouched rather than half-staged.
    /// </remarks>
    /// <exception cref="AppComponentException">An invitee cannot join.</exception>
    /// <exception cref="ArgumentException">The list is empty, or names the same account twice.</exception>
    public static StagedInvite Add(
        MlsGroup group, ICipherSuite cs, IReadOnlyList<KeyPackage> invitees)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(invitees);

        if (invitees.Count == 0)
            throw new ArgumentException("There is nobody to add.", nameof(invitees));

        var accounts = new List<byte[]>(invitees.Count);
        foreach (KeyPackage keyPackage in invitees)
        {
            byte[] account = ValidateInvitee(group, cs, keyPackage);

            // Two leaves for one account in a single commit is a duplicate, not
            // a second device: multi-device is a separate mechanism and its
            // draft says its bytes must not be implemented for interop yet.
            if (accounts.Any(existing => existing.AsSpan().SequenceEqual(account)))
            {
                throw new ArgumentException(
                    $"Account {Convert.ToHexString(account).ToLowerInvariant()} appears twice.",
                    nameof(invitees));
            }

            accounts.Add(account);
        }

        List<Proposal> proposals = group.ProposeAdd([.. invitees]);
        var (commit, welcome) = group.CommitPublic(proposals);

        if (welcome is null)
        {
            // Unreachable for a commit carrying Add proposals, and worth saying
            // so rather than returning a null nobody checks: without the Welcome
            // the members are in the tree and can never derive the group secrets.
            group.ClearPendingCommit();
            throw new InvalidOperationException(
                "The commit added members but produced no Welcome.");
        }

        return new StagedInvite(group, commit, welcome, accounts);
    }

    /// <summary>
    /// The app components a leaf advertises, or an empty set.
    /// </summary>
    private static IReadOnlySet<ushort> AdvertisedComponentsOf(LeafNode leaf)
    {
        MarmotDictionary? dictionary;
        try
        {
            dictionary = MarmotLeaf.ReadDictionary(leaf);
        }
        catch (AppComponentException ex)
        {
            throw new AppComponentException(
                $"The invitee's leaf app_data_dictionary is malformed: {ex.Message}");
        }

        return dictionary?.ComponentList() ?? new HashSet<ushort>();
    }

    private static byte[] CredentialIdentityOf(LeafNode leaf)
    {
        if (leaf.Credential is not BasicCredential credential)
        {
            throw new AppComponentException(
                "The invitee's credential is not a BasicCredential.");
        }

        byte[] identity = credential.Identity;
        if (identity.Length != 32 || !Nostr.Crypto.Bip340.IsValidXOnlyPublicKey(identity))
        {
            throw new AppComponentException(
                "The invitee's credential identity is not a valid x-only secp256k1 public key.");
        }

        return identity;
    }
}
