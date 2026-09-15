using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Session;
using Scramble.Marmot.Identity;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>
/// Any commit, staged but not yet applied.
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
/// <para>
/// <b>Dispose it.</b> Between staging and publishing, the code crosses
/// transport and storage boundaries, and an exception in there used to strand
/// the commit — blocking every later commit on that group, including whatever
/// retry the caller attempted. A <c>using</c> closes that window. Once
/// <see cref="Publishing"/> is called disposal stops cleaning up, because from
/// that point a peer may hold the commit and clearing it locally is what causes
/// a fork rather than what prevents one.
/// </para>
/// </remarks>
public sealed class StagedCommit : IDisposable
{
    private readonly MlsGroup _group;
    private readonly GroupJournal? _journal;
    private State _state = State.Staged;

    internal StagedCommit(
        MlsGroup group,
        PublicMessage commit,
        Welcome? welcome,
        IReadOnlyList<byte[]> affectedAccounts)
    {
        // A class with an internal constructor rather than a positional record:
        // a record's primary constructor is public, and one built through it
        // would carry no group and throw from Applied() at the worst possible
        // moment — after the commit is already on a relay.
        _group = group;
        Commit = commit;
        Welcome = welcome;
        AffectedAccounts = affectedAccounts;

        // Read here and nowhere later. The class comes off the proposal cache
        // the commit's references resolve against, and Applied() clears it --
        // so a property that computed this on demand would answer correctly
        // until the one moment anybody needs it, then answer Ordinary forever.
        OrderingPriority = CommitOrdering.PriorityOf(group, commit)
            ?? throw new InvalidOperationException(
                "A staged commit did not frame a commit. This is a bug in the caller "
                + "that built it, not something the wire can cause.");

        // Also read here and nowhere later, for the same reason as the class
        // above: the group is still at the old epoch while the commit is
        // staged, so a property computing this on demand would answer
        // correctly right up until Applied(), then start naming the epoch
        // after the one this commit produces. CommitPublisher writes it into
        // the durable publish record, and a record naming the wrong epoch
        // tells a recovering session the commit has not landed when it has.
        NewEpoch = new EpochId(checked(group.Epoch + 1));

        // Only for a group a session has taken durable custody of; an unbound
        // group behaves exactly as it did before this existed.
        //
        // The derivation merges the commit into `group`, because that is the
        // only way to compute the state it produces -- see GroupJournal.Prepare
        // -- and the session's answer is to stop pointing at this instance
        // until the publish is confirmed. So `group` is a throwaway from here
        // on and the session's live group is a fresh import of the state it
        // held before. Publish-before-apply is intact: what the session shows
        // its caller is still the old epoch.
        _journal = GroupJournal.For(group);
        PreparedState = _journal?.Prepare(group, Commit, NewEpoch, OrderingPriority);
    }

    /// <summary>The commit, framed as a PublicMessage.</summary>
    public PublicMessage Commit { get; }

    /// <summary>
    /// The ordering class of this commit, computed while it was still staged.
    /// </summary>
    /// <remarks>
    /// Convergence ranks branches by the class of the commit that ends them,
    /// and priority outranks both the committer and the digest — so a member
    /// that cannot say what class its own tip was has deleted the rule rather
    /// than being conservative about it. This is the only moment the answer
    /// exists for a commit of ours: the caller archives it alongside the epoch
    /// the commit produces.
    /// </remarks>
    public CommitOrderingPriority OrderingPriority { get; }

    /// <summary>The epoch the group reaches once this commit is applied.</summary>
    public EpochId NewEpoch { get; }

    /// <summary>
    /// The exported state this commit produces, or null for a group no session
    /// owns.
    /// </summary>
    /// <remarks>
    /// <b>Null is the pre-session behaviour and it is the unrecoverable one.</b>
    /// A crash between publishing this commit and applying it leaves no way to
    /// reach the epoch the rest of the group has moved to: MLS refuses to let a
    /// member process a commit it authored, so our own bytes coming back off a
    /// relay are no help. Non-null means the state was written down before the
    /// bytes left, which is the only form recovery can take.
    /// </remarks>
    public byte[]? PreparedState { get; }

    /// <summary>
    /// The Welcome for the added members, or null when nobody was added.
    /// </summary>
    /// <remarks>
    /// Null is the expected shape for a removal or a self-update — nobody is
    /// being admitted, so there is nothing to admit them with. For an add it is
    /// never null:
    /// <see cref="MarmotGroupInvite.Add"/> refuses a commit that added members
    /// and produced none, because those members would be in the tree and unable
    /// to derive a single group secret.
    /// </remarks>
    public Welcome? Welcome { get; }

    /// <summary>
    /// The account keys this commit adds or removes, in the order given.
    /// </summary>
    /// <remarks>
    /// Empty for a commit that changes no membership — a self-update rotates
    /// the committer's own leaf and affects nobody else.
    /// </remarks>
    public IReadOnlyList<byte[]> AffectedAccounts { get; }

    /// <summary>
    /// Applies the commit, advancing the group to the new epoch.
    /// </summary>
    /// <remarks>
    /// <para>Call only once the commit is durably published.</para>
    /// <para>
    /// <b>A prepared commit has already been merged</b> — deriving the state it
    /// produces is what merged it — so there is nothing left to do to the MLS
    /// object and this only closes the state machine. Confirming it durably,
    /// and pointing the session's live group at the new state, is the session's
    /// half: this class holds no storage and never has.
    /// </para>
    /// </remarks>
    public void Applied()
    {
        if (PreparedState is null)
            _group.MergePendingCommit();

        _state = State.Resolved;
    }

    /// <summary>
    /// Abandons the commit, leaving the group where it was.
    /// </summary>
    /// <remarks>
    /// For a publish that provably failed. If it may have succeeded, discarding
    /// is the wrong move: a peer that received it has advanced, and this group
    /// would be the one left behind.
    /// </remarks>
    public void Discard()
    {
        _group.ClearPendingCommit();

        // A prepared commit leaves a row describing a state the group is not
        // going to reach, and a later session that read it without asking what
        // happened to the commit would adopt an epoch nobody else has. Dropped
        // here rather than left for recovery, because a discard is a caller
        // saying the commit provably never landed.
        _journal?.Abandon();

        _state = State.Resolved;
    }

    /// <summary>
    /// Declares that publishing has begun, so disposal will no longer clean up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Call this immediately before handing the commit to a relay.</b> Up to
    /// that moment the commit exists only here and clearing it is free; after
    /// it, a peer may already hold it, and clearing it locally is the one move
    /// that guarantees a fork — everyone else advances and we stay behind,
    /// convinced nothing happened.
    /// </para>
    /// <para>
    /// So this narrows what disposal is allowed to assume. A commit abandoned
    /// after this point is deliberately left staged rather than tidied away,
    /// because a stranded pending commit is recoverable and a silent fork is
    /// not.
    /// </para>
    /// <para>
    /// <b>This flag does not survive the process, and on its own it cannot.</b>
    /// A restart that finds a staged commit has no way to tell which side of
    /// this line it was on, and the two sides want opposite answers.
    /// <see cref="CommitPublisher"/> is what writes it down — call this through
    /// that rather than by hand, or the durable record and the in-memory flag
    /// will disagree in the one direction that forks the group.
    /// </para>
    /// </remarks>
    public void Publishing()
    {
        _state = State.Publishing;

        // Written down before the caller can send anything, because this is the
        // line recovery has to know which side of. Without it, the prepared
        // state above is indistinguishable from one belonging to a commit that
        // never left the device -- and those two want opposite answers.
        _journal?.HandedToTransport(CommitPublisher.CommitIdOf(Commit), NewEpoch);
    }

    /// <summary>
    /// Clears the commit if it was staged and never went anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window this closes: between staging and the publish attempt the code
    /// crosses transport and storage boundaries, and an exception there used to
    /// leave the group holding a pending commit forever — which blocks every
    /// later commit on that group, including the retry.
    /// </para>
    /// <para>
    /// It deliberately does nothing once <see cref="Publishing"/> has been
    /// called, and nothing after <see cref="Applied"/> or
    /// <see cref="Discard"/>. Disposal is a safety net for the case where
    /// nobody could have seen the commit, not a substitute for deciding what
    /// happened to one that might have been published.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_state == State.Staged)
        {
            _group.ClearPendingCommit();

            // Same reasoning as Discard, and the same narrow condition: only a
            // commit nobody could have seen. A crash gets no disposal at all,
            // which is why recovery does not rely on this -- it asks whether a
            // publish was ever attempted, and a prepared commit with no attempt
            // behind it is abandoned whether or not this ran.
            _journal?.Abandon();
        }

        _state = State.Resolved;
    }

    private enum State
    {
        /// <summary>Staged locally; nobody else can have seen it.</summary>
        Staged,

        /// <summary>Handed to a relay; its fate is the caller's to determine.</summary>
        Publishing,

        /// <summary>Applied or discarded. Nothing left to clean up.</summary>
        Resolved,
    }
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
    /// <remarks>
    /// Every refusal here that means <i>this invitee cannot join</i> carries
    /// <see cref="AppComponentRejection.MissingRequiredCapabilities"/>, so a
    /// caller can offer to drop them and retry without reading the message. The
    /// two that do not are not about the invitee's capabilities at all: an
    /// unverifiable KeyPackage is malformed input, and a group with no
    /// <c>required_capabilities</c> extension is our own state being wrong.
    /// Classifying those the same way would tell a caller to drop an invitee
    /// who is not at fault.
    ///
    /// <b>Which invitee</b> is the caller's own loop index — this takes exactly
    /// one KeyPackage — which is why no subject is carried. Contrast
    /// <see cref="MarmotGroupProfile.Negotiate"/>, which sees every member at
    /// once and must name one.
    /// </remarks>
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
        //
        // Both gates are UNTESTED, and so are their reasons — deleting either
        // check, or dropping its reason, leaves the suite green. Not an
        // oversight: `MlsGroup.CreateKeyPackage` takes no version or ciphersuite
        // argument, so a correctly signed leaf that omits one cannot be built,
        // and editing a valid leaf breaks its signature so the KeyPackage is
        // refused as malformed before either gate runs. Same reason the
        // required-EXTENSION gate below has no negative test. They are written
        // from the RFC, not from a failure anyone has seen here.
        if (!leaf.Capabilities.Versions.Contains(ProtocolVersion.Mls10))
        {
            throw new AppComponentException(
                "The invitee does not advertise MLS 1.0.",
                AppComponentRejection.MissingRequiredCapabilities);
        }

        if (!leaf.Capabilities.CipherSuites.Contains(cs.Id))
        {
            throw new AppComponentException(
                $"The invitee does not advertise ciphersuite 0x{cs.Id:x4}.",
                AppComponentRejection.MissingRequiredCapabilities);
        }

        RequiredCapabilities required =
            RequiredCapabilities.FromExtensions(group.GroupContext.Extensions)
            ?? throw new AppComponentException("The group has no required_capabilities extension.");

        foreach (ushort extensionType in required.ExtensionTypes)
        {
            // Untested, and its reason with it — see
            // ALeafCarryingTheDictionaryAlwaysAdvertisesIt, which records why no
            // leaf omitting 0x0006 can be constructed. The proposal gate below
            // is the one that is actually exercised.
            if (!leaf.Capabilities.Extensions.Contains(extensionType))
            {
                throw new AppComponentException(
                    $"The invitee does not advertise required extension 0x{extensionType:x4}.",
                    AppComponentRejection.MissingRequiredCapabilities);
            }
        }

        foreach (ushort proposalType in required.ProposalTypes)
        {
            if (!leaf.Capabilities.Proposals.Contains(proposalType))
            {
                throw new AppComponentException(
                    $"The invitee does not advertise required proposal 0x{proposalType:x4}.",
                    AppComponentRejection.MissingRequiredCapabilities);
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
                    "which this group requires.",
                    AppComponentRejection.MissingRequiredCapabilities);
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
    public static StagedCommit Add(
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

        return new StagedCommit(group, commit, welcome, accounts);
    }

    /// <summary>
    /// Builds a commit removing members by account key, without applying it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Staged like an add, and for the same reason: a removal applied before it
    /// is published leaves the committer alone in an epoch the group never
    /// reaches, and now believing someone is gone who is not.
    /// </para>
    /// <para>
    /// Removing the last admin is <b>not</b> refused here. Admin authority is
    /// group state, and whether a commit may drop it belongs with the commit
    /// authorization rules rather than with the mechanics of building one —
    /// putting a second, weaker copy of that rule here would let the two
    /// disagree.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// An account is not a member, or the list is empty, or it would empty the
    /// group.
    /// </exception>
    public static StagedCommit Remove(
        MlsGroup group, IReadOnlyList<byte[]> accounts)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(accounts);

        if (accounts.Count == 0)
            throw new ArgumentException("There is nobody to remove.", nameof(accounts));

        var members = group.GetMembers();
        var proposals = new List<Proposal>(accounts.Count);
        var removed = new List<byte[]>(accounts.Count);

        foreach (byte[] account in accounts)
        {
            ArgumentNullException.ThrowIfNull(account);

            uint? leafIndex = null;
            foreach (var (index, identity) in members)
            {
                if (identity.AsSpan().SequenceEqual(account))
                {
                    leafIndex = index;
                    break;
                }
            }

            if (leafIndex is null)
            {
                throw new ArgumentException(
                    $"Account {Convert.ToHexString(account).ToLowerInvariant()} is not a member.",
                    nameof(accounts));
            }

            if (removed.Any(existing => existing.AsSpan().SequenceEqual(account)))
            {
                throw new ArgumentException(
                    $"Account {Convert.ToHexString(account).ToLowerInvariant()} appears twice.",
                    nameof(accounts));
            }

            proposals.Add(group.ProposeRemove(leafIndex.Value));
            removed.Add(account);
        }

        if (removed.Count >= members.Count)
        {
            // An empty group cannot be committed to by anyone, so it can never
            // be repaired or left cleanly. Disband is a separate operation with
            // its own semantics.
            throw new ArgumentException(
                "Removing every member would leave a group nobody can commit to; disband it instead.",
                nameof(accounts));
        }

        var (commit, welcome) = group.CommitPublic(proposals);

        // A removal produces no Welcome — nobody is being admitted — so unlike
        // Add, a null here is the expected shape rather than a failure.
        return new StagedCommit(group, commit, welcome, removed);
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
