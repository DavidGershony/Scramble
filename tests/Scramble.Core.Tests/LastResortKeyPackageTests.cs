using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Data.Sqlite;
using Moq;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Core.Tests.TestHelpers;
using Xunit;
namespace Scramble.Core.Tests;

/// <summary>
/// Tests that KeyPackages with the last_resort extension (MIP-00) can be reused
/// across multiple Welcome messages from different senders.
/// Reproduces the bug where a second invite targeting the same KP failed because
/// ProcessWelcomeAsync discarded the init key after the first use.
/// </summary>
public class LastResortKeyPackageTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    // Three users: A and C invite B using B's single KeyPackage
    private string _pubKeyA = null!, _privKeyA = null!;
    private string _pubKeyB = null!, _privKeyB = null!;
    private string _pubKeyC = null!, _privKeyC = null!;

    private StorageService _storageB = null!;
    private string _dbPathB = null!;
    private MlsTestEngine _engineA = null!, _engineB = null!, _engineC = null!;
    private IMlsService _mlsB = null!;
    private MessageService _msgServiceB = null!;
    private Subject<NostrEventReceived> _eventsB = null!;

    public LastResortKeyPackageTests(ITestOutputHelper output) => _output = output;

    public async ValueTask InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);

        var nostr = new NostrService();
        (_privKeyA, _pubKeyA, _, _) = nostr.GenerateKeyPair();
        (_privKeyB, _pubKeyB, _, _) = nostr.GenerateKeyPair();
        (_privKeyC, _pubKeyC, _, _) = nostr.GenerateKeyPair();

        // Set up User B with full MessageService (the receiver)
        _dbPathB = Path.Combine(Path.GetTempPath(), $"scramble_lr_{Guid.NewGuid()}.db");
        _storageB = new StorageService(_dbPathB, new MockSecureStorage());
        await _storageB.InitializeAsync();
        await _storageB.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = _pubKeyB,
            PrivateKeyHex = _privKeyB,
            DisplayName = "User B",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        _eventsB = new Subject<NostrEventReceived>();
        var mockNostrB = CreateMockNostr(_eventsB);

        // B's engine carries the same identity as B's stored user record, because
        // MessageService re-initialises it from there. Its engine store is a
        // separate database from _storageB: app rows and MLS rows are not the
        // same kind of thing and never share a file.
        _engineB = await MlsTestEngine.StartAsync("lr-b", _privKeyB, _pubKeyB);
        _mlsB = _engineB.Service;

        _msgServiceB = new MessageService(_storageB, mockNostrB.Object, _mlsB);
        await _msgServiceB.InitializeAsync();

        // Senders A and C only need MLS services (no storage/message service needed)
        _engineA = await MlsTestEngine.StartAsync("lr-a", _privKeyA, _pubKeyA);
        _engineC = await MlsTestEngine.StartAsync("lr-c", _privKeyC, _pubKeyC);
    }

    public ValueTask DisposeAsync()
    {
        _msgServiceB?.Dispose();
        _engineA?.Dispose();
        _engineB?.Dispose();
        _engineC?.Dispose();
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (_dbPathB != null) try { File.Delete(_dbPathB); } catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SameKeyPackage_CanBeUsedByTwoSenders_LastResort()
    {
        // ── User B publishes ONE KeyPackage ──
        // One published KeyPackage has one kind-30443 event id, and that is the
        // whole point of this test: A and C both fetch the same event, so both
        // Welcomes name the same id. The old fixture modelled "the same
        // KeyPackage" as the same bytes under two freshly invented event ids,
        // which is not a thing a relay can produce -- and the previous engine,
        // whose MarkKeyPackagePublishedAsync was Task.CompletedTask, could not
        // tell the difference.
        var keyPackageB = await _engineB.PublishKeyPackageAsync();
        _output.WriteLine($"User B KeyPackage: {keyPackageB.Data.Length} bytes, event {keyPackageB.NostrEventId}");

        // ── Sender A creates group and adds B ──
        var groupA = await _engineA.Service.CreateGroupAsync("Group from A", new[] { "wss://relay.test" });
        _output.WriteLine($"Sender A created group: {Convert.ToHexString(groupA.GroupId).ToLowerInvariant()}");

        var kpForA = FetchedByASender(keyPackageB);
        var welcomeA = await _engineA.AddMemberAsync(groupA.GroupId, kpForA);
        _output.WriteLine($"Sender A Welcome: {welcomeA.WelcomeData.Length} bytes");

        // ── Deliver Welcome A to User B and accept ──
        var eventIdA = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var welcomeEventA = new NostrEventReceived
        {
            Kind = 444, EventId = eventIdA, PublicKey = _pubKeyA,
            Content = Convert.ToBase64String(welcomeA.WelcomeData),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "p", _pubKeyB },
                new() { "h", Convert.ToHexString(groupA.GroupId).ToLowerInvariant() },
                new() { "e", kpForA.NostrEventId! },
                new() { "relays", "wss://test.relay" }
            },
            RelayUrl = "wss://test.relay"
        };

        var inviteTaskA = WaitForObservable(_msgServiceB.NewInvites, TimeSpan.FromSeconds(5));
        _eventsB.OnNext(welcomeEventA);
        var inviteA = await inviteTaskA;
        var chatA = await _msgServiceB.AcceptInviteAsync(inviteA.Id);
        _output.WriteLine($"User B accepted group from A: {chatA.Name}");
        Assert.NotNull(chatA.MlsGroupId);

        // ── Sender C creates a DIFFERENT group and adds B using the SAME KeyPackage ──
        var groupC = await _engineC.Service.CreateGroupAsync("Group from C", new[] { "wss://relay.test" });
        _output.WriteLine($"Sender C created group: {Convert.ToHexString(groupC.GroupId).ToLowerInvariant()}");

        var kpForC = FetchedByASender(keyPackageB);
        var welcomeC = await _engineC.AddMemberAsync(groupC.GroupId, kpForC);
        _output.WriteLine($"Sender C Welcome: {welcomeC.WelcomeData.Length} bytes");

        // ── Deliver Welcome C to User B and accept — this MUST succeed (last_resort) ──
        var eventIdC = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var welcomeEventC = new NostrEventReceived
        {
            Kind = 444, EventId = eventIdC, PublicKey = _pubKeyC,
            Content = Convert.ToBase64String(welcomeC.WelcomeData),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "p", _pubKeyB },
                new() { "h", Convert.ToHexString(groupC.GroupId).ToLowerInvariant() },
                new() { "e", kpForC.NostrEventId! },
                new() { "relays", "wss://test.relay" }
            },
            RelayUrl = "wss://test.relay"
        };

        var inviteTaskC = WaitForObservable(_msgServiceB.NewInvites, TimeSpan.FromSeconds(5));
        _eventsB.OnNext(welcomeEventC);
        var inviteC = await inviteTaskC;

        // THIS IS THE BUG REPRODUCTION: before the fix, this threw
        // "None of the stored KeyPackages match this Welcome"
        var chatC = await _msgServiceB.AcceptInviteAsync(inviteC.Id);
        _output.WriteLine($"User B accepted group from C: {chatC.Name}");
        Assert.NotNull(chatC.MlsGroupId);

        // Verify both groups are distinct
        Assert.NotEqual(
            Convert.ToHexString(chatA.MlsGroupId!),
            Convert.ToHexString(chatC.MlsGroupId!));
        _output.WriteLine("Both groups accepted successfully with the same KeyPackage");
    }

    /// <summary>
    /// After a last-resort KeyPackage is consumed by a Welcome, the key material must remain
    /// accessible within the 24-hour MIP-00 grace window so that concurrent senders can still
    /// have their Welcomes processed.
    /// </summary>
    [Fact]
    public async Task ConsumedLastResortKp_StillAccessible_Within24HWindow()
    {
        var keyPackageB = await _engineB.PublishKeyPackageAsync();
        _output.WriteLine($"User B KeyPackage: {keyPackageB.Data.Length} bytes");

        var kpCountBefore = _mlsB.GetStoredKeyPackageCount();

        var groupA = await _engineA.Service.CreateGroupAsync("Group from A", new[] { "wss://relay.test" });
        var kpForA = FetchedByASender(keyPackageB);
        var welcomeA = await _engineA.AddMemberAsync(groupA.GroupId, kpForA);

        var inviteTask = WaitForObservable(_msgServiceB.NewInvites, TimeSpan.FromSeconds(5));
        _eventsB.OnNext(BuildWelcomeEvent(_pubKeyA, _pubKeyB, groupA.GroupId, welcomeA.WelcomeData, kpForA.NostrEventId!));
        var invite = await inviteTask;
        await _msgServiceB.AcceptInviteAsync(invite.Id);

        // Key material must still be accessible (MIP-00 §"Deletion Timing": 24 h grace window)
        Assert.True(_mlsB.HasKeyMaterialForKeyPackage(keyPackageB.Data),
            "Last-resort KP key material must remain accessible after the first Welcome (24 h grace window)");
        Assert.Equal(kpCountBefore, _mlsB.GetStoredKeyPackageCount());

        _output.WriteLine("Last-resort KP retained correctly after first Welcome");
    }

    private NostrEventReceived BuildWelcomeEvent(
        string senderPubKey, string recipientPubKey,
        byte[] groupId, byte[] welcomeData, string kpEventId)
    {
        return new NostrEventReceived
        {
            Kind = 444,
            EventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            PublicKey = senderPubKey,
            Content = Convert.ToBase64String(welcomeData),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "p", recipientPubKey },
                new() { "h", Convert.ToHexString(groupId).ToLowerInvariant() },
                new() { "e", kpEventId },
                new() { "relays", "wss://test.relay" }
            },
            RelayUrl = "wss://test.relay"
        };
    }

    /// <summary>
    /// The KeyPackage as a sender sees it: the bytes and the signed kind-30443
    /// event, off the relay.
    /// </summary>
    /// <remarks>
    /// A copy rather than the original object, so that nothing a sender does to
    /// its own <c>KeyPackage</c> can reach across to B's — but the event id is
    /// deliberately shared, because there is only one published event. Handing
    /// both senders the same instance would have hidden a mutation; handing them
    /// different event ids would have hidden the binding.
    /// </remarks>
    private static KeyPackage FetchedByASender(KeyPackage published) => new()
    {
        Data = published.Data,
        NostrTags = published.NostrTags,
        EventJson = published.EventJson,
        NostrEventId = published.NostrEventId,
        OwnerPublicKey = published.OwnerPublicKey
    };

    private static async Task<T> WaitForObservable<T>(IObservable<T> observable, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = observable.Take(1).Subscribe(
            value => tcs.TrySetResult(value),
            ex => tcs.TrySetException(ex));
        using var cts = new CancellationTokenSource(timeout);
        await using var registration = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
        return await tcs.Task;
    }

    private static Mock<INostrService> CreateMockNostr(Subject<NostrEventReceived> events)
    {
        var mock = new Mock<INostrService>();
        mock.Setup(n => n.Events).Returns(events.AsObservable());
        mock.Setup(n => n.WelcomeMessages).Returns(Observable.Empty<MarmotWelcomeEvent>());
        mock.Setup(n => n.GroupMessages).Returns(Observable.Empty<MarmotGroupMessageEvent>());
        mock.Setup(n => n.ConnectionStatus).Returns(Observable.Empty<NostrConnectionStatus>());
        mock.Setup(n => n.SyncStatus).Returns(Observable.Empty<string?>());
        // Reached for the first time now that these fixtures carry the relays tag
        // the wire requires: AcceptInvite checks the group's relays against the
        // connected ones, and an unstubbed property hands it null.
        mock.Setup(n => n.ConnectedRelayUrls).Returns(new List<string> { "wss://test.relay" });
        mock.Setup(n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>()))
            .ReturnsAsync(() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
        mock.Setup(n => n.PublishWelcomeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(() => "fakewelcome_" + Guid.NewGuid().ToString("N"));
        mock.Setup(n => n.PublishRawEventJsonAsync(It.IsAny<byte[]>()))
            .ReturnsAsync(() => "fakemsg_" + Guid.NewGuid().ToString("N"));
        mock.Setup(n => n.PublishCommitAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => "fakecommit_" + Guid.NewGuid().ToString("N"));
        mock.Setup(n => n.FetchUserMetadataAsync(It.IsAny<string>())).ReturnsAsync((UserMetadata?)null);
        mock.Setup(n => n.FetchKeyPackagesAsync(It.IsAny<string>())).ReturnsAsync(Enumerable.Empty<KeyPackage>());
        mock.Setup(n => n.FetchWelcomeEventsAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(Enumerable.Empty<NostrEventReceived>());
        mock.Setup(n => n.ConnectAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        mock.Setup(n => n.ConnectAsync(It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
        mock.Setup(n => n.DisconnectAsync()).Returns(Task.CompletedTask);
        mock.Setup(n => n.SubscribeToWelcomesAsync(It.IsAny<string>(), It.IsAny<string?>())).Returns(Task.CompletedTask);
        mock.Setup(n => n.SubscribeToGroupMessagesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<DateTimeOffset?>()))
            .Returns(Task.CompletedTask);
        mock.Setup(n => n.SubscribeAsync(It.IsAny<string>(), It.IsAny<NostrFilter>())).Returns(Task.CompletedTask);
        mock.Setup(n => n.UnsubscribeAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        return mock;
    }
}
