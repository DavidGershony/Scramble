using DotnetMls.Crypto;
using DotnetMls.Group;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.Ingest;
using Scramble.Marmot.Ingest;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Wire.Nostr;

namespace Scramble.Marmot.Engine.Session;

/// <summary>
/// What session-open hydration found, and what it did about it.
/// </summary>
/// <remarks>
/// Reported rather than logged. A group that came back on a commit whose fate
/// nobody has confirmed is still owed a reconciliation against a relay, and the
/// only thing that can owe it is the caller — the engine has no transport of
/// its own.
/// </remarks>
/// <param name="Verdict">
/// What the stranded commit was judged to be, or null when there was no commit
/// of ours outstanding. <see cref="StrandedCommitVerdict.Reconcile"/> means the
/// group was moved onto the commit and the answer is still owed.
/// </param>
/// <param name="Epoch">The epoch the group came back on.</param>
/// <summary>Why a group could not be opened.</summary>
/// <remarks>
/// <para>
/// <b>"We do not have it" and "we have it and cannot rebuild it" are different
/// answers</b>, and collapsing them was the defect this type exists to fix. The
/// first is routine — a group we were never in, or have left. The second is a
/// group whose history we are holding and cannot reach, which is a fault
/// somebody has to see.
/// </para>
/// <para>
/// The distinction matters most in the loop the app layer will write:
/// <c>foreach (id in ids) await OpenAsync(id)</c>. One group that cannot be
/// rebuilt must not take the others down with it, and the caller can only
/// arrange that if it can tell the two apart. Upstream reaches the same place
/// from the other side, quarantining a group whose hydration fails so the
/// account still opens — see `remaining-work-2026-09.md` §7 for why we do not
/// need its machinery, only its distinction.
/// </para>
/// <para>
/// Every member here is producible. Nothing is named for symmetry with
/// upstream's larger set: a refusal this build cannot reach would be a promise
/// to a caller that nothing keeps.
/// </para>
/// </remarks>
public enum SessionOpenRefusal
{
    /// <summary>No record of the group. Routine, and not a fault.</summary>
    NotStored,

    /// <summary>
    /// The record is here and its stored MLS state will not load.
    /// </summary>
    /// <remarks>
    /// Corruption, or a blob written by a build whose serialisation this one
    /// cannot read. Upstream calls this <c>OpenMlsLoadFailed</c>.
    /// </remarks>
    StateUnreadable,

    /// <summary>
    /// The record is here, carries no usable state, and nothing retained can
    /// rebuild it.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>OpenMlsGroupMissing</c>. Reachable for a record written
    /// before live state was durable whose archived epochs have since been
    /// pruned past.
    /// </remarks>
    NoRetainedState,
}

/// <summary>The result of trying to open a group.</summary>
/// <param name="Session">The open session, or null when refused.</param>
/// <param name="Refusal">Why, when refused. Null when opened.</param>
public sealed record SessionOpenResult(MarmotSession? Session, SessionOpenRefusal? Refusal)
{
    /// <summary>Whether the group opened.</summary>
    public bool Opened => Session is not null;

    /// <summary>The session, for a caller that has already checked.</summary>
    /// <exception cref="InvalidOperationException">The open was refused.</exception>
    public MarmotSession Require() =>
        Session ?? throw new InvalidOperationException(
            $"The group could not be opened: {Refusal}.");
}

public sealed record SessionRecovery(StrandedCommitVerdict? Verdict, EpochId Epoch)
{
    /// <summary>Whether the relay still has to be asked what became of a commit.</summary>
    public bool ReconciliationOwed => Verdict == StrandedCommitVerdict.Reconcile;
}

/// <summary>
/// Opens groups, and is the only thing that hands out a live one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The piece every other piece was waiting for.</b> The archive, the durable
/// epoch manager, the publisher, ingest and the convergence pass were each
/// built and tested and had no caller, because each of them needs something
/// that owns a live group's lifetime and none of them could be it. This is
/// that: it hydrates a group at open, records what its own commits produce,
/// drives a publish through to an answer, and keeps the durable copy in step
/// with the MLS object.
/// </para>
/// <para>
/// <b>One per process, not one per group.</b>
/// <see cref="DurableEpochManager.RestoreAsync"/> replays every stored epoch
/// state in one pass and must run before any transition, so the manager it
/// wraps has to outlive an individual group — and two hosts over one database
/// would each restore into their own manager and then disagree about which
/// groups are mid-publish.
/// </para>
/// </remarks>
public sealed class MarmotSessionHost
{
    private readonly IMarmotStorageProvider _storage;
    private readonly ICipherSuite _cs;
    private readonly ConvergencePolicy _policy;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IMessageRelay? _messages;
    private readonly ITransportPeeler _peeler;

    /// <param name="relay">Where a commit's bytes go. See <see cref="ICommitRelay"/>.</param>
    /// <remarks>
    /// The hydration-only shape, and the reason the message seam is optional at
    /// all: a restart happens before anything is online, and
    /// <see cref="OpenAsync"/> must not need a transport of either kind. A host
    /// built this way opens and recovers groups; asking one of its sessions to
    /// send throws rather than silently queueing forever.
    /// </remarks>
    public MarmotSessionHost(
        IMarmotStorageProvider storage,
        ICipherSuite cipherSuite,
        ICommitRelay relay,
        ConvergencePolicy policy,
        Func<DateTimeOffset> clock)
        : this(storage, cipherSuite, relay, null, null, policy, clock)
    {
    }

    /// <param name="relay">Where a commit's bytes go. See <see cref="ICommitRelay"/>.</param>
    /// <param name="messages">Where an application message's bytes go.</param>
    public MarmotSessionHost(
        IMarmotStorageProvider storage,
        ICipherSuite cipherSuite,
        ICommitRelay relay,
        IMessageRelay? messages,
        ConvergencePolicy policy,
        Func<DateTimeOffset> clock)
        : this(storage, cipherSuite, relay, messages, null, policy, clock)
    {
    }

    /// <param name="relay">Where a commit's bytes go. See <see cref="ICommitRelay"/>.</param>
    /// <param name="messages">
    /// Where an application message's bytes go. <b>Kept separate from
    /// <paramref name="relay"/> even when one object implements both</b> — see
    /// <see cref="IMessageRelay"/> for why their outcome handling must not be
    /// shared.
    /// </param>
    /// <param name="peeler">
    /// Wraps an outbound message for the wire. Defaults to a Nostr peeler with
    /// no account secret, which is all a send needs — the account secret exists
    /// only to open gift-wrapped Welcomes, which is an inbound concern.
    /// <para>
    /// <b>Owned by the host rather than passed per call</b>, unlike the commit
    /// path's <c>envelope</c> delegate. A queued message is wrapped at drain
    /// time, in a call the original sender is no longer on the stack for, so
    /// there is nobody left to hand one in. See <see cref="MarmotSession.SendAsync"/>.
    /// </para>
    /// </param>
    public MarmotSessionHost(
        IMarmotStorageProvider storage,
        ICipherSuite cipherSuite,
        ICommitRelay relay,
        IMessageRelay? messages,
        ITransportPeeler? peeler,
        ConvergencePolicy policy,
        Func<DateTimeOffset> clock)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _cs = cipherSuite ?? throw new ArgumentNullException(nameof(cipherSuite));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _messages = messages;
        _peeler = peeler ?? new NostrGroupPeeler();

        ArgumentNullException.ThrowIfNull(relay);

        Archive = new EpochArchive(storage, cipherSuite, policy, clock);
        Epochs = new DurableEpochManager(new EpochManager(), storage, clock);
        Publisher = new CommitPublisher(storage, relay, clock);
    }

    /// <summary>Wraps an outbound message for the wire.</summary>
    internal ITransportPeeler Peeler => _peeler;

    /// <summary>
    /// The storage every session here shares.
    /// </summary>
    /// <remarks>
    /// Exposed so <see cref="InboundFanIn"/> cannot be handed a different one.
    /// A fan-in resolving addresses in one database and opening groups out of
    /// another would answer "not ours" for every group it holds, which is a
    /// silent and total failure of the receive path.
    /// </remarks>
    internal IMarmotStorageProvider Storage => _storage;

    /// <summary>
    /// The message transport, or a throw if this host was built without one.
    /// </summary>
    /// <remarks>
    /// Asked before anything durable is written, so a host with no transport
    /// fails the send outright instead of leaving a queue row that nothing in
    /// this process can ever drain.
    /// </remarks>
    internal IMessageRelay RequireMessageRelay() =>
        _messages ?? throw new InvalidOperationException(
            "This session host was constructed without an IMessageRelay, so it can open "
            + "and recover groups but cannot send application messages.");

    /// <summary>The epoch state machine, durable.</summary>
    private readonly Dictionary<GroupId, MarmotSession> _sessions = [];

    public DurableEpochManager Epochs { get; }

    /// <summary>The per-epoch archive every group here is captured into.</summary>
    public EpochArchive Archive { get; }

    /// <summary>The publish seam, including the stranded-commit verdict.</summary>
    public CommitPublisher Publisher { get; }

    /// <summary>
    /// Replays the stored epoch states. Run once, before opening anything.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OpenAsync"/> because it is not per-group: it
    /// reads every stored state in one pass, and running it again after a group
    /// has moved would either be refused or rewind the manager. See
    /// <see cref="DurableEpochManager.RestoreAsync"/>.
    /// </remarks>
    public Task<int> RestoreAsync(CancellationToken ct = default) => Epochs.RestoreAsync(ct);

    /// <summary>
    /// Takes a group we have just created or joined into durable custody.
    /// </summary>
    /// <remarks>
    /// <b>The record is written before the archive is captured</b>, so a crash
    /// between them leaves a group whose durable state is its own record —
    /// <c>ToRecord</c> carries <c>LiveState</c>, so that one write is
    /// self-sufficient and the group opens from it alone. The reverse order
    /// loses the group outright: the checkpoint lands for a group no record
    /// mentions, and hydration starts from the record.
    /// <para>
    /// <b>This ordering is load-bearing and has no test.</b> Swapping the two
    /// writes passes the whole suite, because the difference is only observable
    /// in a crash between them — which needs a storage double that fails one
    /// call, and none exists. Stated here rather than left to look covered. The
    /// same gap applies to the ordering claims in <c>CommitPublisher</c> and
    /// <c>DurableEpochManager</c>, so one fault-injecting provider would pay
    /// for itself three times over.
    /// </para>
    /// </remarks>
    public async Task<MarmotSession> AdoptAsync(
        GroupRecord record, MlsGroup group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(group);

        await _storage.PutGroupAsync(record, ct);

        // Binds the journal as well as writing the checkpoint, which is what
        // makes the group's own commits recordable from here on.
        await Archive.CaptureIfAbsentAsync(record.Id, group, ct);

        // The group's first appearance in the routing index. Until this runs
        // the group is send-only: it can publish to its address and nothing
        // arriving at that address resolves back to it.
        await SyncRoutingAsync(record.Id, group, ct);

        return new MarmotSession(this, _storage, _cs, _policy, _clock, record.Id, group, null);
    }

    /// <summary>
    /// Reconstructs a group from what is durable, or null if we do not have it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three sources, consulted in order, and the order is the design.</b>
    /// The group record's live state is where the group was last confirmed to
    /// be. A staged commit of ours may have moved it since — but only if
    /// something tried to send that commit, so the publish attempt is asked
    /// before the staged state is touched. The epoch archive is the fallback
    /// for a record written before live state was durable at all, and it is a
    /// fallback rather than a source: it holds the epochs a group has passed
    /// through, which is a weaker claim than where it is.
    /// </para>
    /// <para>
    /// <b>The three verdicts want three different things.</b>
    /// <see cref="StrandedCommitVerdict.Abandon"/> — no attempt row, or a relay
    /// that definitively refused — means nobody else can have seen the commit,
    /// so the prepared state is dropped and the group comes back where it was;
    /// keeping it would advance this member alone into an epoch nobody can
    /// reach. <see cref="StrandedCommitVerdict.Adopt"/> — the relay took it —
    /// means the group has already moved without us and we catch up.
    /// <see cref="StrandedCommitVerdict.Reconcile"/> is the one that has to
    /// choose without knowing, and it adopts: the pre-commit state is still in
    /// the archive and in the record, so a reconciliation that comes back "it
    /// never landed" can still rewind, while the opposite mistake cannot be
    /// undone at all — MLS refuses to let a member process a commit it
    /// authored, so a commit abandoned here can never be re-applied from the
    /// relay. The caller is told, through
    /// <see cref="SessionRecovery.ReconciliationOwed"/>, that the question is
    /// still open.
    /// </para>
    /// <para>
    /// <b>Nothing is written on the Reconcile path.</b> The rows are what make
    /// the commit findable, and a hydration that cleared them would leave a
    /// group standing on a commit nobody can any longer ask about.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The session for a group, opening one if this host does not hold it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is how a session should be obtained.</b> A
    /// <see cref="MarmotSession"/> owns a live <see cref="MlsGroup"/> and writes
    /// it down; two of them over one group id each hold their own copy and each
    /// persist it, so whichever writes second silently discards the other's
    /// epoch. That is a fork of our own making, with no peer involved and
    /// nothing to detect it — the group simply is not where it thinks it is.
    /// </para>
    /// <para>
    /// So the cache is not a performance device, it is the ownership. Holding it
    /// on the host rather than in one caller is what lets the receive path, the
    /// send path and a service layer share one owner instead of each minting
    /// their own.
    /// </para>
    /// <para>
    /// <b>Invalidated when the stored record's epoch moves strictly ahead of the
    /// cached session's.</b> Within one host nothing should now be able to do
    /// that — every path that advances a group goes through its session — so
    /// this is defence against a second writer on the same database, which is a
    /// different process rather than a different object. Strictly ahead, not
    /// merely different: a record behind the session is the ordinary
    /// mid-operation state, and re-opening on it would throw away the newer
    /// group for older bytes.
    /// </para>
    /// </remarks>
    public async Task<SessionOpenResult> SessionForAsync(
        GroupId groupId, CancellationToken ct = default)
    {
        if (await _storage.GetGroupAsync(groupId, ct) is not { } record)
        {
            Forget(groupId);
            return new SessionOpenResult(null, SessionOpenRefusal.NotStored);
        }

        if (_sessions.TryGetValue(groupId, out MarmotSession? cached))
        {
            if (record.Epoch.Value <= cached.Group.Epoch)
                return new SessionOpenResult(cached, null);

            Forget(groupId);
        }

        SessionOpenResult opened = await OpenAsync(groupId, ct);

        if (opened.Session is { } session)
            _sessions[groupId] = session;

        return opened;
    }

    /// <summary>
    /// Drops a cached session without closing it.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="MarmotSession.CloseAsync"/>: closing writes
    /// the session's live state, and a session is dropped precisely when that
    /// state is the stale one.
    /// <para>
    /// The journal binding is released with it. Without that, a staging call on
    /// the dropped group would still write through to the group's rows — the
    /// session is gone but the group object it held is not.
    /// </para>
    /// </remarks>
    /// <returns>Whether there was one to drop.</returns>
    public bool Forget(GroupId groupId)
    {
        if (!_sessions.Remove(groupId, out MarmotSession? session))
            return false;

        GroupJournal.Unbind(session.Group);
        return true;
    }

    /// <summary>Drops every cached session. See <see cref="Forget"/>.</summary>
    public void Clear()
    {
        foreach (GroupId groupId in _sessions.Keys.ToList())
            Forget(groupId);
    }

    /// <summary>How many sessions this host currently owns.</summary>
    public int OpenSessions => _sessions.Count;

    /// <summary>
    /// Restores a session from storage, bypassing the cache.
    /// </summary>
    /// <remarks>
    /// <b>Prefer <see cref="SessionForAsync"/>.</b> This hands back a fresh
    /// session every call, so two callers using it on one group get two owners
    /// and the silent fork described there. It stays public because a restart
    /// genuinely wants a fresh read of storage, and because the refusal it
    /// returns is the only place the three reasons are distinguished.
    /// </remarks>
    public async Task<SessionOpenResult> OpenAsync(
        GroupId groupId, CancellationToken ct = default)
    {
        if (await _storage.GetGroupAsync(groupId, ct) is not { } record)
            return new SessionOpenResult(null, SessionOpenRefusal.NotStored);

        MlsGroup? group;
        try
        {
            group = Restore(record.LiveState);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Classified, not thrown. MlsGroup.Import reads straight into a
            // TlsReader, so a corrupt blob throws whatever the codec throws --
            // and a throw out of here takes down every other group in the
            // caller's open loop, which is precisely the blast radius this
            // method is supposed to contain.
            _ = ex;
            return new SessionOpenResult(null, SessionOpenRefusal.StateUnreadable);
        }

        // The fallback, for a record written before groups carried their own
        // state. Newest first: the furthest epoch we have a state for is the
        // closest we can get to where the group actually is.
        if (group is null)
        {
            EpochWindow window = await Archive.LoadWindowAsync(groupId, record.Epoch, ct);

            foreach (EpochId epoch in window.Epochs.Reverse())
            {
                group = window.Restore(epoch);
                if (group is not null)
                    break;
            }
        }

        if (group is null)
            return new SessionOpenResult(null, SessionOpenRefusal.NoRetainedState);

        SessionRecovery? recovery = null;

        if (await _storage.GetStagedCommitAsync(groupId, ct) is { } staged)
        {
            StrandedCommitVerdict verdict = await Publisher.ClassifyAsync(groupId, ct);

            if (verdict == StrandedCommitVerdict.Abandon)
            {
                await _storage.ClearStagedCommitAsync(groupId, ct);

                // Hygiene, not a guard, and it survives mutation: the attempt
                // row is only ever read through ClassifyAsync, which is only
                // consulted when a staged commit exists. Clearing that first
                // puts this row out of reach, and the next publish replaces it
                // regardless. Left in because an unreachable row that outlives
                // its group is still worth not keeping.
                await _storage.ClearCommitPublishAttemptAsync(groupId, ct);
            }
            else if (Restore(staged.GroupState) is { } advanced)
            {
                group = advanced;

                if (verdict == StrandedCommitVerdict.Adopt)
                    await ConfirmAsync(groupId, group, staged, ct);
            }

            recovery = new SessionRecovery(verdict, new EpochId(group.Epoch));
        }

        // Last, and on whatever group we settled on. Binds the journal, and
        // records the epoch a recovered commit landed us at -- an epoch reached
        // by adopting a stranded commit is one a competing branch can still
        // fork from, and nothing else would have archived it.
        await Archive.CaptureIfAbsentAsync(groupId, group, ct);

        // Re-asserted at every open, and it is the only thing that can heal a
        // routing row lost between AdoptAsync's two writes. A group missing
        // from the index receives nothing, and the only other place routing is
        // written is a state advance -- which needs a receive. Without this,
        // that group would be unroutable permanently and silently. Writes
        // nothing when the index already agrees.
        await SyncRoutingAsync(groupId, group, ct);

        return new SessionOpenResult(
            new MarmotSession(
                this, _storage, _cs, _policy, _clock, groupId, group, recovery),
            null);
    }

    /// <summary>
    /// Settles a commit whose bytes a relay confirmed: the state becomes the
    /// group's, and the rows describing it go.
    /// </summary>
    /// <remarks>
    /// <b>The archive is written before the rows are cleared.</b> The rows are
    /// what say a commit may be out there; they have to outlive the move they
    /// authorised, so a crash in between comes back reconciling rather than
    /// with no record of a commit whose local application it cannot vouch for.
    /// </remarks>
    internal async Task ConfirmAsync(
        GroupId groupId, MlsGroup group, StagedCommitRecord staged, CancellationToken ct)
    {
        await Archive.CaptureAsync(groupId, group, staged.Tip, ct);
        await WriteLiveStateAsync(groupId, group, ct);
        await _storage.ClearStagedCommitAsync(groupId, ct);
        await _storage.ClearCommitPublishAttemptAsync(groupId, ct);
    }

    /// <summary>
    /// Brings the group record into line with the MLS state it describes.
    /// </summary>
    /// <remarks>
    /// Epoch and state are written together, always. They are one fact in two
    /// columns, and a record whose epoch says one thing while its bytes say
    /// another is worse than either being stale.
    /// </remarks>
    internal async Task WriteLiveStateAsync(
        GroupId groupId, MlsGroup group, CancellationToken ct)
    {
        if (await _storage.GetGroupAsync(groupId, ct) is not { } record)
            return;

        await _storage.PutGroupAsync(
            record with
            {
                Epoch = new EpochId(group.Epoch),
                LiveState = group.Export(),
                UpdatedAt = _clock(),
            },
            ct);

        // Rotation. The routing address lives in the signed 0x8004 component,
        // so the only thing that can move it is a commit -- and every commit
        // this member accepts, authors or adopts ends here. Hooking rotation
        // anywhere else would mean one of those three paths rotating the
        // address on the wire while the index still pointed at the old one.
        //
        // This line is UNCOVERED and removing it passes the whole suite.
        // Nothing in this build rotates an address: no path stages an
        // AppDataUpdate for 0x8004, so a group's address is whatever creation
        // gave it and AdoptAsync has already registered that. The rotation
        // semantics themselves are tested, at the storage layer, in
        // RoutingIndexTests. What is untested is this call site, and it cannot
        // be tested without building rotation -- which is a feature, not a fan-in.
        // Left in because its absence is silent and total: the day a rotation
        // commit exists, an index still naming the old address makes the group
        // deaf, and this is the one place every such commit passes through.
        await SyncRoutingAsync(groupId, group, ct);
    }

    /// <summary>
    /// Brings the routing index into line with the address the group publishes
    /// to now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Registration is not optional bookkeeping.</b> An inbound kind-445
    /// event carries a routing id and nothing else identifying it, so a group
    /// absent from this index cannot be the destination of anything. The index
    /// having had no writer is why the engine could send but never receive.
    /// </para>
    /// <para>
    /// <b>The address-equality check is load-bearing, not a micro-optimisation.</b>
    /// <see cref="IRoutingIndexStorage.PutRoutingAsync"/> no-ops only for a
    /// re-registration at the <i>same</i> epoch; re-registering the same
    /// address at a later epoch rewrites its <c>FirstEpoch</c> forward, so a
    /// group that never rotated would report its address as having become
    /// current at whatever epoch it last committed at. Comparing the bytes is
    /// what makes "nothing rotated" a genuine no-op.
    /// </para>
    /// <para>
    /// <b>A group with no <c>0x8004</c> component is skipped rather than
    /// refused.</b> It has no address, so there is no row to write and nothing
    /// could ever arrive for it — peers read a group's transport id from that
    /// component and nowhere else. Throwing here would turn an unaddressable
    /// group into a failure of whatever operation happened to notice, which is
    /// a diagnosis pointing at the wrong place.
    /// </para>
    /// <para>
    /// A <see cref="RoutingIdConflictException"/> is deliberately <b>not</b>
    /// caught. Two groups claiming one address is the fail-closed case the
    /// index exists for, and swallowing it here would leave the second group's
    /// traffic steered into the first group's keys.
    /// </para>
    /// </remarks>
    internal async Task SyncRoutingAsync(
        GroupId groupId, MlsGroup group, CancellationToken ct = default)
    {
        byte[] address;
        try
        {
            address = GroupMessages.TransportGroupId(group);
        }
        catch (AppComponentException)
        {
            return;
        }

        RoutingIndexRecord? current = await _storage.CurrentRoutingAsync(groupId, ct);

        if (current is not null && current.TransportGroupId.AsSpan().SequenceEqual(address))
            return;

        await _storage.PutRoutingAsync(address, groupId, new EpochId(group.Epoch), ct);
    }

    private MlsGroup? Restore(byte[]? state) =>
        state is null or { Length: 0 } ? null : MlsGroup.Import(state, _cs);
}

/// <summary>
/// One live group, and everything that has to happen around it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It owns the <see cref="MlsGroup"/> reference, and that is not
/// incidental.</b> Two of the operations here replace the object rather than
/// mutate it — a convergence pass that adopts another branch hands back a
/// rebuilt group, and a commit of ours is prepared by merging an instance that
/// then becomes a throwaway. A caller holding its own reference across either
/// would be holding a branch this member has abandoned, which is exactly the
/// fork the engine spends its effort avoiding. So the reference lives here and
/// <see cref="Group"/> is read fresh each time.
/// </para>
/// <para>
/// <b>Everything it composes was already built and tested.</b> Nothing in here
/// re-decides anything: ingest classifies, the publisher owns the publish line,
/// the pass owns branch selection, the archive owns retention. What was missing
/// was an owner, and the bugs that fell out of not having one were all the same
/// shape — a commit of ours that nothing wrote down.
/// </para>
/// </remarks>
public sealed class MarmotSession
{
    private readonly MarmotSessionHost _host;
    private readonly IMarmotStorageProvider _storage;
    private readonly ICipherSuite _cs;
    private readonly ConvergencePolicy _policy;
    private readonly Func<DateTimeOffset> _clock;
    private readonly MessageIngest _ingest;
    private readonly RetainedTransportKeys _keys;

    private MlsGroup _group;

    internal MarmotSession(
        MarmotSessionHost host,
        IMarmotStorageProvider storage,
        ICipherSuite cs,
        ConvergencePolicy policy,
        Func<DateTimeOffset> clock,
        GroupId groupId,
        MlsGroup group,
        SessionRecovery? recovery)
    {
        _host = host;
        _storage = storage;
        _cs = cs;
        _policy = policy;
        _clock = clock;
        _group = group;

        GroupId = groupId;
        Recovery = recovery;

        _ingest = new MessageIngest(storage, host.Epochs.Epochs, clock, host.Archive);
        _keys = new RetainedTransportKeys(host.Archive);
    }

    /// <summary>The group's Marmot id.</summary>
    public GroupId GroupId { get; }

    /// <summary>The live MLS group. Re-read it after any operation here.</summary>
    public MlsGroup Group => _group;

    /// <summary>
    /// What hydration found outstanding, or null for a clean open.
    /// </summary>
    public SessionRecovery? Recovery { get; }

    /// <summary>
    /// Puts one peeled MLS message through ingest, keeping the durable copy in
    /// step.
    /// </summary>
    /// <remarks>
    /// The live state is rewritten only when the epoch actually moved. Ingest
    /// declines far more than it applies — duplicates, other groups, our own
    /// echoes — and re-exporting a group per refused message would make the
    /// common case the expensive one.
    /// </remarks>
    public async Task<IngestResult> IngestAsync(
        byte[] mlsBytes, string? transportId = null, CancellationToken ct = default)
    {
        ulong before = _group.Epoch;

        IngestResult result = await _ingest.IngestAsync(_group, GroupId, mlsBytes, transportId, ct);

        if (_group.Epoch != before)
            await _host.WriteLiveStateAsync(GroupId, _group, ct);

        return result;
    }

    /// <summary>
    /// Peels one transport envelope for this group and puts it through ingest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The epoch-boundary case is the reason this exists.</b> A kind-445
    /// envelope is sealed under the exporter secret of the epoch it was sent
    /// in, and a member who had not yet seen our latest commit seals under the
    /// epoch we have just left. Peeled against the live key alone, that message
    /// fails at the transport layer and ingest never sees MLS bytes — so it is
    /// not deferred, not stored and not replayed: it is gone, with no record
    /// that it existed. Falling back through
    /// <see cref="RetainedTransportKeys"/> is what makes it survive.
    /// </para>
    /// <para>
    /// <b>It matters more for commits than for chat.</b> A competing commit is
    /// framed at the epoch it forks from and sealed under that epoch's key, so
    /// without this fallback a fork is invisible at the transport layer:
    /// nothing reaches ingest, no <c>Retryable</c> record is written, and
    /// <c>ConvergencePass</c> reads an empty candidate list and reports a
    /// settled group that has in fact split.
    /// </para>
    /// <para>
    /// <b>What is not covered, and cannot be.</b> An envelope sealed under an
    /// epoch we have not yet reached opens under no key we hold, and nothing
    /// here keeps the envelope — the durable records are MLS bytes, and there
    /// are none until it peels. That case is left to the relay redelivering.
    /// </para>
    /// <para>
    /// <b>The retry cost is bounded by the archive window and by the address.</b>
    /// Only an envelope whose signature verifies and whose <c>h</c> tag names an
    /// address this group has used inside the retained window is retried at all,
    /// and then at most once per retained epoch. Each retry re-parses and
    /// re-verifies the envelope, because <see cref="ITransportPeeler.Peel"/>
    /// takes one secret and decides everything else itself; widening that seam
    /// to take several would save those checks and cost a breaking change to
    /// every implementation and interop caller, which the bound does not yet
    /// justify.
    /// </para>
    /// </remarks>
    /// <param name="envelope">The transport envelope, as it came off the wire.</param>
    public async Task<IngestResult> ReceiveAsync(
        string envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        IReadOnlyList<RetainedTransportKey> keys =
            await _keys.NewestFirstAsync(GroupId, _group, ct);

        PeelAttempt attempt = TryPeel(envelope, keys[0]);

        // The first attempt answers two questions, which is why it is not a
        // loop iteration like the rest. It tries the key an envelope almost
        // always wants, and it reports which address the envelope names -- a
        // tag nothing outside the peeler may read, because the peeler verifies
        // the event's signature before any field of it is trustworthy.
        if (attempt.Peeled is null && attempt.Retryable && attempt.AddressedTo is { } address)
        {
            List<RetainedTransportKey> older = keys
                .Skip(1)
                .Where(k => address.SequenceEqual(k.TransportGroupId))
                .ToList();

            // Not ours, as far as this member can tell, and that qualifier is
            // the honest one: an address from beyond the retained window is
            // indistinguishable from another group's, and both are equally
            // unusable. Answered before the retries rather than after, so
            // somebody else's traffic costs one signature check and not six.
            if (older.Count == 0 && !address.SequenceEqual(keys[0].TransportGroupId))
                return Refuse(InputRejectionCategory.WrongRecipient);

            foreach (RetainedTransportKey key in older)
            {
                attempt = TryPeel(envelope, key);
                if (attempt.Peeled is not null || !attempt.Retryable)
                    break;
            }
        }

        if (attempt.Peeled is not { } peeled)
        {
            // Retryable and terminal are kept apart because the caller acts on
            // them differently, and because the peeler is the only thing that
            // can tell them apart: a malformed or unsigned envelope will never
            // become valid, while one we simply have no key for might.
            return new IngestResult(
                attempt.Retryable
                    ? new IngestOutcome.TransportDeferred(GroupId)
                    : new IngestOutcome.Ignored(InputRejectionCategory.InvalidEncoding),
                null);
        }

        // A Welcome opens without any group key at all, so it can reach here
        // addressed to nobody in particular. It is joined from rather than
        // ingested into a group, and this door is a group's.
        if (peeled.Kind != PeeledContentKind.GroupMessage)
            return Refuse(InputRejectionCategory.WrongRecipient);

        return await IngestAsync(peeled.MlsBytes, peeled.TransportId, ct);
    }

    /// <summary>One peel attempt under one key.</summary>
    /// <param name="Peeled">What came out, or null if nothing did.</param>
    /// <param name="AddressedTo">
    /// The routing id the envelope named, or null when the peeler refused
    /// before reading it.
    /// </param>
    /// <param name="Retryable">Whether another key could do better.</param>
    private readonly record struct PeelAttempt(
        PeeledMessage? Peeled, byte[]? AddressedTo, bool Retryable);

    private PeelAttempt TryPeel(string envelope, RetainedTransportKey key)
    {
        byte[]? addressedTo = null;

        try
        {
            PeeledMessage peeled = _host.Peeler.Peel(
                envelope,
                id =>
                {
                    addressedTo = id;

                    // Exact equality, never a prefix or a nearest match. A
                    // routing id is public and appears on every kind-445 event,
                    // so anything looser is a way to steer one group's traffic
                    // into another group's keys.
                    //
                    // Fail-safe rather than load-bearing, and it survives
                    // mutation: handing the key over regardless still refuses a
                    // foreign envelope, because the AEAD fails and the address
                    // check below then answers WrongRecipient on the same
                    // evidence. That check is the one doing the work. This one
                    // is kept so the refusal also holds at the point the key
                    // would leave, which is where it is cheapest to be sure of.
                    return id.SequenceEqual(key.TransportGroupId) ? key.ExporterSecret : null;
                });

            return new PeelAttempt(peeled, addressedTo, false);
        }
        catch (PeelFailedException ex)
        {
            return new PeelAttempt(null, addressedTo, ex.Retryable);
        }
    }

    private static IngestResult Refuse(InputRejectionCategory category) =>
        new(new IngestOutcome.Ignored(category), null);

    /// <summary>
    /// Re-runs the messages that were held rather than delivered.
    /// </summary>
    /// <remarks>
    /// Owed after anything that changes what the group can read: a publish
    /// finishing, and a convergence pass adopting a branch. See
    /// <see cref="MessageIngest.ReplayAsync"/>.
    /// </remarks>
    public Task<ReplayResult> ReplayAsync(CancellationToken ct = default) =>
        _ingest.ReplayAsync(_group, GroupId, ct);

    /// <summary>
    /// Runs one convergence pass, adopting the winning branch if it is not ours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The replay is part of this, not a courtesy afterwards.</b> Adopting a
    /// branch makes readable what the branch we left could not decrypt, and
    /// ingest deduplicates on content — so those messages can only come back
    /// through the retry path. A caller left to remember that would eventually
    /// forget, and the symptom is silently missing history.
    /// </para>
    /// <para>
    /// The pass returns the group to carry on with; a reorg means it is a
    /// different object from the one that went in, and the old one is on a
    /// branch this member has abandoned.
    /// </para>
    /// </remarks>
    public async Task<(ConvergencePassResult Pass, ReplayResult Replay)> ConvergeAsync(
        CancellationToken ct = default)
    {
        var pass = new ConvergencePass(
            _storage, _host.Epochs.Epochs, _host.Archive, _cs, _policy, _clock);

        ConvergencePassResult result = await pass.RunAsync(_group, GroupId, ct);

        if (!result.Reorged)
            return (result, new ReplayResult([], 0, [], 0));

        Adopt(result.Group);
        await _host.WriteLiveStateAsync(GroupId, _group, ct);

        return (result, await ReplayAsync(ct));
    }

    /// <summary>
    /// Stages a commit, publishes it, and resolves it by what the relay said.
    /// </summary>
    /// <param name="stage">
    /// How to build the commit —
    /// <see cref="MarmotSelfUpdate.Stage"/>, <see cref="MarmotGroupInvite.Add"/>
    /// or <see cref="MarmotGroupLeave.CommitDepartures"/>, bound to whatever
    /// arguments it needs.
    /// </param>
    /// <param name="envelope">Wraps the staged commit for the wire.</param>
    /// <param name="kind">What sort of operation this is, for recovery.</param>
    /// <remarks>
    /// <para>
    /// <b>The instance handed to <paramref name="stage"/> becomes a
    /// throwaway.</b> Staging on a group the journal owns derives the state the
    /// commit produces, and the only way to derive it is to merge — see
    /// <see cref="GroupJournal.Prepare"/>. So the session immediately points
    /// itself back at a fresh import of the state it held before, and the
    /// merged instance is kept aside as the state to adopt if the relay takes
    /// the commit. <b>The live group therefore never advances before the
    /// publish is confirmed</b>, which is the rule
    /// <see cref="StagedCommit"/> exists for; what changes early is an object
    /// nobody outside this method can see.
    /// </para>
    /// <para>
    /// <b>Indeterminate leaves everything exactly where it is.</b> The group
    /// stays at the old epoch, the staged state and the publish attempt both
    /// stay, and the epoch state stays pending — so ingest keeps refusing,
    /// which is what stops us applying somebody else's commit while our own may
    /// be live. That is the stranded case, and it is recoverable; discarding
    /// the commit to tidy it up is not.
    /// </para>
    /// </remarks>
    public async Task<CommitPublishOutcome> CommitAsync(
        Func<MlsGroup, StagedCommit> stage,
        Func<StagedCommit, string> envelope,
        PendingKind kind = PendingKind.GroupEvolution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(envelope);

        MlsGroup throwaway = _group;
        byte[] before = throwaway.Export();
        var priorEpoch = new EpochId(throwaway.Epoch);

        using StagedCommit staged = stage(throwaway);

        // Back to where we were, on a new object, before anything is published.
        // Deliberately not conditional on the journal having prepared anything:
        // a session whose group was somehow unbound would otherwise silently
        // publish-then-apply on the live instance, which is the one shape this
        // method exists to make impossible.
        Adopt(MlsGroup.Import(before, _cs));

        PendingStateRef pending = await _host.Epochs.BeginPendingAsync(
            GroupId,
            priorEpoch,
            staged.NewEpoch,
            new StagedCommitHandle(CommitPublisher.CommitIdOf(staged.Commit).Value),
            kind,
            ct);

        CommitPublishOutcome outcome = await _host.Publisher.PublishAsync(
            GroupId, staged, envelope(staged), ct);

        switch (outcome)
        {
            case CommitPublishOutcome.Accepted:
                Adopt(throwaway);

                await _host.ConfirmAsync(
                    GroupId,
                    _group,
                    await _storage.GetStagedCommitAsync(GroupId, ct)
                        ?? throw new InvalidOperationException(
                            "A confirmed commit left no prepared state to adopt. The group "
                            + "cannot reach the epoch it just published."),
                    ct);

                await _host.Epochs.ConfirmPublishAsync(pending, ct);
                break;

            case CommitPublishOutcome.Rejected:
                // Nothing anywhere refers to this commit, so the group stays
                // where it already is -- which is where it has been all along.
                await _host.Epochs.RollbackPublishAsync(pending, ct);
                break;

            default:
                // Indeterminate. Everything stays, deliberately.
                break;
        }

        return outcome;
    }

    // -- Outbound application messages --

    /// <summary>
    /// The <see cref="QueuedOutboundIntent.IntentKind"/> of a queued chat send.
    /// </summary>
    /// <remarks>
    /// The queue is shared with whatever else the engine one day defers, and the
    /// payload of a row is opaque to storage. Draining reads this before it
    /// treats a payload as a <see cref="MarmotAppEvent"/>, so a future kind
    /// sitting in the same queue is stepped over rather than decoded as
    /// something it is not.
    /// </remarks>
    public const string AppMessageIntentKind = "app-message";

    /// <summary>
    /// Sends an application message, queueing it durably first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row is written and awaited before any transport call, on every
    /// path.</b> That is the whole ordering, and it is the opposite of the
    /// commit path's problem: there, the dangerous window is forgetting a commit
    /// a relay may hold, so the record has to outlive the local move. Here the
    /// dangerous window is losing a message the user believes they sent, so the
    /// record has to precede the wire. A crash between the two leaves the
    /// message queued, and a queued message that was in fact delivered costs one
    /// duplicate that every receiver drops on content — Dark Matter's dedup is
    /// the MLS bytes, not the transport id.
    /// </para>
    /// <para>
    /// <b>None of <c>CommitPublisher</c>'s three-way machinery appears here, and
    /// its absence is deliberate.</b> Abandon/Reconcile/Adopt exists because a
    /// commit cannot be reissued — MLS refuses to let a member process a commit
    /// it authored, so a commit discarded on a guess can never be recovered from
    /// the relay. An application message has no such constraint. Re-sending is
    /// free, so <see cref="MessageSendOutcome.Rejected"/> and
    /// <see cref="MessageSendOutcome.Indeterminate"/> collapse to one answer:
    /// still queued, try again.
    /// </para>
    /// <para>
    /// <b>An unsettled group is not asked to send.</b> Not a transport
    /// optimisation — a message sealed now under an epoch the group is about to
    /// leave arrives as history from before the commit, readable only inside the
    /// receiver's past-epoch window. Queueing it and wrapping it fresh at drain
    /// time is what a user means by "it sent when the network came back".
    /// </para>
    /// </remarks>
    /// <param name="message">
    /// The payload. Its author must be this member —
    /// <see cref="MarmotAppEvent.RequireSender"/> is checked while the envelope
    /// is built, and a mismatch throws rather than queueing a message no
    /// receiver would accept.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The host was built without an <see cref="IMessageRelay"/>.
    /// </exception>
    public async Task<SendResult> SendAsync(
        MarmotAppEvent message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        IMessageRelay relay = _host.RequireMessageRelay();

        // Before the row, not after. A group we have left must not accumulate a
        // queue at all -- a row written and then refused would be drained by
        // anything that later cleared the Removed flag, which is exactly the
        // "queued sends must never be drained into a group we have left" case
        // ClearIntentsAsync exists for.
        if (await IsRemovedAsync(ct))
            return new SendResult(SendDisposition.Refused, await QueueDepthAsync(ct));

        var intent = new QueuedOutboundIntent(
            NewIntentId(), GroupId, AppMessageIntentKind, message.Encode(), _clock());

        await _storage.PutIntentAsync(intent, ct);

        if (!IsSettled)
            return new SendResult(SendDisposition.Queued, await QueueDepthAsync(ct));

        MessageSendOutcome outcome = await AttemptAsync(relay, intent, message, ct);

        return new SendResult(
            outcome == MessageSendOutcome.Accepted
                ? SendDisposition.Sent
                : SendDisposition.Queued,
            await QueueDepthAsync(ct));
    }

    /// <summary>
    /// Sends what is queued, oldest first, and stops at the first refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It stops rather than skipping.</b> A transport that has just declined
    /// one message is unlikely to take the next, and carrying on would reorder
    /// the queue against a relay that then recovers — the second message
    /// delivered and the first still waiting. Order is worth more than draining
    /// eagerly, so the rest stay queued for the next call.
    /// </para>
    /// <para>
    /// <b>Called by a caller, never by a timer.</b> Retry scheduling and backoff
    /// are not here; this method answers "drain now" and reports what that
    /// achieved.
    /// </para>
    /// <para>
    /// <b>Not gated on the group being settled.</b> Only eviction stops a drain.
    /// Draining an unsettled group is safe because the live group never advances
    /// before a publish is confirmed, so an envelope built here is built at the
    /// epoch the group is still actually in — and a caller that wants to wait
    /// for a settled group can simply not call this.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The host was built without an <see cref="IMessageRelay"/>.
    /// </exception>
    public async Task<DrainResult> DrainAsync(CancellationToken ct = default)
    {
        IMessageRelay relay = _host.RequireMessageRelay();

        if (await IsRemovedAsync(ct))
        {
            // Counted before the clear, because the count is the report. A queue
            // emptied silently is indistinguishable from one that was empty, and
            // the messages dropped here are messages a user wrote.
            int dropped = await QueueDepthAsync(ct);
            await _storage.ClearIntentsAsync(GroupId, ct);

            return new DrainResult(0, 0, dropped);
        }

        IReadOnlyList<QueuedOutboundIntent> queued =
            await _storage.ListIntentsAsync(GroupId, ct);

        int sent = 0;

        // Ordered here as well as in storage. The ordering is a rule of this
        // method, and a rule that holds only because one SQL statement happens
        // to carry an ORDER BY is a rule nothing states.
        foreach (QueuedOutboundIntent intent in queued
            .Where(intent => intent.IntentKind == AppMessageIntentKind)
            .OrderBy(intent => intent.CreatedAt))
        {
            MessageSendOutcome outcome = await AttemptAsync(
                relay, intent, MarmotAppEvent.Decode(intent.Payload), ct);

            if (outcome != MessageSendOutcome.Accepted)
                break;

            sent++;
        }

        return new DrainResult(sent, await QueueDepthAsync(ct), 0);
    }

    /// <summary>
    /// Wraps one queued message, hands it to the transport, and resolves its row.
    /// </summary>
    /// <remarks>
    /// <b>The envelope is built here rather than stored at queue time</b>, which
    /// is the point of storing the event instead of the bytes: the kind-445 wrap
    /// is keyed on the group's exporter secret, which changes every epoch, and
    /// the MLS framing inside is keyed on the epoch too. A message that waited
    /// across a commit has to be sealed at the epoch it actually leaves in.
    /// </remarks>
    private async Task<MessageSendOutcome> AttemptAsync(
        IMessageRelay relay,
        QueuedOutboundIntent intent,
        MarmotAppEvent message,
        CancellationToken ct)
    {
        string envelope = GroupMessages.Send(_group, _host.Peeler, message, SenderIdentity());

        // Sealing the envelope moved the sender ratchet, in memory. Persisted
        // here -- before the send, not after -- because a crash in between
        // otherwise comes back with the generation counter rewound, and the next
        // message encrypts a different plaintext under a key and nonce this one
        // has already used. NOT covered by any test in this suite: it is only
        // observable across a process restart, which needs a crash the send path
        // has no way to stage.
        await _host.WriteLiveStateAsync(GroupId, _group, ct);

        MessageSendOutcome outcome;
        try
        {
            outcome = await relay.SendAsync(envelope, ct);
        }
        catch (Exception)
        {
            // Indeterminate, not rejected -- the same reading CommitPublisher
            // gives a throwing transport. It changes nothing here, since both
            // keep the row, but the two answers are still different facts and a
            // caller logging them should not be told the wrong one.
            outcome = MessageSendOutcome.Indeterminate;
        }

        if (outcome == MessageSendOutcome.Accepted)
        {
            await _storage.DeleteIntentAsync(intent.Id, ct);
            return outcome;
        }

        // Diagnostic only. Nothing reads Attempts to decide anything: an
        // application message stays retryable however many times it has failed,
        // and a bound here would silently discard a user's message on a long
        // outage.
        await _storage.PutIntentAsync(intent with { Attempts = intent.Attempts + 1 }, ct);

        return outcome;
    }

    /// <summary>
    /// Whether the group is settled enough to put a message on the wire.
    /// </summary>
    /// <remarks>
    /// <b>A group with no recorded state counts as settled</b>, matching
    /// <see cref="EpochManager.CanIngest"/> and
    /// <see cref="DurableEpochManager.BeginPendingAsync"/>, both of which read
    /// absence as <c>Stable</c>. Absent is the ordinary condition of a group
    /// nobody has staged a commit in — the manager only ever writes a state for
    /// a group that has left Stable — so reading it as unsettled would queue
    /// every message in every freshly opened group and drain none of them.
    /// </remarks>
    private bool IsSettled =>
        _host.Epochs.Epochs.GetState(GroupId) is not { } state || state.IsStable;

    private async Task<bool> IsRemovedAsync(CancellationToken ct) =>
        await _storage.GetGroupAsync(GroupId, ct) is { Removed: true };

    private async Task<int> QueueDepthAsync(CancellationToken ct) =>
        (await _storage.ListIntentsAsync(GroupId, ct)).Count;

    /// <summary>
    /// This member's account key, as the ratchet tree has it.
    /// </summary>
    /// <remarks>
    /// Taken from the tree rather than from the event's own <c>pubkey</c>, so
    /// that <see cref="MarmotAppEvent.RequireSender"/> is still checking one
    /// against the other. Reading it off the event would make that check compare
    /// a value with itself and pass for a message every receiver rejects.
    /// </remarks>
    private byte[] SenderIdentity()
    {
        foreach ((uint index, byte[] identity) in _group.GetMembers())
        {
            if (index == _group.MyLeafIndex)
                return identity;
        }

        throw new InvalidOperationException(
            $"Group {GroupId} does not list our own leaf {_group.MyLeafIndex} as a member.");
    }

    /// <summary>
    /// A fresh random id for a queue row.
    /// </summary>
    /// <remarks>
    /// <b>The one place a <see cref="MessageId"/> is not content-derived</b>,
    /// and it has to be. The MLS bytes it would be derived from do not exist
    /// until the envelope is built, which is at drain time; and hashing the
    /// event instead would collapse two identical messages a user deliberately
    /// sent twice into one row, so the second would never leave. This id names a
    /// queue row, not a message — the message's content id is computed by the
    /// receiver from the bytes, exactly as before.
    /// </remarks>
    private static MessageId NewIntentId() =>
        new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Releases the group from durable custody.
    /// </summary>
    /// <remarks>
    /// The live state is written first. Closing is the last chance to record
    /// where the group got to, and a close that unbound first would be unable
    /// to.
    /// </remarks>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        await _host.WriteLiveStateAsync(GroupId, _group, ct);
        GroupJournal.Unbind(_group);
    }

    /// <summary>
    /// Points the session at a different MLS object, moving custody with it.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Binding the new one is what keeps its commits
    /// recordable; unbinding the old one is what stops a stale reference
    /// writing through to the same rows — two objects bound to one group would
    /// each prepare commits into the same single row, and the second would
    /// overwrite the first's description of a commit that may already be on a
    /// relay.
    /// </remarks>
    private void Adopt(MlsGroup group)
    {
        if (ReferenceEquals(group, _group))
            return;

        GroupJournal.Unbind(_group);
        GroupJournal.Bind(group, GroupId, _storage, _clock);
        _group = group;

        // The retained transport keys describe the branch we are leaving. See
        // RetainedTransportKeys.Invalidate for why the epoch alone does not
        // settle this.
        //
        // This line is UNCOVERED and removing it passes the suite. The case
        // that needs it is a reorg onto a branch of equal depth, which lands on
        // an epoch numbered the same as the one it left -- and which branch
        // wins a tie is decided by commit ordering over freshly generated keys,
        // so a test that reproduced it would be deciding the tie by luck. The
        // invalidation itself is tested; its being called from here is not.
        _keys.Invalidate();
    }
}
