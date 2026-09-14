using DotnetMls.Crypto;
using DotnetMls.Group;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.Ingest;
using Scramble.Marmot.Storage;

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

    /// <param name="relay">Where a commit's bytes go. See <see cref="ICommitRelay"/>.</param>
    public MarmotSessionHost(
        IMarmotStorageProvider storage,
        ICipherSuite cipherSuite,
        ICommitRelay relay,
        ConvergencePolicy policy,
        Func<DateTimeOffset> clock)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _cs = cipherSuite ?? throw new ArgumentNullException(nameof(cipherSuite));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        ArgumentNullException.ThrowIfNull(relay);

        Archive = new EpochArchive(storage, cipherSuite, policy, clock);
        Epochs = new DurableEpochManager(new EpochManager(), storage, clock);
        Publisher = new CommitPublisher(storage, relay, clock);
    }

    /// <summary>The epoch state machine, durable.</summary>
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
    }
}
