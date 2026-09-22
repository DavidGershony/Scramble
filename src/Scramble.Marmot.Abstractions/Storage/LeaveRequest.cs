namespace Scramble.Marmot.Storage;

/// <summary>
/// A durable record that the local member intends to leave a group.
/// </summary>
/// <remarks>
/// Leaving is a two-party operation: the leaver sends a SelfRemove proposal and
/// a <i>remaining</i> member commits it (RFC 9420 requires committer != leaver).
/// The request therefore outlives a single epoch and must be re-proposed if the
/// epoch advances before anyone commits it — hence <see cref="ProposedInEpoch"/>.
/// </remarks>
public sealed record LeaveRequest(
    GroupId GroupId,
    EpochId RequestedInEpoch,
    DateTimeOffset CreatedAt)
{
    /// <summary>The epoch the SelfRemove proposal was last published in, if any.</summary>
    public EpochId? ProposedInEpoch { get; init; }
}
