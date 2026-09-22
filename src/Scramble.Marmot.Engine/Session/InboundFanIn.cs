using Scramble.Marmot.Engine.Ingest;
using Scramble.Marmot.Ingest;
using Scramble.Marmot.Storage;

namespace Scramble.Marmot.Engine.Session;

/// <summary>
/// Where one inbound envelope ended up.
/// </summary>
/// <remarks>
/// Three answers rather than one, because the caller does three different
/// things with them. A delivery belongs to a group and may carry a message a
/// user has to see; a Welcome belongs to no group yet and is a join, not a
/// receive; a refusal is the ordinary outcome for most of what a relay hands a
/// client and must be quiet.
/// </remarks>
public abstract record InboundDelivery
{
    private InboundDelivery()
    {
    }

    /// <summary>Routed to a group and put through that group's ingest.</summary>
    /// <param name="GroupId">The group it resolved to.</param>
    /// <param name="Result">What ingest made of it, refusals included.</param>
    public sealed record Delivered(GroupId GroupId, IngestResult Result) : InboundDelivery;

    /// <summary>
    /// A gift-wrapped Welcome addressed to us.
    /// </summary>
    /// <remarks>
    /// <b>Handed back rather than routed, because there is nothing to route
    /// to.</b> A Welcome names a group we are not yet in, so no routing id
    /// resolves it and no session can ingest it — joining is
    /// <see cref="Groups.GroupJoin"/>'s work and it needs a KeyPackage's
    /// private material, which the session layer does not hold. Reporting it as
    /// an unknown group would be the wrong answer to the right question.
    /// </remarks>
    /// <param name="Peeled">The unwrapped Welcome, with its details.</param>
    public sealed record Welcome(PeeledMessage Peeled) : InboundDelivery;

    /// <summary>It reached no group, and this says why.</summary>
    /// <param name="Outcome">
    /// Always an <see cref="IngestOutcome.Ignored"/>: a refusal made before a
    /// group was found cannot be anything a retry would change, because there
    /// is nothing to retry it against.
    /// </param>
    public sealed record Refused(IngestOutcome Outcome) : InboundDelivery;
}

/// <summary>
/// The door every inbound transport envelope comes through, and the only thing
/// that knows which group one belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The piece that made the engine send-only.</b>
/// <see cref="MarmotSession"/> is per-group and
/// <see cref="MarmotSession.ReceiveAsync"/> takes an envelope it has already
/// been told is this group's. Nothing decided that. So a relay subscription had
/// nowhere to deliver to, <see cref="IRoutingIndexStorage"/> had no reader, and
/// every test that "received" a message did so by handing MLS bytes straight to
/// a session it picked itself.
/// </para>
/// <para>
/// <b>Order of the refusals, cheapest and most selective first.</b> Most of
/// what a relay hands a client is other people's traffic, so the common path
/// through here is a refusal and it has to be cheap:
/// </para>
/// <list type="number">
/// <item><b>Peel far enough to read the address.</b> Nothing in an envelope is
/// trustworthy until its signature verifies, so this cannot be skipped and
/// nothing can come before it. It yields the routing id and the envelope's
/// authenticated id, and refuses a malformed one outright.</item>
/// <item><b>Resolve the routing id.</b> An exact 32-byte lookup against the
/// index, retired addresses included. No match is the common case and costs one
/// indexed read.</item>
/// <item><b>The duplicate-envelope pre-filter.</b> Only now, because only now
/// is there an id worth trusting — and still before a session is opened, which
/// is the expensive step it exists to avoid.</item>
/// <item><b>Ingest, through the group's session</b>, where the authoritative
/// content-derived deduplication lives.</item>
/// </list>
/// <para>
/// <b>The pre-filter is not the deduplication and must never become it.</b> It
/// is keyed by envelope; the same MLS message legitimately arrives under
/// several envelopes with different ids, and every one of them passes this
/// filter. What catches the second copy is <c>MessageId.FromMlsBytes</c> inside
/// <see cref="MessageIngest"/>, over the MLS bytes. Deduplicating on the
/// transport id is the exact mistake the previous engine made.
/// </para>
/// <para>
/// <b>An envelope is peeled twice on the way in, deliberately.</b> Once here to
/// learn the address, once inside the session, which owns the fallback through
/// retained epoch keys that an envelope from before our latest commit needs.
/// Re-deriving that here would be a second implementation of the one thing the
/// epoch-boundary tests exist to protect. It is the same trade
/// <see cref="MarmotSession.ReceiveAsync"/> already takes for its own retries,
/// and the same seam is why: <see cref="ITransportPeeler.Peel"/> takes one
/// secret and decides everything else itself.
/// </para>
/// <para>
/// <b>Drive it from one loop.</b> A <see cref="MarmotSession"/> owns a mutable
/// <c>MlsGroup</c> whose ratchet state is order-sensitive, so neither it nor
/// this is thread-safe, and a lock here would give false comfort — a concurrent
/// send through the same session would race regardless.
/// </para>
/// </remarks>
public sealed class InboundFanIn
{
    private readonly MarmotSessionHost _host;

    /// <param name="host">
    /// The host whose groups this routes to. It supplies the storage as well,
    /// so a fan-in cannot resolve addresses in one database while opening
    /// groups out of another.
    /// </param>
    public InboundFanIn(MarmotSessionHost host) =>
        _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>How many sessions are currently held open.</summary>
    public int OpenSessions => _host.OpenSessions;

    /// <summary>
    /// Routes one envelope to the group it belongs to, and ingests it there.
    /// </summary>
    /// <remarks>
    /// <b>Never throws for traffic that is not ours.</b> An envelope for a
    /// group we do not have, a malformed one, and a redelivery are all ordinary
    /// and all answer with <see cref="InboundDelivery.Refused"/>. A client that
    /// treats those as errors looks broken while working and buries the
    /// failures that matter — the philosophy is
    /// <see cref="MessageIngest"/>'s, and it has to hold here too or the first
    /// stranger's message takes the subscription loop down.
    /// </remarks>
    /// <param name="envelope">The transport envelope, as it came off the wire.</param>
    public async Task<InboundDelivery> ReceiveAsync(
        string envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        Probe probe = Address(envelope);

        if (probe.Welcome is { } welcome)
            return new InboundDelivery.Welcome(welcome);

        if (probe.TransportGroupId is not { } address)
        {
            // No address means no destination, and that is terminal whatever
            // the peeler said about retrying: there is no group to defer it
            // against and no record to write it into. A peeler that reports a
            // retryable failure without naming an address is asking us to try
            // again at something we cannot name.
            return Refuse(InputRejectionCategory.InvalidEncoding);
        }

        RoutingIndexRecord? routing = await _host.Storage.ResolveAsync(address, ct);

        if (routing is null)
        {
            // The common case, and not an error. Every kind-445 event on a
            // relay carries somebody's routing id, and almost none of them are
            // ours. It stops here having cost one indexed read.
            return Refuse(InputRejectionCategory.UnknownGroup);
        }

        if (probe.TransportId is { } transportId
            && await _host.Storage.HasTransportSeenAsync(transportId, ct))
        {
            // The pre-filter, in the only place it is worth anything: after the
            // id is trustworthy and before a group is opened. It answers only
            // for an envelope we have already processed -- a record is marked
            // seen once it is past content deduplication -- so it never
            // shadows the content check below.
            return Refuse(InputRejectionCategory.Duplicate);
        }

        MarmotSession? session = (await _host.SessionForAsync(routing.GroupId, ct)).Session;

        if (session is null)
        {
            // The index named a group whose record is gone or unreadable. Same
            // answer as no index entry at all: we cannot receive into it, and
            // saying so is more use than a throw from a subscription loop.
            return Refuse(InputRejectionCategory.UnknownGroup);
        }

        return new InboundDelivery.Delivered(
            routing.GroupId, await session.ReceiveAsync(envelope, ct));
    }

    /// <summary>
    /// The session this fan-in holds for a group, opening one if it must.
    /// </summary>
    /// <remarks>
    /// <b>Use it for sending too.</b> Two sessions over one group id in one
    /// process each hold their own <c>MlsGroup</c> and each write live state,
    /// so the second write silently discards the first's epoch — the fork
    /// <see cref="MarmotSession"/> spends its effort avoiding, reached without
    /// any peer's help. A caller that sends through
    /// <see cref="MarmotSessionHost.OpenAsync"/> while receiving through here
    /// has built exactly that, and <see cref="Forget"/> is the only way back.
    /// </remarks>
    /// <returns>The session, or null when the group is not ours to open.</returns>
    public async Task<MarmotSession?> SessionForAsync(
        GroupId groupId, CancellationToken ct = default) =>
        (await _host.SessionForAsync(groupId, ct)).Session;

    /// <summary>
    /// Drops a cached session without writing anything through it.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not <see cref="MarmotSession.CloseAsync"/>.</b> Closing
    /// writes the session's live state first, and a session dropped for being
    /// stale is holding exactly the state that must not be written. The journal
    /// binding is released, which is what stops a stray staging call on the
    /// dropped group writing through to the group's rows.
    /// </remarks>
    /// <returns>Whether there was one to drop.</returns>
    public bool Forget(GroupId groupId) => _host.Forget(groupId);

    /// <summary>Drops every cached session. See <see cref="Forget"/>.</summary>
    public void Clear() => _host.Clear();


    /// <summary>What the first peel established about an envelope.</summary>
    /// <param name="TransportGroupId">
    /// The address it names, or null when the peeler refused before reading it.
    /// </param>
    /// <param name="TransportId">
    /// Its authenticated id, or null when the peeler could not vouch for one.
    /// </param>
    /// <param name="Welcome">Set when the envelope turned out to be a Welcome.</param>
    private readonly record struct Probe(
        byte[]? TransportGroupId, string? TransportId, PeeledMessage? Welcome);

    /// <summary>
    /// Peels far enough to learn where an envelope is addressed.
    /// </summary>
    /// <remarks>
    /// <b>The callback always declines, and that is the whole trick.</b>
    /// <see cref="ITransportPeeler.Peel"/> hands the routing id to the caller
    /// before it needs a key, so declining turns a peel into an address
    /// lookup — and the refusal that comes back carries the envelope's
    /// authenticated id with it. Reading the routing tag directly instead would
    /// mean reading it before the signature is checked, which is reading an
    /// attacker-chosen value.
    /// </remarks>
    private Probe Address(string envelope)
    {
        byte[]? addressedTo = null;

        try
        {
            PeeledMessage peeled = _host.Peeler.Peel(envelope, id =>
            {
                addressedTo = id;
                return null;
            });

            // A Welcome opens with the account secret alone and never asks for
            // a group key, so it peels completely on a probe. A group message
            // reaching here would mean a peeler that opened one without a
            // secret; route it on its own reported address rather than
            // pretending this cannot happen.
            return peeled.Kind == PeeledContentKind.Welcome
                ? new Probe(null, peeled.TransportId, peeled)
                : new Probe(peeled.TransportGroupId ?? addressedTo, peeled.TransportId, null);
        }
        catch (PeelFailedException ex)
        {
            return new Probe(addressedTo, ex.TransportId, null);
        }
    }

    private static InboundDelivery Refuse(InputRejectionCategory category) =>
        new InboundDelivery.Refused(new IngestOutcome.Ignored(category));
}
