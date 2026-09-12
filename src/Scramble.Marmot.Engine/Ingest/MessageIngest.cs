using DotnetMls.Codec;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Ingest;
using Scramble.Marmot.Storage;

namespace Scramble.Marmot.Engine.Ingest;

/// <summary>What ingest produced, alongside the disposition.</summary>
/// <param name="Outcome">The disposition.</param>
/// <param name="Message">
/// The decrypted application event, when one was produced. Null for handshake
/// messages and for every refusal.
/// </param>
public sealed record IngestResult(IngestOutcome Outcome, ReceivedGroupMessage? Message);

/// <summary>What a replay pass delivered, and what it gave up on.</summary>
/// <param name="Delivered">
/// Messages readable now that were not before, oldest epoch first. These have
/// not been shown to anyone yet — the caller owes them to the user.
/// </param>
/// <param name="StillDeferred">
/// How many are still waiting. Not a failure: the commit that would make them
/// readable may simply not have arrived.
/// </param>
/// <param name="Retired">
/// Messages given up on, because the epoch they were sent in has fallen out of
/// the delivery window and the keys are gone.
/// </param>
public sealed record ReplayResult(
    IReadOnlyList<ReceivedGroupMessage> Delivered,
    int StillDeferred,
    IReadOnlyList<MessageId> Retired);

/// <summary>
/// The single door every inbound MLS message comes through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Most of what a relay hands a client should be declined.</b> Other
/// members' traffic for groups we are not in, our own echoes, duplicates under
/// fresh transport envelopes, messages from before we joined. A client that
/// treats those as errors looks broken while working, and buries the failures
/// that matter. So the chain below is mostly refusals, each one classified
/// rather than thrown.
/// </para>
/// <para>
/// <b>Order is the design.</b> The cheap, certain refusals come first —
/// duplicate, unknown group, our own echo — because they are the common case
/// and none of them needs the group's keys. Decryption is last, because it is
/// the only step that costs anything and the only one that can advance state.
/// </para>
/// <para>
/// <b>Deduplication is content-derived and that is not a detail.</b> The key is
/// <see cref="MessageId.FromMlsBytes"/> — SHA-256 over the MLS bytes — never
/// the transport event id. The same MLS message legitimately arrives under
/// different Nostr envelopes, so a transport id can only ever be a cheap
/// pre-filter. Deduplicating on it is a conformance bug and it is the exact
/// mistake the previous engine made.
/// </para>
/// <para>
/// <b>A held message is a promise, and <see cref="ReplayAsync"/> is how it is
/// kept.</b> A message that cannot be applied because the group is
/// mid-transition comes back <see cref="IngestOutcome.Buffered"/>, and one that
/// cannot be decrypted comes back
/// <see cref="IngestOutcome.TransportDeferred"/>. Nothing on this path can
/// resolve either — what has to change is outside it — so the caller runs a
/// replay once something has: a publish finishing, or a convergence pass
/// adopting a branch.
/// </para>
/// </remarks>
public sealed class MessageIngest(
    IMarmotStorageProvider storage,
    EpochManager epochs,
    Func<DateTimeOffset> clock,
    EpochArchive? archive = null)
{
    private readonly IMarmotStorageProvider _storage =
        storage ?? throw new ArgumentNullException(nameof(storage));

    private readonly EpochManager _epochs =
        epochs ?? throw new ArgumentNullException(nameof(epochs));

    private readonly Func<DateTimeOffset> _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// The pinned convergence policy, for the delivery window alone.
    /// </summary>
    /// <remarks>
    /// Ingest does not converge, but it does have to know how far back a
    /// message stays readable — and that number is the same one convergence
    /// pins to the group's MLS window. Reading it from anywhere else would let
    /// the two drift, and the drift would show as messages retired while the
    /// group could still have read them.
    /// </remarks>
    private readonly ConvergencePolicy _policy = ConvergencePolicy.V1;

    /// <summary>
    /// Where each epoch reached is kept, so a fork can be evaluated later.
    /// </summary>
    /// <remarks>
    /// Optional because the engine is still additive and a caller that has no
    /// interest in convergence should not be made to construct one. A caller
    /// that omits it gets a group that ingests correctly and can never evaluate
    /// a competing branch, which is the behaviour this class had before the
    /// archive existed.
    /// </remarks>
    private readonly EpochArchive? _archive = archive;

    /// <summary>
    /// Ingests one peeled MLS message against a group.
    /// </summary>
    /// <param name="group">The live group the message is addressed to.</param>
    /// <param name="groupId">Its Marmot group id.</param>
    /// <param name="mlsBytes">The peeled MLS message bytes.</param>
    /// <param name="transportId">The transport envelope id, if known.</param>
    public async Task<IngestResult> IngestAsync(
        MlsGroup group,
        GroupId groupId,
        byte[] mlsBytes,
        string? transportId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(mlsBytes);

        var id = MessageId.FromMlsBytes(mlsBytes);

        // 1. Deduplicate on content. Checked before anything else because it is
        //    the most common refusal and the cheapest: a relay re-delivering the
        //    same MLS message under a new envelope is routine, not an anomaly.
        if (await _storage.GetMessageAsync(id, ct) is not null)
            return Refuse(InputRejectionCategory.Duplicate);

        if (transportId is not null)
            await _storage.PutTransportSeenAsync(transportId, ct);

        // 2. Do we have this group at all, and are we still in it? A removed
        //    member holds keys for an epoch the group has left, so nothing it
        //    receives can be applied -- and no retry changes that, which is why
        //    this is LocalState rather than a deferral.
        GroupRecord? record = await _storage.GetGroupAsync(groupId, ct);
        if (record is null)
            return Refuse(InputRejectionCategory.UnknownGroup);

        if (record.Removed)
            return new IngestResult(new IngestOutcome.LocalState(LocalIngestState.Removed), null);

        // 3. Is it decodable as an MLS message at all? Decoded ahead of the
        //    ingestibility gate below, because the epoch a message was produced
        //    in is readable on its wire and every record written from here on
        //    needs it -- a buffered commit is a convergence candidate, and one
        //    filed under the wrong fork epoch describes a branch forking from a
        //    state it was never built on.
        MlsMessage message;
        try
        {
            message = MlsMessage.ReadFrom(new TlsReader(mlsBytes));
        }
        catch (Exception ex) when (ex is TlsDecodingException or ArgumentException)
        {
            // The one record whose source epoch cannot be read, so the local
            // epoch stands in. Harmless because the bytes are terminally
            // refused: nothing replays them and no branch is built from them.
            await PersistAsync(id, groupId, new EpochId(group.Epoch), mlsBytes, transportId,
                MessageRecordState.Failed, $"undecodable: {ex.Message}", ct);

            return Refuse(InputRejectionCategory.InvalidEncoding);
        }

        EpochId sourceEpoch = SourceEpochOf(message);

        // 4. Can the group take input right now? Mid-publish it cannot: applying
        //    an inbound commit while our own is staged and unacknowledged would
        //    fork us from the epoch we are about to ask everyone to adopt.
        if (!_epochs.CanIngest(groupId))
        {
            await PersistAsync(id, groupId, sourceEpoch, mlsBytes, transportId,
                MessageRecordState.Created, "group is not ingestible", ct);

            return new IngestResult(
                new IngestOutcome.Buffered(groupId, new EpochId(group.Epoch)), null);
        }

        return await DispatchAsync(
            group, groupId, id, sourceEpoch, mlsBytes, message, transportId, ct);
    }

    /// <summary>
    /// Re-runs the messages that were held rather than delivered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The promise <see cref="IngestOutcome.Buffered"/> and
    /// <see cref="IngestOutcome.TransportDeferred"/> make.</b> Both say the
    /// bytes were kept and will be tried again, and nothing in ingest can keep
    /// that on its own — a message is held because the group could not read it
    /// yet, so something outside has to change first. Two things do: a publish
    /// finishing, which lets a group ingest again, and a convergence pass
    /// adopting a branch, which makes readable what the branch we left could
    /// not decrypt. Call this after either.
    /// </para>
    /// <para>
    /// <b>It cannot go through the front door.</b> Ingest deduplicates on
    /// content, and the record being replayed is exactly what that check finds
    /// — so every retry would be refused as a duplicate of itself. This takes
    /// the same path from the decode onwards and skips only the two questions a
    /// stored record has already answered.
    /// </para>
    /// <para>
    /// <b>Bounded by the delivery window, not by a count of attempts.</b> An
    /// application message stays readable for as many epochs back as the group
    /// keeps keys for, and past that no number of retries helps. A retry budget
    /// would be wrong in both directions at once: it would give up on a message
    /// still perfectly deliverable, and keep retrying one whose keys are already
    /// gone. Attempts are counted all the same — how often we tried is worth
    /// seeing — they are just not what decides.
    /// </para>
    /// </remarks>
    /// <param name="group">The group as it stands, after whatever changed.</param>
    /// <param name="groupId">Its Marmot group id.</param>
    public async Task<ReplayResult> ReplayAsync(
        MlsGroup group, GroupId groupId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (!_epochs.CanIngest(groupId))
            return new ReplayResult([], 0, []);

        if (await _storage.GetGroupAsync(groupId, ct) is not { Removed: false })
            return new ReplayResult([], 0, []);

        // Oldest epoch first, and within an epoch the order they arrived in.
        // That is the best order available: where a message sits within its
        // epoch is inside the ciphertext, so until it is read there is nothing
        // else to sort on.
        var held = (await _storage.ListMessagesByStateAsync(
                groupId, MessageRecordState.PeelDeferred, ct))
            .Concat(await _storage.ListMessagesByStateAsync(
                groupId, MessageRecordState.Created, ct))
            .OrderBy(r => r.SourceEpoch.Value)
            .ThenBy(r => r.CreatedAt)
            .ToList();

        var delivered = new List<ReceivedGroupMessage>();
        var retired = new List<MessageId>();
        int stillDeferred = 0;

        foreach (MessageRecord record in held)
        {
            if (record.State == MessageRecordState.PeelDeferred
                && BeyondDeliveryWindow(group, record.SourceEpoch))
            {
                await RetireAsync(record, ct);
                retired.Add(record.Id);
                continue;
            }

            MlsMessage message;
            try
            {
                message = MlsMessage.ReadFrom(new TlsReader(record.Wire));
            }
            catch (Exception ex) when (ex is TlsDecodingException or ArgumentException)
            {
                // The buffered path keeps bytes without reading them, so this is
                // the first chance anything has had to notice. Terminal either
                // way: undecodable bytes do not become decodable.
                await PersistAsync(record.Id, groupId, record.SourceEpoch, record.Wire,
                    record.TransportId, MessageRecordState.Failed,
                    $"undecodable: {ex.Message}", ct);

                retired.Add(record.Id);
                continue;
            }

            IngestResult result = await DispatchAsync(
                group, groupId, record.Id, record.SourceEpoch, record.Wire, message,
                record.TransportId, ct);

            await PreserveAsync(record, ct);

            if (result.Message is { } readable)
                delivered.Add(readable);

            if (result.Outcome is IngestOutcome.Buffered or IngestOutcome.TransportDeferred)
                stillDeferred++;
        }

        return new ReplayResult(delivered, stillDeferred, retired);
    }

    /// <summary>
    /// Whether a message was sent too long ago for the group to still read it.
    /// </summary>
    /// <remarks>
    /// The window is the convergence policy's, which
    /// <see cref="ConvergencePolicy.RequireWindowMatches"/> pins to the MLS one
    /// the group is actually running with. The two must agree, or this asks a
    /// question the group answers differently.
    /// </remarks>
    private bool BeyondDeliveryWindow(MlsGroup group, EpochId sourceEpoch) =>
        group.Epoch > sourceEpoch.Value
        && group.Epoch - sourceEpoch.Value > _policy.AppMessagePastEpochLimit;

    /// <summary>Gives up on a message whose keys the group no longer holds.</summary>
    private async Task RetireAsync(MessageRecord record, CancellationToken ct) =>
        await _storage.PutMessageAsync(
            record with
            {
                State = MessageRecordState.Failed,
                UpdatedAt = _clock(),
                Reason = $"sent at epoch {record.SourceEpoch.Value}, beyond the "
                    + $"{_policy.AppMessagePastEpochLimit}-epoch delivery window",
            },
            ct);

    /// <summary>
    /// Keeps what the stored record knew that reprocessing does not.
    /// </summary>
    /// <remarks>
    /// The handlers write a record from scratch, which is right for a message
    /// arriving for the first time and wrong for one being retried: it would
    /// stamp a fresh arrival time onto bytes that have been held for hours, and
    /// the replay orders by that time. The attempt count is carried forward
    /// here too — it decides nothing, but how often a message has been tried is
    /// worth being able to see.
    /// </remarks>
    private async Task PreserveAsync(MessageRecord before, CancellationToken ct)
    {
        if (await _storage.GetMessageAsync(before.Id, ct) is not { } after)
            return;

        await _storage.PutMessageAsync(
            after with { CreatedAt = before.CreatedAt, Attempts = before.Attempts + 1 }, ct);
    }

    /// <summary>
    /// Processes a decoded message according to what it is.
    /// </summary>
    /// <remarks>
    /// Shared by the front door and by <see cref="ReplayAsync"/>, so a replayed
    /// message is put through exactly the same handling as a fresh one. The
    /// checks that are <i>not</i> here are the point: deduplication, and whether
    /// the group is known. A replay has already answered both — the record it is
    /// replaying is the proof — and re-asking would refuse every message it was
    /// asked to retry, as a duplicate of itself.
    /// </remarks>
    private async Task<IngestResult> DispatchAsync(
        MlsGroup group,
        GroupId groupId,
        MessageId id,
        EpochId sourceEpoch,
        byte[] mlsBytes,
        MlsMessage message,
        string? transportId,
        CancellationToken ct) =>
        message.WireFormat switch
        {
            WireFormat.MlsPrivateMessage =>
                await IngestApplicationAsync(
                    group, groupId, id, sourceEpoch, mlsBytes, transportId, ct),

            WireFormat.MlsPublicMessage =>
                await IngestHandshakeAsync(
                    group, groupId, id, sourceEpoch, mlsBytes, (PublicMessage)message.Body,
                    transportId, ct),

            // A Welcome arrives outside a group and is joined from, not ingested
            // into one. Reaching here means it was routed to the wrong door.
            _ => Refuse(InputRejectionCategory.WrongRecipient),
        };

    private async Task<IngestResult> IngestApplicationAsync(
        MlsGroup group,
        GroupId groupId,
        MessageId id,
        EpochId sourceEpoch,
        byte[] mlsBytes,
        string? transportId,
        CancellationToken ct)
    {
        ReceivedGroupMessage received;
        try
        {
            received = GroupMessages.Receive(group, mlsBytes);
        }
        catch (MarmotAppEventException ex)
        {
            // Decodable as MLS, but the payload is not a Marmot event or claims
            // an author who did not send it. Terminal: the bytes will not
            // become valid later.
            await PersistAsync(id, groupId, sourceEpoch, mlsBytes, transportId,
                MessageRecordState.Failed, ex.Message, ct);

            return Refuse(InputRejectionCategory.InvalidSignature);
        }
        catch (Exception ex)
        {
            // Could not decrypt. Deliberately NOT terminal: the epoch it
            // belongs to may still be reachable once a commit we have not seen
            // arrives, so this is held for retry rather than rejected.
            await PersistAsync(id, groupId, sourceEpoch, mlsBytes, transportId,
                MessageRecordState.PeelDeferred, ex.Message, ct);

            return new IngestResult(new IngestOutcome.TransportDeferred(groupId), null);
        }

        await PersistAsync(id, groupId, sourceEpoch, mlsBytes, transportId,
            MessageRecordState.Processed, null, ct);

        return new IngestResult(
            new IngestOutcome.Processed(groupId, new EpochId(group.Epoch)), received);
    }

    private async Task<IngestResult> IngestHandshakeAsync(
        MlsGroup group,
        GroupId groupId,
        MessageId id,
        EpochId sourceEpoch,
        byte[] mlsBytes,
        PublicMessage framed,
        string? transportId,
        CancellationToken ct)
    {
        // Everything convergence will later need to say about this commit,
        // taken before it is applied. That ordering is forced rather than tidy:
        // the commit cites proposals by reference and applying it clears the
        // cache that says what those were, and the sender is a leaf index into
        // a tree the commit is about to move. Null means the handshake is a
        // proposal rather than a commit, which advances nothing and so archives
        // nothing.
        CommitTip? tip = TipOf(group, framed, id);

        ReceivedHandshake handshake;
        try
        {
            handshake = GroupHandshake.Receive(group, mlsBytes);
        }
        catch (Exception ex)
        {
            await PersistAsync(id, groupId, sourceEpoch, mlsBytes, transportId,
                MessageRecordState.Retryable, ex.Message, ct);

            return new IngestResult(new IngestOutcome.TransportDeferred(groupId), null);
        }

        if (handshake.Outcome == HandshakeOutcome.RemovedByCommit)
        {
            // The commit was valid and it removed us. Recorded as processed
            // because it is exactly what it claims to be, while the group is
            // marked removed so nothing tries to send in it again.
            await PersistAsync(id, groupId, sourceEpoch, mlsBytes, transportId,
                MessageRecordState.Processed, "removed by commit", ct);

            if (await _storage.GetGroupAsync(groupId, ct) is { } record)
            {
                await _storage.PutGroupAsync(
                    record with { Removed = true, UpdatedAt = _clock() }, ct);
            }

            return new IngestResult(
                new IngestOutcome.LocalState(LocalIngestState.Removed), null);
        }

        await PersistAsync(id, groupId, sourceEpoch, mlsBytes, transportId,
            MessageRecordState.Processed, null, ct);

        // A cached proposal has not changed group state, so the epoch is
        // unchanged and reporting Processed would overstate what happened.
        if (handshake.Outcome == HandshakeOutcome.ProposalCached)
            return new IngestResult(new IngestOutcome.Buffered(groupId, new EpochId(group.Epoch)), null);

        // The commit applied, so this epoch is now one a later fork may need to
        // be rebuilt from. Archived after the apply because the state belonging
        // to an epoch is the state its commit produced -- the other half of the
        // pair read before it.
        if (_archive is not null && tip is not null)
            await _archive.CaptureAsync(groupId, group, tip, ct);

        return new IngestResult(new IngestOutcome.Processed(groupId, new EpochId(group.Epoch)), null);
    }

    private static IngestResult Refuse(InputRejectionCategory category) =>
        new(new IngestOutcome.Ignored(category), null);

    /// <summary>
    /// What branch selection will need to say about a commit, read while it can
    /// still be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null for a handshake that is not a commit, and also for one whose sender
    /// leaf we cannot resolve. The second is fail-closed rather than tidy: such
    /// a commit will not apply, so nothing is lost, and inventing a committer
    /// for it would put a value into a group-wide comparison that no other
    /// member could have computed.
    /// </para>
    /// <para>
    /// The commit's digest is its content id — the same SHA-256 over the same
    /// MLS bytes — so the branch this tip names and the record stored beside it
    /// cannot drift apart.
    /// </para>
    /// </remarks>
    private static CommitTip? TipOf(MlsGroup group, PublicMessage framed, MessageId id)
    {
        CommitOrderingPriority? priority = CommitOrdering.PriorityOf(group, framed);
        if (priority is null)
            return null;

        byte[]? committer = group
            .GetMembers()
            .Where(m => m.leafIndex == framed.Content.Sender.LeafIndex)
            .Select(m => m.identity)
            .FirstOrDefault();

        return committer is null ? null : new CommitTip(priority.Value, id, committer);
    }

    /// <summary>
    /// The epoch a message was produced in, read off its own wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the epoch we were at when it arrived</b>, which is what a
    /// receiver knows and not what the message says. The two differ exactly
    /// when it matters: a commit that competes with ours was framed against the
    /// epoch we have already left, and that framed epoch is where its branch
    /// forks. Recording ours instead would file every competing commit as
    /// forking from wherever we happened to be, which is a branch no member can
    /// rebuild.
    /// </para>
    /// <para>
    /// Both wire formats carry it in the clear — a private message's epoch is
    /// outside its ciphertext precisely so a receiver can tell which keys to
    /// reach for before it can read anything.
    /// </para>
    /// </remarks>
    private static EpochId SourceEpochOf(MlsMessage message) => new(
        message.Body switch
        {
            PublicMessage handshake => handshake.Content.Epoch,
            PrivateMessage application => application.Epoch,

            // A Welcome carries no epoch and is refused a step later as
            // WrongRecipient. Zero is never read: the record that would carry
            // it is not written on that path.
            _ => 0,
        });

    /// <summary>
    /// Records what was seen and what became of it.
    /// </summary>
    /// <remarks>
    /// Written for refusals too, not only successes. The record is the
    /// deduplication key, so a message rejected once must be recognisable the
    /// next time it arrives — otherwise every redelivery pays the full
    /// validation cost again, and a flood of malformed traffic becomes a way to
    /// keep a client busy.
    /// </remarks>
    private async Task PersistAsync(
        MessageId id,
        GroupId groupId,
        EpochId sourceEpoch,
        byte[] mlsBytes,
        string? transportId,
        MessageRecordState state,
        string? reason,
        CancellationToken ct)
    {
        DateTimeOffset now = _clock();

        await _storage.PutMessageAsync(
            new MessageRecord(
                id,
                groupId,
                transportId,
                sourceEpoch,
                state,
                mlsBytes,
                now,
                now)
            {
                Reason = reason,
            },
            ct);
    }
}
