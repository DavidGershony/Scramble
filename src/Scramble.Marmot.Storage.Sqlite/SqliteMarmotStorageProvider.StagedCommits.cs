using Microsoft.Data.Sqlite;
using Scramble.Marmot.AppComponents;

namespace Scramble.Marmot.Storage.Sqlite;

/// <summary>
/// The post-commit state of a commit of ours that is not yet settled.
/// </summary>
/// <remarks>
/// One row per group and only while a commit of ours is outstanding, so the
/// table is empty in the ordinary case — hence no index beyond the primary key,
/// and hence a recovery path that reads the whole table rather than asking
/// group by group.
/// </remarks>
public sealed partial class SqliteMarmotStorageProvider
{
    public async Task PutStagedCommitAsync(
        StagedCommitRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var cmd = Command($@"
            INSERT OR REPLACE INTO {_tp}staged_commits
                (group_id, new_epoch, group_state, tip_priority, tip_commit, tip_committer,
                 created_at)
            VALUES (@group, @epoch, @state, @priority, @commit, @committer, @created);");

        cmd.Parameters.AddWithValue("@group", record.GroupId.Value);
        cmd.Parameters.AddWithValue("@epoch", (long)record.NewEpoch.Value);
        cmd.Parameters.AddWithValue("@state", record.GroupState);
        cmd.Parameters.AddWithValue("@priority", (int)record.Tip.Priority);
        cmd.Parameters.AddWithValue("@commit", record.Tip.Commit.Value);
        cmd.Parameters.AddWithValue("@committer", record.Tip.Committer);
        cmd.Parameters.AddWithValue("@created", Iso(record.CreatedAt));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<StagedCommitRecord?> GetStagedCommitAsync(
        GroupId groupId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT * FROM {_tp}staged_commits WHERE group_id = @group;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadStagedCommit(reader) : null;
    }

    public async Task<IReadOnlyList<StagedCommitRecord>> ListStagedCommitsAsync(
        CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT * FROM {_tp}staged_commits ORDER BY created_at;");

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var records = new List<StagedCommitRecord>();
        while (await reader.ReadAsync(ct))
            records.Add(ReadStagedCommit(reader));

        return records;
    }

    public async Task ClearStagedCommitAsync(GroupId groupId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"DELETE FROM {_tp}staged_commits WHERE group_id = @group;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static StagedCommitRecord ReadStagedCommit(SqliteDataReader r) => new(
        new GroupId((byte[])r["group_id"]),
        new EpochId((ulong)r.GetInt64(r.GetOrdinal("new_epoch"))),
        (byte[])r["group_state"],
        new CommitTip(
            (CommitOrderingPriority)r.GetInt64(r.GetOrdinal("tip_priority")),
            new MessageId((byte[])r["tip_commit"]),
            (byte[])r["tip_committer"]),
        DateTimeOffset.Parse(GetString(r, "created_at")!));
}
