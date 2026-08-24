using System.Data;
using Microsoft.Data.Sqlite;

namespace Scramble.Marmot.Storage.Sqlite;

/// <summary>
/// KeyPackage records and their private material.
/// </summary>
/// <remarks>
/// Every write here is a targeted <c>UPDATE</c> with the legal predecessor
/// states in its <c>WHERE</c>, rather than a read-modify-write. Two reasons:
/// the transition stays correct without holding a transaction open across the
/// read, and — more importantly — no code path ever holds a whole record in
/// memory and writes it back, which is the shape that resurrects erased key
/// material.
/// </remarks>
public sealed partial class SqliteMarmotStorageProvider
{
    public async Task PutKeyPackageAsync(KeyPackageRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var cmd = Command($@"
            INSERT INTO {_tp}key_packages
                (key_package_ref, slot_id, event_id, public_bytes, private_material,
                 last_resort, not_before, not_after, state, created_at)
            VALUES (@ref, @slot, @event, @public, @private,
                    @last_resort, @not_before, @not_after, @state, @created);");
        cmd.Parameters.AddWithValue("@ref", record.KeyPackageRefHex);
        cmd.Parameters.AddWithValue("@slot", record.SlotId);
        cmd.Parameters.AddWithValue("@event", (object?)record.EventIdHex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@public", record.PublicKeyPackage);
        cmd.Parameters.AddWithValue("@private", (object?)record.PrivateMaterial ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last_resort", record.LastResort ? 1 : 0);
        cmd.Parameters.AddWithValue("@not_before", record.NotBefore);
        cmd.Parameters.AddWithValue("@not_after", record.NotAfter);
        cmd.Parameters.AddWithValue("@state", (int)record.State);
        cmd.Parameters.AddWithValue("@created", Iso(record.CreatedAt));

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // Insert-only on purpose: a replace would let a caller holding a
            // record from before an erase write the private material back.
            throw new InvalidOperationException(
                $"A KeyPackage record already exists for {record.KeyPackageRefHex}.", ex);
        }
    }

    public async Task<KeyPackageRecord?> GetKeyPackageAsync(
        string keyPackageRefHex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyPackageRefHex);

        await using var cmd = Command(
            $"SELECT * FROM {_tp}key_packages WHERE key_package_ref = @ref;");
        cmd.Parameters.AddWithValue("@ref", keyPackageRefHex);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadKeyPackage(reader) : null;
    }

    public async Task<KeyPackageRecord?> GetKeyPackageByEventAsync(
        string eventIdHex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(eventIdHex);

        await using var cmd = Command(
            $"SELECT * FROM {_tp}key_packages WHERE event_id = @event;");
        cmd.Parameters.AddWithValue("@event", eventIdHex);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadKeyPackage(reader) : null;
    }

    public async Task<IReadOnlyList<KeyPackageRecord>> ListKeyPackagesAsync(
        string? slotId = null,
        KeyPackageRecordState? state = null,
        CancellationToken ct = default)
    {
        var clauses = new List<string>(2);
        if (slotId is not null)
            clauses.Add("slot_id = @slot");
        if (state is not null)
            clauses.Add("state = @state");

        string where = clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);

        await using var cmd = Command(
            $"SELECT * FROM {_tp}key_packages{where} ORDER BY created_at;");
        if (slotId is not null)
            cmd.Parameters.AddWithValue("@slot", slotId);
        if (state is { } s)
            cmd.Parameters.AddWithValue("@state", (int)s);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<KeyPackageRecord>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadKeyPackage(reader));
        return results;
    }

    public async Task<bool> MarkPublishedAsync(
        string keyPackageRefHex, string eventIdHex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyPackageRefHex);
        ArgumentNullException.ThrowIfNull(eventIdHex);

        // Only from Created. A publish confirmation arriving for a KeyPackage
        // that is already consumed or retired is late, not new, and must not
        // re-open it.
        await using var cmd = Command($@"
            UPDATE {_tp}key_packages
               SET state = @published, event_id = @event
             WHERE key_package_ref = @ref AND state = @created;");
        cmd.Parameters.AddWithValue("@published", (int)KeyPackageRecordState.Published);
        cmd.Parameters.AddWithValue("@created", (int)KeyPackageRecordState.Created);
        cmd.Parameters.AddWithValue("@event", eventIdHex);
        cmd.Parameters.AddWithValue("@ref", keyPackageRefHex);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> MarkConsumedAsync(
        string keyPackageRefHex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyPackageRefHex);

        // Retired is excluded rather than merely not expected: erasure is
        // one-way, and a record whose material is gone must not read as
        // consumable again.
        await using var cmd = Command($@"
            UPDATE {_tp}key_packages
               SET state = @consumed
             WHERE key_package_ref = @ref AND state <> @retired;");
        cmd.Parameters.AddWithValue("@consumed", (int)KeyPackageRecordState.Consumed);
        cmd.Parameters.AddWithValue("@retired", (int)KeyPackageRecordState.Retired);
        cmd.Parameters.AddWithValue("@ref", keyPackageRefHex);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> ErasePrivateMaterialAsync(
        string keyPackageRefHex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyPackageRefHex);

        // Unconditional on state, so this is idempotent and always available:
        // the deadlines that force erasure — a consumed normal KeyPackage, a
        // superseded or expired last-resort one — must never be blocked by
        // which state the record happens to be in.
        await using var cmd = Command($@"
            UPDATE {_tp}key_packages
               SET private_material = NULL, state = @retired
             WHERE key_package_ref = @ref;");
        cmd.Parameters.AddWithValue("@retired", (int)KeyPackageRecordState.Retired);
        cmd.Parameters.AddWithValue("@ref", keyPackageRefHex);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> DeleteKeyPackageAsync(
        string keyPackageRefHex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyPackageRefHex);

        await using var cmd = Command(
            $"DELETE FROM {_tp}key_packages WHERE key_package_ref = @ref;");
        cmd.Parameters.AddWithValue("@ref", keyPackageRefHex);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static KeyPackageRecord ReadKeyPackage(IDataRecord r) =>
        new(
            GetString(r, "key_package_ref")!,
            GetString(r, "slot_id")!,
            Blob(r, "public_bytes"),
            IsNull(r, "private_material") ? null : Blob(r, "private_material"),
            GetInt64(r, "last_resort") != 0,
            GetInt64(r, "not_before"),
            GetInt64(r, "not_after"),
            (KeyPackageRecordState)GetInt64(r, "state"),
            DateTimeOffset.Parse(GetString(r, "created_at")!))
        {
            EventIdHex = GetString(r, "event_id"),
        };
}
