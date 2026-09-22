using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using Scramble.Core.Services;
using Scramble.Core.Tests.TestHelpers;
using Scramble.Marmot;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Storage.Sqlite;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// At-rest protection for the Dark Matter engine's store, checked against the
/// bytes on disk rather than against the decorator's own arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not a test per method.</b> The failure mode
/// <see cref="SecureMarmotStorageProvider"/> exists to prevent is not a wrong
/// method — it is a field nobody thought about. A suite that asserts
/// "PutGroupAsync protects LiveState" passes forever while a column added next
/// month writes a ratchet secret in the clear beside it, because nothing in such
/// a suite is even aware the column exists. So the assertions here are
/// exhaustive over the store rather than enumerated over the API.
/// </para>
/// <para>
/// <b>How a future unprotected field is caught — three mechanisms, and the
/// limits of each.</b>
/// </para>
/// <para>
/// 1. <see cref="EveryColumnInTheSchemaHasARecordedDisposition"/> reads the real
/// schema with <c>PRAGMA table_info</c> and requires every column of every
/// engine table to appear in <see cref="Census"/>. A migration that adds a
/// column — of any type, not only BLOB — fails this test until somebody writes
/// down what the column holds and why it is or is not protected. This is the
/// mechanism that catches the field nobody thought about: it cannot be satisfied
/// by accident, only by a decision.
/// </para>
/// <para>
/// 2. <see cref="EveryByteCarryingRecordMemberHasARecordedDisposition"/> walks
/// <see cref="IMarmotStorageProvider"/>'s whole signature surface by reflection,
/// collects every record type reachable through it, and requires every member
/// that carries bytes — a <c>byte[]</c>, or an id struct wrapping one — to
/// appear in <see cref="MemberCensus"/>. It catches a new field on an existing
/// record even before a migration lands, and it catches a new sub-interface's
/// records with no edit to this test.
/// </para>
/// <para>
/// 3. <see cref="ProtectedColumnsCarryTheEncryptionPrefixOnDisk"/> and
/// <see cref="TheRawDatabaseFileContainsNoPlaintext"/> then prove the
/// classification is not merely written down. The first asserts positively that
/// every column classified <c>protected</c> carries the <see cref="ISecureStorage"/>
/// magic prefix in every row; the second sweeps every byte of every column of
/// every table for the known plaintexts, in raw and Base64 form, so a leak
/// through a path nobody modelled — a JSON document, a debug copy of a field,
/// a new column holding an old secret — shows up without anybody predicting it.
/// </para>
/// <para>
/// <b>What none of this catches.</b> A secret written to a column the census
/// already classifies as non-secret — say a future change that starts putting
/// key material into <c>messages.reason</c> — passes mechanisms 1 and 2, because
/// the column and the member are both already classified, and passes 3 unless
/// the value happens to be one of the needles. Closing that would take a
/// property-based check over the engine's own writes, or entropy heuristics on
/// column contents; neither is here. The census is a forcing function on the
/// schema, not a proof about content.
/// </para>
/// </remarks>
public sealed class SecureMarmotStorageProviderTests : IDisposable
{
    /// <summary>
    /// The prefix every <see cref="ISecureStorage"/> implementation prepends, and
    /// the only externally visible difference between a protected value and one
    /// that merely looks fine.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately a literal, and deliberately not
    /// <c>SecureStorageFormat.MagicPrefix</c>.</b> The platform implementations
    /// now share that one definition, which is what stops them drifting from each
    /// other. This copy exists to catch the case sharing cannot: a change to the
    /// shared value itself. A test that imported the constant would follow it and
    /// stay green, while every profile database ever written became unreadable.
    /// So this stays an independent pin — see the remarks on
    /// <c>SecureStorageFormat</c> — as does each project's
    /// <see cref="MockSecureStorage"/>.
    /// </remarks>
    private static readonly byte[] MagicPrefix = { 0xEE, 0xCC, 0x01, 0x00 };

    private const string Prefix = "marmot_";

    /// <summary>
    /// Every column of every engine table, and what it is allowed to hold in the
    /// clear. Format is <c>disposition table.column</c>, one per line.
    /// </summary>
    /// <remarks>
    /// <para><c>protected</c> — secret. Must carry <see cref="MagicPrefix"/> in
    /// every stored row, and must round-trip back to plaintext through the
    /// decorator.</para>
    /// <para><c>transitive</c> — secret, and protected without the decorator
    /// touching it: the provider composes the value <i>below</i> the decorator
    /// out of rows it reads for itself, so what it serialises is already
    /// protected. Asserted by the plaintext sweep, in raw and Base64 form,
    /// because the value is a JSON document rather than a protected blob.</para>
    /// <para><c>id</c> — a content-derived identifier or routing address that
    /// <b>cannot</b> be encrypted. <see cref="ISecureStorage.Protect"/> is not
    /// deterministic, so an encrypted primary key would differ on every write:
    /// lookups by it would miss, <c>INSERT OR REPLACE</c> would insert, and the
    /// byte-equality the engine uses to recognise its own staged commit and its
    /// own branch would never hold. Each of these is a digest of bytes a relay
    /// already carries, so there is nothing secret to lose. Asserted <i>not</i>
    /// to carry the prefix, so that a well-meant future change to encrypt one
    /// fails here rather than in production lookups.</para>
    /// <para><c>public</c> — published to relays as its own event; secrecy is
    /// not a property it ever had.</para>
    /// <para><c>meta</c> — bookkeeping with no secret in it: epoch numbers,
    /// enum states, counters, timestamps, slot names, failure reasons.</para>
    /// </remarks>
    private const string Census = """
        id          groups.group_id
        meta        groups.epoch
        meta        groups.profile
        meta        groups.removed
        meta        groups.join_epoch
        meta        groups.validated_tree
        meta        groups.created_at
        meta        groups.updated_at
        protected   groups.live_state

        id          messages.id
        id          messages.group_id
        id          messages.transport_id
        meta        messages.source_epoch
        meta        messages.state
        protected   messages.wire
        meta        messages.attempts
        meta        messages.reason
        meta        messages.created_at
        meta        messages.updated_at
        meta        messages.last_attempt_epoch

        id          transport_seen.transport_id
        meta        transport_seen.seen_at

        id          outbound_intents.id
        id          outbound_intents.group_id
        meta        outbound_intents.kind
        protected   outbound_intents.payload
        meta        outbound_intents.attempts
        meta        outbound_intents.created_at

        id          leave_requests.group_id
        meta        leave_requests.requested_epoch
        meta        leave_requests.proposed_epoch
        meta        leave_requests.created_at

        id          welcomes.id
        protected   welcomes.wire
        meta        welcomes.state
        id          welcomes.group_id
        meta        welcomes.reason
        meta        welcomes.created_at

        meta        snapshots.name
        id          snapshots.group_id
        meta        snapshots.epoch
        transitive  snapshots.data
        meta        snapshots.created_at

        id          key_packages.key_package_ref
        meta        key_packages.slot_id
        id          key_packages.event_id
        public      key_packages.public_bytes
        protected   key_packages.private_material
        meta        key_packages.last_resort
        meta        key_packages.not_before
        meta        key_packages.not_after
        meta        key_packages.state
        meta        key_packages.created_at

        id          routing_index.transport_group_id
        id          routing_index.group_id
        meta        routing_index.first_epoch
        meta        routing_index.last_epoch
        meta        routing_index.created_at

        id          epoch_archive.group_id
        meta        epoch_archive.epoch
        protected   epoch_archive.group_state
        meta        epoch_archive.tip_priority
        id          epoch_archive.tip_commit
        protected   epoch_archive.tip_committer
        meta        epoch_archive.created_at

        id          epoch_states.group_id
        meta        epoch_states.kind
        meta        epoch_states.epoch
        meta        epoch_states.prior_epoch
        id          epoch_states.staged_commit
        meta        epoch_states.pending_ref
        meta        epoch_states.pending_kind
        meta        epoch_states.updated_at

        id          commit_publish_attempts.group_id
        id          commit_publish_attempts.commit_id
        meta        commit_publish_attempts.new_epoch
        meta        commit_publish_attempts.state
        meta        commit_publish_attempts.handed_off_at
        meta        commit_publish_attempts.updated_at

        id          staged_commits.group_id
        meta        staged_commits.new_epoch
        protected   staged_commits.group_state
        meta        staged_commits.tip_priority
        id          staged_commits.tip_commit
        protected   staged_commits.tip_committer
        meta        staged_commits.created_at

        meta        schema_version.version
        """;

    /// <summary>
    /// Every byte-carrying member of every record the storage surface exchanges,
    /// and its disposition. Same vocabulary as <see cref="Census"/>; format is
    /// <c>disposition Type.Member</c>.
    /// </summary>
    private const string MemberCensus = """
        id          GroupRecord.Id
        protected   GroupRecord.LiveState

        id          MessageRecord.Id
        id          MessageRecord.GroupId
        protected   MessageRecord.Wire

        id          QueuedOutboundIntent.Id
        id          QueuedOutboundIntent.GroupId
        protected   QueuedOutboundIntent.Payload

        id          LeaveRequest.GroupId

        id          WelcomeRecord.Id
        id          WelcomeRecord.GroupId
        protected   WelcomeRecord.Wire

        public      KeyPackageRecord.PublicKeyPackage
        protected   KeyPackageRecord.PrivateMaterial

        id          RoutingIndexRecord.TransportGroupId
        id          RoutingIndexRecord.GroupId

        id          EpochCheckpoint.GroupId
        protected   EpochCheckpoint.GroupState

        id          CommitTip.Commit
        protected   CommitTip.Committer

        id          EpochStateRecord.GroupId
        id          EpochStateRecord.StagedCommit

        id          CommitPublishAttempt.GroupId
        id          CommitPublishAttempt.CommitId

        id          StagedCommitRecord.GroupId
        protected   StagedCommitRecord.GroupState
        """;

    // -- Known plaintexts. Distinctive, long, and never a substring of one
    //    another, so a sweep hit names exactly one field.

    private const string LiveStateNeedle = "SCRAMBLE-NEEDLE-groups-live-state-ratchet-and-leaf-keys";
    private const string MessageWireNeedle = "SCRAMBLE-NEEDLE-messages-wire-application-plaintext";
    private const string WelcomeWireNeedle = "SCRAMBLE-NEEDLE-welcomes-wire-group-secrets";
    private const string IntentPayloadNeedle = "SCRAMBLE-NEEDLE-outbound-intent-payload-unsent-message";
    private const string PrivateMaterialNeedle = "SCRAMBLE-NEEDLE-key-package-private-init-key";
    private const string PublicBytesNeedle = "SCRAMBLE-NEEDLE-key-package-published-public-bytes";
    private const string ArchiveStateNeedle = "SCRAMBLE-NEEDLE-epoch-archive-exported-group-state";
    private const string StagedStateNeedle = "SCRAMBLE-NEEDLE-staged-commit-post-commit-group-state";
    private const string ArchiveCommitterNeedle = "SCRAMBLE-NEEDLE-epoch-archive-committer-account-key";
    private const string StagedCommitterNeedle = "SCRAMBLE-NEEDLE-staged-commit-committer-account-key";

    private static readonly (string Field, string Needle)[] SecretNeedles =
    {
        ("groups.live_state", LiveStateNeedle),
        ("messages.wire", MessageWireNeedle),
        ("welcomes.wire", WelcomeWireNeedle),
        ("outbound_intents.payload", IntentPayloadNeedle),
        ("key_packages.private_material", PrivateMaterialNeedle),
        ("epoch_archive.group_state", ArchiveStateNeedle),
        ("staged_commits.group_state", StagedStateNeedle),
        ("epoch_archive.tip_committer", ArchiveCommitterNeedle),
        ("staged_commits.tip_committer", StagedCommitterNeedle),
    };

    private readonly List<string> _paths = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _paths)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); }
                catch { /* a leaked handle is not this suite's failure to report */ }
            }
        }
    }

    // ── The load-bearing test ──────────────────────────────────────────────

    /// <summary>
    /// Populate every sensitive column through the decorator, close the store,
    /// then read the file as bytes and look for the plaintexts anywhere in it.
    /// </summary>
    /// <remarks>
    /// The sweep is over every column of every table, not over the columns the
    /// needles were written to. That is deliberate: it is what turns "I checked
    /// the fields I know about" into "the secret is not in this file", and it is
    /// the only form that notices a value copied somewhere nobody modelled. Each
    /// needle is searched for raw and Base64-encoded, because
    /// <c>snapshots.data</c> is JSON and would carry a leak in Base64 rather
    /// than verbatim.
    /// </remarks>
    [Fact]
    public async Task TheRawDatabaseFileContainsNoPlaintext()
    {
        string path = await PopulateEverySensitiveColumnAsync();

        var columns = ReadEveryColumnValue(path);

        var leaks = new List<string>();
        foreach (var (field, needle) in SecretNeedles)
        {
            byte[] raw = Encoding.UTF8.GetBytes(needle);
            byte[] b64 = Encoding.UTF8.GetBytes(Convert.ToBase64String(raw));

            foreach (var (column, value) in columns)
            {
                if (Contains(value, raw))
                    leaks.Add($"{field}'s plaintext is readable in {column}");
                else if (Contains(value, b64))
                    leaks.Add($"{field}'s plaintext is readable in {column} (Base64)");
            }
        }

        Assert.True(
            leaks.Count == 0,
            "The engine's database holds secrets in the clear:\n  " + string.Join("\n  ", leaks));
    }

    /// <summary>
    /// Positive proof, so that "no plaintext found" cannot be satisfied by a
    /// column that was simply never written.
    /// </summary>
    [Fact]
    public async Task ProtectedColumnsCarryTheEncryptionPrefixOnDisk()
    {
        string path = await PopulateEverySensitiveColumnAsync();

        var census = ParseCensus(Census);
        var columns = ReadEveryColumnValue(path);
        var byColumn = columns.GroupBy(c => c.Column, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var wrong = new List<string>();
        foreach (var (column, disposition) in census)
        {
            if (disposition != "protected")
                continue;

            Assert.True(
                byColumn.TryGetValue(column, out var values) && values.Count > 0,
                $"{column} is classified 'protected' but this suite never wrote a row to it, " +
                "so nothing about it is being checked. Populate it in " +
                nameof(PopulateEverySensitiveColumnAsync) + ".");

            foreach (var (_, value) in values!)
            {
                if (!StartsWith(value, MagicPrefix))
                    wrong.Add(column);
            }
        }

        Assert.True(
            wrong.Count == 0,
            "Classified 'protected' but stored without the ISecureStorage prefix: "
                + string.Join(", ", wrong.Distinct()));
    }

    /// <summary>
    /// The other direction: a column the store looks values up by must not be
    /// encrypted, so an attempt to protect one fails here rather than as a
    /// mysteriously empty query.
    /// </summary>
    [Fact]
    public async Task IdentifierColumnsAreNotEncrypted()
    {
        string path = await PopulateEverySensitiveColumnAsync();

        var census = ParseCensus(Census);
        var wrong = new List<string>();

        foreach (var (column, value) in ReadEveryColumnValue(path))
        {
            if (census.TryGetValue(column, out var disposition)
                && disposition == "id"
                && StartsWith(value, MagicPrefix))
            {
                wrong.Add(column);
            }
        }

        Assert.True(
            wrong.Count == 0,
            "Encrypted, but used as a primary key, a WHERE term or a byte-equality "
                + "comparison — lookups against it will silently miss: "
                + string.Join(", ", wrong.Distinct()));
    }

    /// <summary>
    /// Everything written protected comes back as what went in. A decorator that
    /// protects but does not reveal passes every test above and breaks the app.
    /// </summary>
    [Fact]
    public async Task EverySensitiveValueRoundTripsThroughTheDecorator()
    {
        string path = NewDatabasePath();

        using (var store = OpenDecorated(path))
        {
            await PopulateAsync(store);
        }

        using (var store = OpenDecorated(path))
        {
            var group = await store.GetGroupAsync(Group1);
            Assert.Equal(LiveStateNeedle, Utf8(group!.LiveState!));

            var message = await store.GetMessageAsync(Message1);
            Assert.Equal(MessageWireNeedle, Utf8(message!.Wire));

            var listed = await store.ListMessagesAsync(Group1);
            Assert.Equal(MessageWireNeedle, Utf8(Assert.Single(listed).Wire));

            var welcome = await store.GetWelcomeAsync(Welcome1);
            Assert.Equal(WelcomeWireNeedle, Utf8(welcome!.Wire));

            var welcomes = await store.ListWelcomesAsync();
            Assert.Equal(WelcomeWireNeedle, Utf8(Assert.Single(welcomes).Wire));

            var intents = await store.ListIntentsAsync(Group1);
            Assert.Equal(IntentPayloadNeedle, Utf8(Assert.Single(intents).Payload));

            var kp = await store.GetKeyPackageAsync(KeyPackageRef);
            Assert.Equal(PrivateMaterialNeedle, Utf8(kp!.PrivateMaterial!));
            Assert.Equal(PublicBytesNeedle, Utf8(kp.PublicKeyPackage));

            var byEvent = await store.GetKeyPackageByEventAsync(KeyPackageEventId);
            Assert.Equal(PrivateMaterialNeedle, Utf8(byEvent!.PrivateMaterial!));

            var kps = await store.ListKeyPackagesAsync();
            Assert.Equal(PrivateMaterialNeedle, Utf8(Assert.Single(kps).PrivateMaterial!));

            var checkpoint = await store.GetEpochCheckpointAsync(Group1, new EpochId(4));
            Assert.Equal(ArchiveStateNeedle, Utf8(checkpoint!.GroupState));
            Assert.Equal(ArchiveCommitterNeedle, Utf8(checkpoint.Tip!.Committer));

            var checkpoints = await store.ListEpochCheckpointsAsync(Group1, new EpochId(0));
            Assert.Equal(ArchiveStateNeedle, Utf8(Assert.Single(checkpoints).GroupState));

            var staged = await store.GetStagedCommitAsync(Group1);
            Assert.Equal(StagedStateNeedle, Utf8(staged!.GroupState));
            Assert.Equal(StagedCommitterNeedle, Utf8(staged.Tip.Committer));

            var allStaged = await store.ListStagedCommitsAsync();
            Assert.Equal(StagedStateNeedle, Utf8(Assert.Single(allStaged).GroupState));

            // Identifiers survive as themselves, which is the whole reason they
            // are exempt: the engine looks rows up by them.
            Assert.Equal(CommitId.Value, staged.Tip.Commit.Value);
            var epochState = await store.GetEpochStateAsync(Group1);
            Assert.Equal(CommitId.Value, epochState!.StagedCommit!.Value.Value);

            // Nothing in a freshly protected database should read as legacy.
            Assert.Empty(store.FieldsFoundUnprotected);
        }
    }

    // ── The census, which is what catches the field nobody thought about ───

    [Fact]
    public void EveryColumnInTheSchemaHasARecordedDisposition()
    {
        string path = NewDatabasePath();
        using (new SqliteMarmotStorageProvider($"Data Source={path}", Prefix)) { }

        var census = ParseCensus(Census);
        var schema = ReadSchema(path);

        var unclassified = schema.Except(census.Keys, StringComparer.Ordinal).ToList();
        var stale = census.Keys.Except(schema, StringComparer.Ordinal).ToList();

        Assert.True(
            unclassified.Count == 0,
            "These columns exist in the engine's schema and nobody has said what they hold. "
                + "Add a line to SecureMarmotStorageProviderTests.Census classifying each — "
                + "'protected' if it can carry key material, message content or anything a "
                + "relay does not already have; 'id', 'public' or 'meta' with a reason if not. "
                + "Then make the decorator protect the ones you classified 'protected'.\n  "
                + string.Join("\n  ", unclassified));

        Assert.True(
            stale.Count == 0,
            "Classified in Census but no longer in the schema — delete the lines so the census "
                + "keeps describing the real store:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void EveryByteCarryingRecordMemberHasARecordedDisposition()
    {
        var census = ParseCensus(MemberCensus);
        var found = new List<string>();

        foreach (var type in RecordTypesOnTheStorageSurface())
        {
            foreach (var property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                    continue;

                if (CarriesBytes(Unwrap(property.PropertyType)))
                    found.Add($"{type.Name}.{property.Name}");
            }
        }

        var unclassified = found.Except(census.Keys, StringComparer.Ordinal).ToList();
        var stale = census.Keys.Except(found, StringComparer.Ordinal).ToList();

        Assert.True(
            unclassified.Count == 0,
            "These record members carry bytes and nobody has said what the bytes are. Add a "
                + "line to SecureMarmotStorageProviderTests.MemberCensus, and if the answer is "
                + "'protected', make SecureMarmotStorageProvider protect it and populate it in "
                + nameof(PopulateEverySensitiveColumnAsync) + ":\n  "
                + string.Join("\n  ", unclassified));

        Assert.True(
            stale.Count == 0,
            "Classified in MemberCensus but no longer on the storage surface:\n  "
                + string.Join("\n  ", stale));
    }

    /// <summary>
    /// The two censuses classify the same secrets, allowing for the fact that one
    /// member can land in more than one column.
    /// </summary>
    /// <remarks>
    /// Without this they can drift into disagreeing and a reader has no way to
    /// tell which of them describes the store. The mapping is stated rather than
    /// derived because there is nothing in the schema that says which member a
    /// column came from; <c>snapshots.data</c> is absent from it because it is
    /// <c>transitive</c> precisely by having no member of its own.
    /// </remarks>
    [Fact]
    public void TheTwoCensusesAgreeOnWhatIsSecret()
    {
        var columnToMember = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["groups.live_state"] = "GroupRecord.LiveState",
            ["messages.wire"] = "MessageRecord.Wire",
            ["outbound_intents.payload"] = "QueuedOutboundIntent.Payload",
            ["welcomes.wire"] = "WelcomeRecord.Wire",
            ["key_packages.private_material"] = "KeyPackageRecord.PrivateMaterial",
            ["epoch_archive.group_state"] = "EpochCheckpoint.GroupState",
            ["epoch_archive.tip_committer"] = "CommitTip.Committer",
            ["staged_commits.group_state"] = "StagedCommitRecord.GroupState",
            ["staged_commits.tip_committer"] = "CommitTip.Committer",
        };

        var columns = ParseCensus(Census);
        var members = ParseCensus(MemberCensus);

        Assert.Equal(
            columns.Where(c => c.Value == "protected").Select(c => c.Key).OrderBy(k => k, StringComparer.Ordinal),
            columnToMember.Keys.OrderBy(k => k, StringComparer.Ordinal));

        Assert.Equal(
            members.Where(m => m.Value == "protected").Select(m => m.Key).OrderBy(k => k, StringComparer.Ordinal),
            columnToMember.Values.Distinct().OrderBy(k => k, StringComparer.Ordinal));
    }

    // ── Legacy databases ───────────────────────────────────────────────────

    /// <summary>
    /// A developer's existing profile holds rows written before any of this
    /// existed. Reading one must neither corrupt the value nor pass silently.
    /// </summary>
    /// <remarks>
    /// There is no migration and none is wanted — pre-cutover MLS state is
    /// abandoned by decision — so the requirement is legibility, not repair.
    /// <see cref="ISecureStorage.Unprotect"/> returns a prefix-less value
    /// unchanged, which makes the value correct and the situation invisible; the
    /// decorator's job is to notice, which is what
    /// <see cref="SecureMarmotStorageProvider.FieldsFoundUnprotected"/> reports.
    /// </remarks>
    [Fact]
    public async Task AnUnprotectedLegacyRowIsReadableAndReported()
    {
        string path = NewDatabasePath();

        // Written straight to the provider, exactly as the engine did between the
        // cutover and this change.
        using (var bare = new SqliteMarmotStorageProvider($"Data Source={path}", Prefix))
        {
            await bare.PutGroupAsync(GroupWithLiveState());
            await bare.PutMessageAsync(MessageWithWire());
        }

        using (var store = OpenDecorated(path))
        {
            var group = await store.GetGroupAsync(Group1);
            var message = await store.GetMessageAsync(Message1);

            // Legible: correct value, and an explicit record that it was in the clear.
            Assert.Equal(LiveStateNeedle, Utf8(group!.LiveState!));
            Assert.Equal(MessageWireNeedle, Utf8(message!.Wire));

            var reported = store.FieldsFoundUnprotected;
            Assert.Equal(
                new[] { "GroupRecord.LiveState", "MessageRecord.Wire" },
                reported.Keys.OrderBy(k => k, StringComparer.Ordinal));

            // And the store heals forward: rewriting the row protects it.
            await store.PutGroupAsync(group);
        }

        var live = ReadEveryColumnValue(path)
            .Where(c => c.Column == "groups.live_state")
            .Select(c => c.Value);
        Assert.All(live, v => Assert.True(StartsWith(v, MagicPrefix)));
    }

    /// <summary>
    /// The factory refuses to build an unprotected store rather than quietly
    /// building one. <c>IStorageService.SecureStorage</c> is nullable, and under
    /// the legacy engine a null there cost the MLS store nothing.
    /// </summary>
    [Fact]
    public void TheFactoryRefusesToBuildAStoreWithNoProtection()
    {
        var storage = new StorageService(NewDatabasePath(), secureStorage: null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => DarkMatterMlsServiceFactory.Create(storage));

        Assert.Contains("ISecureStorage", ex.Message, StringComparison.Ordinal);
    }

    // ── Fixture ────────────────────────────────────────────────────────────

    private static readonly GroupId Group1 = new(Bytes(0x11, 32));
    private static readonly MessageId Message1 = new(Bytes(0x22, 32));
    private static readonly MessageId Welcome1 = new(Bytes(0x33, 32));
    private static readonly MessageId Intent1 = new(Bytes(0x44, 32));
    private static readonly MessageId CommitId = new(Bytes(0x55, 32));
    private const string KeyPackageRef = "aabbccdd";
    private const string KeyPackageEventId = "eeff0011";

    private static byte[] Bytes(byte fill, int length)
    {
        var b = new byte[length];
        b.AsSpan().Fill(fill);
        return b;
    }

    private static byte[] Utf8Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);

    private static GroupRecord GroupWithLiveState() =>
        new(Group1, new EpochId(4), ProtocolProfile.Current,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        {
            JoinEpoch = new EpochId(1),
            LiveState = Utf8Bytes(LiveStateNeedle),
        };

    private static MessageRecord MessageWithWire() =>
        new(Message1, Group1, "transport-1", new EpochId(4), MessageRecordState.Processed,
            Utf8Bytes(MessageWireNeedle), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private string NewDatabasePath()
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"scramble-secure-store-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        return path;
    }

    private static SecureMarmotStorageProvider OpenDecorated(string path) =>
        new(new SqliteMarmotStorageProvider($"Data Source={path}", Prefix),
            new MockSecureStorage());

    /// <summary>
    /// A store with every column the census classifies <c>protected</c>,
    /// <c>transitive</c> or <c>public</c> holding a known plaintext, plus a row
    /// in every other engine table so the sweep has something to sweep.
    /// </summary>
    private async Task<string> PopulateEverySensitiveColumnAsync()
    {
        string path = NewDatabasePath();
        using var store = OpenDecorated(path);
        await PopulateAsync(store);
        return path;
    }

    private static async Task PopulateAsync(IMarmotStorageProvider store)
    {
        await store.PutGroupAsync(GroupWithLiveState());
        await store.PutMessageAsync(MessageWithWire());
        await store.PutTransportSeenAsync("transport-1");

        await store.PutIntentAsync(new QueuedOutboundIntent(
            Intent1, Group1, "message", Utf8Bytes(IntentPayloadNeedle),
            DateTimeOffset.UnixEpoch));

        await store.PutLeaveRequestAsync(
            new LeaveRequest(Group1, new EpochId(4), DateTimeOffset.UnixEpoch));

        await store.PutWelcomeAsync(new WelcomeRecord(
            Welcome1, Utf8Bytes(WelcomeWireNeedle), WelcomeRecordState.Pending,
            DateTimeOffset.UnixEpoch));

        await store.PutKeyPackageAsync(new KeyPackageRecord(
            KeyPackageRef, "slot-0", Utf8Bytes(PublicBytesNeedle),
            Utf8Bytes(PrivateMaterialNeedle), LastResort: false,
            NotBefore: 0, NotAfter: 1, KeyPackageRecordState.Created,
            DateTimeOffset.UnixEpoch));
        await store.MarkPublishedAsync(KeyPackageRef, KeyPackageEventId);

        await store.PutRoutingAsync(Bytes(0x66, 32), Group1, new EpochId(4));

        await store.PutEpochCheckpointAsync(new EpochCheckpoint(
            Group1, new EpochId(4), Utf8Bytes(ArchiveStateNeedle),
            new CommitTip(
                CommitOrderingPriority.Privileged, CommitId,
                Utf8Bytes(ArchiveCommitterNeedle)),
            DateTimeOffset.UnixEpoch));

        await store.PutStagedCommitAsync(new StagedCommitRecord(
            Group1, new EpochId(5), Utf8Bytes(StagedStateNeedle),
            new CommitTip(
                CommitOrderingPriority.Ordinary, CommitId,
                Utf8Bytes(StagedCommitterNeedle)),
            DateTimeOffset.UnixEpoch));

        await store.PutEpochStateAsync(EpochStateRecord.Pending(
            Group1, new EpochId(5), new EpochId(4),
            new StagedCommitHandle(CommitId.Value), new PendingStateRef(7),
            PendingKind.GroupEvolution, DateTimeOffset.UnixEpoch));

        await store.PutCommitPublishAttemptAsync(CommitPublishAttempt.HandedToTransport(
            Group1, CommitId, new EpochId(5), DateTimeOffset.UnixEpoch));

        // Last, so the JSON document it builds carries the rows above.
        await store.CreateSnapshotAsync(Group1, new EpochId(4));
    }

    // ── Reading the file, not the abstraction ──────────────────────────────

    /// <summary>
    /// Every value in the database, as bytes, keyed <c>table.column</c> with the
    /// prefix stripped. TEXT and INTEGER are included as their UTF-8 form: a
    /// secret leaked into a TEXT column is still a leaked secret.
    /// </summary>
    private static List<(string Column, byte[] Value)> ReadEveryColumnValue(string path)
    {
        var values = new List<(string, byte[])>();

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        foreach (string table in Tables(connection))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{table}\";";
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.IsDBNull(i))
                        continue;

                    string column = $"{Unprefixed(table)}.{reader.GetName(i)}";
                    object raw = reader.GetValue(i);
                    values.Add((
                        column,
                        raw is byte[] bytes
                            ? bytes
                            : Encoding.UTF8.GetBytes(
                                Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)
                                ?? string.Empty)));
                }
            }
        }

        return values;
    }

    private static List<string> ReadSchema(string path)
    {
        var columns = new List<string>();

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        foreach (string table in Tables(connection))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                columns.Add($"{Unprefixed(table)}.{reader.GetString(1)}");
        }

        return columns;
    }

    private static List<string> Tables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE @p ORDER BY name;";
        cmd.Parameters.AddWithValue("@p", Prefix + "%");

        var tables = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static string Unprefixed(string table) =>
        table.StartsWith(Prefix, StringComparison.Ordinal) ? table[Prefix.Length..] : table;

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle.AsSpan()) >= 0;

    private static bool StartsWith(byte[] value, byte[] prefix) =>
        value.Length >= prefix.Length && value.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static Dictionary<string, string> ParseCensus(string census)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string line in census.Split('\n'))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                continue;

            Assert.Equal(2, parts.Length);
            Assert.Contains(parts[0], new[] { "protected", "transitive", "id", "public", "meta" });
            parsed.Add(parts[1], parts[0]);
        }

        return parsed;
    }

    // ── Reflection over the storage surface ───────────────────────────────

    /// <summary>
    /// Every record type <see cref="IMarmotStorageProvider"/> exchanges, found by
    /// walking its signatures and then those types' own members.
    /// </summary>
    /// <remarks>
    /// Signature-derived rather than listed, so a new sub-interface carrying a
    /// new record is censused with no edit here.
    /// </remarks>
    private static IEnumerable<Type> RecordTypesOnTheStorageSurface()
    {
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>();

        var surface = typeof(IMarmotStorageProvider).GetInterfaces()
            .Append(typeof(IMarmotStorageProvider));

        foreach (var iface in surface)
        {
            foreach (var method in iface.GetMethods())
            {
                pending.Enqueue(method.ReturnType);
                foreach (var parameter in method.GetParameters())
                    pending.Enqueue(parameter.ParameterType);
            }
        }

        while (pending.Count > 0)
        {
            var type = Unwrap(pending.Dequeue());

            if (!IsMarmotRecord(type) || !seen.Add(type))
                continue;

            foreach (var property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length == 0)
                    pending.Enqueue(property.PropertyType);
            }
        }

        return seen.OrderBy(t => t.Name, StringComparer.Ordinal);
    }

    /// <summary>Peels Task, Nullable and single-element collections.</summary>
    private static Type Unwrap(Type type)
    {
        while (true)
        {
            if (type.IsArray && type != typeof(byte[]))
            {
                type = type.GetElementType()!;
                continue;
            }

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                if (definition == typeof(Task<>)
                    || definition == typeof(Nullable<>)
                    || definition == typeof(IReadOnlyList<>)
                    || definition == typeof(IEnumerable<>)
                    || definition == typeof(List<>))
                {
                    type = type.GetGenericArguments()[0];
                    continue;
                }
            }

            return type;
        }
    }

    /// <summary>
    /// A record of the engine's own, as opposed to a primitive, an enum, a BCL
    /// type or an interface — and not a byte wrapper, which is terminal.
    /// </summary>
    private static bool IsMarmotRecord(Type type) =>
        !type.IsPrimitive
        && !type.IsEnum
        && !type.IsInterface
        && type != typeof(string)
        && !CarriesBytes(type)
        && (type.Assembly.GetName().Name?.StartsWith("Scramble.Marmot", StringComparison.Ordinal)
            ?? false);

    /// <summary>
    /// Whether a value of this type is a byte payload: a <c>byte[]</c>, or one of
    /// the id structs that wrap one.
    /// </summary>
    private static bool CarriesBytes(Type type)
    {
        if (type == typeof(byte[]))
            return true;

        return type.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)
            ?.PropertyType == typeof(byte[]);
    }
}
