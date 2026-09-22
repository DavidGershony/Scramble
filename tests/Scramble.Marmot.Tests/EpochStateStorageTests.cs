using Microsoft.Data.Sqlite;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Storage.Sqlite;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The durable half of the epoch state machine.
/// </summary>
/// <remarks>
/// What matters here is that a row says enough to reconcile a commit nobody
/// knows the fate of: the epoch confirmation would reach, the staged commit
/// itself, the epoch to fall back to, and the reference the caller will name it
/// by. A row missing any of those reads as a reconcilable publish with the rest
/// invented, which is worse than no row at all.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class EpochStateStorageTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"marmot-epoch-state-{Guid.NewGuid():N}.db");

    private readonly SqliteMarmotStorageProvider _provider;

    public EpochStateStorageTests()
    {
        // Its own database rather than the shared fixture's, because several of
        // these tests write rows the provider deliberately cannot build, which
        // needs a second connection to the same file.
        _provider = new SqliteMarmotStorageProvider($"Data Source={_path}");
    }

    private IEpochStateStorage Storage => _provider;

    private static GroupId NewGroup() => new(Guid.NewGuid().ToByteArray());

    private static StagedCommitHandle Staged(byte fill = 0x5a) =>
        new(Enumerable.Repeat(fill, 48).ToArray());

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A stray temp file is not worth failing a test run over.
        }
    }

    /// <summary>Opens a second connection, to write what the provider will not.</summary>
    private SqliteConnection RawConnection()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        return connection;
    }

    private static int ExecuteRaw(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task APendingPublishComesBackWithEverythingNeededToReconcileIt()
    {
        var group = NewGroup();

        await Storage.PutEpochStateAsync(EpochStateRecord.Pending(
            group,
            newEpoch: new EpochId(9),
            priorEpoch: new EpochId(8),
            Staged(0xc3),
            new PendingStateRef(4),
            PendingKind.Disband,
            DateTimeOffset.UnixEpoch.AddSeconds(120)));

        EpochStateRecord? read = await Storage.GetEpochStateAsync(group);

        Assert.NotNull(read);
        Assert.Equal(EpochStateKind.PendingPublish, read.Kind);
        Assert.Equal(new EpochId(9), read.Epoch);
        Assert.Equal(new EpochId(8), read.PriorEpoch);
        Assert.Equal(Staged(0xc3), read.StagedCommit);
        Assert.Equal(new PendingStateRef(4), read.Reference);

        // The operation, not merely that something was pending: a disband that
        // comes back as a group evolution would be retried as one.
        Assert.Equal(PendingKind.Disband, read.PendingOperation);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(120), read.UpdatedAt);
    }

    [Fact]
    public async Task AFrozenGroupStoresOnlyTheEpochItCanStillAssert()
    {
        var group = NewGroup();

        await Storage.PutEpochStateAsync(
            EpochStateRecord.Unrecoverable(group, new EpochId(12), DateTimeOffset.UnixEpoch));

        EpochStateRecord? read = await Storage.GetEpochStateAsync(group);

        Assert.NotNull(read);
        Assert.Equal(EpochStateKind.Unrecoverable, read.Kind);
        Assert.Equal(new EpochId(12), read.Epoch);
        Assert.Null(read.PriorEpoch);
        Assert.Null(read.StagedCommit);
        Assert.Null(read.Reference);
        Assert.Null(read.PendingOperation);
    }

    [Fact]
    public async Task ADisbandedGroupIsStoredAtItsFinalEpoch()
    {
        var group = NewGroup();

        await Storage.PutEpochStateAsync(
            EpochStateRecord.Disbanded(group, new EpochId(3), DateTimeOffset.UnixEpoch));

        EpochStateRecord? read = await Storage.GetEpochStateAsync(group);

        Assert.NotNull(read);
        Assert.Equal(EpochStateKind.Disbanded, read.Kind);
        Assert.Equal(new EpochId(3), read.Epoch);
    }

    [Fact]
    public async Task AGroupHasOneStateAndTheLatestWriteIsIt()
    {
        // A pending publish abandoned into a frozen group. Two rows would make
        // which one describes the group a coin flip at session open, which is
        // the one moment nobody is there to arbitrate.
        var group = NewGroup();

        await Storage.PutEpochStateAsync(EpochStateRecord.Pending(
            group, new EpochId(2), new EpochId(1), Staged(), new PendingStateRef(1),
            PendingKind.GroupEvolution, DateTimeOffset.UnixEpoch));

        await Storage.PutEpochStateAsync(
            EpochStateRecord.Unrecoverable(group, new EpochId(1), DateTimeOffset.UnixEpoch));

        EpochStateRecord? read = await Storage.GetEpochStateAsync(group);

        Assert.Equal(EpochStateKind.Unrecoverable, read!.Kind);
        Assert.Null(read.StagedCommit);
        Assert.Single(await Storage.ListEpochStatesAsync());
    }

    [Fact]
    public async Task AGroupWithNoStoredStateReadsAsAbsent()
    {
        Assert.Null(await Storage.GetEpochStateAsync(NewGroup()));
        Assert.Empty(await Storage.ListEpochStatesAsync());
    }

    [Fact]
    public async Task ClearingSaysWhetherThereWasAnythingToClear()
    {
        var group = NewGroup();
        await Storage.PutEpochStateAsync(
            EpochStateRecord.Disbanded(group, new EpochId(1), DateTimeOffset.UnixEpoch));

        Assert.True(await Storage.ClearEpochStateAsync(group));
        Assert.Null(await Storage.GetEpochStateAsync(group));
        Assert.False(await Storage.ClearEpochStateAsync(group));
    }

    [Fact]
    public async Task EveryStoredStateIsListedForSessionOpen()
    {
        var pending = NewGroup();
        var frozen = NewGroup();
        var gone = NewGroup();

        await Storage.PutEpochStateAsync(EpochStateRecord.Pending(
            pending, new EpochId(2), new EpochId(1), Staged(), new PendingStateRef(1),
            PendingKind.CreateGroup, DateTimeOffset.UnixEpoch));
        await Storage.PutEpochStateAsync(EpochStateRecord.Unrecoverable(
            frozen, new EpochId(4), DateTimeOffset.UnixEpoch.AddSeconds(1)));
        await Storage.PutEpochStateAsync(EpochStateRecord.Disbanded(
            gone, new EpochId(6), DateTimeOffset.UnixEpoch.AddSeconds(2)));

        IReadOnlyList<EpochStateRecord> all = await Storage.ListEpochStatesAsync();

        Assert.Equal(3, all.Count);
        Assert.Equal(
            new[] { EpochStateKind.PendingPublish, EpochStateKind.Unrecoverable, EpochStateKind.Disbanded },
            all.Select(r => r.Kind));
    }

    [Fact]
    public async Task APendingReferenceSurvivesTheWholeRangeOfItsType()
    {
        // The reference is a ulong and the column is a signed integer, so the
        // top half of the range round-trips only if both casts are there. A
        // caller handed a reference the engine cannot recognise is a publish
        // that can be neither confirmed nor discarded.
        var group = NewGroup();

        await Storage.PutEpochStateAsync(EpochStateRecord.Pending(
            group, new EpochId(ulong.MaxValue), new EpochId(ulong.MaxValue - 1), Staged(),
            new PendingStateRef(ulong.MaxValue), PendingKind.GroupEvolution,
            DateTimeOffset.UnixEpoch));

        EpochStateRecord? read = await Storage.GetEpochStateAsync(group);

        Assert.Equal(new PendingStateRef(ulong.MaxValue), read!.Reference);
        Assert.Equal(new EpochId(ulong.MaxValue), read.Epoch);
        Assert.Equal(new EpochId(ulong.MaxValue - 1), read.PriorEpoch);
    }

    [Fact]
    public void APendingPublishWithoutItsStagedCommitCannotBeBuilt()
    {
        // The four pending fields stand or fall together. This is the half the
        // type can refuse; the schema refuses the rest.
        var ex = Assert.Throws<ArgumentException>(() => EpochStateRecord.Pending(
            NewGroup(), new EpochId(2), new EpochId(1), default, new PendingStateRef(1),
            PendingKind.GroupEvolution, DateTimeOffset.UnixEpoch));

        Assert.Contains("staged commit", ex.Message);
    }

    [Fact]
    public void AHalfWrittenPendingRowIsRefusedBySchema()
    {
        // Not reachable through the provider -- EpochStateRecord will not build
        // one -- so it is written the only way it could ever appear: by another
        // writer on the same file.
        using SqliteConnection raw = RawConnection();

        SqliteException ex = Assert.Throws<SqliteException>(() => ExecuteRaw(raw, @"
            INSERT INTO marmot_epoch_states
                (group_id, kind, epoch, prior_epoch, staged_commit, pending_ref,
                 pending_kind, updated_at)
            VALUES (x'01', 1, 5, 4, NULL, 1, 1, '2026-01-01T00:00:00.0000000+00:00');"));

        Assert.Contains("CHECK constraint failed", ex.Message);
    }

    [Fact]
    public void ASettledRowMayNotCarryAStagedCommit()
    {
        // The other direction of the same rule. A frozen group holding a staged
        // commit would be restored as frozen while a commit sat in its row
        // describing a publish nothing will ever reconcile.
        using SqliteConnection raw = RawConnection();

        SqliteException ex = Assert.Throws<SqliteException>(() => ExecuteRaw(raw, @"
            INSERT INTO marmot_epoch_states
                (group_id, kind, epoch, prior_epoch, staged_commit, pending_ref,
                 pending_kind, updated_at)
            VALUES (x'02', 2, 5, NULL, x'aabb', NULL, NULL, '2026-01-01T00:00:00.0000000+00:00');"));

        Assert.Contains("CHECK constraint failed", ex.Message);
    }

    [Fact]
    public async Task AKindThisBuildDoesNotKnowIsRefusedRatherThanReadAsAbsent()
    {
        // A newer build's row. Returning null would restore the group as
        // ordinary -- and every kind stored here exists to stop a group doing
        // something.
        var group = new GroupId(new byte[] { 0x03 });
        using (SqliteConnection raw = RawConnection())
        {
            ExecuteRaw(raw, @"
                INSERT INTO marmot_epoch_states (group_id, kind, epoch, updated_at)
                VALUES (x'03', 9, 5, '2026-01-01T00:00:00.0000000+00:00');");
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Storage.GetEpochStateAsync(group));

        Assert.Contains("Unknown epoch state kind 9", ex.Message);
    }
}
