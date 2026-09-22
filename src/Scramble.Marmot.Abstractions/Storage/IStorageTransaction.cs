namespace Scramble.Marmot.Storage;

/// <summary>
/// A unit of work that commits or rolls back as a whole.
/// </summary>
/// <remarks>
/// Applying a commit touches several records at once — the group's epoch, the
/// message record, invalidated siblings. A crash partway through must not leave
/// the engine's view half-advanced, so those writes run inside one transaction.
/// Disposing without <see cref="CommitAsync"/> rolls back.
/// </remarks>
public interface IStorageTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);

    Task RollbackAsync(CancellationToken ct = default);
}
