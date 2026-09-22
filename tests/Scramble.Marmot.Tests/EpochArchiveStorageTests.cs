using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Storage;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The retained MLS state a competing branch is rebuilt from.
/// </summary>
/// <remarks>
/// An MLS group cannot rewind, so without these rows a commit that forks from
/// an epoch we have left is simply unprocessable — and unprocessable reads as
/// transport noise rather than as a fork. What matters here is the window: the
/// archive must retain exactly what the rewind horizon says is eligible, since
/// retaining less silently narrows the horizon for this member alone.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class EpochArchiveStorageTests : IDisposable
{
    private readonly StorageFixture _fixture = new();

    private IEpochArchiveStorage Archive => _fixture.Provider;

    public void Dispose() => _fixture.Dispose();

    private static CommitTip Tip(
        CommitOrderingPriority priority = CommitOrderingPriority.Ordinary,
        byte fill = 0x33) =>
        new(priority,
            new MessageId(Enumerable.Repeat(fill, 32).ToArray()),
            Enumerable.Repeat((byte)0x99, 32).ToArray());

    private static EpochCheckpoint Checkpoint(
        GroupId group,
        ulong epoch,
        byte fill = 0x11,
        CommitOrderingPriority priority = CommitOrderingPriority.Ordinary) =>
        new(group, new EpochId(epoch), Enumerable.Repeat(fill, 16).ToArray(),
            Tip(priority), DateTimeOffset.UnixEpoch.AddSeconds(epoch));

    [Fact]
    public async Task ACheckpointComesBackAsItWentIn()
    {
        var group = StorageFixture.NewGroupId();
        await Archive.PutEpochCheckpointAsync(
            Checkpoint(group, 7, 0xab, CommitOrderingPriority.Privileged));

        EpochCheckpoint? read = await Archive.GetEpochCheckpointAsync(group, new EpochId(7));

        Assert.NotNull(read);
        Assert.Equal(group.Value, read.GroupId.Value);
        Assert.Equal(new EpochId(7), read.Epoch);
        Assert.Equal(Enumerable.Repeat((byte)0xab, 16), read.GroupState);
        Assert.Equal(CommitOrderingPriority.Privileged, read.Tip!.Priority);
    }

    [Fact]
    public async Task AnEpochNoCommitOfOursProducedHasNoTip()
    {
        // A group's first epoch, or one joined through a Welcome. Nullable
        // because convergence cannot state its own branch's terms from such an
        // epoch, and refusing to decide is the honest answer -- an invented
        // committer would compete on terms no other member computed.
        var group = StorageFixture.NewGroupId();

        await Archive.PutEpochCheckpointAsync(
            new EpochCheckpoint(
                group, new EpochId(0), [1, 2, 3], null, DateTimeOffset.UnixEpoch));

        EpochCheckpoint? read = await Archive.GetEpochCheckpointAsync(group, new EpochId(0));

        Assert.NotNull(read);
        Assert.Null(read.Tip);
    }

    [Fact]
    public async Task TheWholeTipSurvivesTheRoundTripNotJustItsClass()
    {
        // Branch selection reads three things about a tip, in order: class,
        // committer, digest. Storage that kept one and dropped the others would
        // leave two thirds of the tie-break to be invented at read time.
        var group = StorageFixture.NewGroupId();
        CommitTip tip = Tip(CommitOrderingPriority.Privileged, 0xc4);

        await Archive.PutEpochCheckpointAsync(
            new EpochCheckpoint(
                group, new EpochId(2), [9], tip, DateTimeOffset.UnixEpoch));

        CommitTip read = (await Archive.GetEpochCheckpointAsync(group, new EpochId(2)))!.Tip!;

        Assert.Equal(tip.Priority, read.Priority);
        Assert.Equal(tip.Commit, read.Commit);
        Assert.Equal(tip.Committer, read.Committer);
        Assert.Equal(tip.BranchId, read.BranchId);
    }

    [Fact]
    public async Task TheTipPriorityIsNotFlattenedOnTheWayThrough()
    {
        // The whole reason the column exists. A commit's ordering class can only
        // be read before it is applied, so if storage loses it there is no
        // second chance to ask -- and a branch filed as Ordinary when it was
        // Privileged loses a tie-break the rest of the group wins.
        var group = StorageFixture.NewGroupId();

        await Archive.PutEpochCheckpointAsync(
            Checkpoint(group, 1, priority: CommitOrderingPriority.Ordinary));
        await Archive.PutEpochCheckpointAsync(
            Checkpoint(group, 2, priority: CommitOrderingPriority.Privileged));

        Assert.Equal(
            CommitOrderingPriority.Ordinary,
            (await Archive.GetEpochCheckpointAsync(group, new EpochId(1)))!.Tip!.Priority);

        Assert.Equal(
            CommitOrderingPriority.Privileged,
            (await Archive.GetEpochCheckpointAsync(group, new EpochId(2)))!.Tip!.Priority);
    }

    [Fact]
    public async Task ReArchivingAnEpochReplacesIt()
    {
        // The same epoch is legitimately reached twice: once on a branch that
        // loses, and again after a reorg onto the one that wins. The later state
        // is what a further branch forks from, so a stale row here would rebuild
        // history that was already abandoned.
        var group = StorageFixture.NewGroupId();

        await Archive.PutEpochCheckpointAsync(Checkpoint(group, 4, 0x01));
        await Archive.PutEpochCheckpointAsync(Checkpoint(group, 4, 0x02));

        EpochCheckpoint? read = await Archive.GetEpochCheckpointAsync(group, new EpochId(4));

        Assert.Equal(Enumerable.Repeat((byte)0x02, 16), read!.GroupState);
        Assert.Single(await Archive.ListEpochCheckpointsAsync(group, new EpochId(0)));
    }

    [Fact]
    public async Task AnUnarchivedEpochIsNullRatherThanAnError()
    {
        // Null is the answer convergence branches on: it means this branch
        // cannot be evaluated, which is a refusal to record and not a failure.
        var group = StorageFixture.NewGroupId();

        Assert.Null(await Archive.GetEpochCheckpointAsync(group, new EpochId(3)));
    }

    [Fact]
    public async Task TheWindowIsListedOldestFirstAndStartsWhereAsked()
    {
        var group = StorageFixture.NewGroupId();
        foreach (ulong epoch in new ulong[] { 5, 3, 1, 4, 2 })
            await Archive.PutEpochCheckpointAsync(Checkpoint(group, epoch));

        IReadOnlyList<EpochCheckpoint> window =
            await Archive.ListEpochCheckpointsAsync(group, new EpochId(3));

        Assert.Equal(
            new ulong[] { 3, 4, 5 },
            window.Select(c => c.Epoch.Value));
    }

    [Fact]
    public async Task OneGroupsHistoryIsNeverAnothers()
    {
        var mine = StorageFixture.NewGroupId();
        var theirs = StorageFixture.NewGroupId();

        await Archive.PutEpochCheckpointAsync(Checkpoint(mine, 1, 0xaa));
        await Archive.PutEpochCheckpointAsync(Checkpoint(theirs, 1, 0xbb));

        Assert.Equal(
            Enumerable.Repeat((byte)0xaa, 16),
            (await Archive.GetEpochCheckpointAsync(mine, new EpochId(1)))!.GroupState);

        Assert.Single(await Archive.ListEpochCheckpointsAsync(mine, new EpochId(0)));
    }

    [Fact]
    public async Task PruningDropsWhatIsBelowTheHorizonAndKeepsTheHorizonItself()
    {
        // Off by one here narrows the rewind horizon by an epoch for this member
        // alone: a branch the policy calls eligible becomes one only we cannot
        // evaluate, and a member that cannot evaluate a branch cannot agree
        // about it either.
        var group = StorageFixture.NewGroupId();
        for (ulong epoch = 1; epoch <= 6; epoch++)
            await Archive.PutEpochCheckpointAsync(Checkpoint(group, epoch));

        int dropped = await Archive.PruneEpochCheckpointsBeforeAsync(group, new EpochId(4));

        Assert.Equal(3, dropped);
        Assert.Equal(
            new ulong[] { 4, 5, 6 },
            (await Archive.ListEpochCheckpointsAsync(group, new EpochId(0)))
                .Select(c => c.Epoch.Value));
    }

    [Fact]
    public async Task PruningLeavesOtherGroupsAlone()
    {
        var mine = StorageFixture.NewGroupId();
        var theirs = StorageFixture.NewGroupId();

        await Archive.PutEpochCheckpointAsync(Checkpoint(mine, 1));
        await Archive.PutEpochCheckpointAsync(Checkpoint(theirs, 1));

        Assert.Equal(1, await Archive.PruneEpochCheckpointsBeforeAsync(mine, new EpochId(9)));
        Assert.Single(await Archive.ListEpochCheckpointsAsync(theirs, new EpochId(0)));
    }
}
