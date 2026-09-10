namespace Scramble.Marmot.Engine.Convergence;

/// <summary>How far a group is from having decided its history.</summary>
/// <remarks>
/// Four states rather than a boolean, because "not settled" hides three
/// different situations a client should act on differently: one resolves by
/// waiting, one by working, and one by never resolving at all.
/// </remarks>
public enum ConvergenceStatus
{
    /// <summary>
    /// Input arrived too recently to decide on.
    /// </summary>
    /// <remarks>
    /// Not a problem — the quiescence window has not elapsed. Deciding now
    /// would mean choosing from a candidate set still being delivered, and
    /// then choosing again when the rest arrives.
    /// </remarks>
    Syncing,

    /// <summary>
    /// Quiet long enough, but some input is still unclassified.
    /// </summary>
    /// <remarks>
    /// The pass has work left to do rather than waiting to be allowed to start.
    /// </remarks>
    Resolving,

    /// <summary>Quiet, and every input accounted for.</summary>
    Settled,

    /// <summary>
    /// Something is missing that no amount of waiting supplies.
    /// </summary>
    /// <remarks>
    /// Distinguished from <see cref="Syncing"/> precisely because it does not
    /// resolve on its own: a client that shows a spinner for this is lying to
    /// the user indefinitely.
    /// </remarks>
    Blocked,
}

/// <summary>What a pass was given and what it made of it.</summary>
/// <param name="Inputs">Every message the pass was asked to account for.</param>
/// <param name="Resolved">
/// Those it classified — accepted, deferred, dropped or already-seen. Any input
/// not in here is what keeps the pass in <see cref="ConvergenceStatus.Resolving"/>.
/// </param>
/// <param name="Blocked">
/// True when the pass hit a condition waiting cannot fix — a missing retained
/// anchor, an absent own-commit checkpoint.
/// </param>
public sealed record CanonicalizationOutcome(
    IReadOnlySet<MessageId> Inputs,
    IReadOnlySet<MessageId> Resolved,
    bool Blocked);

/// <summary>
/// Deciding <i>when</i> a group has heard enough to choose a history.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BranchSelection"/> answers which branch wins. This answers the
/// question before it: is the candidate set complete enough to choose from at
/// all? Getting that wrong is not a smaller mistake than choosing badly —
/// deciding early means deciding on a partial set, and a member that decides
/// early on a different partial set than its peers reaches a different answer
/// from the same rules.
/// </para>
/// <para>
/// <b>Two bounds, and they fail in opposite directions.</b> The quiescence
/// window is how long the group must be quiet before deciding: too short and
/// members decide from different partial views, too long and every fork stalls
/// the conversation. The pass cap is a ceiling on the whole attempt regardless
/// of quiet, because a peer trickling candidates in just under the quiescence
/// window would otherwise keep a pass open forever — a bound on waiting is not
/// a bound on total time.
/// </para>
/// <para>
/// The rule is upstream's <c>convergence_status_for_result</c>, in the same
/// order: quiescence first, then completeness, then blocking errors. Order
/// matters — an unclassified input during the quiescence window is expected,
/// not a sign of work outstanding, so testing completeness first would report
/// <see cref="ConvergenceStatus.Resolving"/> for a pass that has not begun.
/// </para>
/// </remarks>
/// <param name="policy">The pinned v1 policy. Both bounds come from it.</param>
public sealed class CanonicalizationPipeline(ConvergencePolicy policy)
{
    private readonly ConvergencePolicy _policy =
        policy ?? throw new ArgumentNullException(nameof(policy));

    /// <summary>The policy this pipeline runs under.</summary>
    public ConvergencePolicy Policy => _policy;

    /// <summary>
    /// Classifies where a group stands.
    /// </summary>
    /// <param name="nowMs">The current time, in milliseconds.</param>
    /// <param name="lastRelevantInputMs">
    /// When convergence-relevant input last arrived. Not the last message of
    /// any kind: traffic that cannot change the candidate set must not keep
    /// resetting the window, or a chatty group never settles.
    /// </param>
    /// <param name="outcome">What the pass made of its inputs.</param>
    public ConvergenceStatus Classify(
        ulong nowMs, ulong lastRelevantInputMs, CanonicalizationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        // Saturating, because a clock that steps backwards must read as "input
        // just arrived" rather than as an enormous elapsed time that settles
        // the group instantly.
        ulong elapsed = nowMs > lastRelevantInputMs ? nowMs - lastRelevantInputMs : 0;

        if (elapsed < _policy.SettlementQuiescenceMs)
            return ConvergenceStatus.Syncing;

        if (outcome.Inputs.Any(id => !outcome.Resolved.Contains(id)))
            return ConvergenceStatus.Resolving;

        return outcome.Blocked ? ConvergenceStatus.Blocked : ConvergenceStatus.Settled;
    }

    /// <summary>
    /// Whether a pass that started at <paramref name="startedMs"/> has run out
    /// of time.
    /// </summary>
    /// <remarks>
    /// <b>Measured from the start of the pass, not from the last input.</b>
    /// That is the whole point: the quiescence window already resets on every
    /// arrival, so a peer delivering one candidate just inside it could hold a
    /// pass open indefinitely. This is the bound that cannot be pushed back.
    /// </remarks>
    public bool HasExpired(ulong nowMs, ulong startedMs) =>
        nowMs > startedMs && nowMs - startedMs > _policy.MaxConvergencePassMs;

    /// <summary>
    /// The instant a pass started at <paramref name="startedMs"/> must end by.
    /// </summary>
    public ulong DeadlineFor(ulong startedMs) =>
        startedMs > ulong.MaxValue - _policy.MaxConvergencePassMs
            ? ulong.MaxValue
            : startedMs + _policy.MaxConvergencePassMs;
}
