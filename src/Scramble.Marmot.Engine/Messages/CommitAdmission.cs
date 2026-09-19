using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Engine.Messages;

/// <summary>
/// A commit this member refuses to apply, whoever else applied it.
/// </summary>
/// <remarks>
/// Distinct from every other failure on the ingest path, and that is the point:
/// a commit we could not decrypt may become applicable when something else
/// arrives, while a commit its author was not allowed to make never will. The
/// two have opposite retry behaviour, so they must not share a type.
/// </remarks>
public sealed class UnauthorizedCommitException : Exception
{
    /// <summary>A commit that is not permitted.</summary>
    public UnauthorizedCommitException(string message)
        : base(message)
    {
    }

    /// <summary>A commit refused by a component rule.</summary>
    public UnauthorizedCommitException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A commit that passed the authorization half, holding what the integrity half
/// still needs.
/// </summary>
/// <remarks>
/// The split is forced by where the two rules can be answered. Authorization is
/// a question about the epoch the commit was <i>built on</i> — who held admin
/// authority then, and what the commit's references resolved to — and every bit
/// of that is gone the moment the commit applies. Integrity is a question about
/// the epoch it <i>produces</i>, which does not exist until it has been applied
/// to something. So one runs before and one after, against the same view read
/// once, and this object is what carries that view across the apply.
/// </remarks>
public sealed class AdmittedCommit
{
    private readonly StagedCommitView _view;
    private readonly MarmotDictionary _current;
    private readonly IReadOnlySet<ushort> _currentRequired;

    internal AdmittedCommit(
        StagedCommitView view, MarmotDictionary current, IReadOnlySet<ushort> currentRequired)
    {
        _view = view;
        _current = current;
        _currentRequired = currentRequired;
    }

    /// <summary>
    /// Checks the component state a commit actually produced against what it
    /// proposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><paramref name="applied"/> must be a throwaway copy, never the live
    /// group.</b> The whole rule is that a refused commit leaves the member
    /// where it was; running this on the group a caller intends to keep would
    /// report the violation from inside the state it was supposed to prevent.
    /// </para>
    /// <para>
    /// <b>The resulting dictionary is read off the applied group and is never
    /// derived here.</b> Deriving it would make this circular — comparing the
    /// commit's operations against a state computed from those same operations,
    /// which agrees however wrong both are. <c>MarmotGroupAdminPolicy.Rehearse</c>
    /// records the same reasoning for the send side. What is read here is the
    /// output of the library's own <c>ApplyAppDataUpdates</c>: the code every
    /// peer runs on these bytes.
    /// </para>
    /// </remarks>
    /// <param name="applied">A throwaway copy with the commit applied.</param>
    /// <exception cref="UnauthorizedCommitException">The commit is not permitted.</exception>
    public void RequireIntegrity(MlsGroup applied)
    {
        ArgumentNullException.ThrowIfNull(applied);

        try
        {
            // Both rules, because either alone leaves half the door open: the
            // batch rule proves each operation decodes and is legal, the
            // integrity rule proves the resulting dictionary changed only what
            // some operation accounts for. An AppDataUpdate carrying corrupt
            // bytes is perfectly "update-backed", and a GroupContextExtensions
            // rewrite is backed by no operation at all.
            AppComponentIntegrity.ValidateUpdateBatch(_view, _currentRequired);
            AppComponentIntegrity.ValidateStagedCommit(
                _view, _current, CommitAdmission.DictionaryOf(applied));
        }
        catch (AppComponentException ex)
        {
            throw new UnauthorizedCommitException(
                $"The commit's resulting component state is not permitted: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Whether an inbound commit is one this member accepts at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>The receive half of the rule <c>MarmotGroupAdminPolicy</c> implements on
/// the send half.</b> MLS has no opinion here: a <c>GroupContextExtensions</c>
/// proposal is legal from any member, so the library accepts it and rewrites
/// the GroupContext wholesale — <c>app_data_dictionary</c> included, and the
/// admin policy <c>0x8003</c> with it. Without this, any member can hand us a
/// commit making themselves sole admin and we apply it; every later admin check
/// then passes, because it reads the dictionary they just rewrote.
/// </para>
/// <para>
/// <b>Authority is judged in the pre-commit epoch</b> — the epoch the committer
/// had to hold authority in. Judging it in the resulting epoch is the same
/// circularity in a different costume: a commit that grants its author admin
/// would authorise itself.
/// </para>
/// <para>
/// <b>What this deliberately does not refuse.</b> A guard that rejects
/// legitimate commits is worse than the hole it closes, because a stranded
/// member has no recovery path. So the non-admin shapes stay exactly the two
/// <see cref="CommitAuthorization.IsAllowedNonAdminCommit"/> names — an ordinary
/// self-update and a SelfRemove-only commit — with no second, weaker copy of
/// that rule here; a commit framed against an epoch other than the one it is
/// being judged at is not judged at all, because the admin list it would be
/// measured against is not the one its author saw; and a commit that does not
/// apply to the probe is left to fail on its own terms rather than being
/// reclassified as a refusal.
/// </para>
/// </remarks>
public static class CommitAdmission
{
    /// <summary>
    /// Runs both halves against a commit, on a throwaway copy of the group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a caller with no probe of its own. The copy costs a second full
    /// commit application per inbound handshake, and that is the price of not
    /// deriving the resulting state: there is no shortcut that reads the
    /// resulting GroupContext without applying the commit, and every shortcut
    /// that computes it instead is the circular check this exists to avoid.
    /// </para>
    /// <para>
    /// <b>Silent for anything that is not a commit framed against this group's
    /// current epoch.</b> A proposal changes nothing, and a commit framed
    /// against another epoch belongs to a branch this group is not on — it will
    /// not apply here, and convergence judges it against the epoch it actually
    /// forks from, where its author's authority is the one they had.
    /// </para>
    /// </remarks>
    /// <param name="group">The live group, which is not touched.</param>
    /// <param name="cs">The group's ciphersuite, for the throwaway copy.</param>
    /// <param name="handshake">The peeled handshake message.</param>
    /// <exception cref="UnauthorizedCommitException">The commit is not permitted.</exception>
    public static void Require(MlsGroup group, ICipherSuite cs, PublicMessage handshake)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(handshake);

        if (handshake.Content.ContentType != ContentType.Commit)
            return;

        if (handshake.Content.Epoch != group.Epoch)
            return;

        Commit framed;
        try
        {
            framed = Commit.ReadFrom(new TlsReader(handshake.Content.Content));
        }
        catch (Exception ex) when (ex is TlsDecodingException or ArgumentException)
        {
            // Undecodable as a commit, so there is nothing to authorize and the
            // apply below will refuse it anyway, with a better message than any
            // this could invent.
            return;
        }

        AdmittedCommit admitted = Inspect(group, framed, handshake.Content.Sender.LeafIndex);

        MlsGroup probe = MlsGroup.Import(group.Export(), cs);
        try
        {
            probe.ProcessCommit(handshake);
        }
        catch (Exception)
        {
            // The probe could not apply it, so there is no resulting state to
            // check. Three ways to land here, and none of them is a reason to
            // refuse:
            //
            //  - the commit removes us, which the caller reports as its own
            //    outcome and after which the resulting epoch is unreadable and
            //    meaningless to us;
            //  - the commit does not apply at all, which the live apply is
            //    about to say in its own words;
            //  - the commit cites a proposal by reference. A copy restored from
            //    bytes has an empty proposal cache -- MlsGroup does not
            //    serialise it -- so no probe anywhere in this engine can resolve
            //    a reference. That is a real gap in the integrity half and it is
            //    the library's to close; the authorization half above is
            //    unaffected, because it resolves references against the live
            //    group's cache.
            return;
        }

        admitted.RequireIntegrity(probe);
    }

    /// <summary>
    /// Reads a commit against the epoch it was built on, and refuses one its
    /// committer was not authorized to make.
    /// </summary>
    /// <remarks>
    /// Everything here has to be read before the commit applies: applying it
    /// clears the proposal cache that says what its references were, and moves
    /// the tree its sender is a leaf index into.
    /// </remarks>
    /// <param name="preCommit">
    /// The group at the epoch the commit was framed against — the live group at
    /// ingest, or a probe restored to the fork epoch during convergence.
    /// </param>
    /// <param name="commit">The framed commit.</param>
    /// <param name="senderLeafIndex">The leaf that framed it.</param>
    /// <exception cref="UnauthorizedCommitException">The committer may not make it.</exception>
    public static AdmittedCommit Inspect(MlsGroup preCommit, Commit commit, uint senderLeafIndex)
    {
        ArgumentNullException.ThrowIfNull(preCommit);
        ArgumentNullException.ThrowIfNull(commit);

        MarmotDictionary current = DictionaryOf(preCommit)
            ?? throw new UnauthorizedCommitException(
                "The group has no app_data_dictionary, so no commit in it can be authorized.");

        IReadOnlySet<ushort> currentRequired = current.ComponentList()
            ?? throw new UnauthorizedCommitException(
                "The group carries no app_components requirement list, so no commit in it can "
                + "be authorized.");

        StagedCommitView view = CommitOrdering.ViewOf(preCommit, commit, senderLeafIndex);

        if (CommitAuthorization.RequiresAdmin(view))
            RequireActiveAdmin(preCommit, current, senderLeafIndex);

        return new AdmittedCommit(view, current, currentRequired);
    }

    /// <summary>
    /// Refuses a privileged commit whose committer is not an active admin in the
    /// epoch it was built on.
    /// </summary>
    /// <remarks>
    /// <b>Fails closed on a committer we cannot resolve.</b> "We could not tell
    /// who sent this" is not evidence that they were entitled to send it — and
    /// such a commit does not apply anyway, so nothing legitimate is lost.
    /// </remarks>
    private static void RequireActiveAdmin(
        MlsGroup preCommit, MarmotDictionary current, uint senderLeafIndex)
    {
        byte[]? adminBytes = current.Get(AppComponent.GroupAdminPolicy);
        if (adminBytes is null)
        {
            throw new UnauthorizedCommitException(
                "The group carries no admin-policy component, so no member is authorized to "
                + "commit this. A group in this state is frozen, not unrestricted.");
        }

        List<(uint leafIndex, byte[] identity)> members = preCommit.GetMembers();

        byte[]? committer = members
            .Where(member => member.leafIndex == senderLeafIndex)
            .Select(member => member.identity)
            .FirstOrDefault();

        if (committer is null)
        {
            throw new UnauthorizedCommitException(
                $"The commit was framed by leaf {senderLeafIndex}, which no member of this "
                + "epoch holds, so its committer cannot be shown to be an admin.");
        }

        AdminPolicy policy;
        try
        {
            policy = AdminPolicy.Decode(adminBytes);
        }
        catch (AppComponentException ex)
        {
            throw new UnauthorizedCommitException(
                $"The group's admin-policy component does not decode: {ex.Message}", ex);
        }

        // Active, not merely listed: authority is a listed key that still holds
        // a member leaf. The accounts come from the pre-commit tree, which is
        // the epoch the committer had to have authority in.
        //
        // <b>The leaf half of that is not load-bearing here, and no test pins
        // it.</b> Replacing this with IsListed passes the whole suite, and it
        // would: the committer was just read out of `members`, so it holds a
        // leaf by construction and the second condition cannot fail. Stated
        // rather than simplified away, because this is the rule the admin set
        // is read by everywhere else and asking the weaker question here would
        // make it the odd one out -- and because if the committer ever comes
        // from somewhere other than the live member list, the leaf check is
        // what stops a phantom admin from authorising a commit.
        if (!policy.IsActiveAdmin(committer, members.Select(member => member.identity)))
        {
            throw new UnauthorizedCommitException(
                $"Account {Convert.ToHexString(committer).ToLowerInvariant()} is not an active "
                + $"admin in epoch {preCommit.Epoch}, so it may not make this commit.");
        }
    }

    /// <summary>The GroupContext's Marmot dictionary, or null.</summary>
    internal static MarmotDictionary? DictionaryOf(MlsGroup group)
    {
        foreach (Extension extension in group.GroupContext.Extensions)
        {
            if (extension.ExtensionType == MarmotDictionary.ExtensionType)
                return MarmotDictionary.Decode(extension.ExtensionData);
        }

        return null;
    }
}
