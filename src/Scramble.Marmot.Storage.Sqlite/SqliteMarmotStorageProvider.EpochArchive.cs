using Microsoft.Data.Sqlite;
using Scramble.Marmot.AppComponents;

namespace Scramble.Marmot.Storage.Sqlite;

/// <summary>
/// Retained MLS group state, one row per epoch.
/// </summary>
/// <remarks>
/// Separate from the snapshots table on purpose: a snapshot is a JSON capture
/// of this layer's own rows, and a checkpoint is an opaque MLS export. They are
/// written at different moments, pruned against the same horizon for different
/// reasons, and confusing the two would mean rolling back rows without the
/// state that makes them readable.
/// </remarks>
public sealed partial class SqliteMarmotStorageProvider
{
    public async Task PutEpochCheckpointAsync(
        EpochCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        await using var cmd = Command($@"
            INSERT OR REPLACE INTO {_tp}epoch_archive
                (group_id, epoch, group_state, tip_priority, created_at)
            VALUES (@group, @epoch, @state, @priority, @created);");

        cmd.Parameters.AddWithValue("@group", checkpoint.GroupId.Value);
        cmd.Parameters.AddWithValue("@epoch", (long)checkpoint.Epoch.Value);
        cmd.Parameters.AddWithValue("@state", checkpoint.GroupState);
        cmd.Parameters.AddWithValue("@priority", (int)checkpoint.TipPriority);
        cmd.Parameters.AddWithValue("@created", Iso(checkpoint.CreatedAt));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<EpochCheckpoint?> GetEpochCheckpointAsync(
        GroupId groupId, EpochId epoch, CancellationToken ct = default)
    {
        await using var cmd = Command($@"
            SELECT group_id, epoch, group_state, tip_priority, created_at
            FROM {_tp}epoch_archive
            WHERE group_id = @group AND epoch = @epoch;");

        cmd.Parameters.AddWithValue("@group", groupId.Value);
        cmd.Parameters.AddWithValue("@epoch", (long)epoch.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCheckpoint(reader) : null;
    }

    public async Task<IReadOnlyList<EpochCheckpoint>> ListEpochCheckpointsAsync(
        GroupId groupId, EpochId oldestEpoch, CancellationToken ct = default)
    {
        await using var cmd = Command($@"
            SELECT group_id, epoch, group_state, tip_priority, created_at
            FROM {_tp}epoch_archive
            WHERE group_id = @group AND epoch >= @oldest
            ORDER BY epoch;");

        cmd.Parameters.AddWithValue("@group", groupId.Value);
        cmd.Parameters.AddWithValue("@oldest", (long)oldestEpoch.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var checkpoints = new List<EpochCheckpoint>();
        while (await reader.ReadAsync(ct))
            checkpoints.Add(ReadCheckpoint(reader));

        return checkpoints;
    }

    public async Task<int> PruneEpochCheckpointsBeforeAsync(
        GroupId groupId, EpochId oldestRetainedEpoch, CancellationToken ct = default)
    {
        await using var cmd = Command($@"
            DELETE FROM {_tp}epoch_archive
            WHERE group_id = @group AND epoch < @oldest;");

        cmd.Parameters.AddWithValue("@group", groupId.Value);
        cmd.Parameters.AddWithValue("@oldest", (long)oldestRetainedEpoch.Value);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static EpochCheckpoint ReadCheckpoint(SqliteDataReader r) => new(
        new GroupId((byte[])r["group_id"]),
        new EpochId((ulong)r.GetInt64(r.GetOrdinal("epoch"))),
        (byte[])r["group_state"],
        (CommitOrderingPriority)r.GetInt32(r.GetOrdinal("tip_priority")),
        DateTimeOffset.Parse(GetString(r, "created_at")!));
}
