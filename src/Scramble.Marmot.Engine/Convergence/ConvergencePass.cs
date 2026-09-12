using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Storage;

namespace Scramble.Marmot.Engine.Convergence;

/// <summary>What one convergence pass decided.</summary>
/// <param name="Status">How far the group is from having settled its history.</param>
/// <param name="Group">
/// The group to carry on with. The same instance unless a reorg happened, in
/// which case it is the rebuilt one and the caller must adopt it — the old
/// instance is on a branch this member has abandoned.
/// </param>
/// <param name="Reorged">Whether the live branch was replaced.</param>
/// <param name="Trace">
/// Why the selector chose what it chose, or null when no selection was made.
/// </param>
/// <param name="Refused">Stored commits that could not be turned into branches.</param>
/// <param name="Retired">
/// Commits given up on for good, because they fork beyond the rewind horizon
/// and the horizon only moves away from them.
/// </param>
public sealed record ConvergencePassResult(
    ConvergenceStatus Status,
    MlsGroup Group,
    bool Reorged,
    BranchSelectionTrace? Trace,
    IReadOnlyList<RefusedCommit> Refused,
    IReadOnlyList<MessageId> Retired);

/// <summary>
/// The step that makes the convergence machinery reachable.
/// </summary>
/// <remarks>
/// <para>
/// Ingest records a commit it cannot apply and stops there. Every piece needed
/// to do something about it — the archive, the materializer, the selector, the
/// ordering rule — existed and had no caller, so a fork resolved itself only in
/// tests. This is the pass that reads what ingest could not apply, builds the
/// branches it describes, and either keeps the branch we hold or moves onto the
/// one the group agrees on.
/// </para>
/// <para>
/// <b>Deciding is the dangerous part, not building.</b> Everything up to
/// selection runs on throwaway copies and can be repeated freely. So the pass
/// declines to decide far more readily than it decides: while input is still
/// arriving, while a branch is unclassified, while our own branch cannot be
/// described. A member that decides early decides on a partial candidate set,
/// and two members with different partial sets reach different answers from
/// identical rules — which is the one failure convergence exists to prevent.
/// </para>
/// <para>
/// <b>A branch that can never be assessed is given up on, not kept.</b> The
/// rewind horizon only moves forward, so a commit forking below it is not
/// merely unevaluable now — it is unevaluable for good, and no member will ever
/// adopt it. Left waiting it returns to every later pass with the same answer,
/// and a pass holding an unassessable branch cannot report itself settled. One
/// commit framed at an ancient epoch would then stop the group converging at
/// all, permanently, whatever else arrived. So it is retired on sight: refused
/// once, terminally, before any work is spent on it.
/// </para>
/// <para>
/// <b>What this does not do.</b> Adopting a branch makes messages readable that
/// were not readable before, and re-delivering those is a separate obligation
/// the caller still owes — ingest deduplicates on content, so a replay has to
/// go through a retry path rather than through the front door. The records are
/// left in a state that says so.
/// </para>
/// </remarks>
public sealed class ConvergencePass
{
    private readonly IMarmotStorageProvider _storage;
    private readonly EpochManager _epochs;
    private readonly EpochArchive _archive;
    private readonly ICipherSuite _cs;
    private readonly ConvergencePolicy _policy;
    private readonly CanonicalizationPipeline _pipeline;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="policy">
    /// The convergence policy. Checked on the way in, because a policy that
    /// reaches branch selection unvalidated has already had its chance to fork
    /// the group.
    /// </param>
    public ConvergencePass(
        IMarmotStorageProvider storage,
        EpochManager epochs,
        EpochArchive archive,
        ICipherSuite cipherSuite,
        ConvergencePolicy policy,
        Func<DateTimeOffset> clock)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _epochs = epochs ?? throw new ArgumentNullException(nameof(epochs));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _cs = cipherSuite ?? throw new ArgumentNullException(nameof(cipherSuite));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        _policy.RequireAcceptable((ulong)MarmotGroupSettings.MaxPastEpochs);
        _pipeline = new CanonicalizationPipeline(_policy);
    }

    /// <summary>
    /// Runs one bounded pass over the commits ingest could not apply.
    /// </summary>
    /// <param name="live">The group as it stands.</param>
    /// <param name="groupId">Its Marmot group id.</param>
    public async Task<ConvergencePassResult> RunAsync(
        MlsGroup live, GroupId groupId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(live);

        IReadOnlyList<MessageRecord> retryable =
            await _storage.ListMessagesByStateAsync(groupId, MessageRecordState.Retryable, ct);

        var byId = new Dictionary<MessageId, MessageRecord>();
        var stored = new List<StoredCommit>();
        var retired = new List<MessageRecord>();

        foreach (MessageRecord record in retryable)
        {
            if (ToStoredCommit(record, live) is not { } commit)
                continue;

            if (BeyondHorizon(live, commit.SourceEpoch))
            {
                retired.Add(record);
                continue;
            }

            stored.Add(commit);
            byId[record.Id] = record;
        }

        await RetireAsync(retired, ct);
        IReadOnlyList<MessageId> retiredIds = retired.Select(r => r.Id).ToList();

        // Nothing competing means nothing to decide, and saying Settled is not a
        // shortcut: every input is accounted for and none of them is a branch.
        if (stored.Count == 0)
            return new ConvergencePassResult(
                ConvergenceStatus.Settled, live, false, null, [], retiredIds);

        var liveEpoch = new EpochId(live.Epoch);
        EpochWindow window = await _archive.LoadWindowAsync(groupId, liveEpoch, ct);

        // Without a description of our own tip this member cannot put its branch
        // into the comparison on the same terms as everyone else's. Waiting does
        // not produce one -- the moment it could have been read has passed -- so
        // this is Blocked rather than Syncing, which is the difference between
        // a client that says "catching up" and one that says "I cannot".
        if (window.TipAt(liveEpoch) is not { } liveTip)
        {
            return new ConvergencePassResult(
                ConvergenceStatus.Blocked, live, false, null, [], retiredIds);
        }

        IReadOnlyList<MessageRecord> witnessable = await WitnessableAsync(groupId, ct);

        var materializer = new CandidateMaterializer(_policy, window.Restore);

        MaterializationResult materialized = materializer.Materialize(
            live, liveTip, stored, probe => WitnessesOn(probe, witnessable, _cs));

        ConvergenceStatus status = Classify(stored, materialized, retryable);

        if (status != ConvergenceStatus.Settled)
        {
            return new ConvergencePassResult(
                status, live, false, null, materialized.Refused, retiredIds);
        }

        BranchSelectionTrace trace = BranchSelectionAudit.SelectCanonicalTraced(
            live.Epoch, materialized.Candidates, _policy);

        // Every candidate forks beyond the rewind horizon, our own branch
        // included -- which can only happen if the live branch itself is out of
        // range, and there is no branch this member may adopt.
        if (trace.SelectedBranchId is null)
        {
            return new ConvergencePassResult(
                ConvergenceStatus.Blocked, live, false, trace, materialized.Refused, retiredIds);
        }

        if (string.Equals(trace.SelectedBranchId, liveTip.BranchId, StringComparison.Ordinal))
        {
            return new ConvergencePassResult(
                ConvergenceStatus.Settled, live, false, trace, materialized.Refused, retiredIds);
        }

        BranchCandidate winner = materialized.Candidates.Single(
            c => string.Equals(c.Id, trace.SelectedBranchId, StringComparison.Ordinal));

        // Built completely before anything is written down. A failure here
        // leaves the caller holding the group it came in with, which is the only
        // outcome that is always recoverable: staying on a losing branch is a
        // disagreement later convergence can settle, while a half-adopted one is
        // a group whose state matches nobody's.
        ReorgResult reorg = materializer.Reorg(winner, stored);

        await AdoptAsync(groupId, winner, reorg, byId, ct);

        return new ConvergencePassResult(
            ConvergenceStatus.Settled, reorg.Group, true, trace, materialized.Refused, retiredIds);
    }

    /// <summary>
    /// Reads a stored record as a commit convergence can consider, or null.
    /// </summary>
    /// <remarks>
    /// <b>Ours is recognised by the committing leaf, not by comparing bytes.</b>
    /// Our own commit comes back off the relay like anybody else's and ingest
    /// cannot apply it either, so it lands here — and MLS will not replay a
    /// commit it authored, so one taken for a competitor materialises as a
    /// branch that cannot apply and quietly leaves the race with one runner. A
    /// leaf index is stable for a member across the epochs in the window; the
    /// case where one is reused belongs to a member who has been removed, and a
    /// removed member's group does not converge.
    /// </remarks>
    private static StoredCommit? ToStoredCommit(MessageRecord record, MlsGroup live) =>
        FramedCommit(record) is { } framed
            ? new StoredCommit(
                record.Id,
                record.SourceEpoch,
                record.Wire,
                IsOurs: framed.Content.Sender.LeafIndex == live.MyLeafIndex)
            : null;

    /// <summary>
    /// Whether a commit forks further back than this member may ever rewind.
    /// </summary>
    /// <remarks>
    /// <b>The same bound <see cref="BranchSelection.IsEligible"/> applies</b>,
    /// asked before the work rather than after it — a branch the selector would
    /// refuse as beyond the horizon need not be built to find that out.
    /// Answering it here is also what makes the refusal permanent: eligibility
    /// is measured from the current tip, and the tip only advances, so a commit
    /// that fails this today fails it on every later pass too.
    /// </remarks>
    private bool BeyondHorizon(MlsGroup live, EpochId forkEpoch) =>
        live.Epoch > forkEpoch.Value
        && live.Epoch - forkEpoch.Value > _policy.MaxRewindCommits;

    /// <summary>
    /// Gives up on commits that can never be rebuilt.
    /// </summary>
    /// <remarks>
    /// <see cref="MessageRecordState.Failed"/> rather than deletion, and with a
    /// reason: a branch abandoned because history moved on is something a user
    /// may later ask about, and "we never saw it" is a different answer from
    /// "we saw it too late".
    /// </remarks>
    private async Task RetireAsync(IReadOnlyList<MessageRecord> retired, CancellationToken ct)
    {
        DateTimeOffset now = _clock();

        foreach (MessageRecord record in retired)
        {
            await _storage.PutMessageAsync(
                record with
                {
                    State = MessageRecordState.Failed,
                    UpdatedAt = now,
                    Reason = $"forks at epoch {record.SourceEpoch.Value}, beyond the rewind "
                        + $"horizon of {_policy.MaxRewindCommits}",
                },
                ct);
        }
    }

    /// <summary>The record read as a framed commit, or null if it is not one.</summary>
    private static PublicMessage? FramedCommit(MessageRecord record)
    {
        PublicMessage framed;
        try
        {
            MlsMessage message = MlsMessage.ReadFrom(new TlsReader(record.Wire));
            if (message.WireFormat != WireFormat.MlsPublicMessage)
                return null;

            framed = (PublicMessage)message.Body;
        }
        catch (Exception ex) when (ex is TlsDecodingException or ArgumentException
                                       or InvalidCastException)
        {
            return null;
        }

        return framed.Content.ContentType == ContentType.Commit ? framed : null;
    }

    /// <summary>
    /// Where the group stands, in the pipeline's terms.
    /// </summary>
    /// <remarks>
    /// The two mappings that carry meaning. A commit refused for want of replay
    /// budget is <i>unresolved</i> — the pass ran out of room, so there is work
    /// left and another pass will do it. A commit refused for want of a retained
    /// snapshot is <i>blocking</i> — the state it forks from is gone and no
    /// amount of waiting brings it back, so a client that shows a spinner for
    /// that is lying to the user indefinitely.
    /// </remarks>
    private ConvergenceStatus Classify(
        IReadOnlyList<StoredCommit> stored,
        MaterializationResult materialized,
        IReadOnlyList<MessageRecord> records)
    {
        var inputs = stored.Select(c => c.Id).ToHashSet();

        var unresolved = materialized.Refused
            .Where(r => r.Reason == MaterializationRefusal.BudgetExhausted)
            .Select(r => r.Id)
            .ToHashSet();

        var outcome = new CanonicalizationOutcome(
            inputs,
            inputs.Where(id => !unresolved.Contains(id)).ToHashSet(),
            materialized.Refused.Any(r => r.Reason == MaterializationRefusal.NoSnapshot));

        ulong nowMs = (ulong)_clock().ToUnixTimeMilliseconds();

        // The last convergence-relevant arrival, not the last message of any
        // kind: traffic that cannot change the candidate set must not keep
        // resetting the window, or a chatty group never settles.
        ulong lastInputMs = records
            .Where(r => inputs.Contains(r.Id))
            .Select(r => (ulong)r.UpdatedAt.ToUnixTimeMilliseconds())
            .DefaultIfEmpty(0UL)
            .Max();

        return _pipeline.Classify(nowMs, lastInputMs, outcome);
    }

    /// <summary>
    /// The application messages that might vouch for a branch.
    /// </summary>
    /// <remarks>
    /// Both the ones we delivered and the ones we could not. A message that
    /// failed to decrypt on the branch we hold is exactly the kind that
    /// witnesses a competing one, and a message we delivered is what witnesses
    /// ours.
    /// </remarks>
    private async Task<IReadOnlyList<MessageRecord>> WitnessableAsync(
        GroupId groupId, CancellationToken ct)
    {
        var records = new List<MessageRecord>();

        records.AddRange(
            await _storage.ListMessagesByStateAsync(groupId, MessageRecordState.Processed, ct));

        records.AddRange(
            await _storage.ListMessagesByStateAsync(groupId, MessageRecordState.PeelDeferred, ct));

        return records;
    }

    /// <summary>
    /// Which of those messages a branch can actually read.
    /// </summary>
    /// <remarks>
    /// <b>Never against the group it is handed.</b> Reading a message consumes
    /// ratchet keys, and one of the groups handed here is the live one — asking
    /// it to prove a branch would spend the keys it needs to deliver real
    /// traffic, and the damage would show up much later as messages that cannot
    /// be read. So every attempt runs against a copy, which also makes a failed
    /// decryption free of consequences.
    /// </remarks>
    private static IReadOnlyList<AppWitness> WitnessesOn(
        MlsGroup branch, IReadOnlyList<MessageRecord> witnessable, ICipherSuite cs)
    {
        if (witnessable.Count == 0)
            return [];

        MlsGroup probe = MlsGroup.Import(branch.Export(), cs);

        var witnesses = new List<AppWitness>();

        foreach (MessageRecord record in witnessable)
        {
            try
            {
                ReceivedGroupMessage received = GroupMessages.Receive(probe, record.Wire);
                witnesses.Add(new AppWitness(record.SourceEpoch.Value, received.SenderIdentity));
            }
            catch (Exception)
            {
                // Not readable on this branch, which is the answer rather than a
                // failure: a witness counts only where it can be read.
            }
        }

        return witnesses;
    }

    /// <summary>
    /// Brings the durable record into line with the branch that was adopted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One transaction, because these writes are only correct together. A member
    /// whose MLS state adopted a branch while its records still describe the old
    /// one shows history that no longer exists and cannot read the history that
    /// does.
    /// </para>
    /// <para>
    /// Invalidation runs before the adopted commits are marked, not after: it
    /// sweeps everything produced past the fork, which necessarily includes the
    /// commits that built the winning branch. Marking them afterwards is what
    /// separates the history we took from the history we dropped.
    /// </para>
    /// </remarks>
    private async Task AdoptAsync(
        GroupId groupId,
        BranchCandidate winner,
        ReorgResult reorg,
        IReadOnlyDictionary<MessageId, MessageRecord> byId,
        CancellationToken ct)
    {
        DateTimeOffset now = _clock();
        var tip = new EpochId(reorg.Group.Epoch);

        await using IStorageTransaction tx = await _storage.BeginTransactionAsync(ct);

        await _storage.InvalidateAfterEpochAsync(groupId, new EpochId(winner.ForkEpoch), ct);

        // The sweep above stops at the fork epoch, and at exactly that epoch the
        // two kinds of record want opposite answers. An application message
        // framed there was sent under keys both branches share, so it survives
        // the reorg and must not be swept. A commit framed there is a branch
        // head, and only one head survives. Same epoch, opposite fates -- which
        // is why the sweep goes by epoch and this goes by kind.
        IReadOnlyList<MessageRecord> delivered =
            await _storage.ListMessagesByStateAsync(groupId, MessageRecordState.Processed, ct);

        foreach (MessageRecord record in delivered)
        {
            if (record.SourceEpoch.Value != winner.ForkEpoch || FramedCommit(record) is null)
                continue;

            await _storage.PutMessageAsync(
                record with { State = MessageRecordState.EpochInvalidated, UpdatedAt = now }, ct);
        }

        foreach (MessageId applied in reorg.Applied)
        {
            if (byId.TryGetValue(applied, out MessageRecord? record))
            {
                await _storage.PutMessageAsync(
                    record with { State = MessageRecordState.Processed, UpdatedAt = now }, ct);
            }
        }

        if (await _storage.GetGroupAsync(groupId, ct) is { } group)
            await _storage.PutGroupAsync(group with { Epoch = tip, UpdatedAt = now }, ct);

        await tx.CommitAsync(ct);

        // Outside the transaction, and last. The archive is a cache of state we
        // can rebuild; the records are the history we cannot. If this fails the
        // group is correct and merely unable to evaluate the next fork from
        // here, which the pass reports as Blocked rather than getting wrong.
        await _archive.CaptureAsync(
            groupId,
            reorg.Group,
            new CommitTip(
                winner.TipPriority, new MessageId(winner.TipDigest), winner.TipCommitter),
            ct);

        _epochs.SetStable(groupId, tip);
    }
}
