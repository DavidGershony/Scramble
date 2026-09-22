using System.Data;

namespace Scramble.Marmot.Storage.Sqlite;

/// <summary>
/// The rotation-aware routing index.
/// </summary>
public sealed partial class SqliteMarmotStorageProvider
{
    public async Task PutRoutingAsync(
        byte[] transportGroupId,
        GroupId groupId,
        EpochId firstEpoch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transportGroupId);
        ArgumentNullException.ThrowIfNull(groupId);

        var existing = await ResolveAsync(transportGroupId, ct);
        if (existing is not null)
        {
            // Rebinding to another group is refused rather than overwritten. A
            // routing id is public — it is on every kind-445 event — so
            // last-write-wins would let anyone who has seen one redirect that
            // group's traffic into state they control.
            if (!existing.GroupId.Value.AsSpan().SequenceEqual(groupId.Value))
            {
                throw new RoutingIdConflictException(
                    "That routing id is already bound to a different group.");
            }

            // Re-registering a group's own current address is a no-op, so a
            // replayed or retried rotation does not retire the address it is
            // re-affirming.
            if (existing.IsCurrent && existing.FirstEpoch.Value == firstEpoch.Value)
                return;
        }

        // Retire the previous current address in the same step as binding the
        // new one: a group with two current addresses publishes to one and
        // listens on both, which looks fine until a rotation is missed.
        await using (var retire = Command($@"
            UPDATE {_tp}routing_index
               SET last_epoch = @last
             WHERE group_id = @group AND last_epoch IS NULL AND transport_group_id <> @id;"))
        {
            retire.Parameters.AddWithValue("@last", (long)firstEpoch.Value);
            retire.Parameters.AddWithValue("@group", groupId.Value);
            retire.Parameters.AddWithValue("@id", transportGroupId);
            await retire.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = Command($@"
            INSERT OR REPLACE INTO {_tp}routing_index
                (transport_group_id, group_id, first_epoch, last_epoch, created_at)
            VALUES (@id, @group, @first, NULL, @created);");
        cmd.Parameters.AddWithValue("@id", transportGroupId);
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        cmd.Parameters.AddWithValue("@first", (long)firstEpoch.Value);
        cmd.Parameters.AddWithValue("@created", Iso(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<RoutingIndexRecord?> ResolveAsync(
        byte[] transportGroupId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transportGroupId);

        // Exact equality on the full 32 bytes. SQLite compares BLOBs bytewise,
        // so this is the whole-value match the receive path requires.
        await using var cmd = Command(
            $"SELECT * FROM {_tp}routing_index WHERE transport_group_id = @id;");
        cmd.Parameters.AddWithValue("@id", transportGroupId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRouting(reader) : null;
    }

    public async Task<IReadOnlyList<RoutingIndexRecord>> ListRoutingAsync(
        GroupId groupId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        await using var cmd = Command($@"
            SELECT * FROM {_tp}routing_index
             WHERE group_id = @group
             ORDER BY last_epoch IS NULL DESC, first_epoch DESC;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<RoutingIndexRecord>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadRouting(reader));
        return results;
    }

    public async Task<RoutingIndexRecord?> CurrentRoutingAsync(
        GroupId groupId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        await using var cmd = Command(
            $"SELECT * FROM {_tp}routing_index WHERE group_id = @group AND last_epoch IS NULL;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRouting(reader) : null;
    }

    public async Task<int> PruneRoutingAsync(
        GroupId groupId, EpochId horizon, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        // The current address survives any horizon twice over: `last_epoch`
        // is NULL for it, and `NULL < n` is NULL rather than true, so SQL's
        // three-valued logic already excludes the row. The explicit
        // `IS NOT NULL` states that intent rather than leaving it resting on
        // a subtlety a later edit could quietly undo — it is belt-and-braces,
        // not the thing doing the work.
        await using var cmd = Command($@"
            DELETE FROM {_tp}routing_index
             WHERE group_id = @group AND last_epoch IS NOT NULL AND last_epoch < @horizon;");
        cmd.Parameters.AddWithValue("@group", groupId.Value);
        cmd.Parameters.AddWithValue("@horizon", (long)horizon.Value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static RoutingIndexRecord ReadRouting(IDataRecord r) =>
        new(
            Blob(r, "transport_group_id"),
            new GroupId(Blob(r, "group_id")),
            new EpochId((ulong)GetInt64(r, "first_epoch")),
            IsNull(r, "last_epoch") ? null : new EpochId((ulong)GetInt64(r, "last_epoch")),
            DateTimeOffset.Parse(GetString(r, "created_at")!));
}
