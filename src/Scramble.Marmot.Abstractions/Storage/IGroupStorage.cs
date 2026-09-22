namespace Scramble.Marmot.Storage;

/// <summary>Durable group records.</summary>
public interface IGroupStorage
{
    Task PutGroupAsync(GroupRecord group, CancellationToken ct = default);

    Task<GroupRecord?> GetGroupAsync(GroupId id, CancellationToken ct = default);

    Task<IReadOnlyList<GroupRecord>> ListGroupsAsync(CancellationToken ct = default);

    /// <summary>
    /// Groups the local member is still in. Excludes groups we have been
    /// removed from, which remain readable but must reject sends.
    /// </summary>
    Task<IReadOnlyList<GroupRecord>> ListLiveGroupsAsync(CancellationToken ct = default);

    Task DeleteGroupAsync(GroupId id, CancellationToken ct = default);
}
