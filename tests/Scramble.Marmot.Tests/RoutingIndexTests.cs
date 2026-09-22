using Scramble.Marmot.Storage;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The rotation-aware routing index.
/// </summary>
/// <remarks>
/// This is the first lookup on the receive path — a kind-445 event arrives
/// carrying a routing id and nothing else identifying — so it decides which
/// group's keys are even tried. Two properties matter most: a prior address
/// stays resolvable after a rotation, and resolution is exact.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class RoutingIndexTests : IDisposable
{
    private readonly StorageFixture _fixture = new();

    private IRoutingIndexStorage Index => _fixture.Provider;

    private static byte[] RoutingId(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ARegisteredAddressResolvesToItsGroup()
    {
        var group = StorageFixture.NewGroupId();
        await Index.PutRoutingAsync(RoutingId(0x01), group, new EpochId(0));

        var resolved = await Index.ResolveAsync(RoutingId(0x01));

        Assert.NotNull(resolved);
        Assert.Equal(group.Value, resolved.GroupId.Value);
        Assert.True(resolved.IsCurrent);
    }

    [Fact]
    public async Task AnUnknownAddressResolvesToNull()
    {
        Assert.Null(await Index.ResolveAsync(RoutingId(0xff)));
    }

    [Fact]
    public async Task APriorAddressStillResolvesAfterARotation()
    {
        // The whole point of the index. A message from an epoch before the
        // rotation was published at the old address; refusing to resolve it
        // would silently drop history.
        var group = StorageFixture.NewGroupId();
        await Index.PutRoutingAsync(RoutingId(0x01), group, new EpochId(0));
        await Index.PutRoutingAsync(RoutingId(0x02), group, new EpochId(5));

        var old = await Index.ResolveAsync(RoutingId(0x01));

        Assert.NotNull(old);
        Assert.Equal(group.Value, old.GroupId.Value);
        Assert.False(old.IsCurrent);
        Assert.Equal(new EpochId(5), old.LastEpoch);
    }

    [Fact]
    public async Task ManyAddressesMapToOneGroup()
    {
        var group = StorageFixture.NewGroupId();
        await Index.PutRoutingAsync(RoutingId(0x01), group, new EpochId(0));
        await Index.PutRoutingAsync(RoutingId(0x02), group, new EpochId(5));
        await Index.PutRoutingAsync(RoutingId(0x03), group, new EpochId(9));

        var all = await Index.ListRoutingAsync(group);

        Assert.Equal(3, all.Count);
        Assert.Equal(RoutingId(0x03), all[0].TransportGroupId);
        Assert.True(all[0].IsCurrent);
    }

    [Fact]
    public async Task AGroupHasExactlyOneCurrentAddress()
    {
        // Retirement happens in the same call as the new binding. Two current
        // addresses would mean publishing to one and listening on both, which
        // looks like it works until a rotation is missed.
        var group = StorageFixture.NewGroupId();
        await Index.PutRoutingAsync(RoutingId(0x01), group, new EpochId(0));
        await Index.PutRoutingAsync(RoutingId(0x02), group, new EpochId(5));

        var all = await Index.ListRoutingAsync(group);

        Assert.Single(all.Where(r => r.IsCurrent));
        Assert.Equal(RoutingId(0x02), (await Index.CurrentRoutingAsync(group))!.TransportGroupId);
    }

    [Fact]
    public async Task ARoutingIdCannotBeReboundToAnotherGroup()
    {
        // Fails closed. A routing id is in the clear on every kind-445 event,
        // so last-write-wins would let anyone who has seen one redirect that
        // group's traffic into state they control.
        var mine = StorageFixture.NewGroupId();
        var theirs = StorageFixture.NewGroupId();
        await Index.PutRoutingAsync(RoutingId(0x01), mine, new EpochId(0));

        await Assert.ThrowsAsync<RoutingIdConflictException>(
            () => Index.PutRoutingAsync(RoutingId(0x01), theirs, new EpochId(0)));

        var resolved = await Index.ResolveAsync(RoutingId(0x01));
        Assert.Equal(mine.Value, resolved!.GroupId.Value);
    }

    [Fact]
    public async Task ReRegisteringAGroupsOwnCurrentAddressIsANoOp()
    {
        // A replayed or retried rotation must not retire the address it is
        // re-affirming.
        var group = StorageFixture.NewGroupId();
        await Index.PutRoutingAsync(RoutingId(0x01), group, new EpochId(3));
        await Index.PutRoutingAsync(RoutingId(0x01), group, new EpochId(3));

        var current = await Index.CurrentRoutingAsync(group);

        Assert.NotNull(current);
        Assert.Equal(RoutingId(0x01), current.TransportGroupId);
        Assert.Single(await Index.ListRoutingAsync(group));
    }

    [Fact]
    public async Task ReturningToAPreviouslyRetiredAddressIsAllowedForTheSameGroup()
    {
        var group = StorageFixture.NewGroupId();
        await Index.PutRoutingAsync(RoutingId(0x01), group, new EpochId(0));
        await Index.PutRoutingAsync(RoutingId(0x02), group, new EpochId(5));
        await Index.PutRoutingAsync(RoutingId(0x01), group, new EpochId(9));

        var current = await Index.CurrentRoutingAsync(group);

        Assert.Equal(RoutingId(0x01), current!.TransportGroupId);
        Assert.Equal(new EpochId(9), current.FirstEpoch);
        Assert.False((await Index.ResolveAsync(RoutingId(0x02)))!.IsCurrent);
    }

    [Fact]
    public async Task ResolutionIsExactRatherThanByPrefix()
    {
        // A routing id is public, so anything looser than whole-value equality
        // invites steering one group's traffic into another's state.
        var group = StorageFixture.NewGroupId();
        byte[] registered = RoutingId(0x01);
        await Index.PutRoutingAsync(registered, group, new EpochId(0));

        byte[] nearMiss = RoutingId(0x01);
        nearMiss[31] = 0x02;

        Assert.Null(await Index.ResolveAsync(nearMiss));
        Assert.Null(await Index.ResolveAsync(registered[..31]));
    }

    [Fact]
    public async Task TwoGroupsKeepTheirOwnAddresses()
    {
        var first = StorageFixture.NewGroupId();
        var second = StorageFixture.NewGroupId();
        await Index.PutRoutingAsync(RoutingId(0x01), first, new EpochId(0));
        await Index.PutRoutingAsync(RoutingId(0x02), second, new EpochId(0));

        Assert.Equal(first.Value, (await Index.ResolveAsync(RoutingId(0x01)))!.GroupId.Value);
        Assert.Equal(second.Value, (await Index.ResolveAsync(RoutingId(0x02)))!.GroupId.Value);
        Assert.Single(await Index.ListRoutingAsync(first));
    }

    // -- Pruning --

    [Fact]
    public async Task PruningDropsRetiredAddressesBelowTheHorizon()
    {
        var group = StorageFixture.NewGroupId();
        await Index.PutRoutingAsync(RoutingId(0x01), group, new EpochId(0));   // retired at 5
        await Index.PutRoutingAsync(RoutingId(0x02), group, new EpochId(5));   // retired at 9
        await Index.PutRoutingAsync(RoutingId(0x03), group, new EpochId(9));   // current

        Assert.Equal(1, await Index.PruneRoutingAsync(group, new EpochId(6)));

        Assert.Null(await Index.ResolveAsync(RoutingId(0x01)));
        Assert.NotNull(await Index.ResolveAsync(RoutingId(0x02)));
        Assert.NotNull(await Index.ResolveAsync(RoutingId(0x03)));
    }

    [Fact]
    public async Task PruningNeverDropsTheCurrentAddressHoweverFarTheHorizonAdvances()
    {
        var group = StorageFixture.NewGroupId();
        await Index.PutRoutingAsync(RoutingId(0x01), group, new EpochId(0));

        Assert.Equal(0, await Index.PruneRoutingAsync(group, new EpochId(ulong.MaxValue)));

        Assert.NotNull(await Index.CurrentRoutingAsync(group));
    }

    [Fact]
    public async Task PruningLeavesOtherGroupsAlone()
    {
        var mine = StorageFixture.NewGroupId();
        var theirs = StorageFixture.NewGroupId();
        await Index.PutRoutingAsync(RoutingId(0x01), theirs, new EpochId(0));
        await Index.PutRoutingAsync(RoutingId(0x02), theirs, new EpochId(5));
        await Index.PutRoutingAsync(RoutingId(0x03), mine, new EpochId(0));

        Assert.Equal(0, await Index.PruneRoutingAsync(mine, new EpochId(99)));
        Assert.NotNull(await Index.ResolveAsync(RoutingId(0x01)));
    }

    [Fact]
    public async Task ARolledBackRotationLeavesTheOldAddressCurrent()
    {
        var group = StorageFixture.NewGroupId();
        await Index.PutRoutingAsync(RoutingId(0x01), group, new EpochId(0));

        await using (var tx = await _fixture.Provider.BeginTransactionAsync())
        {
            await Index.PutRoutingAsync(RoutingId(0x02), group, new EpochId(5));
            await tx.RollbackAsync();
        }

        var current = await Index.CurrentRoutingAsync(group);

        Assert.Equal(RoutingId(0x01), current!.TransportGroupId);
        Assert.Null(await Index.ResolveAsync(RoutingId(0x02)));
    }
}
