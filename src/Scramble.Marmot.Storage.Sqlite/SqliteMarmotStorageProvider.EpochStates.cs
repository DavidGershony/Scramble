using System.Data;

namespace Scramble.Marmot.Storage.Sqlite;

/// <summary>
/// The durable half of the epoch state machine.
/// </summary>
/// <remarks>
/// One row per group and only while the group is refusing something, so the
/// table is empty in the ordinary case and holds a handful of rows in the worst
/// one. That is why there is no index beyond the primary key, and why the
/// restore path reads the whole table rather than asking group by group.
/// </remarks>
public sealed partial class SqliteMarmotStorageProvider
{
    public async Task PutEpochStateAsync(EpochStateRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var cmd = Command($@"
            INSERT OR REPLACE INTO {_tp}epoch_states
                (group_id, kind, epoch, prior_epoch, staged_commit, pending_ref,
                 pending_kind, updated_at)
            VALUES (@group, @kind, @epoch, @prior, @staged, @ref, @pendingKind, @updated);");

        cmd.Parameters.AddWithValue("@group", record.GroupId.Value);
        cmd.Parameters.AddWithValue("@kind", (int)record.Kind);
        cmd.Parameters.AddWithValue("@epoch", (long)record.Epoch.Value);
        cmd.Parameters.AddWithValue("@updated", Iso(record.UpdatedAt));

        // Written as a set of four or not at all, and the schema says so too.
        // A row carrying some of them would read back as a pending publish with
        // the rest invented -- and what would be invented is the commit to
        // reconcile and the epoch to fall back to.
        cmd.Parameters.AddWithValue(
            "@prior", record.PriorEpoch is { } prior ? (long)prior.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@staged", record.StagedCommit is { } staged ? staged.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@ref", record.Reference is { } reference ? (long)reference.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@pendingKind",
            record.PendingOperation is { } pendingKind ? (int)pendingKind : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<EpochStateRecord?> GetEpochStateAsync(
        GroupId groupId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT * FROM {_tp}epoch_states WHERE group_id = @group;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEpochState(reader) : null;
    }

    public async Task<IReadOnlyList<EpochStateRecord>> ListEpochStatesAsync(
        CancellationToken ct = default)
    {
        await using var cmd = Command($"SELECT * FROM {_tp}epoch_states ORDER BY updated_at;");
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var results = new List<EpochStateRecord>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadEpochState(reader));

        return results;
    }

    public async Task<bool> ClearEpochStateAsync(GroupId groupId, CancellationToken ct = default)
    {
        await using var cmd = Command($"DELETE FROM {_tp}epoch_states WHERE group_id = @group;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static EpochStateRecord ReadEpochState(IDataRecord r)
    {
        var groupId = new GroupId(Blob(r, "group_id"));
        var kind = (EpochStateKind)GetInt64(r, "kind");
        var epoch = new EpochId((ulong)GetInt64(r, "epoch"));
        var updatedAt = DateTimeOffset.Parse(GetString(r, "updated_at")!);

        return kind switch
        {
            EpochStateKind.PendingPublish => EpochStateRecord.Pending(
                groupId,
                epoch,
                new EpochId((ulong)GetInt64(r, "prior_epoch")),
                new StagedCommitHandle(Blob(r, "staged_commit")),
                new PendingStateRef((ulong)GetInt64(r, "pending_ref")),
                (PendingKind)GetInt64(r, "pending_kind"),
                updatedAt),
            EpochStateKind.Unrecoverable =>
                EpochStateRecord.Unrecoverable(groupId, epoch, updatedAt),
            EpochStateKind.Disbanded =>
                EpochStateRecord.Disbanded(groupId, epoch, updatedAt),

            // A kind this build does not know. Refused rather than skipped: the
            // states stored here are the ones that stop a group doing something,
            // so a row read as absent is read as permission to carry on.
            _ => throw new InvalidOperationException(
                $"Unknown epoch state kind {(int)kind} for group {groupId}. "
                + "The database was written by a newer build."),
        };
    }
}
