using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
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
    private IMlsService _mlsA = null!, _mlsB = null!, _mlsC = null!;
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

        _mlsB = new ManagedMlsService(_storageB);

        _msgServiceB = new MessageService(_storageB, mockNostrB.Object, _mlsB);
        await _msgServiceB.InitializeAsync();

        // Senders A and C only need MLS services (no storage/message service needed)
        _mlsA = new ManagedMlsService(new StorageService(
            Path.Combine(Path.GetTempPath(), $"scramble_lr_a_{Guid.NewGuid()}.db"),
            new MockSecureStorage()));
        await (_mlsA as ManagedMlsService)!.InitializeAsync(_privKeyA, _pubKeyA);

        _mlsC = new ManagedMlsService(new StorageService(
            Path.Combine(Path.GetTempPath(), $"scramble_lr_c_{Guid.NewGuid()}.db"),
            new MockSecureStorage()));
        await (_mlsC as ManagedMlsService)!.InitializeAsync(_privKeyC, _pubKeyC);
    }

    public ValueTask DisposeAsync()
    {
        _msgServiceB?.Dispose();
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (_dbPathB != null) try { File.Delete(_dbPathB); } catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SameKeyPackage_CanBeUsedByTwoSenders_LastResort()
    {
        // ── User B generates ONE KeyPackage ──
        var keyPackageB = await _mlsB.GenerateKeyPackageAsync();
        _output.WriteLine($"User B KeyPackage: {keyPackageB.Data.Length} bytes");

        // ── Sender A creates group and adds B ──
        var groupA = await _mlsA.CreateGroupAsync("Group from A", new[] { "wss://relay.test" });
        _output.WriteLine($"Sender A created group: {Convert.ToHexString(groupA.GroupId).ToLowerInvariant()}");

        var fakeKpJsonA = CreateFakeKeyPackageEventJson(_pubKeyB, keyPackageB.Data, keyPackageB.NostrTags);
        var kpForA = new KeyPackage
        {
            Data = keyPackageB.Data,
            NostrTags = keyPackageB.NostrTags,
            EventJson = fakeKpJsonA,
            NostrEventId = "fakekp_a_" + Guid.NewGuid().ToString("N")
        };
        var welcomeA = await _mlsA.AddMemberAsync(groupA.GroupId, kpForA);
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
                new() { "encoding", "base64" }
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
        var groupC = await _mlsC.CreateGroupAsync("Group from C", new[] { "wss://relay.test" });
        _output.WriteLine($"Sender C created group: {Convert.ToHexString(groupC.GroupId).ToLowerInvariant()}");

        var fakeKpJsonC = CreateFakeKeyPackageEventJson(_pubKeyB, keyPackageB.Data, keyPackageB.NostrTags);
        var kpForC = new KeyPackage
        {
            Data = keyPackageB.Data,
            NostrTags = keyPackageB.NostrTags,
            EventJson = fakeKpJsonC,
            NostrEventId = "fakekp_c_" + Guid.NewGuid().ToString("N")
        };
        var welcomeC = await _mlsC.AddMemberAsync(groupC.GroupId, kpForC);
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
                new() { "encoding", "base64" }
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
        var keyPackageB = await _mlsB.GenerateKeyPackageAsync();
        _output.WriteLine($"User B KeyPackage: {keyPackageB.Data.Length} bytes");

        var kpCountBefore = _mlsB.GetStoredKeyPackageCount();

        var groupA = await _mlsA.CreateGroupAsync("Group from A", new[] { "wss://relay.test" });
        var kpForA = new KeyPackage
        {
            Data = keyPackageB.Data,
            NostrTags = keyPackageB.NostrTags,
            EventJson = CreateFakeKeyPackageEventJson(_pubKeyB, keyPackageB.Data, keyPackageB.NostrTags),
            NostrEventId = "fakekp_a_" + Guid.NewGuid().ToString("N")
        };
        var welcomeA = await _mlsA.AddMemberAsync(groupA.GroupId, kpForA);

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

    /// <summary>
    /// A consumed last-resort KP's ConsumedAt timestamp must survive
    /// ExportServiceStateAsync / ImportServiceStateAsync (service state v5).
    /// Within the 24 h grace window the KP must still be recognised after reimport.
    /// </summary>
    [Fact]
    public async Task ConsumedKeyPackage_ConsumedAtSurvivesServiceStateRoundtrip()
    {
        var keyPackageB = await _mlsB.GenerateKeyPackageAsync();

        // Process a Welcome so ConsumedAt gets stamped on the in-memory KP
        var groupA = await _mlsA.CreateGroupAsync("Group from A", new[] { "wss://relay.test" });
        var kpForA = new KeyPackage
        {
            Data = keyPackageB.Data,
            NostrTags = keyPackageB.NostrTags,
            EventJson = CreateFakeKeyPackageEventJson(_pubKeyB, keyPackageB.Data, keyPackageB.NostrTags),
            NostrEventId = "fakekp_a_" + Guid.NewGuid().ToString("N")
        };
        var welcomeA = await _mlsA.AddMemberAsync(groupA.GroupId, kpForA);

        var inviteTask = WaitForObservable(_msgServiceB.NewInvites, TimeSpan.FromSeconds(5));
        _eventsB.OnNext(BuildWelcomeEvent(_pubKeyA, _pubKeyB, groupA.GroupId, welcomeA.WelcomeData, kpForA.NostrEventId!));
        var invite = await inviteTask;
        await _msgServiceB.AcceptInviteAsync(invite.Id);

        // Export state — v5 format encodes ConsumedAt per KP
        var stateBytes = await _mlsB.ExportServiceStateAsync();
        Assert.NotNull(stateBytes);
        _output.WriteLine($"Exported service state: {stateBytes!.Length} bytes");

        // Import into a fresh MLS service instance
        var mls2 = new ManagedMlsService(_storageB);
        await mls2.InitializeAsync(_privKeyB, _pubKeyB);
        await mls2.ImportServiceStateAsync(stateBytes);

        _output.WriteLine($"After reimport: stored KP count = {mls2.GetStoredKeyPackageCount()}");

        // ConsumedAt was preserved and is still within the 24 h grace window — KP must be accessible
        Assert.True(mls2.GetStoredKeyPackageCount() > 0,
            "Consumed last-resort KP must survive service state export/import (v5 format)");
        Assert.True(mls2.HasKeyMaterialForKeyPackage(keyPackageB.Data),
            "Consumed last-resort KP key material must be accessible after reimport (within 24 h grace window)");
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
                new() { "encoding", "base64" }
            },
            RelayUrl = "wss://test.relay"
        };
    }

    private static string CreateFakeKeyPackageEventJson(string ownerPubKey, byte[] keyPackageData, List<List<string>>? tags = null)
    {
        var contentBase64 = Convert.ToBase64String(keyPackageData);
        var tagsArray = tags?.Select(t => t.ToArray()).ToArray()
            ?? new[] { new[] { "encoding", "base64" }, new[] { "mls_protocol_version", "1.0" }, new[] { "mls_ciphersuite", "0x0001" } };
        var eventObj = new
        {
            id = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            pubkey = ownerPubKey,
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            kind = 30443,
            tags = tagsArray,
            content = contentBase64,
            sig = new string('a', 128)
        };
        return JsonSerializer.Serialize(eventObj);
    }

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
        mock.Setup(n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>()))
            .ReturnsAsync(() => "fakekp_" + Guid.NewGuid().ToString("N"));
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
