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
        (4, "Routing index", V004),
        (5, "Epoch archive", V005),
        (6, "Epoch archive tip", V006),
        (7, "Replay attempt epoch", V007),
        (8, "Epoch states", V008),
        (9, "Commit publish attempts", V009),
        (10, "Durable live group state", V010),
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

    private static void V004(SqliteConnection connection, string tp)
    {
        // The routing id is the primary key, not the group: several addresses
        // map to one group over time as rotations happen, and each must stay
        // resolvable while an epoch that used it is still fetchable.
        Execute(connection, $@"
            CREATE TABLE {tp}routing_index (
                transport_group_id BLOB    NOT NULL PRIMARY KEY,
                group_id           BLOB    NOT NULL,
                first_epoch        INTEGER NOT NULL,
                last_epoch         INTEGER NULL,
                created_at         TEXT    NOT NULL
            );

            CREATE INDEX {tp}idx_routing_group ON {tp}routing_index (group_id);

            -- A group publishes to exactly one address at a time. Enforcing it
            -- here means a missed retirement fails loudly instead of leaving
            -- the group listening on two addresses and publishing to one.
            CREATE UNIQUE INDEX {tp}idx_routing_current
                ON {tp}routing_index (group_id) WHERE last_epoch IS NULL;");
    }

    private static void V005(SqliteConnection connection, string tp)
    {
        // Exported MLS group state, one row per epoch, so a branch forking a
        // few epochs back can be rebuilt -- an MLS group cannot rewind, so the
        // only way to evaluate a competing commit is to restore a copy of the
        // state it was built from and replay onto that.
        //
        // Keyed by (group, epoch) rather than by a name: recovery asks for 'the
        // state at epoch N', and the same epoch can legitimately be written
        // twice, once on a branch that loses and again after a reorg onto the
        // one that wins. The later write is the one a further branch forks from.
        //
        // tip_priority is the ordering class of the commit that produced the
        // epoch. It is stored rather than recomputed because it can only be
        // read before that commit is applied: applying it clears the proposal
        // cache its references resolve against.
        Execute(connection, $@"
            CREATE TABLE {tp}epoch_archive (
                group_id     BLOB    NOT NULL,
                epoch        INTEGER NOT NULL,
                group_state  BLOB    NOT NULL,
                tip_priority INTEGER NOT NULL,
                created_at   TEXT    NOT NULL,
                PRIMARY KEY (group_id, epoch)
            );

            CREATE INDEX {tp}idx_epoch_archive_group ON {tp}epoch_archive (group_id, epoch);");
    }

    private static void V006(SqliteConnection connection, string tp)
    {
        // The checkpoint has to describe the commit that produced its epoch, not
        // just how privileged that commit was. Branch selection reads three
        // things about a tip -- class, committer, digest -- and a member that
        // remembers one and guesses the other two scores its own branch on terms
        // nobody else computed.
        //
        // All three are nullable together, which is why this is a rebuild rather
        // than two ADD COLUMNs: a group's first epoch was produced by no commit,
        // and an epoch joined through a Welcome by a commit we never held. The
        // old tip_priority was NOT NULL and cannot express either.
        Execute(connection, $@"
            CREATE TABLE {tp}epoch_archive_new (
                group_id      BLOB    NOT NULL,
                epoch         INTEGER NOT NULL,
                group_state   BLOB    NOT NULL,
                tip_priority  INTEGER NULL,
                tip_commit    BLOB    NULL,
                tip_committer BLOB    NULL,
                created_at    TEXT    NOT NULL,
                PRIMARY KEY (group_id, epoch)
            );

            INSERT INTO {tp}epoch_archive_new
                (group_id, epoch, group_state, tip_priority, created_at)
            SELECT group_id, epoch, group_state, tip_priority, created_at
              FROM {tp}epoch_archive;

            DROP INDEX IF EXISTS {tp}idx_epoch_archive_group;
            DROP TABLE {tp}epoch_archive;
            ALTER TABLE {tp}epoch_archive_new RENAME TO {tp}epoch_archive;

            CREATE INDEX {tp}idx_epoch_archive_group ON {tp}epoch_archive (group_id, epoch);");
    }

    private static void V007(SqliteConnection connection, string tp)
    {
        // The epoch a held message was last tried at. A message waiting on keys
        // cannot become readable until the group's state moves, so without this
        // every replay re-attempts every held record -- and a peer can leave a
        // quiet group with as many of those as it likes.
        //
        // Nullable because "never tried" is not "tried at epoch zero", and the
        // two must not be confused: the second would skip a brand new record in
        // a group still at its first epoch.
        Execute(connection, $@"
            ALTER TABLE {tp}messages ADD COLUMN last_attempt_epoch INTEGER NULL;");
    }

    private static void V008(SqliteConnection connection, string tp)
    {
        // The epoch state machine, for the three states a restart cannot
        // otherwise recover. Nothing writes Stable, Merging or Recovering --
        // EpochStateRecord says why for each. So a row here always means the
        // group is refusing something, and an absent row means it is ordinary,
        // which is what EpochManager already reads a group it has no state for
        // as.
        //
        // Keyed by the group, not by the pending reference: a group has one
        // state, and a second row for it would make which one describes the
        // group a coin flip at exactly the moment -- session open, after a
        // crash -- when nobody is there to arbitrate.
        //
        // The CHECK is the pending fields standing or falling together. Four
        // fields that are meaningless apart: a row with the epoch and no staged
        // commit reads back as a reconcilable publish with nothing to publish.
        // It cannot be tripped through EpochStateRecord, whose factories refuse
        // the same shape earlier and with a better message -- it is here for a
        // writer that is not that type, which is the only kind of writer a
        // schema can still catch.
        Execute(connection, $@"
            CREATE TABLE {tp}epoch_states (
                group_id      BLOB    NOT NULL PRIMARY KEY,
                kind          INTEGER NOT NULL,
                epoch         INTEGER NOT NULL,
                prior_epoch   INTEGER NULL,
                staged_commit BLOB    NULL,
                pending_ref   INTEGER NULL,
                pending_kind  INTEGER NULL,
                updated_at    TEXT    NOT NULL,
                CHECK (
                    (kind =  1 AND prior_epoch   IS NOT NULL
                               AND staged_commit IS NOT NULL
                               AND pending_ref   IS NOT NULL
                               AND pending_kind  IS NOT NULL)
                 OR (kind <> 1 AND prior_epoch   IS NULL
                               AND staged_commit IS NULL
                               AND pending_ref   IS NULL
                               AND pending_kind  IS NULL)
                )
            );");
    }

    private static void V009(SqliteConnection connection, string tp)
    {
        // Publish intent for commits. StagedCommit.Publishing() draws the line
        // that decides crash recovery -- before it the commit is ours alone and
        // clearing it is free, after it a relay may hold it and clearing it
        // locally is the one move that guarantees a fork -- and that line is a
        // private field in an in-memory object. This table is what remembers
        // which side of it a dead process was on.
        //
        // Separate from epoch_states rather than a column on it, because the
        // two rows record different moments and are cleared at different ones:
        // the epoch-state row says a commit was STAGED and is dropped the
        // instant a publish is confirmed, which is exactly the instant this
        // fact -- the relay took it, we have not applied it yet -- becomes the
        // only thing standing between a restart and abandoning a live commit.
        //
        // Keyed by the group because MLS refuses a second staged commit, so a
        // second row could only describe one that no longer exists.
        //
        // No CHECK on `state`, unlike V008's. V008 pins four fields that are
        // meaningless apart; here every column is required in every state, so
        // the only CHECK available would enumerate the known values -- and
        // SQLite cannot alter a CHECK, so the day a fifth state is added it
        // would cost a table rebuild to say something the reader already
        // refuses, with a better message.
        Execute(connection, $@"
            CREATE TABLE {tp}commit_publish_attempts (
                group_id      BLOB    NOT NULL PRIMARY KEY,
                commit_id     BLOB    NOT NULL,
                new_epoch     INTEGER NOT NULL,
                state         INTEGER NOT NULL,
                handed_off_at TEXT    NOT NULL,
                updated_at    TEXT    NOT NULL
            );");
    }

    private static void V010(SqliteConnection connection, string tp)
    {
        // Two halves of one fact: where a group actually is.
        //
        // groups.live_state is the exported MLS group the engine last
        // confirmed. Until now nothing persisted it at all -- the epoch archive
        // is fed from the inbound commit path, so a group whose own member does
        // all the committing had nothing durable behind it and a restart could
        // not reconstruct the group its own record described. Nullable because
        // this is an ALTER on a live table and rows written before it exist;
        // a null reads as "no durable state", which is what those rows mean.
        //
        // staged_commits is the state a commit of OURS produces, written before
        // the commit is published. It is the one thing a crash between publish
        // and apply cannot reconstruct: MLS refuses to let a member process a
        // commit it authored, so the bytes coming back off a relay are no help,
        // and an export of the live group drops a staged-but-unmerged commit.
        //
        // Separate from commit_publish_attempts rather than a column on it,
        // because they answer different questions and are written at different
        // moments: this row says what the commit would make the group, and
        // exists from the moment it is staged; that row says how far it got
        // towards a relay, and exists only once something tried to send it. A
        // restart needs both, and needs them in that order -- adopt the state
        // only if the attempt says somebody else may have seen the commit.
        //
        // tip_priority, tip_commit and tip_committer are NOT NULL here, unlike
        // in epoch_archive. That table has to describe epochs no commit of ours
        // produced -- a group's first, or one a Welcome admitted us at. This
        // one describes a commit we authored and are holding, so all three are
        // readable by definition, and a null would mean the row was built
        // somewhere it could not have been.
        Execute(connection, $@"
            ALTER TABLE {tp}groups ADD COLUMN live_state BLOB NULL;

            CREATE TABLE {tp}staged_commits (
                group_id      BLOB    NOT NULL PRIMARY KEY,
                new_epoch     INTEGER NOT NULL,
                group_state   BLOB    NOT NULL,
                tip_priority  INTEGER NOT NULL,
                tip_commit    BLOB    NOT NULL,
                tip_committer BLOB    NOT NULL,
                created_at    TEXT    NOT NULL
            );");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
