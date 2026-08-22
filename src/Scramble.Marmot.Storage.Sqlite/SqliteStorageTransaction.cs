using Microsoft.Data.Sqlite;

namespace Scramble.Marmot.Storage.Sqlite;

/// <summary>
/// A SQLite-backed <see cref="IStorageTransaction"/>.
/// </summary>
/// <remarks>
/// Disposing without committing rolls back, so an engine operation that throws
/// partway cannot leave the record set half-advanced.
/// </remarks>
internal sealed class SqliteStorageTransaction : IStorageTransaction
{
    private readonly SqliteTransaction _transaction;
    private readonly Action _onClosed;
    private bool _closed;

    internal SqliteStorageTransaction(SqliteTransaction transaction, Action onClosed)
    {
        _transaction = transaction;
        _onClosed = onClosed;
    }

    internal SqliteTransaction Inner => _transaction;

    public async Task CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        await _transaction.CommitAsync(ct);
        Close();
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        await _transaction.RollbackAsync(ct);
        Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed)
        {
            // Not committed: roll back rather than silently keeping partial work.
            try
            {
                await _transaction.RollbackAsync();
            }
            catch (SqliteException)
            {
                // The connection may already be gone; nothing left to undo.
            }

            Close();
        }

        await _transaction.DisposeAsync();
    }

    private void Close()
    {
        _closed = true;
        _onClosed();
    }
}
