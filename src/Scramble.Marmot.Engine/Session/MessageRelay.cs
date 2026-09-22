namespace Scramble.Marmot.Engine.Session;

/// <summary>
/// What a transport did with an application message.
/// </summary>
/// <remarks>
/// <para>
/// The same three answers as <see cref="Groups.CommitPublishOutcome"/>, and
/// <b>deliberately a separate enum</b> for the same reason that one is separate
/// from <c>KeyPackagePublishOutcome</c>: one enum invites one handler, and the
/// handling here is genuinely different. There,
/// <see cref="Groups.CommitPublishOutcome.Rejected"/> is the only answer that
/// authorises discarding a commit, because a commit cannot be reissued — MLS
/// refuses to let a member process a commit it authored. An application message
/// has no such constraint: dedup is content-derived, a receiver drops a
/// duplicate, and so both <see cref="Rejected"/> and <see cref="Indeterminate"/>
/// simply mean "still ours to retry".
/// </para>
/// <para>
/// The distinction is kept anyway rather than collapsed to a boolean, because it
/// is what a transport actually knows and it costs nothing to carry. Nothing in
/// this engine branches on it today; a caller that wants to log a definite
/// refusal differently from a timeout can.
/// </para>
/// </remarks>
public enum MessageSendOutcome
{
    /// <summary>The relay accepted it.</summary>
    Accepted,

    /// <summary>The relay definitively refused it.</summary>
    Rejected,

    /// <summary>The attempt neither succeeded nor provably failed.</summary>
    /// <remarks>A timeout, a dropped socket, a cancelled send.</remarks>
    Indeterminate,
}

/// <summary>Publishes a wrapped application-message envelope.</summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="Groups.ICommitRelay"/> even though one object
/// will implement both.</b> They are the same signature over the same wire and
/// they are not the same seam: a commit's answer decides whether local state may
/// advance, and a message's answer decides only whether a queue row goes. Fusing
/// them would put the commit path's three-way recovery in reach of a code path
/// that must never use it.
/// </para>
/// <para>
/// An interface only so the send sequence can be tested without a relay — the
/// cutover rules say interfaces get extracted when a second concrete
/// implementation exists, not before.
/// </para>
/// </remarks>
public interface IMessageRelay
{
    /// <summary>
    /// Publishes <paramref name="envelope"/>.
    /// </summary>
    /// <remarks>
    /// Should not throw for an ordinary failure: return
    /// <see cref="MessageSendOutcome.Rejected"/> or
    /// <see cref="MessageSendOutcome.Indeterminate"/> instead. An escaped
    /// exception is read as indeterminate, which keeps the message queued —
    /// the same reading <c>CommitPublisher</c> gives a throwing transport.
    /// </remarks>
    Task<MessageSendOutcome> SendAsync(string envelope, CancellationToken ct = default);
}

/// <summary>What became of a message the caller asked to send.</summary>
/// <remarks>
/// Three, not two, and the third is not a transport answer at all.
/// <see cref="Refused"/> is the engine declining before anything durable
/// happens; <see cref="Sent"/> and <see cref="Queued"/> are what the transport
/// left behind.
/// </remarks>
public enum SendDisposition
{
    /// <summary>A relay took it, and nothing is left queued for it.</summary>
    Sent,

    /// <summary>
    /// It is durably queued and still owed. Either the group was not settled
    /// and no transport was tried, or one was tried and did not accept.
    /// </summary>
    /// <remarks>
    /// <b>A refused send and an indeterminate one are the same answer here.</b>
    /// Both are retryable for a message, so a caller that distinguished them
    /// would be distinguishing something it cannot act on differently.
    /// </remarks>
    Queued,

    /// <summary>
    /// The local member has been removed from the group. Nothing was queued and
    /// nothing was sent.
    /// </summary>
    Refused,
}

/// <summary>The outcome of one <c>SendAsync</c>.</summary>
/// <param name="Disposition">What became of this message.</param>
/// <param name="Queued">
/// How many intents the group's queue holds now — a depth, not a flag for this
/// message. A caller uses it to see a backlog forming; <see cref="Disposition"/>
/// already says what happened to the message it asked about.
/// </param>
public sealed record SendResult(SendDisposition Disposition, int Queued);

/// <summary>The outcome of one <c>DrainAsync</c>.</summary>
/// <param name="Sent">How many queued messages a relay accepted.</param>
/// <param name="Queued">How many intents the group's queue still holds.</param>
/// <param name="Dropped">
/// How many were discarded unsent because the local member has been removed.
/// Non-zero only on the eviction path, where it is the whole point: a queue
/// cleared silently looks exactly like a queue that was empty.
/// </param>
public sealed record DrainResult(int Sent, int Queued, int Dropped);
