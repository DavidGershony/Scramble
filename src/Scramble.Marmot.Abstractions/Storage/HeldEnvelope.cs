namespace Scramble.Marmot.Storage;

/// <summary>
/// A transport envelope addressed to one of our groups that no key we hold will
/// open, kept so it can be tried again once the group's state moves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a <see cref="MessageRecord"/>.</b> Every durable message
/// record is keyed by a content id derived from MLS bytes, and an envelope that
/// will not peel has no MLS bytes — that is the whole of what is wrong with it.
/// So it cannot be filed as a message, and until it peels there is nothing to
/// deduplicate it by except the transport id the relay gave it.
/// </para>
/// <para>
/// <b>The case it exists for is an epoch we have not reached yet.</b> A kind-445
/// envelope is sealed under the exporter secret of the epoch it was sent in, and
/// a member who never commits is behind by construction: every other member's
/// commit moves the group without them. A message sent at the new epoch is
/// sealed under a key they cannot derive and will not hold until that commit
/// arrives. The mirror case — an epoch we have <i>left</i> — is covered by
/// retained transport keys and never reaches here.
/// </para>
/// <para>
/// <b>It is untrusted, and the fields say only what was checked.</b> The
/// envelope's signature verified and its routing tag named an address this group
/// has used; nothing inside it has been read, and nothing about it can be, so
/// there is no epoch to file it under and no sender to attribute it to. Anyone
/// may publish a signed event to a routing id, which is a public value — so
/// what is held has to be bounded by count and by age rather than by trust.
/// </para>
/// </remarks>
/// <param name="TransportId">
/// The transport envelope id — a Nostr event id for kind-445. The primary key:
/// it is the only identifier an unpeeled envelope has.
/// </param>
/// <param name="GroupId">The group whose address the envelope named.</param>
/// <param name="Envelope">
/// The envelope exactly as it came off the wire, UTF-8 encoded. Bytes rather
/// than a string so it carries the same disposition as
/// <see cref="MessageRecord.Wire"/> and rides the same protection path: the two
/// hold the same class of thing, an inbound payload nothing has read, and one of
/// them stored in the clear would be a question nobody could answer.
/// </param>
/// <param name="HeldAtEpoch">
/// The group's live epoch when the envelope was first held. What bounds
/// retention: an envelope still unreadable several epochs later is not waiting
/// on a commit we have yet to see.
/// </param>
public sealed record HeldEnvelope(
    string TransportId,
    GroupId GroupId,
    byte[] Envelope,
    EpochId HeldAtEpoch,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>How many times peeling has been attempted. Diagnostic only.</summary>
    /// <remarks>
    /// Not a budget, for the same reason <see cref="MessageRecord.Attempts"/> is
    /// not one: what decides is whether the group's state has moved since the
    /// last attempt, and a count would give up on an envelope one commit away
    /// from opening.
    /// </remarks>
    public int Attempts { get; init; }

    /// <summary>
    /// The group epoch we last tried to peel this at, if ever.
    /// </summary>
    /// <remarks>
    /// Nothing about an unpeelable envelope changes while the epoch stands
    /// still: the key it wants is derived from group state, so asking again at
    /// the same epoch asks a question already answered. Null means never
    /// attempted since it was held, which is not the same as attempted at epoch
    /// zero.
    /// </remarks>
    public EpochId? LastAttemptEpoch { get; init; }
}
