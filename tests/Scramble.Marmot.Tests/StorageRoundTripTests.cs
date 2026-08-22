using Scramble.Marmot.Storage;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>Round-trip coverage for every record type.</summary>
[Trait("Category", "MarmotEngine")]
public class StorageRoundTripTests
{
    [Fact]
    public async Task GroupRoundTripsWithAllFlags()
    {
        using var fx = new StorageFixture();
        var id = StorageFixture.NewGroupId();
        var stored = new GroupRecord(id, new EpochId(7), ProtocolProfile.Current,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            Removed = true,
            JoinEpoch = new EpochId(3),
            ValidatedTree = true,
        };

        await fx.Provider.PutGroupAsync(stored);
        var loaded = await fx.Provider.GetGroupAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal(new EpochId(7), loaded!.Epoch);
        Assert.Equal(ProtocolProfile.Current, loaded.Profile);
        Assert.True(loaded.Removed);
        Assert.Equal(new EpochId(3), loaded.JoinEpoch);
        Assert.True(loaded.ValidatedTree);
    }

    [Fact]
    public async Task GroupNullableJoinEpochRoundTripsAsNull()
    {
        using var fx = new StorageFixture();
        var id = StorageFixture.NewGroupId();

        await fx.Provider.PutGroupAsync(StorageFixture.Group(id));
        var loaded = await fx.Provider.GetGroupAsync(id);

        Assert.Null(loaded!.JoinEpoch);
        Assert.False(loaded.Removed);
    }

    [Fact]
    public async Task ListLiveGroupsExcludesRemovedOnes()
    {
        using var fx = new StorageFixture();
        var live = StorageFixture.NewGroupId();
        var gone = StorageFixture.NewGroupId();

        await fx.Provider.PutGroupAsync(StorageFixture.Group(live));
        await fx.Provider.PutGroupAsync(StorageFixture.Group(gone) with { Removed = true });

        var all = await fx.Provider.ListGroupsAsync();
        var liveOnly = await fx.Provider.ListLiveGroupsAsync();

        Assert.Equal(2, all.Count);
        Assert.Single(liveOnly);
        Assert.Equal(live, liveOnly[0].Id);
    }

    [Fact]
    public async Task MessageRoundTripsEveryState()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();
        await fx.Provider.PutGroupAsync(StorageFixture.Group(group));

        foreach (var state in Enum.GetValues<MessageRecordState>())
        {
            var id = StorageFixture.NewMessageId(state.ToString());
            await fx.Provider.PutMessageAsync(
                StorageFixture.Message(group, id, state: state) with
                {
                    TransportId = "evt-" + state,
                    Attempts = 2,
                    Reason = "because",
                });

            var loaded = await fx.Provider.GetMessageAsync(id);
            Assert.NotNull(loaded);
            Assert.Equal(state, loaded!.State);
            Assert.Equal("evt-" + state, loaded.TransportId);
            Assert.Equal(2, loaded.Attempts);
            Assert.Equal("because", loaded.Reason);
        }
    }

    [Fact]
    public async Task MessageIdIsContentDerivedSoIdenticalBytesCollapse()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();
        await fx.Provider.PutGroupAsync(StorageFixture.Group(group));

        byte[] wire = { 9, 9, 9 };
        var id = MessageId.FromMlsBytes(wire);

        // The same MLS bytes arriving under two different transport envelopes
        // must be one record, not two.
        await fx.Provider.PutMessageAsync(
            StorageFixture.Message(group, id) with { TransportId = "envelope-a", Wire = wire });
        await fx.Provider.PutMessageAsync(
            StorageFixture.Message(group, id) with { TransportId = "envelope-b", Wire = wire });

        var messages = await fx.Provider.ListMessagesAsync(group);
        Assert.Single(messages);
    }

    [Fact]
    public async Task ListMessagesByStateFilters()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();
        await fx.Provider.PutGroupAsync(StorageFixture.Group(group));

        await fx.Provider.PutMessageAsync(StorageFixture.Message(
            group, StorageFixture.NewMessageId("a"), state: MessageRecordState.Processed));
        await fx.Provider.PutMessageAsync(StorageFixture.Message(
            group, StorageFixture.NewMessageId("b"), state: MessageRecordState.PeelDeferred));
        await fx.Provider.PutMessageAsync(StorageFixture.Message(
            group, StorageFixture.NewMessageId("c"), state: MessageRecordState.PeelDeferred));

        var deferred = await fx.Provider.ListMessagesByStateAsync(group, MessageRecordState.PeelDeferred);
        Assert.Equal(2, deferred.Count);
    }

    [Fact]
    public async Task TransportSeenIsAPreFilterNotADedupKey()
    {
        using var fx = new StorageFixture();

        Assert.False(await fx.Provider.HasTransportSeenAsync("evt1"));
        await fx.Provider.PutTransportSeenAsync("evt1");
        Assert.True(await fx.Provider.HasTransportSeenAsync("evt1"));

        // Recording the same envelope twice is not an error.
        await fx.Provider.PutTransportSeenAsync("evt1");
        Assert.True(await fx.Provider.HasTransportSeenAsync("evt1"));
    }

    [Fact]
    public async Task OutboundIntentRoundTripsAndClears()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();
        var id = StorageFixture.NewMessageId("intent");

        await fx.Provider.PutIntentAsync(new QueuedOutboundIntent(
            id, group, "AppMessage", new byte[] { 4, 5 }, DateTimeOffset.UtcNow) { Attempts = 1 });

        var intents = await fx.Provider.ListIntentsAsync(group);
        Assert.Single(intents);
        Assert.Equal("AppMessage", intents[0].IntentKind);
        Assert.Equal(new byte[] { 4, 5 }, intents[0].Payload);
        Assert.Equal(1, intents[0].Attempts);

        await fx.Provider.ClearIntentsAsync(group);
        Assert.Empty(await fx.Provider.ListIntentsAsync(group));
    }

    [Fact]
    public async Task LeaveRequestRoundTripsIncludingProposedEpoch()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();

        await fx.Provider.PutLeaveRequestAsync(
            new LeaveRequest(group, new EpochId(4), DateTimeOffset.UtcNow));
        var pending = await fx.Provider.GetLeaveRequestAsync(group);
        Assert.NotNull(pending);
        Assert.Null(pending!.ProposedInEpoch);

        // Re-proposing in a later epoch updates in place rather than duplicating.
        await fx.Provider.PutLeaveRequestAsync(pending with { ProposedInEpoch = new EpochId(5) });
        var reproposed = await fx.Provider.GetLeaveRequestAsync(group);
        Assert.Equal(new EpochId(5), reproposed!.ProposedInEpoch);
        Assert.Single(await fx.Provider.ListLeaveRequestsAsync());

        await fx.Provider.ClearLeaveRequestAsync(group);
        Assert.Null(await fx.Provider.GetLeaveRequestAsync(group));
    }

    [Fact]
    public async Task WelcomeRoundTripsAndFiltersByState()
    {
        using var fx = new StorageFixture();
        var pendingId = StorageFixture.NewMessageId("w1");
        var acceptedId = StorageFixture.NewMessageId("w2");
        var group = StorageFixture.NewGroupId();

        await fx.Provider.PutWelcomeAsync(new WelcomeRecord(
            pendingId, new byte[] { 1 }, WelcomeRecordState.Pending, DateTimeOffset.UtcNow));
        await fx.Provider.PutWelcomeAsync(new WelcomeRecord(
            acceptedId, new byte[] { 2 }, WelcomeRecordState.Accepted, DateTimeOffset.UtcNow)
        {
            GroupId = group,
        });

        Assert.Equal(2, (await fx.Provider.ListWelcomesAsync()).Count);
        Assert.Single(await fx.Provider.ListWelcomesAsync(WelcomeRecordState.Pending));

        var accepted = await fx.Provider.GetWelcomeAsync(acceptedId);
        Assert.Equal(group, accepted!.GroupId);
        Assert.Null((await fx.Provider.GetWelcomeAsync(pendingId))!.GroupId);
    }

    [Fact]
    public async Task InvalidateAfterEpochRetainsRecordsRatherThanDeleting()
    {
        using var fx = new StorageFixture();
        var group = StorageFixture.NewGroupId();
        await fx.Provider.PutGroupAsync(StorageFixture.Group(group));

        var kept = StorageFixture.NewMessageId("kept");
        var lost = StorageFixture.NewMessageId("lost");
        await fx.Provider.PutMessageAsync(StorageFixture.Message(
            group, kept, epoch: 5, state: MessageRecordState.Processed));
        await fx.Provider.PutMessageAsync(StorageFixture.Message(
            group, lost, epoch: 6, state: MessageRecordState.Processed));

        await fx.Provider.InvalidateAfterEpochAsync(group, new EpochId(5));

        // The reorg victim must still be readable, so the UI can explain it.
        Assert.Equal(2, (await fx.Provider.ListMessagesAsync(group)).Count);
        Assert.Equal(MessageRecordState.Processed, (await fx.Provider.GetMessageAsync(kept))!.State);
        Assert.Equal(MessageRecordState.EpochInvalidated, (await fx.Provider.GetMessageAsync(lost))!.State);
    }
}
