using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Scramble.Marmot.Storage.Sqlite;

/// <summary>
/// Epoch-anchored snapshot support.
/// </summary>
/// <remarks>
/// A snapshot captures the Marmot-layer rows for one group as a JSON document.
/// It deliberately does NOT capture MLS state: that lives in MLS storage and is
/// snapshotted by exporting the group there. Rolling back both together is the
/// engine's job, which is why rollback runs inside a transaction here.
/// </remarks>
public sealed partial class SqliteMarmotStorageProvider
{
    /// <summary>
    /// Anchor name for a group's snapshot at an epoch. Parsed by
    /// <see cref="TryParseSnapshotName"/>, so the format is load-bearing.
    /// </summary>
    internal static string SnapshotName(GroupId groupId, EpochId epoch) =>
        $"epoch-{groupId}-{epoch.Value}";

    internal static bool TryParseSnapshotName(string name, out EpochId epoch)
    {
        epoch = default;
        int lastDash = name.LastIndexOf('-');
        if (lastDash < 0 || !ulong.TryParse(name.AsSpan(lastDash + 1), out ulong value))
            return false;

        epoch = new EpochId(value);
        return true;
    }

    public async Task<string> CreateSnapshotAsync(
        GroupId groupId, EpochId epoch, CancellationToken ct = default)
    {
        string name = SnapshotName(groupId, epoch);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(await CaptureAsync(groupId, ct));

        // Re-snapshotting an epoch replaces it: a retried commit at the same
        // epoch must not leave a stale anchor behind.
        await using var cmd = Command($@"
            INSERT OR REPLACE INTO {_tp}snapshots (name, group_id, epoch, data, created_at)
            VALUES (@name, @group, @epoch, @data, @created);");
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        cmd.Parameters.AddWithValue("@epoch", (long)epoch.Value);
        cmd.Parameters.AddWithValue("@data", payload);
        cmd.Parameters.AddWithValue("@created", Iso(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);

        return name;
    }

    public async Task<string?> GetSnapshotAsync(
        GroupId groupId, EpochId epoch, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT name FROM {_tp}snapshots WHERE group_id = @group AND epoch = @epoch;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        cmd.Parameters.AddWithValue("@epoch", (long)epoch.Value);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyList<string>> ListSnapshotsAsync(
        GroupId groupId, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"SELECT name FROM {_tp}snapshots WHERE group_id = @group ORDER BY epoch;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));
        return names;
    }

    public async Task ReleaseSnapshotAsync(string snapshotName, CancellationToken ct = default)
    {
        await using var cmd = Command($"DELETE FROM {_tp}snapshots WHERE name = @name;");
        cmd.Parameters.AddWithValue("@name", snapshotName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PruneSnapshotsBeforeAsync(
        GroupId groupId, EpochId oldestRetainedEpoch, CancellationToken ct = default)
    {
        await using var cmd = Command(
            $"DELETE FROM {_tp}snapshots WHERE group_id = @group AND epoch < @epoch;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        cmd.Parameters.AddWithValue("@epoch", (long)oldestRetainedEpoch.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RollbackToSnapshotAsync(string snapshotName, CancellationToken ct = default)
    {
        byte[] data;
        byte[] rawGroupId;
        await using (var cmd = Command(
            $"SELECT group_id, data FROM {_tp}snapshots WHERE name = @name;"))
        {
            cmd.Parameters.AddWithValue("@name", snapshotName);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new KeyNotFoundException($"Snapshot '{snapshotName}' not found.");

            rawGroupId = (byte[])reader["group_id"];
            data = (byte[])reader["data"];
        }

        var snapshot = JsonSerializer.Deserialize<GroupSnapshot>(data)
            ?? throw new InvalidOperationException($"Snapshot '{snapshotName}' is corrupt.");
        var groupId = new GroupId(rawGroupId);

        // Restoring must be all-or-nothing. If the caller already opened a
        // transaction, join it rather than nesting a second one.
        bool ownsTransaction = _active is null;
        IStorageTransaction? tx = ownsTransaction ? await BeginTransactionAsync(ct) : null;

        try
        {
            await RestoreAsync(groupId, snapshot, ct);
            if (tx is not null)
                await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null)
                await tx.DisposeAsync();
        }
    }

    private async Task<GroupSnapshot> CaptureAsync(GroupId groupId, CancellationToken ct)
    {
        var group = await GetGroupAsync(groupId, ct);
        var messages = await ListMessagesAsync(groupId, ct);
        var intents = await ListIntentsAsync(groupId, ct);
        var leave = await GetLeaveRequestAsync(groupId, ct);

        return new GroupSnapshot
        {
            Group = group is null ? null : GroupDto.From(group),
            Messages = messages.Select(MessageDto.From).ToList(),
            Intents = intents.Select(IntentDto.From).ToList(),
            Leave = leave is null ? null : LeaveDto.From(leave),
        };
    }

    private async Task RestoreAsync(GroupId groupId, GroupSnapshot snapshot, CancellationToken ct)
    {
        await DeleteWhereGroupAsync($"{_tp}messages", groupId, ct);
        await DeleteWhereGroupAsync($"{_tp}outbound_intents", groupId, ct);
        await ClearLeaveRequestAsync(groupId, ct);

        if (snapshot.Group is { } g)
            await PutGroupAsync(g.ToRecord(), ct);
        else
            await DeleteGroupAsync(groupId, ct);

        foreach (var message in snapshot.Messages)
            await PutMessageAsync(message.ToRecord(), ct);

        foreach (var intent in snapshot.Intents)
            await PutIntentAsync(intent.ToRecord(), ct);

        if (snapshot.Leave is { } l)
            await PutLeaveRequestAsync(l.ToRecord(), ct);
    }

    private async Task DeleteWhereGroupAsync(string table, GroupId groupId, CancellationToken ct)
    {
        await using var cmd = Command($"DELETE FROM {table} WHERE group_id = @group;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -- Serialisation DTOs --
    // Records are serialised through explicit DTOs rather than directly, so a
    // change to a record shape cannot silently invalidate snapshots on disk.

    private sealed class GroupSnapshot
    {
        public GroupDto? Group { get; set; }
        public List<MessageDto> Messages { get; set; } = new();
        public List<IntentDto> Intents { get; set; } = new();
        public LeaveDto? Leave { get; set; }
    }

    private sealed class GroupDto
    {
        public byte[] Id { get; set; } = Array.Empty<byte>();
        public ulong Epoch { get; set; }
        public int Profile { get; set; }
        public bool Removed { get; set; }
        public ulong? JoinEpoch { get; set; }
        public bool ValidatedTree { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public static GroupDto From(GroupRecord r) => new()
        {
            Id = r.Id.Value,
            Epoch = r.Epoch.Value,
            Profile = (int)r.Profile,
            Removed = r.Removed,
            JoinEpoch = r.JoinEpoch?.Value,
            ValidatedTree = r.ValidatedTree,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
        };

        public GroupRecord ToRecord() =>
            new(new GroupId(Id), new EpochId(Epoch), (ProtocolProfile)Profile, CreatedAt, UpdatedAt)
            {
                Removed = Removed,
                JoinEpoch = JoinEpoch is { } e ? new EpochId(e) : null,
                ValidatedTree = ValidatedTree,
            };
    }

    private sealed class MessageDto
    {
        public byte[] Id { get; set; } = Array.Empty<byte>();
        public byte[] GroupId { get; set; } = Array.Empty<byte>();
        public string? TransportId { get; set; }
        public ulong SourceEpoch { get; set; }
        public int State { get; set; }
        public byte[] Wire { get; set; } = Array.Empty<byte>();
        public int Attempts { get; set; }
        public string? Reason { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public static MessageDto From(MessageRecord r) => new()
        {
            Id = r.Id.Value,
            GroupId = r.GroupId.Value,
            TransportId = r.TransportId,
            SourceEpoch = r.SourceEpoch.Value,
            State = (int)r.State,
            Wire = r.Wire,
            Attempts = r.Attempts,
            Reason = r.Reason,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
        };

        public MessageRecord ToRecord() =>
            new(
                new MessageId(Id),
                new GroupId(GroupId),
                TransportId,
                new EpochId(SourceEpoch),
                (MessageRecordState)State,
                Wire,
                CreatedAt,
                UpdatedAt)
            {
                Attempts = Attempts,
                Reason = Reason,
            };
    }

    private sealed class IntentDto
    {
        public byte[] Id { get; set; } = Array.Empty<byte>();
        public byte[] GroupId { get; set; } = Array.Empty<byte>();
        public string Kind { get; set; } = string.Empty;
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public int Attempts { get; set; }
        public DateTimeOffset CreatedAt { get; set; }

        public static IntentDto From(QueuedOutboundIntent r) => new()
        {
            Id = r.Id.Value,
            GroupId = r.GroupId.Value,
            Kind = r.IntentKind,
            Payload = r.Payload,
            Attempts = r.Attempts,
            CreatedAt = r.CreatedAt,
        };

        public QueuedOutboundIntent ToRecord() =>
            new(new MessageId(Id), new GroupId(GroupId), Kind, Payload, CreatedAt)
            {
                Attempts = Attempts,
            };
    }

    private sealed class LeaveDto
    {
        public byte[] GroupId { get; set; } = Array.Empty<byte>();
        public ulong RequestedInEpoch { get; set; }
        public ulong? ProposedInEpoch { get; set; }
        public DateTimeOffset CreatedAt { get; set; }

        public static LeaveDto From(LeaveRequest r) => new()
        {
            GroupId = r.GroupId.Value,
            RequestedInEpoch = r.RequestedInEpoch.Value,
            ProposedInEpoch = r.ProposedInEpoch?.Value,
            CreatedAt = r.CreatedAt,
        };

        public LeaveRequest ToRecord() =>
            new(new GroupId(GroupId), new EpochId(RequestedInEpoch), CreatedAt)
            {
                ProposedInEpoch = ProposedInEpoch is { } e ? new EpochId(e) : null,
            };
    }
}
