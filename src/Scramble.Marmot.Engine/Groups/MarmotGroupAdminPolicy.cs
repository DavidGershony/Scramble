using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Nostr.Crypto;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;
using MarmotUpdate = Scramble.Marmot.AppComponents.AppDataUpdate;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>
/// Changing a group's admin set — the one component this engine can rewrite.
/// </summary>
/// <remarks>
/// <para>
/// Until this existed a group's app-data dictionary was whatever creation gave
/// it, permanently: the engine could <i>validate</i> an <c>AppDataUpdate</c>
/// from every direction — <see cref="AppComponentIntegrity"/> models one,
/// <see cref="CommitOrdering"/> classifies one, the leaf advertises proposal
/// type <c>0x0008</c> — and could not build one. This is the missing half, for
/// <c>0x8003</c> only.
/// </para>
/// <para>
/// <b>What would generalise, and what would not.</b> The mechanism is
/// component-agnostic: encode the component, put it in an
/// <see cref="AppDataUpdateProposal"/>, commit it inline, and check the
/// resulting dictionary against the operation. The <i>rules</i> are not.
/// Everything below the mechanism here — an empty set being unrecoverable, an
/// admin needing a member leaf, a removal of this component being invalid at
/// any privilege level, the committer having to be an active admin already —
/// belongs to admin policy alone. <c>0x8001</c> (profile) and <c>0x8004</c>
/// (routing) each have their own, and routing's is the sharper one: rewriting a
/// group's relays can strand the members who miss the commit, which no rule
/// here would notice. So this is deliberately not a general
/// <c>StageComponentUpdate(id, bytes)</c>; the generalisation is worth doing
/// when a second component actually needs it and its rules are written down.
/// </para>
/// <para>
/// The result is an ordinary <see cref="StagedCommit"/> and goes out through
/// <see cref="Session.MarmotSession.CommitAsync"/> like every other commit, so
/// it inherits publish-before-apply. There is no second commit path and there
/// must not be one: a governance change applied locally and never published is
/// a member who believes they changed who governs the group while nobody else
/// agrees.
/// </para>
/// </remarks>
public static class MarmotGroupAdminPolicy
{
    /// <summary>
    /// Stages a commit replacing the group's admin set, without applying it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole set is replaced rather than added to or subtracted from.
    /// <see cref="AdminPolicy"/> is a canonical sorted list whose bytes sit in
    /// signed group state, so "the admins are now exactly these" is the only
    /// statement that has one encoding; a delta would have to be resolved
    /// against a base every caller would have to agree about.
    /// </para>
    /// <para>
    /// <b>Removing yourself is allowed and is the supported way for an admin to
    /// leave.</b> <see cref="CommitAuthorization.RequireNoAdminSelfRemove"/>
    /// refuses a SelfRemove from a listed admin precisely so that this commit
    /// has to come first — while another active admin still remains to run the
    /// group afterwards, which <see cref="AdminPolicy.Create"/>'s non-empty rule
    /// and the membership check below together guarantee.
    /// </para>
    /// </remarks>
    /// <param name="group">The group, which stays at its current epoch.</param>
    /// <param name="cs">The group's ciphersuite.</param>
    /// <param name="admins">
    /// The account keys that will be admins. Canonicalised — order and
    /// duplicates in the argument do not reach the wire.
    /// </param>
    /// <exception cref="AppComponentException">
    /// The resulting admin set, or the commit carrying it, would not be valid.
    /// </exception>
    /// <exception cref="ArgumentException">The commit would change nothing.</exception>
    /// <exception cref="InvalidOperationException">
    /// A commit is already staged on this group.
    /// </exception>
    public static StagedCommit Stage(
        MlsGroup group, ICipherSuite cs, IReadOnlyList<byte[]> admins)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(admins);

        if (group.HasPendingCommit)
        {
            throw new InvalidOperationException(
                "This group already has a staged commit; apply or discard it before "
                + "changing the admin policy.");
        }

        // Canonicalises, and refuses an empty set. That refusal is the
        // depletion rule and it is not a formality: v1 has no succession and no
        // promotion, so a group that reaches epoch N+1 with no admins has
        // frozen its membership and settings for good. There is no commit that
        // repairs it, because every commit that could is admin-gated.
        AdminPolicy policy = AdminPolicy.Create(admins);

        // Before the membership check, so a key that is not an account key at
        // all is named as such rather than reported as a non-member. The admin
        // set is compared as raw bytes everywhere it is *read* -- see
        // CommitAuthorization.RequireNoAdminSelfRemove on why both sides of that
        // comparison must be the same kind of value -- and this is the opposite
        // end: what we are about to *write* into signed state, where an entry
        // that is not a usable key could never authorise anything and could
        // never be taken out again except by another admin commit.
        foreach (byte[] admin in policy.Admins)
        {
            if (!Bip340.IsValidXOnlyPublicKey(admin))
            {
                throw new AppComponentException(
                    $"Admin {Convert.ToHexString(admin).ToLowerInvariant()} is not a valid "
                    + "x-only secp256k1 account key.");
            }
        }

        IReadOnlyList<byte[]> memberAccounts =
            [.. group.GetMembers().Select(member => member.identity)];

        // The coupling rule, in the direction this commit can break it: a listed
        // admin with no member leaf is a phantom that becomes active the moment
        // a matching leaf appears, with no commit any member observed granting
        // it. MarmotGroupBuilder refuses the same shape at creation.
        if (!policy.EveryAdminHasAMemberLeaf(memberAccounts))
        {
            throw new AppComponentException(
                "Every admin must hold a member leaf in this group; the commit would list "
                + "an admin who is not a member.");
        }

        MarmotDictionary current = DictionaryOf(group)
            ?? throw new AppComponentException(
                "The group has no app_data_dictionary extension, so it has no component "
                + "state to update.");

        byte[] currentBytes = current.Get(AppComponent.GroupAdminPolicy)
            ?? throw new AppComponentException(
                "The group carries no admin-policy component, so no member is authorized to "
                + "change one. A group in this state is frozen, not unrestricted.");

        // Admin-gated at the point of authorship as well as of classification.
        // CommitAuthorization makes this commit Privileged, which is what tells
        // every *recipient* to check the committer -- and a commit our own peers
        // will refuse is worse than a refusal here, because we would publish it,
        // apply it, and be alone in an epoch nobody accepted.
        byte[] committer = CommitterOf(group);
        if (!AdminPolicy.Decode(currentBytes).IsActiveAdmin(committer, memberAccounts))
        {
            throw new AppComponentException(
                "Only an active admin may change the admin policy.");
        }

        byte[] encoded = policy.Encode();

        // A commit that rewrites the component to the bytes already there still
        // advances the epoch, and it reads to every member as a governance
        // change that did nothing. Refused rather than sent: a settings screen
        // that saves an unchanged list should not be a group event. Rotating key
        // material with no governance change is MarmotSelfUpdate's job.
        if (currentBytes.AsSpan().SequenceEqual(encoded))
        {
            throw new ArgumentException(
                "The group's admin set is already exactly this.", nameof(admins));
        }

        // Update, never Remove. AdminPolicy says an AppDataUpdate remove
        // targeting 0x8003 is invalid -- nobody, admin included, can commit one,
        // because a group without the component has no authority to add anyone
        // and cannot be repaired. This path has no code that can express one:
        // the only proposal it builds is an Update, and the wire check below
        // refuses anything else before the commit is returned.
        var proposal = AppDataUpdateProposal.Update(AppComponent.GroupAdminPolicy, encoded);

        // The resulting GroupContext is not readable off a staged commit --
        // the library keeps the pending state private -- and applying ours to
        // look at it is the one thing publish-before-apply forbids. So the
        // commit is built twice: once on a throwaway copy that is merged and
        // read, and once on the caller's group, which does not move. The two
        // differ in fresh key material and not in the dictionary they produce,
        // which is the only part being read here.
        //
        // Deriving the resulting dictionary ourselves instead would make the
        // integrity check below circular: it would compare our operation
        // against a state computed from that same operation, and agree however
        // wrong both were. This way the state comes from the library's own
        // ApplyAppDataUpdates -- the code every peer runs on these bytes.
        MarmotDictionary resulting = Rehearse(group, cs, proposal);

        var (commit, welcome) = group.CommitPublic([proposal]);

        try
        {
            if (welcome is not null)
            {
                throw new InvalidOperationException(
                    "An admin-policy commit produced a Welcome, so it added a member. "
                    + "Refusing it: the commit is not what it claims to be.");
            }

            // Read back off the commit rather than trusting what was passed in.
            // Everything below judges the view a *peer* computes from these
            // bytes, so the view has to be built from the bytes.
            Commit framed = CommitOf(commit);
            AppDataUpdateProposal onWire = SoleAppDataUpdateOf(framed);

            if (onWire.ComponentId != AppComponent.GroupAdminPolicy
                || onWire.Operation != AppDataUpdateOperationType.Update
                || !onWire.Data.AsSpan().SequenceEqual(encoded))
            {
                throw new AppComponentException(
                    "The staged commit does not carry the admin-policy update it was built "
                    + "for.");
            }

            var view = new StagedCommitView(
                [
                    new StagedProposal(
                        CommitProposalKind.AppDataUpdate,
                        committer,
                        MarmotUpdate.Update(onWire.ComponentId, onWire.Data)),
                ],
                framed.Path is not null);

            // Classified the way a fork-recovery pass on the receiving side
            // classifies it, from the commit and the group whose proposal cache
            // resolves it. Privileged is what keeps a governance change from
            // being dropped in favour of a routine self-update that raced it.
            CommitOrderingPriority priority = CommitOrdering.PriorityOf(group, framed);
            if (priority != CommitOrderingPriority.Privileged)
            {
                throw new AppComponentException(
                    $"An admin-policy commit classified as {priority}, so a peer would not "
                    + "treat it as admin-gated.");
            }

            // Both rules, because either alone leaves half the door open: the
            // batch rule proves the operation itself is legal and decodes, the
            // integrity rule proves the resulting dictionary changed only what
            // the operation accounts for. The required set is the current
            // epoch's -- this commit does not touch 0x0001, and the batch rule
            // is what would notice if it somehow did.
            IReadOnlySet<ushort> currentRequired = current.ComponentList()
                ?? throw new AppComponentException(
                    "The group carries no app_components requirement list.");

            AppComponentIntegrity.ValidateUpdateBatch(view, currentRequired);
            AppComponentIntegrity.ValidateStagedCommit(view, current, resulting);
        }
        catch
        {
            // Nothing has been published, so clearing is free and leaving the
            // commit staged is not: it would block every later commit on this
            // group, including the caller's retry.
            group.ClearPendingCommit();
            throw;
        }

        // Nobody joins and nobody leaves. AffectedAccounts names the members a
        // commit adds or removes, and an admin grant is neither -- the admin
        // being granted was already in the group, which is what
        // EveryAdminHasAMemberLeaf above insists on.
        return new StagedCommit(group, commit, welcome, []);
    }

    /// <summary>
    /// Builds the same commit on a throwaway copy and returns the dictionary it
    /// produces.
    /// </summary>
    /// <remarks>
    /// The copy is merged, which is the only way to read a resulting
    /// GroupContext; the caller's group is untouched. The rehearsal also runs
    /// the Current-profile invariants over the resulting state, which is what
    /// catches a commit that is individually well-formed and leaves a group no
    /// peer would accept — the required set losing <c>0x8003</c>, say, or its
    /// entry decoding to something that is not an admin policy.
    /// </remarks>
    private static MarmotDictionary Rehearse(
        MlsGroup group, ICipherSuite cs, AppDataUpdateProposal proposal)
    {
        MlsGroup rehearsal = MlsGroup.Import(group.Export(), cs);

        rehearsal.CommitPublic([proposal]);
        rehearsal.MergePendingCommit();

        MarmotGroupBuilder.ValidateCreated(rehearsal, "staged admin-policy commit");

        return DictionaryOf(rehearsal)
            ?? throw new AppComponentException(
                "The staged commit's resulting GroupContext has no app_data_dictionary.");
    }

    /// <summary>The GroupContext's Marmot dictionary, or null.</summary>
    private static MarmotDictionary? DictionaryOf(MlsGroup group)
    {
        foreach (Extension extension in group.GroupContext.Extensions)
        {
            if (extension.ExtensionType == MarmotDictionary.ExtensionType)
                return MarmotDictionary.Decode(extension.ExtensionData);
        }

        return null;
    }

    /// <summary>Our own account identity, as the admin list spells it.</summary>
    /// <remarks>
    /// Ours by construction — we are about to author the commit — so unlike the
    /// inbound path there is no unresolvable case. A group where this could not
    /// be answered is one we are not a member of, and staging a commit on it was
    /// already impossible.
    /// </remarks>
    private static byte[] CommitterOf(MlsGroup group) =>
        group.GetMembers().First(member => member.leafIndex == group.MyLeafIndex).identity;

    private static Commit CommitOf(PublicMessage message)
    {
        if (message.Content.ContentType != ContentType.Commit)
        {
            throw new AppComponentException(
                $"A staged admin-policy commit framed {message.Content.ContentType}.");
        }

        return Commit.ReadFrom(new TlsReader(message.Content.Content));
    }

    /// <summary>
    /// The commit's single inline <c>AppDataUpdate</c>, or a refusal.
    /// </summary>
    /// <remarks>
    /// Inline, and exactly one. A second proposal of any kind would mean this
    /// commit does something beyond the admin change the caller asked for —
    /// upstream folds an admin grant into an invite commit, and that shape is a
    /// separate slice with its own rules, not something to accept here by
    /// accident.
    /// </remarks>
    private static AppDataUpdateProposal SoleAppDataUpdateOf(Commit commit)
    {
        if (commit.Proposals.Length != 1)
        {
            throw new AppComponentException(
                $"A staged admin-policy commit carries {commit.Proposals.Length} proposals; "
                + "it must carry exactly one.");
        }

        if (commit.Proposals[0] is not InlineProposal inline
            || inline.Proposal is not AppDataUpdateProposal update)
        {
            throw new AppComponentException(
                "A staged admin-policy commit's proposal is not an inline AppDataUpdate.");
        }

        return update;
    }
}
