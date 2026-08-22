using System.Data;
using Microsoft.Data.Sqlite;

namespace Scramble.Marmot.Storage.Sqlite;

/// <summary>
/// SQLite-backed <see cref="IMarmotStorageProvider"/>.
/// </summary>
/// <remarks>
/// Single long-lived connection in WAL mode, mirroring the storage provider the
/// previous engine used. All SQL is parameterised.
/// </remarks>
public sealed partial class SqliteMarmotStorageProvider : IMarmotStorageProvider, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _tp;
    private SqliteStorageTransaction? _active;

    /// <param name="connectionString">SQLite connection string.</param>
    /// <param name="tablePrefix">Prefix for table names when sharing a database.</param>
    public SqliteMarmotStorageProvider(string connectionString, string tablePrefix = "marmot_")
    {
        _tp = tablePrefix;
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        MarmotSqliteMigrations.Apply(_connection, _tp);
    }

    // -- Transactions --

    public async Task<IStorageTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_active is not null)
            throw new InvalidOperationException("A storage transaction is already open on this provider.");

        var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        _active = new SqliteStorageTransaction(tx, () => _active = null);
        return _active;
    }

    private SqliteCommand Command(string sql)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _active?.Inner;
        return cmd;
    }

    // -- Groups --

    public async Task PutGroupAsync(GroupRecord group, CancellationToken ct = default)
    {
        await using var cmd = Command($@"
            INSERT OR REPLACE INTO {_tp}groups
                (group_id, epoch, profile, removed, join_epoch, validated_tree, created_at, updated_at)
            VALUES (@id, @epoch, @profile, @removed, @join_epoch, @validated, @created, @updated);");
        cmd.Parameters.AddWithValue("@id", group.Id.Value);
        cmd.Parameters.AddWithValue("@epoch", (long)group.Epoch.Value);
        cmd.Parameters.AddWithValue("@profile", (int)group.Profile);
        cmd.Parameters.AddWithValue("@removed", group.Removed ? 1 : 0);
        cmd.Parameters.AddWithValue("@join_epoch",
            group.JoinEpoch is { } je ? (long)je.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@validated", group.ValidatedTree ? 1 : 0);
        cmd.Parameters.AddWithValue("@created", Iso(group.CreatedAt));
        cmd.Parameters.AddWithValue("@updated", Iso(group.UpdatedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<GroupRecord?> GetGroupAsync(GroupId id, CancellationToken ct = default)
    {
        await using var cmd = Command($"SELECT * FROM {_tp}groups WHERE group_id = @id;");
        cmd.Parameters.AddWithValue("@id", id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadGroup(reader) : null;
    }

    public Task<IReadOnlyList<GroupRecord>> ListGroupsAsync(CancellationToken ct = default) =>
        QueryGroupsAsync($"SELECT * FROM {_tp}groups ORDER BY created_at;", ct);

    public Task<IReadOnlyList<GroupRecord>> ListLiveGroupsAsync(CancellationToken ct = default) =>
        QueryGroupsAsync($"SELECT * FROM {_tp}groups WHERE removed = 0 ORDER BY created_at;", ct);

    public async Task DeleteGroupAsync(GroupId id, CancellationToken ct = default)
    {
        await using var cmd = Command($"DELETE FROM {_tp}groups WHERE group_id = @id;");
        cmd.Parameters.AddWithValue("@id", id.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<GroupRecord>> QueryGroupsAsync(string sql, CancellationToken ct)
    {
        await using var cmd = Command(sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<GroupRecord>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadGroup(reader));
        return results;
    }

    private static GroupRecord ReadGroup(IDataRecord r) =>
        new(
            new GroupId(Blob(r, "group_id")),
            new EpochId((ulong)GetInt64(r, "epoch")),
            (ProtocolProfile)GetInt64(r, "profile"),
            DateTimeOffset.Parse(GetString(r, "created_at")!),
            DateTimeOffset.Parse(GetString(r, "updated_at")!))
        {
            Removed = GetInt64(r, "removed") != 0,
            JoinEpoch = IsNull(r, "join_epoch") ? null : new EpochId((ulong)GetInt64(r, "join_epoch")),
            ValidatedTree = GetInt64(r, "validated_tree") != 0,
        };

    // -- Messages --

    public async Task PutMessageAsync(MessageRecord message, CancellationToken ct = default)
    {
        await using var cmd = Command($@"
            INSERT OR REPLACE INTO {_tp}messages
                (id, group_id, transport_id, source_epoch, state, wire, attempts, reason, created_at, updated_at)
            VALUES (@id, @group, @transport, @epoch, @state, @wire, @attempts, @reason, @created, @updated);");
        cmd.Parameters.AddWithValue("@id", message.Id.Value);
        cmd.Parameters.AddWithValue("@group", message.GroupId.Value);
        cmd.Parameters.AddWithValue("@transport", (object?)message.TransportId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@epoch", (long)message.SourceEpoch.Value);
        cmd.Parameters.AddWithValue("@state", (int)message.State);
        cmd.Parameters.AddWithValue("@wire", message.Wire);
        cmd.Parameters.AddWithValue("@attempts", message.Attempts);
        cmd.Parameters.AddWithValue("@reason", (object?)message.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", Iso(message.CreatedAt));
        cmd.Parameters.AddWithValue("@updated", Iso(message.UpdatedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<MessageRecord?> GetMessageAsync(MessageId id, CancellationToken ct = default)
    {
        await using var cmd = Command($"SELECT * FROM {_tp}messages WHERE id = @id;");
        cmd.Parameters.AddWithValue("@id", id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadMessage(reader) : null;
    }

    public async Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(
        GroupId groupId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT * FROM {_tp}messages WHERE group_id = @group ORDER BY created_at;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        return await ReadMessagesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<MessageRecord>> ListMessagesByStateAsync(
        GroupId groupId, MessageRecordState state, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT * FROM {_tp}messages WHERE group_id = @group AND state = @state ORDER BY created_at;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        cmd.Parameters.AddWithValue("@state", (int)state);
        return await ReadMessagesAsync(cmd, ct);
    }

    public async Task PutTransportSeenAsync(string transportId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"INSERT OR REPLACE INTO {_tp}transport_seen (transport_id, seen_at) VALUES (@id, @at);");
        cmd.Parameters.AddWithValue("@id", transportId);
        cmd.Parameters.AddWithValue("@at", Iso(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> HasTransportSeenAsync(string transportId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT 1 FROM {_tp}transport_seen WHERE transport_id = @id;");
        cmd.Parameters.AddWithValue("@id", transportId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task InvalidateAfterEpochAsync(
        GroupId groupId, EpochId epoch, CancellationToken ct = default)
    {
        // Retained, not deleted: a message that vanishes after a reorg still has
        // to be explainable to the user.
        await using var cmd = Command($@"
            UPDATE {_tp}messages
               SET state = @invalidated, updated_at = @now
             WHERE group_id = @group AND source_epoch > @epoch AND state <> @invalidated;");
        cmd.Parameters.AddWithValue("@invalidated", (int)MessageRecordState.EpochInvalidated);
        cmd.Parameters.AddWithValue("@now", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        cmd.Parameters.AddWithValue("@epoch", (long)epoch.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<MessageRecord>> ReadMessagesAsync(
        SqliteCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<MessageRecord>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadMessage(reader));
        return results;
    }

    private static MessageRecord ReadMessage(IDataRecord r) =>
        new(
            new MessageId(Blob(r, "id")),
            new GroupId(Blob(r, "group_id")),
            GetString(r, "transport_id"),
            new EpochId((ulong)GetInt64(r, "source_epoch")),
            (MessageRecordState)GetInt64(r, "state"),
            Blob(r, "wire"),
            DateTimeOffset.Parse(GetString(r, "created_at")!),
            DateTimeOffset.Parse(GetString(r, "updated_at")!))
        {
            Attempts = (int)GetInt64(r, "attempts"),
            Reason = GetString(r, "reason"),
        };

    // -- Outbound intents --

    public async Task PutIntentAsync(QueuedOutboundIntent intent, CancellationToken ct = default)
    {
        await using var cmd = Command($@"
            INSERT OR REPLACE INTO {_tp}outbound_intents
                (id, group_id, kind, payload, attempts, created_at)
            VALUES (@id, @group, @kind, @payload, @attempts, @created);");
        cmd.Parameters.AddWithValue("@id", intent.Id.Value);
        cmd.Parameters.AddWithValue("@group", intent.GroupId.Value);
        cmd.Parameters.AddWithValue("@kind", intent.IntentKind);
        cmd.Parameters.AddWithValue("@payload", intent.Payload);
        cmd.Parameters.AddWithValue("@attempts", intent.Attempts);
        cmd.Parameters.AddWithValue("@created", Iso(intent.CreatedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<QueuedOutboundIntent>> ListIntentsAsync(
        GroupId groupId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT * FROM {_tp}outbound_intents WHERE group_id = @group ORDER BY created_at;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<QueuedOutboundIntent>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new QueuedOutboundIntent(
                new MessageId(Blob(reader, "id")),
                new GroupId(Blob(reader, "group_id")),
                GetString(reader, "kind")!,
                Blob(reader, "payload"),
                DateTimeOffset.Parse(GetString(reader, "created_at")!))
            {
                Attempts = (int)GetInt64(reader, "attempts"),
            });
        }

        return results;
    }

    public async Task DeleteIntentAsync(MessageId id, CancellationToken ct = default)
    {
        await using var cmd = Command($"DELETE FROM {_tp}outbound_intents WHERE id = @id;");
        cmd.Parameters.AddWithValue("@id", id.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearIntentsAsync(GroupId groupId, CancellationToken ct = default)
    {
        await using var cmd = Command($"DELETE FROM {_tp}outbound_intents WHERE group_id = @group;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -- Leave requests --

    public async Task PutLeaveRequestAsync(LeaveRequest request, CancellationToken ct = default)
    {
        await using var cmd = Command($@"
            INSERT OR REPLACE INTO {_tp}leave_requests
                (group_id, requested_epoch, proposed_epoch, created_at)
            VALUES (@group, @requested, @proposed, @created);");
        cmd.Parameters.AddWithValue("@group", request.GroupId.Value);
        cmd.Parameters.AddWithValue("@requested", (long)request.RequestedInEpoch.Value);
        cmd.Parameters.AddWithValue("@proposed",
            request.ProposedInEpoch is { } pe ? (long)pe.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@created", Iso(request.CreatedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<LeaveRequest?> GetLeaveRequestAsync(GroupId groupId, CancellationToken ct = default)
    {
        await using var cmd = Command($"SELECT * FROM {_tp}leave_requests WHERE group_id = @group;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLeaveRequest(reader) : null;
    }

    public async Task<IReadOnlyList<LeaveRequest>> ListLeaveRequestsAsync(CancellationToken ct = default)
    {
        await using var cmd = Command($"SELECT * FROM {_tp}leave_requests ORDER BY created_at;");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<LeaveRequest>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadLeaveRequest(reader));
        return results;
    }

    public async Task ClearLeaveRequestAsync(GroupId groupId, CancellationToken ct = default)
    {
        await using var cmd = Command($"DELETE FROM {_tp}leave_requests WHERE group_id = @group;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static LeaveRequest ReadLeaveRequest(IDataRecord r) =>
        new(
            new GroupId(Blob(r, "group_id")),
            new EpochId((ulong)GetInt64(r, "requested_epoch")),
            DateTimeOffset.Parse(GetString(r, "created_at")!))
        {
            ProposedInEpoch = IsNull(r, "proposed_epoch")
                ? null
                : new EpochId((ulong)GetInt64(r, "proposed_epoch")),
        };

    // -- Welcomes --

    public async Task PutWelcomeAsync(WelcomeRecord welcome, CancellationToken ct = default)
    {
        await using var cmd = Command($@"
            INSERT OR REPLACE INTO {_tp}welcomes (id, wire, state, group_id, reason, created_at)
            VALUES (@id, @wire, @state, @group, @reason, @created);");
        cmd.Parameters.AddWithValue("@id", welcome.Id.Value);
        cmd.Parameters.AddWithValue("@wire", welcome.Wire);
        cmd.Parameters.AddWithValue("@state", (int)welcome.State);
        cmd.Parameters.AddWithValue("@group",
            welcome.GroupId is { } g ? g.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@reason", (object?)welcome.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", Iso(welcome.CreatedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<WelcomeRecord?> GetWelcomeAsync(MessageId id, CancellationToken ct = default)
    {
        await using var cmd = Command($"SELECT * FROM {_tp}welcomes WHERE id = @id;");
        cmd.Parameters.AddWithValue("@id", id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadWelcome(reader) : null;
    }

    public async Task<IReadOnlyList<WelcomeRecord>> ListWelcomesAsync(
        WelcomeRecordState? state = null, CancellationToken ct = default)
    {
        await using var cmd = state is null
            ? Command($"SELECT * FROM {_tp}welcomes ORDER BY created_at;")
            : Command($"SELECT * FROM {_tp}welcomes WHERE state = @state ORDER BY created_at;");
        if (state is { } s)
            cmd.Parameters.AddWithValue("@state", (int)s);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<WelcomeRecord>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadWelcome(reader));
        return results;
    }

    private static WelcomeRecord ReadWelcome(IDataRecord r) =>
        new(
            new MessageId(Blob(r, "id")),
            Blob(r, "wire"),
            (WelcomeRecordState)GetInt64(r, "state"),
            DateTimeOffset.Parse(GetString(r, "created_at")!))
        {
            GroupId = IsNull(r, "group_id") ? null : new GroupId(Blob(r, "group_id")),
            Reason = GetString(r, "reason"),
        };

    // -- Helpers --

    private static string Iso(DateTimeOffset value) => value.ToString("O");

    private static byte[] Blob(IDataRecord r, string column) => (byte[])r[column];

    private static long GetInt64(IDataRecord r, string column) => Convert.ToInt64(r[column]);

    private static string? GetString(IDataRecord r, string column) =>
        r[column] is DBNull ? null : (string)r[column];

    private static bool IsNull(IDataRecord r, string column) => r[column] is DBNull;

    public void Dispose()
    {
        _connection.Dispose();
    }
}
