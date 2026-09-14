using DotnetMls.Codec;
using DotnetMls.Types;
using Scramble.Marmot.Storage;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>
/// What a transport did with a commit.
/// </summary>
/// <remarks>
/// <para>
/// Three outcomes, not two, and the third is the one that matters. Whether the
/// commit reached a relay decides whether it may be cleared locally, so "the
/// publish failed" and "the publish may have succeeded" have to be different
/// answers. Collapsing them into a boolean forces the caller to guess, and the
/// wrong guess clears a commit every other member has already applied — which
/// is the fork that cannot be repaired from this side, because MLS will not let
/// a member process a commit it authored, so our own bytes coming back off the
/// relay are no help.
/// </para>
/// <para>
/// <b>Deliberately not shared with <c>KeyPackagePublishOutcome</c>,</b> which
/// has the same three members and the same asymmetry. They authorise different
/// destructive acts — erasing a KeyPackage's private key there, clearing a
/// staged commit here — and one enum across both invites one handler across
/// both, which would be right for one subsystem and wrong for the other.
/// </para>
/// </remarks>
public enum CommitPublishOutcome
{
    /// <summary>The relay accepted it.</summary>
    Accepted,

    /// <summary>
    /// The relay definitively refused it, and no copy of it exists anywhere.
    /// </summary>
    /// <remarks>
    /// A relay <c>OK: false</c>, or a refusal to even send. Report this only
    /// when the commit provably never landed — it is what authorises
    /// discarding it.
    /// </remarks>
    Rejected,

    /// <summary>The attempt neither succeeded nor provably failed.</summary>
    /// <remarks>
    /// A timeout, a dropped socket, a cancelled send. The commit may be live,
    /// so it is left staged for a later reconciliation to settle against what
    /// the relay actually holds.
    /// </remarks>
    Indeterminate,
}

/// <summary>Publishes a wrapped commit envelope.</summary>
/// <remarks>
/// An interface only so the publish sequence can be tested without a relay —
/// the cutover rules say interfaces get extracted when a second concrete
/// implementation exists, not before.
/// </remarks>
public interface ICommitRelay
{
    /// <summary>
    /// Publishes <paramref name="envelope"/>.
    /// </summary>
    /// <remarks>
    /// Must not throw for an ordinary failure: return
    /// <see cref="CommitPublishOutcome.Rejected"/> or
    /// <see cref="CommitPublishOutcome.Indeterminate"/> instead. An escaped
    /// exception is read as indeterminate, which is the safe reading but loses
    /// the distinction the enum exists for.
    /// </remarks>
    Task<CommitPublishOutcome> PublishAsync(string envelope, CancellationToken ct = default);
}

/// <summary>
/// Handing a staged commit to a transport, durably.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this exists for.</b> <see cref="StagedCommit.Publishing"/>
/// draws the one line that matters for crash recovery: before it the commit
/// exists only on this device and clearing it is free; after it a relay may
/// already hold it, and clearing it locally is the single move that guarantees
/// a fork — everyone else advances and we stay behind, convinced nothing
/// happened. That line is a private field in an in-memory object, so across a
/// process restart nothing says which side of it we were on. This type writes
/// it down.
/// </para>
/// <para>
/// <b>Why the durable write is here and not inside
/// <see cref="StagedCommit.Publishing"/>.</b> It is the same split the engine
/// already made between <c>EpochManager</c> and <c>DurableEpochManager</c>:
/// the pure object stays pure and the writes wrap it. Three specific reasons,
/// in descending order of how much they would hurt:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>StagedCommit</c> is built by static factories —
/// <see cref="MarmotGroupInvite.Add"/>, <c>MarmotSelfUpdate.Stage</c>,
/// <c>MarmotGroupLeave.CommitDepartures</c> — none of which has storage.
/// Giving the type a storage dependency means giving it to every one of them,
/// so staging a commit could fail on I/O long before anybody decided to
/// publish.
/// </description></item>
/// <item><description>
/// <c>Publishing()</c> does not know what to write. The row needs the group id
/// and the commit's content digest; the staged commit holds an
/// <c>MlsGroup</c>, which does not carry the former, and a
/// <c>PublicMessage</c>, which is not the latter.
/// </description></item>
/// <item><description>
/// It is synchronous, and the only ways to keep it so are a blocking write or
/// a fire-and-forget one. A blocking write inside a method whose entire job is
/// to be called immediately before a send is a deadlock waiting for a caller
/// with a synchronization context; a fire-and-forget one is a durable record
/// that races the thing it is supposed to precede, which is worse than no
/// record at all because it looks like one.
/// </description></item>
/// </list>
/// <para>
/// <b>The ordering, which is the substance.</b> Row first, then
/// <c>Publishing()</c>, then the send; then the answer, then the local move,
/// then the clear. Every window errs the same way — towards believing a commit
/// may be out there — because that costs one relay query, while the opposite
/// forgets a commit the rest of the group has already applied.
/// </para>
/// </remarks>
public sealed class CommitPublisher
{
    private readonly ICommitPublishAttemptStorage _storage;
    private readonly ICommitRelay _relay;
    private readonly Func<DateTimeOffset> _clock;

    public CommitPublisher(
        ICommitPublishAttemptStorage storage, ICommitRelay relay, Func<DateTimeOffset> clock)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// The content id of a commit: SHA-256 over its MLS bytes.
    /// </summary>
    /// <remarks>
    /// Computed here rather than taken from the caller, because there is a
    /// tempting wrong answer and it round-trips. Hashing the transport envelope
    /// yields 64 hex characters that look exactly like a digest and that no
    /// peer can reproduce — a relay is free to re-serialise the envelope — so
    /// the reconciliation query this id exists for would match nothing, and
    /// report a live commit as absent.
    /// </remarks>
    public static MessageId CommitIdOf(PublicMessage commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        return MessageId.FromMlsBytes(
            TlsCodec.Serialize(new MlsMessage(WireFormat.MlsPublicMessage, commit).WriteTo));
    }

    /// <summary>
    /// What a restart should do with a commit it finds staged for a group.
    /// </summary>
    /// <remarks>
    /// The question session-open hydration has to answer before it touches
    /// anything, and the reason the row is written at all. No row means the
    /// commit never reached a transport, which is a positive statement and not
    /// an absence of information.
    /// </remarks>
    public async Task<StrandedCommitVerdict> ClassifyAsync(
        GroupId groupId, CancellationToken ct = default) =>
        CommitPublishAttempt.VerdictFor(
            await _storage.GetCommitPublishAttemptAsync(groupId, ct).ConfigureAwait(false));

    /// <summary>
    /// Publishes a staged commit and resolves it according to what the relay
    /// said.
    /// </summary>
    /// <param name="groupId">The group the commit belongs to.</param>
    /// <param name="staged">
    /// The staged commit. On <see cref="CommitPublishOutcome.Accepted"/> it is
    /// applied, on <see cref="CommitPublishOutcome.Rejected"/> discarded, and
    /// on <see cref="CommitPublishOutcome.Indeterminate"/> left exactly as it
    /// is.
    /// </param>
    /// <param name="envelope">The wrapped commit, ready for the wire.</param>
    /// <remarks>
    /// <para>
    /// <b>Returns the outcome instead of throwing on failure</b>, unlike
    /// <c>KeyPackagePublisher</c>. There, a failed publish leaves the caller
    /// nothing to do and an exception is the honest shape. Here each of the
    /// three outcomes demands a different next action from the caller — retry,
    /// give up, or reconcile — and an enum that has to be caught before it can
    /// be read is a worse way to say that. It also makes forgetting the
    /// indeterminate case a compile-time-visible omission rather than a missing
    /// catch block.
    /// </para>
    /// <para>
    /// <b>The commit is left staged on Indeterminate, and that is the point.</b>
    /// Discarding it there is the fork: it may be on a relay, and a member that
    /// clears a commit others applied is the one left behind. A stranded
    /// pending commit blocks the group's next commit until reconciliation
    /// settles it, which is recoverable; a silent fork is not.
    /// </para>
    /// </remarks>
    public async Task<CommitPublishOutcome> PublishAsync(
        GroupId groupId,
        StagedCommit staged,
        string envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(envelope);

        CommitPublishAttempt attempt = CommitPublishAttempt.HandedToTransport(
            groupId, CommitIdOf(staged.Commit), staged.NewEpoch, _clock());

        // Durable before anything else, and awaited. If this throws, the commit
        // has not been declared published, so disposal still clears it and the
        // group can commit again -- a failed storage write must not be able to
        // strand a group. Declaring it published first would invert that:
        // disposal would be disarmed for a commit nobody ever sent.
        await _storage.PutCommitPublishAttemptAsync(attempt, ct).ConfigureAwait(false);

        // Only now. Everything past this line has to assume a peer may hold the
        // commit.
        staged.Publishing();

        CommitPublishOutcome outcome;
        try
        {
            outcome = await _relay.PublishAsync(envelope, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Indeterminate, not rejected. A transport that throws has told us
            // nothing about whether the relay saw the commit, and the safe
            // reading of "nothing" is that it might have. This is the same rule
            // the KeyPackage publisher applies, for the same reason: only a
            // definite refusal authorises the destructive move.
            outcome = CommitPublishOutcome.Indeterminate;
        }

        // The answer is recorded before it is acted on. The window between "the
        // relay has it" and "we have applied it" is the one that must not come
        // back as abandonment: the rest of the group has already moved, and we
        // cannot reissue the commit, because MLS refuses to let a member
        // process a commit it authored.
        await _storage.PutCommitPublishAttemptAsync(
            attempt.Resolved(StateOf(outcome), _clock()), ct).ConfigureAwait(false);

        switch (outcome)
        {
            case CommitPublishOutcome.Accepted:
                staged.Applied();
                break;

            case CommitPublishOutcome.Rejected:
                // Only here. The relay said no, so nothing anywhere refers to
                // this commit and keeping it staged only blocks the next one.
                staged.Discard();
                break;

            default:
                // Indeterminate. Nothing local happens and the row stays, which
                // is what makes the commit findable by whatever reconciles it.
                return outcome;
        }

        // Cleared last, never first. The row is what says a commit may be out
        // there; it has to outlive the local move it authorised. A crash
        // between the clear and the move would come back with no record of a
        // commit whose fate we knew and whose application we cannot vouch for.
        await _storage.ClearCommitPublishAttemptAsync(groupId, ct).ConfigureAwait(false);

        return outcome;
    }

    private static CommitPublishState StateOf(CommitPublishOutcome outcome) => outcome switch
    {
        CommitPublishOutcome.Accepted => CommitPublishState.Accepted,
        CommitPublishOutcome.Rejected => CommitPublishState.Rejected,
        CommitPublishOutcome.Indeterminate => CommitPublishState.Indeterminate,

        // The exhaustiveness arm, for a transport returning a value outside
        // the enum. NOT LOAD-BEARING: it survives mutation, because nothing
        // reachable produces such a value and no test can drive it. Kept
        // because the safe reading of an outcome we cannot interpret is the
        // one that keeps the commit, and a switch that threw here would turn
        // an unknown answer into a lost record.
        _ => CommitPublishState.Indeterminate,
    };
}
