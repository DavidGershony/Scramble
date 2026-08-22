using Scramble.Marmot.Storage;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Transaction atomicity and epoch-anchored snapshot behaviour — the pieces
/// fork recovery and convergence replay are built on.
/// </summary>
[Trait("Category", "MarmotEngine")]
public class SnapshotAndTransactionTests
{
    [Fact]
    public async Task CommittedTransactionPersistsEveryWrite()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();

        await using (var tx = await fx.Provider.BeginTransactionAsync())
        {
            await fx.Provider.PutGroupAsync(StorageFixture.Group(group, epoch: 1));
            await fx.Provider.PutMessageAsync(
                StorageFixture.Message(group, StorageFixture.NewMessageId("m")));
            await tx.CommitAsync();
        }

        Assert.NotNull(await fx.Provider.GetGroupAsync(group));
        Assert.Single(await fx.Provider.ListMessagesAsync(group));
    }

    [Fact]
    public async Task RolledBackTransactionDiscardsEveryWrite()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();

        await using (var tx = await fx.Provider.BeginTransactionAsync())
        {
            await fx.Provider.PutGroupAsync(StorageFixture.Group(group));
            await fx.Provider.PutMessageAsync(
                StorageFixture.Message(group, StorageFixture.NewMessageId("m")));
            await tx.RollbackAsync();
        }

        Assert.Null(await fx.Provider.GetGroupAsync(group));
        Assert.Empty(await fx.Provider.ListMessagesAsync(group));
    }

    [Fact]
    public async Task AbandonedTransactionRollsBack()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();

        // The crash case: an engine operation throws partway through. Disposing
        // without committing must not leave the record set half-advanced.
        await using (await fx.Provider.BeginTransactionAsync())
        {
            await fx.Provider.PutGroupAsync(StorageFixture.Group(group));
        }

        Assert.Null(await fx.Provider.GetGroupAsync(group));
    }

    [Fact]
    public async Task NestedTransactionIsRejected()
    {
        using var fx = new StorageFixture();

        await using var tx = await fx.Provider.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fx.Provider.BeginTransactionAsync());
    }

    [Fact]
    public async Task SnapshotIsAnchoredToItsEpochAndFoundByIt()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();
        await fx.Provider.PutGroupAsync(StorageFixture.Group(group, epoch: 4));

        string name = await fx.Provider.CreateSnapshotAsync(group, new EpochId(4));

        Assert.Equal(name, await fx.Provider.GetSnapshotAsync(group, new EpochId(4)));
        Assert.Null(await fx.Provider.GetSnapshotAsync(group, new EpochId(5)));
    }

    [Fact]
    public async Task RollbackRestoresGroupEpochAndDropsLaterMessages()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();
        var before = StorageFixture.NewMessageId("before");

        await fx.Provider.PutGroupAsync(StorageFixture.Group(group, epoch: 4));
        await fx.Provider.PutMessageAsync(StorageFixture.Message(group, before, epoch: 4));

        string name = await fx.Provider.CreateSnapshotAsync(group, new EpochId(4));

        // Apply a commit that later loses a fork.
        await fx.Provider.PutGroupAsync(StorageFixture.Group(group, epoch: 5));
        await fx.Provider.PutMessageAsync(
            StorageFixture.Message(group, StorageFixture.NewMessageId("after"), epoch: 5));

        await fx.Provider.RollbackToSnapshotAsync(name);

        var restored = await fx.Provider.GetGroupAsync(group);
        Assert.Equal(new EpochId(4), restored!.Epoch);

        var messages = await fx.Provider.ListMessagesAsync(group);
        Assert.Single(messages);
        Assert.Equal(before, messages[0].Id);
    }

    [Fact]
    public async Task RollbackRestoresQueuedIntentsAndLeaveRequests()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();
        var intentId = StorageFixture.NewMessageId("intent");

        await fx.Provider.PutGroupAsync(StorageFixture.Group(group, epoch: 2));
        await fx.Provider.PutIntentAsync(new QueuedOutboundIntent(
            intentId, group, "AppMessage", new byte[] { 1 }, DateTimeOffset.UtcNow));
        await fx.Provider.PutLeaveRequestAsync(
            new LeaveRequest(group, new EpochId(2), DateTimeOffset.UtcNow));

        string name = await fx.Provider.CreateSnapshotAsync(group, new EpochId(2));

        await fx.Provider.ClearIntentsAsync(group);
        await fx.Provider.ClearLeaveRequestAsync(group);

        await fx.Provider.RollbackToSnapshotAsync(name);

        Assert.Single(await fx.Provider.ListIntentsAsync(group));
        Assert.NotNull(await fx.Provider.GetLeaveRequestAsync(group));
    }

    [Fact]
    public async Task RollbackToASnapshotTakenBeforeTheGroupExistedRemovesIt()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();

        string name = await fx.Provider.CreateSnapshotAsync(group, new EpochId(0));
        await fx.Provider.PutGroupAsync(StorageFixture.Group(group));

        await fx.Provider.RollbackToSnapshotAsync(name);

        Assert.Null(await fx.Provider.GetGroupAsync(group));
    }

    [Fact]
    public async Task ReSnapshottingAnEpochReplacesTheAnchor()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();
        await fx.Provider.PutGroupAsync(StorageFixture.Group(group, epoch: 3));

        await fx.Provider.CreateSnapshotAsync(group, new EpochId(3));
        await fx.Provider.PutMessageAsync(
            StorageFixture.Message(group, StorageFixture.NewMessageId("later"), epoch: 3));
        string second = await fx.Provider.CreateSnapshotAsync(group, new EpochId(3));

        Assert.Single(await fx.Provider.ListSnapshotsAsync(group));

        // The retried snapshot is authoritative, not the stale first one.
        await fx.Provider.RollbackToSnapshotAsync(second);
        Assert.Single(await fx.Provider.ListMessagesAsync(group));
    }

    [Fact]
    public async Task PruningDropsSnapshotsBelowTheRewindHorizon()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();
        await fx.Provider.PutGroupAsync(StorageFixture.Group(group));

        for (ulong epoch = 1; epoch <= 8; epoch++)
            await fx.Provider.CreateSnapshotAsync(group, new EpochId(epoch));

        // Horizon of 5 commits behind epoch 8.
        await fx.Provider.PruneSnapshotsBeforeAsync(group, new EpochId(3));

        var remaining = await fx.Provider.ListSnapshotsAsync(group);
        Assert.Equal(6, remaining.Count);
        Assert.Null(await fx.Provider.GetSnapshotAsync(group, new EpochId(2)));
        Assert.NotNull(await fx.Provider.GetSnapshotAsync(group, new EpochId(3)));
    }

    [Fact]
    public async Task ReleasingASnapshotIsIdempotent()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();
        await fx.Provider.PutGroupAsync(StorageFixture.Group(group));

        string name = await fx.Provider.CreateSnapshotAsync(group, new EpochId(1));
        await fx.Provider.ReleaseSnapshotAsync(name);
        await fx.Provider.ReleaseSnapshotAsync(name);

        Assert.Empty(await fx.Provider.ListSnapshotsAsync(group));
    }

    [Fact]
    public async Task RollbackToAMissingSnapshotFailsLoudly()
    {
        using var fx = new StorageFixture();

        // Fork recovery must fail closed rather than silently continue on a
        // state it cannot restore.
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => fx.Provider.RollbackToSnapshotAsync("epoch-deadbeef-9"));
    }

    [Fact]
    public async Task SnapshotsAreScopedToTheirGroup()
    {
        using var fx = new StorageFixture();
        var a = StorageFixture.NewGroupId();
        var b = StorageFixture.NewGroupId();

        await fx.Provider.PutGroupAsync(StorageFixture.Group(a, epoch: 1));
        await fx.Provider.PutGroupAsync(StorageFixture.Group(b, epoch: 1));
        string snapshotOfA = await fx.Provider.CreateSnapshotAsync(a, new EpochId(1));

        await fx.Provider.PutMessageAsync(
            StorageFixture.Message(b, StorageFixture.NewMessageId("b-msg")));

        await fx.Provider.RollbackToSnapshotAsync(snapshotOfA);

        // Rolling back one group must not disturb another.
        Assert.Single(await fx.Provider.ListMessagesAsync(b));
        Assert.NotNull(await fx.Provider.GetGroupAsync(b));
    }
}
