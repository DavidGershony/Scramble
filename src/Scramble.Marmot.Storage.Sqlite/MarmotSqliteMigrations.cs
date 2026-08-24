using Microsoft.Data.Sqlite;

namespace Scramble.Marmot.Storage.Sqlite;

/// <summary>
/// Versioned schema migrations, applied in order on open.
/// </summary>
internal static class MarmotSqliteMigrations
{
    private static readonly (int Version, string Name, Action<SqliteConnection, string> Apply)[] Migrations =
    {
        (1, "Core engine tables", V001),
        (2, "Query indexes", V002),
        (3, "KeyPackage bundles", V003),
    };

    public static void Apply(SqliteConnection connection, string tablePrefix)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                $"CREATE TABLE IF NOT EXISTS {tablePrefix}schema_version (version INTEGER NOT NULL PRIMARY KEY);";
            cmd.ExecuteNonQuery();
        }

        int current;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT COALESCE(MAX(version), 0) FROM {tablePrefix}schema_version;";
            current = Convert.ToInt32(cmd.ExecuteScalar());
        }

        foreach (var migration in Migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using var tx = connection.BeginTransaction();
            migration.Apply(connection, tablePrefix);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"INSERT INTO {tablePrefix}schema_version (version) VALUES (@v);";
                cmd.Parameters.AddWithValue("@v", migration.Version);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private static void V001(SqliteConnection connection, string tp)
    {
        Execute(connection, $@"
            CREATE TABLE {tp}groups (
                group_id       BLOB    NOT NULL PRIMARY KEY,
                epoch          INTEGER NOT NULL,
                profile        INTEGER NOT NULL,
                removed        INTEGER NOT NULL DEFAULT 0,
                join_epoch     INTEGER NULL,
                validated_tree INTEGER NOT NULL DEFAULT 0,
                created_at     TEXT    NOT NULL,
                updated_at     TEXT    NOT NULL
            );

            -- Keyed by the content-derived message id, never a transport id.
            CREATE TABLE {tp}messages (
                id           BLOB    NOT NULL PRIMARY KEY,
                group_id     BLOB    NOT NULL,
                transport_id TEXT    NULL,
                source_epoch INTEGER NOT NULL,
                state        INTEGER NOT NULL,
                wire         BLOB    NOT NULL,
                attempts     INTEGER NOT NULL DEFAULT 0,
                reason       TEXT    NULL,
                created_at   TEXT    NOT NULL,
                updated_at   TEXT    NOT NULL
            );

            -- Cheap pre-filter so a duplicate envelope is not peeled twice.
            CREATE TABLE {tp}transport_seen (
                transport_id TEXT NOT NULL PRIMARY KEY,
                seen_at      TEXT NOT NULL
            );

            CREATE TABLE {tp}outbound_intents (
                id          BLOB    NOT NULL PRIMARY KEY,
                group_id    BLOB    NOT NULL,
                kind        TEXT    NOT NULL,
                payload     BLOB    NOT NULL,
                attempts    INTEGER NOT NULL DEFAULT 0,
                created_at  TEXT    NOT NULL
            );

            CREATE TABLE {tp}leave_requests (
                group_id         BLOB    NOT NULL PRIMARY KEY,
                requested_epoch  INTEGER NOT NULL,
                proposed_epoch   INTEGER NULL,
                created_at       TEXT    NOT NULL
            );

            CREATE TABLE {tp}welcomes (
                id         BLOB    NOT NULL PRIMARY KEY,
                wire       BLOB    NOT NULL,
                state      INTEGER NOT NULL,
                group_id   BLOB    NULL,
                reason     TEXT    NULL,
                created_at TEXT    NOT NULL
            );

            -- Anchored to the epoch they capture, so recovery can ask for
            -- 'the snapshot at epoch N' and pruning follows the rewind horizon.
            CREATE TABLE {tp}snapshots (
                name       TEXT    NOT NULL PRIMARY KEY,
                group_id   BLOB    NOT NULL,
                epoch      INTEGER NOT NULL,
                data       BLOB    NOT NULL,
                created_at TEXT    NOT NULL
            );");
    }

    private static void V002(SqliteConnection connection, string tp)
    {
        Execute(connection, $@"
            CREATE INDEX {tp}idx_messages_group      ON {tp}messages (group_id);
            CREATE INDEX {tp}idx_messages_state      ON {tp}messages (group_id, state);
            CREATE INDEX {tp}idx_messages_epoch      ON {tp}messages (group_id, source_epoch);
            CREATE INDEX {tp}idx_intents_group       ON {tp}outbound_intents (group_id);
            CREATE INDEX {tp}idx_welcomes_state      ON {tp}welcomes (state);
            CREATE UNIQUE INDEX {tp}idx_snapshot_epoch ON {tp}snapshots (group_id, epoch);");
    }

    private static void V003(SqliteConnection connection, string tp)
    {
        // Keyed by KeyPackageRef because that is the key the MLS layer looks a
        // bundle up under while processing a Welcome. private_material is
        // nullable so erasing it leaves the record behind: a Welcome naming a
        // spent KeyPackage must be answerable, and a missing row cannot say
        // 'consumed already' as distinct from 'never mine'.
        Execute(connection, $@"
            CREATE TABLE {tp}key_packages (
                key_package_ref  TEXT    NOT NULL PRIMARY KEY,
                slot_id          TEXT    NOT NULL,
                event_id         TEXT    NULL,
                public_bytes     BLOB    NOT NULL,
                private_material BLOB    NULL,
                last_resort      INTEGER NOT NULL DEFAULT 0,
                not_before       INTEGER NOT NULL,
                not_after        INTEGER NOT NULL,
                state            INTEGER NOT NULL,
                created_at       TEXT    NOT NULL
            );

            CREATE INDEX {tp}idx_key_packages_slot  ON {tp}key_packages (slot_id);
            CREATE INDEX {tp}idx_key_packages_state ON {tp}key_packages (state);

            -- A Welcome names its KeyPackage by event id, so the lookup must be
            -- unambiguous: two records claiming one event would make which
            -- private material to use a coin flip.
            CREATE UNIQUE INDEX {tp}idx_key_packages_event
                ON {tp}key_packages (event_id) WHERE event_id IS NOT NULL;");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
