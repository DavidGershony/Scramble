namespace Scramble.Marmot.Storage;

/// <summary>
/// The rotation-aware map from routing address to group.
/// </summary>
/// <remarks>
/// Every inbound kind-445 event arrives with a routing id and nothing else
/// identifying, so this is the first lookup on the receive path and the one
/// that decides which group's keys are even tried.
/// </remarks>
public interface IRoutingIndexStorage
{
    /// <summary>
    /// Binds a routing address to a group from <paramref name="firstEpoch"/> on,
    /// and retires whatever address the group was using.
    /// </summary>
    /// <remarks>
    /// The retirement is part of the same call rather than a second one a
    /// caller could forget: a group with two current addresses would publish to
    /// one and listen on both, which looks like it works right up until a
    /// rotation is missed.
    /// </remarks>
    /// <exception cref="RoutingIdConflictException">
    /// The routing id is already bound to a different group.
    /// </exception>
    Task PutRoutingAsync(
        byte[] transportGroupId,
        GroupId groupId,
        EpochId firstEpoch,
        CancellationToken ct = default);

    /// <summary>
    /// The group reachable at <paramref name="transportGroupId"/>, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <b>exact</b> 32-byte match, always. No prefix matching, no nearest
    /// neighbour, no "probably this group" fallback — a routing id is public,
    /// so anything less than exact equality is an invitation to steer one
    /// group's traffic into another's state.
    /// </para>
    /// <para>
    /// Resolves retired addresses too. That is the point: a message from an
    /// epoch before a rotation is published at that epoch's address, and
    /// refusing to resolve it would silently drop history.
    /// </para>
    /// </remarks>
    Task<RoutingIndexRecord?> ResolveAsync(
        byte[] transportGroupId, CancellationToken ct = default);

    /// <summary>
    /// Every address the group has used, current first.
    /// </summary>
    Task<IReadOnlyList<RoutingIndexRecord>> ListRoutingAsync(
        GroupId groupId, CancellationToken ct = default);

    /// <summary>The address the group publishes to now, or null.</summary>
    Task<RoutingIndexRecord?> CurrentRoutingAsync(
        GroupId groupId, CancellationToken ct = default);

    /// <summary>
    /// Drops retired addresses whose last epoch is below
    /// <paramref name="horizon"/>.
    /// </summary>
    /// <remarks>
    /// Bounded by the retained-history horizon rather than by age or count: an
    /// address must stay resolvable exactly as long as some epoch that used it
    /// can still be fetched. The current address is never pruned.
    /// </remarks>
    /// <returns>How many were dropped.</returns>
    Task<int> PruneRoutingAsync(
        GroupId groupId, EpochId horizon, CancellationToken ct = default);
}
