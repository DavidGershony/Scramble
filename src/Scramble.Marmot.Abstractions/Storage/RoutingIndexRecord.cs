namespace Scramble.Marmot.Storage;

/// <summary>
/// One routing address a group has been reachable at.
/// </summary>
/// <remarks>
/// <para>
/// A group's routing id rotates, and the group stays reachable at the old
/// address while any epoch that used it is still inside a retained-history
/// window. So the mapping from routing id to group is genuinely many-to-one
/// over time, and a member must be able to resolve a prior address as readily
/// as the current one — a catch-up fetch for an older epoch goes to the address
/// that epoch was actually published at.
/// </para>
/// <para>
/// The routing id is <b>not</b> the MLS group id. It lives in the
/// <c>0x8004</c> routing component and is derived from nothing; assuming the
/// two are equal is exactly the mistake this index exists to prevent.
/// </para>
/// </remarks>
/// <param name="TransportGroupId">The 32-byte routing handle from the <c>h</c> tag.</param>
/// <param name="GroupId">The group reachable at that address.</param>
/// <param name="FirstEpoch">The epoch this address became current in.</param>
/// <param name="LastEpoch">
/// The last epoch that used this address, or null while it is the current one.
/// </param>
public sealed record RoutingIndexRecord(
    byte[] TransportGroupId,
    GroupId GroupId,
    EpochId FirstEpoch,
    EpochId? LastEpoch,
    DateTimeOffset CreatedAt)
{
    /// <summary>Whether this is the address the group publishes to now.</summary>
    public bool IsCurrent => LastEpoch is null;
}

/// <summary>
/// Raised when a routing id is claimed for a group that does not own it.
/// </summary>
/// <remarks>
/// Fails closed on purpose. A routing id appears in the clear on every kind-445
/// event, so any observer can copy one; letting a second group bind an address
/// already bound elsewhere would let an attacker point a victim group's traffic
/// at their own state. Rebinding is an error rather than a last-write-wins
/// update.
/// </remarks>
public sealed class RoutingIdConflictException(string message) : Exception(message);
