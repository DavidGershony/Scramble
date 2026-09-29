using Scramble.Core.Models;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// The report exists to tell three situations apart that all present as "this group is
/// empty" and each need a different response. These tests pin that they stay told apart.
/// </summary>
public class HistoryFetchReportTests
{
    private static HistoryFetchReport Report(
        int addresses = 1,
        bool stale = false,
        int before = 0,
        int after = 0,
        ulong epoch = 4,
        string? error = null) => new()
        {
            KnownAddresses = Enumerable.Range(0, addresses).Select(i => $"{i:x64}").ToList(),
            PreviouslySubscribedAddress = stale ? "deadbeef" : $"{0:x64}",
            StoredAddressWasStale = stale,
            MessagesBefore = before,
            MessagesAfter = after,
            Epoch = epoch,
            Waited = TimeSpan.FromSeconds(10),
            Error = error
        };

    [Fact]
    public void Gained_IsTheDifference() =>
        Assert.Equal(27, Report(before: 3, after: 30).Gained);

    [Fact]
    public void RecoveringMessages_SaysHowMany()
    {
        var summary = Report(addresses: 3, before: 0, after: 27).Summary;

        Assert.Contains("Recovered 27 message(s)", summary);
        Assert.Contains("3 addresses", summary);
    }

    [Fact]
    public void OneAddressAndNothingNew_BlamesTheRelayNotTheClient()
    {
        // The honest answer when we asked the right place: there is nothing there. It must
        // NOT hint at undecryptable history, because that would send the user looking for
        // a problem that does not exist here.
        var summary = Report(addresses: 1, stale: false, after: 0).Summary;

        Assert.Contains("nothing for this group", summary);
        Assert.DoesNotContain("cannot be decrypted", summary);
    }

    [Fact]
    public void SeveralAddressesAndNothingNew_PointsAtEpochsRatherThanTheRelay()
    {
        // We asked everywhere and still got nothing, so the remaining explanation is that
        // whatever is there predates this account's membership.
        var summary = Report(addresses: 3, stale: false, after: 0).Summary;

        Assert.Contains("cannot be decrypted", summary);
        Assert.DoesNotContain("nothing for this group", summary);
    }

    [Fact]
    public void AStaleStoredAddress_IsCalledOutExplicitly()
    {
        // The single most useful fact the pass can report, and it must survive into the
        // text whether or not anything was recovered.
        Assert.Contains("out of date", Report(addresses: 2, stale: true, after: 5).Summary);
        Assert.Contains("out of date", Report(addresses: 2, stale: true, after: 0).Summary);
    }

    [Fact]
    public void AStaleAddressOnItsOwn_StillAvoidsTheRelayIsEmptyClaim()
    {
        // One address in the index but the chat had a different one stored: we were asking
        // the wrong place, so "the relay has nothing" would be an unsupported conclusion.
        var summary = Report(addresses: 1, stale: true, after: 0).Summary;

        Assert.DoesNotContain("nothing for this group", summary);
    }

    [Fact]
    public void AnError_ReplacesTheSummaryEntirely()
    {
        // No counts, no epoch, no speculation about relays when the pass never ran.
        var summary = Report(error: "This is not a group chat, so it has no group history to fetch.").Summary;

        Assert.Equal("This is not a group chat, so it has no group history to fetch.", summary);
        Assert.DoesNotContain("epoch", summary);
    }

    [Fact]
    public void TheEpochIsReported_SoAMissingCommitIsIdentifiable() =>
        Assert.Contains("epoch 9", Report(epoch: 9, after: 1).Summary);
}
