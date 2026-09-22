using System.Data;

namespace Scramble.Marmot.Storage.Sqlite;

/// <summary>
/// The durable half of publish intent for commits.
/// </summary>
/// <remarks>
/// One row per group and only while a commit of ours is unresolved, so the
/// table is empty in the ordinary case. That is why there is no index beyond
/// the primary key, and why the recovery path reads the whole table rather than
/// asking group by group — it cannot know which groups need anything until it
/// has looked.
/// </remarks>
public sealed partial class SqliteMarmotStorageProvider
{
    public async Task PutCommitPublishAttemptAsync(
        CommitPublishAttempt attempt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        await using var cmd = Command($@"
            INSERT OR REPLACE INTO {_tp}commit_publish_attempts
                (group_id, commit_id, new_epoch, state, handed_off_at, updated_at)
            VALUES (@group, @commit, @epoch, @state, @handed, @updated);");

        cmd.Parameters.AddWithValue("@group", attempt.GroupId.Value);
        cmd.Parameters.AddWithValue("@commit", attempt.CommitId.Value);
        cmd.Parameters.AddWithValue("@epoch", (long)attempt.NewEpoch.Value);
        cmd.Parameters.AddWithValue("@state", (int)attempt.State);
        cmd.Parameters.AddWithValue("@handed", Iso(attempt.HandedOffAt));
        cmd.Parameters.AddWithValue("@updated", Iso(attempt.UpdatedAt));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<CommitPublishAttempt?> GetCommitPublishAttemptAsync(
        GroupId groupId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT * FROM {_tp}commit_publish_attempts WHERE group_id = @group;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadPublishAttempt(reader) : null;
    }

    public async Task<IReadOnlyList<CommitPublishAttempt>> ListCommitPublishAttemptsAsync(
        CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT * FROM {_tp}commit_publish_attempts ORDER BY handed_off_at;");
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var results = new List<CommitPublishAttempt>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadPublishAttempt(reader));

        return results;
    }

    public async Task<bool> ClearCommitPublishAttemptAsync(
        GroupId groupId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"DELETE FROM {_tp}commit_publish_attempts WHERE group_id = @group;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static CommitPublishAttempt ReadPublishAttempt(IDataRecord r)
    {
        var groupId = new GroupId(Blob(r, "group_id"));
        var state = (CommitPublishState)GetInt64(r, "state");

        // A state this build does not know. Refused rather than read as absent,
        // because absent is the one answer that means "nobody else saw this
        // commit" -- so a row a newer build wrote would authorise discarding a
        // commit that may be live on a relay, which is the failure this table
        // exists to prevent.
        if (!Enum.IsDefined(state))
        {
            throw new InvalidOperationException(
                $"Unknown commit publish state {(int)state} for group {groupId}. "
                + "The database was written by a newer build.");
        }

        return CommitPublishAttempt.FromStorage(
            groupId,
            new MessageId(Blob(r, "commit_id")),
            new EpochId((ulong)GetInt64(r, "new_epoch")),
            state,
            DateTimeOffset.Parse(GetString(r, "handed_off_at")!),
            DateTimeOffset.Parse(GetString(r, "updated_at")!));
    }
}
