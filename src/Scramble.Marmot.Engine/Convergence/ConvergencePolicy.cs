namespace Scramble.Marmot.Engine.Convergence;

/// <summary>A convergence policy that is not the adopted v1 baseline.</summary>
public sealed class ConvergencePolicyException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ConvergencePolicyException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The constants that decide which branch of a forked group wins.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are pinned, not configured, and the pin is the point.</b> Branch
/// selection has to reach the same answer on every member's device or the group
/// splits permanently — two halves each convinced they hold the real history,
/// each unable to read the other. A member running different constants is not
/// slightly out of tune; it is computing a different function, and it will
/// disagree exactly when a fork makes agreement matter.
/// </para>
/// <para>
/// So there is no negotiation and no per-group override. Upstream fails closed
/// on any policy that is not byte-identical to the v1 baseline
/// (<c>ensure_pinned_v1</c>), and so does this. When a negotiation mechanism
/// exists it will arrive behind a required capability, which is the only way a
/// group can know that every member understands the same rules.
/// </para>
/// <para>
/// Values from <c>cgka-engine/src/convergence.rs</c> and
/// <c>canonicalization.rs</c>, unchanged from <c>wn-agent-v0.9.10</c> through
/// <c>v0.9.17</c>.
/// </para>
/// </remarks>
public sealed record ConvergencePolicy
{
    /// <summary>How far back a branch may fork and still be considered.</summary>
    /// <remarks>
    /// The rollback horizon. A branch forking further back than this is refused
    /// however good it otherwise looks, because accepting it would silently
    /// discard more already-delivered history than a member can be expected to
    /// re-render.
    /// </remarks>
    public const ulong V1MaxRewindCommits = 5;

    /// <summary>Distinct senders in one epoch that constitute a witness quorum.</summary>
    public const int V1WitnessQuorumSendersPerEpoch = 2;

    /// <summary>Branch epochs that must meet quorum for the branch to be witnessed.</summary>
    public const int V1WitnessQuorumEpochs = 1;

    /// <summary>How much depth a witness quorum may add to a branch's score.</summary>
    public const ulong V1MaxWitnessOverrideDepth = 1;

    /// <summary>
    /// How many epochs back an application message stays deliverable.
    /// </summary>
    /// <remarks>
    /// Must equal the MLS <c>max_past_epochs</c> window. See
    /// <see cref="RequireWindowMatches"/> for why the two cannot be allowed to
    /// drift apart.
    /// </remarks>
    public const ulong V1AppMessagePastEpochLimit = 5;

    /// <summary>How long a pass waits for quiet before declaring settlement.</summary>
    public const ulong V1SettlementQuiescenceMs = 1_000;

    /// <summary>The absolute cap on one convergence pass.</summary>
    public const ulong V1MaxConvergencePassMs = 5_000;

    /// <summary>The rollback horizon.</summary>
    public ulong MaxRewindCommits { get; init; } = V1MaxRewindCommits;

    /// <summary>Distinct senders per epoch needed for a witness quorum.</summary>
    public int WitnessQuorumSendersPerEpoch { get; init; } = V1WitnessQuorumSendersPerEpoch;

    /// <summary>Epochs that must meet quorum.</summary>
    public int WitnessQuorumEpochs { get; init; } = V1WitnessQuorumEpochs;

    /// <summary>The cap on the witness-quorum depth boost.</summary>
    public ulong MaxWitnessOverrideDepth { get; init; } = V1MaxWitnessOverrideDepth;

    /// <summary>The app-message delivery window, in epochs.</summary>
    public ulong AppMessagePastEpochLimit { get; init; } = V1AppMessagePastEpochLimit;

    /// <summary>The settlement quiescence window, in milliseconds.</summary>
    public ulong SettlementQuiescenceMs { get; init; } = V1SettlementQuiescenceMs;

    /// <summary>The absolute convergence-pass cap, in milliseconds.</summary>
    public ulong MaxConvergencePassMs { get; init; } = V1MaxConvergencePassMs;

    /// <summary>The adopted v1 baseline.</summary>
    public static ConvergencePolicy V1 { get; } = new();

    /// <summary>Whether this policy is exactly the v1 baseline.</summary>
    public bool IsPinnedV1 => this == V1;

    /// <summary>
    /// Checks the policy's own internal bound.
    /// </summary>
    /// <remarks>
    /// A witness boost that could exceed the rewind horizon would let app-message
    /// traffic push a branch past an arbitrarily longer branch of valid commits —
    /// so a group could be talked onto a shorter history by volume alone. The
    /// bound is what keeps the boost a tie-breaker rather than an override.
    /// </remarks>
    /// <exception cref="ConvergencePolicyException">The bound does not hold.</exception>
    public void Validate()
    {
        if (MaxWitnessOverrideDepth > MaxRewindCommits)
        {
            throw new ConvergencePolicyException(
                $"The witness override depth {MaxWitnessOverrideDepth} exceeds the rewind "
                + $"horizon {MaxRewindCommits}, so app traffic could push a branch past a "
                + "longer branch of valid commits.");
        }
    }

    /// <summary>
    /// Requires the MLS past-epoch window to equal the app-message window.
    /// </summary>
    /// <remarks>
    /// The two answer the same question from different sides: how far back a
    /// message can still be read, and how far back one still counts as a witness.
    /// If the delivery window were the larger, we would count witnesses for
    /// messages we cannot decrypt; if the decrypt window were larger, we would
    /// hold readable messages that no longer vote. Either way two members with
    /// different pairings score the same branch differently, which is the one
    /// thing branch selection may not do.
    /// </remarks>
    /// <param name="maxPastEpochs">The MLS window the group is running with.</param>
    /// <exception cref="ConvergencePolicyException">The two disagree.</exception>
    public void RequireWindowMatches(ulong maxPastEpochs)
    {
        if (AppMessagePastEpochLimit != maxPastEpochs)
        {
            throw new ConvergencePolicyException(
                $"The app-message window {AppMessagePastEpochLimit} must equal the MLS "
                + $"past-epoch window {maxPastEpochs}.");
        }
    }

    /// <summary>
    /// The full acceptance check: valid, pinned to v1, and window-aligned.
    /// </summary>
    /// <remarks>
    /// Applied wherever a policy enters the engine — construction, a stored
    /// policy being decoded, a group being opened. Fail-closed at every one of
    /// them, because a policy that reaches branch selection unchecked is one
    /// that has already had the chance to fork the group.
    /// </remarks>
    /// <exception cref="ConvergencePolicyException">Not acceptable.</exception>
    public void RequireAcceptable(ulong maxPastEpochs)
    {
        Validate();
        RequireWindowMatches(maxPastEpochs);

        if (!IsPinnedV1)
        {
            throw new ConvergencePolicyException(
                "The convergence policy must equal the pinned v1 baseline exactly. There is "
                + "no negotiation mechanism yet, so a member running different constants "
                + "computes a different branch selection and forks the group.");
        }
    }
}
