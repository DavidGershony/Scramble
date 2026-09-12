using DotnetMls.Codec;
using DotnetMls.Group;
using DotnetMls.Types;
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
/// Convergence is stubbed here, as the phase plan intends. A message that
/// cannot be applied because the group is mid-transition comes back
/// <see cref="IngestOutcome.Buffered"/>, and <b>the caller owes it a replay</b>
/// — that outcome is a promise, and nothing here can keep it.
/// </para>
/// </remarks>
public sealed class MessageIngest(
    IMarmotStorageProvider storage,
    EpochManager epochs,
    Func<DateTimeOffset> clock)
{
    private readonly IMarmotStorageProvider _storage =
        storage ?? throw new ArgumentNullException(nameof(storage));

    private readonly EpochManager _epochs =
        epochs ?? throw new ArgumentNullException(nameof(epochs));

    private readonly Func<DateTimeOffset> _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));

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

        return message.WireFormat switch
        {
            WireFormat.MlsPrivateMessage =>
                await IngestApplicationAsync(
                    group, groupId, id, sourceEpoch, mlsBytes, transportId, ct),

            WireFormat.MlsPublicMessage =>
                await IngestHandshakeAsync(
                    group, groupId, id, sourceEpoch, mlsBytes, transportId, ct),

            // A Welcome arrives outside a group and is joined from, not ingested
            // into one. Reaching here means it was routed to the wrong door.
            _ => Refuse(InputRejectionCategory.WrongRecipient),
        };
    }

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
        string? transportId,
        CancellationToken ct)
    {
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
        return handshake.Outcome == HandshakeOutcome.ProposalCached
            ? new IngestResult(new IngestOutcome.Buffered(groupId, new EpochId(group.Epoch)), null)
            : new IngestResult(new IngestOutcome.Processed(groupId, new EpochId(group.Epoch)), null);
    }

    private static IngestResult Refuse(InputRejectionCategory category) =>
        new(new IngestOutcome.Ignored(category), null);

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
