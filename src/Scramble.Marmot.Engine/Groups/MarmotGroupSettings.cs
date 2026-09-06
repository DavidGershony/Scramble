using DotnetMls.Group;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>
/// The MLS group settings every Marmot group runs with.
/// </summary>
/// <remarks>
/// <para>
/// These are Marmot's, not MLS's. <c>dotnet-mls</c> keeps the small tolerances
/// RFC 9420 suggests, because how much reordering to expect is a property of the
/// transport rather than of the protocol — and Marmot's transport is Nostr
/// relays, which promise nothing about order.
/// </para>
/// <para>
/// So the window has to cover ordinary relay reordering <b>and</b> an offline
/// client's catch-up flood, where a whole backlog arrives at once in whatever
/// order the relay returns it. A message from the same sender that lands after a
/// later one is not delayed by a short window, it is lost: the sender chain
/// ratchets forward and the key is gone.
/// </para>
/// <para>
/// The values match the reference implementation
/// (<c>cgka-engine/src/wire_format.rs</c>), which raised them from the OpenMLS
/// defaults for exactly this reason. Matching matters beyond taste: two members
/// with different windows disagree about which messages are deliverable, and the
/// disagreement shows up as one of them missing history the other has.
/// </para>
/// </remarks>
public static class MarmotGroupSettings
{
    /// <summary>
    /// Generations of reordering tolerated per sender, within an epoch.
    /// </summary>
    /// <remarks>
    /// Reading a backlog of <c>N</c> messages newest-first needs <c>N-1</c>
    /// retained keys, so this is also the largest batch that can arrive
    /// completely backwards and still be read in full.
    /// </remarks>
    public const int OutOfOrderTolerance = 100;

    /// <summary>
    /// How far ahead of the current generation a message may claim to be.
    /// </summary>
    /// <remarks>
    /// A bound on work, not on delivery: the generation is attacker-chosen and
    /// each one costs a derivation.
    /// </remarks>
    public const int MaxForwardDistance = 1000;

    /// <summary>
    /// How many past epochs stay readable, for delayed application messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a transport allowance like the reordering window — this one is a
    /// <b>consensus</b> value. Convergence scores a branch partly on the
    /// application messages that witness it, and a member can only witness a
    /// message it can decrypt. Two members retaining different depths therefore
    /// count different witnesses for the same branch and can select different
    /// branches, which is the one thing branch selection may not do.
    /// </para>
    /// <para>
    /// So it must equal
    /// <see cref="Convergence.ConvergencePolicy.V1AppMessagePastEpochLimit"/>,
    /// and <see cref="Convergence.ConvergencePolicy.RequireWindowMatches"/>
    /// exists to refuse the pairing where it does not. Upstream ties the same
    /// two together with <c>ensure_app_window_matches</c>.
    /// </para>
    /// </remarks>
    public const int MaxPastEpochs = (int)Convergence.ConvergencePolicy.V1AppMessagePastEpochLimit;

    /// <summary>The configuration every Marmot group is built and joined with.</summary>
    /// <remarks>
    /// A new instance each time: the type is immutable in the properties that
    /// matter, but sharing one instance across groups invites someone to add a
    /// mutable field later and couple every group together.
    /// </remarks>
    public static MlsGroupConfig Create() => new()
    {
        OutOfOrderTolerance = OutOfOrderTolerance,
        MaxForwardDistance = MaxForwardDistance,
        MaxPastEpochs = MaxPastEpochs,
    };
}
