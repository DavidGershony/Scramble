using DotnetMls.Codec;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.Engine.Groups;

namespace Scramble.Marmot.Engine.Messages;

/// <summary>What arrived in a handshake message, once applied.</summary>
public enum HandshakeOutcome
{
    /// <summary>A commit was applied and the group advanced an epoch.</summary>
    CommitApplied,

    /// <summary>
    /// A commit removed us from the group.
    /// </summary>
    /// <remarks>
    /// The group is <b>not</b> advanced and is no longer usable: a removed
    /// member cannot derive the new epoch, because an UpdatePath encrypts path
    /// secrets only to remaining members. Drop it rather than polling with it.
    /// </remarks>
    RemovedByCommit,

    /// <summary>A proposal was cached, awaiting a commit that references it.</summary>
    ProposalCached,
}

/// <summary>The result of receiving a handshake message.</summary>
/// <param name="Outcome">What the message turned out to be.</param>
/// <param name="ProposalReference">
/// For a cached proposal, the reference a later commit cites it by. Null
/// otherwise.
/// </param>
public sealed record ReceivedHandshake(HandshakeOutcome Outcome, byte[]? ProposalReference);

/// <summary>
/// Carrying commits and proposals over kind-445, alongside app messages.
/// </summary>
/// <remarks>
/// <para>
/// Handshake messages travel the same transport as application messages and are
/// framed differently: Marmot is a pure-plaintext-wire-format deployment, so a
/// commit or proposal is an <b>MlsPublicMessage</b> — signed, not encrypted —
/// where an application message is an MlsPrivateMessage. Confidentiality for
/// the handshake comes from the kind-445 wrap alone.
/// </para>
/// <para>
/// <b>The epoch a handshake is wrapped under is the one it is sent from, not
/// the one it produces.</b> Recipients are still at the old epoch and hold only
/// its exporter secret, so a commit wrapped under the epoch it creates is one
/// nobody can peel — the group would be stranded with no way to tell it had
/// happened. Hence <see cref="Wrap"/> takes a staged commit and refuses to look
/// at a group that has already applied it.
/// </para>
/// </remarks>
public static class GroupHandshake
{
    /// <summary>
    /// Wraps a staged commit for the wire, under the epoch it is sent from.
    /// </summary>
    /// <remarks>
    /// Call this <i>before</i> <see cref="StagedInvite.Applied"/>: that is both
    /// the publish-before-apply rule and, here, a hard requirement, because
    /// applying first would change the key this must be wrapped under.
    /// </remarks>
    /// <param name="group">The group, still at the sending epoch.</param>
    /// <param name="peeler">The transport codec.</param>
    /// <param name="commit">The staged commit.</param>
    /// <param name="expiresAt">Optional relay expiry, Unix seconds.</param>
    public static string Wrap(
        MlsGroup group,
        ITransportPeeler peeler,
        PublicMessage commit,
        long? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(peeler);
        ArgumentNullException.ThrowIfNull(commit);

        if (commit.Content.ContentType != ContentType.Commit)
        {
            throw new ArgumentException(
                $"Expected a commit, got {commit.Content.ContentType}.", nameof(commit));
        }

        if (!group.HasPendingCommit)
        {
            // Either it was already applied -- in which case the exporter secret
            // below belongs to the epoch this commit creates, and no recipient
            // could peel the result -- or it was never staged on this group at
            // all. Both produce an envelope that looks fine and reaches nobody.
            throw new InvalidOperationException(
                "The commit is not pending on this group, so wrapping it would use the "
                + "wrong epoch's key. Wrap before applying.");
        }

        return WrapPublic(group, peeler, commit, expiresAt);
    }

    /// <summary>
    /// Wraps a standalone proposal for the wire.
    /// </summary>
    /// <remarks>
    /// A proposal changes nothing locally, so unlike a commit there is no
    /// ordering rule to respect — but it is still epoch-bound, and every
    /// recipient will refuse it once the epoch has moved on.
    /// </remarks>
    public static string WrapProposal(
        MlsGroup group,
        ITransportPeeler peeler,
        PublicMessage proposal,
        long? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(peeler);
        ArgumentNullException.ThrowIfNull(proposal);

        if (proposal.Content.ContentType != ContentType.Proposal)
        {
            throw new ArgumentException(
                $"Expected a proposal, got {proposal.Content.ContentType}.", nameof(proposal));
        }

        return WrapPublic(group, peeler, proposal, expiresAt);
    }

    private static string WrapPublic(
        MlsGroup group, ITransportPeeler peeler, PublicMessage message, long? expiresAt)
    {
        byte[] mlsBytes = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, message).WriteTo);

        return peeler.WrapGroupMessage(
            mlsBytes,
            GroupMessages.TransportGroupId(group),
            GroupMessages.ExporterSecret(group),
            expiresAt);
    }

    /// <summary>
    /// Applies a peeled handshake message: a commit advances the group, a
    /// proposal is cached for a later commit to reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both arms are refusals as much as they are applications. A proposal is
    /// cached only once its signature and membership tag verify against the
    /// sender's leaf, because a commit can later cite it by hash — an
    /// unauthenticated proposal in the cache is a way to have a member apply a
    /// change nobody authorised.
    /// </para>
    /// <para>
    /// A commit that removes <i>us</i> is reported rather than thrown, because
    /// it is a legitimate outcome and the group state afterwards is
    /// meaningless: we cannot decrypt anything from the new epoch. The caller
    /// must stop using the group.
    /// </para>
    /// </remarks>
    /// <exception cref="MarmotAppEventException">Not a decodable handshake.</exception>
    public static ReceivedHandshake Receive(MlsGroup group, ReadOnlySpan<byte> mlsBytes)
    {
        ArgumentNullException.ThrowIfNull(group);

        MlsMessage message;
        try
        {
            message = MlsMessage.ReadFrom(new TlsReader(mlsBytes.ToArray()));
        }
        catch (Exception ex) when (ex is TlsDecodingException or ArgumentException)
        {
            throw new MarmotAppEventException($"Not a decodable MLSMessage: {ex.Message}");
        }

        if (message.WireFormat != WireFormat.MlsPublicMessage
            || message.Body is not PublicMessage publicMessage)
        {
            throw new MarmotAppEventException(
                $"Expected a handshake message, got {message.WireFormat}.");
        }

        return publicMessage.Content.ContentType switch
        {
            ContentType.Commit => ApplyCommit(group, publicMessage),
            ContentType.Proposal => CacheProposal(group, publicMessage),
            var other => throw new MarmotAppEventException(
                $"Expected a commit or a proposal, got {other}."),
        };
    }

    private static ReceivedHandshake ApplyCommit(MlsGroup group, PublicMessage commit)
    {
        try
        {
            group.ProcessCommit(commit);
        }
        catch (RemovedFromGroupException)
        {
            // Reported, not rethrown: this is a legitimate outcome of an
            // ordinary commit, and the caller has to act on it rather than
            // treat it as a transport failure. The group is left untouched by
            // the library, and is no longer usable -- the caller must drop it.
            return new ReceivedHandshake(HandshakeOutcome.RemovedByCommit, ProposalReference: null);
        }

        return new ReceivedHandshake(HandshakeOutcome.CommitApplied, ProposalReference: null);
    }

    private static ReceivedHandshake CacheProposal(MlsGroup group, PublicMessage proposal)
    {
        // The reference comes back from the cache rather than being recomputed
        // here: the caller needs it to commit the proposal later, and it is the
        // hash of an exact serialisation this layer should not reproduce.
        return new ReceivedHandshake(
            HandshakeOutcome.ProposalCached, group.CacheProposal(proposal));
    }

}
