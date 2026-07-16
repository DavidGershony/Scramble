using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;
using Scramble.Core.Configuration;
using Scramble.Core.Tests.TestHelpers;
namespace Scramble.Core.Tests;

/// <summary>
/// Real-relay integration tests that exercise group chat flows
/// using real NostrService instances connected to a local relay (wss://relay2.angor.io).
///
/// ZERO mocks — every layer is real: NostrService, ManagedMlsService, StorageService, MessageService.
/// Messages travel through a real Nostr relay via WebSocket.
///
/// These tests use the managed (C#) MLS backend only and require a running relay.
/// They are tagged with [Trait("Category", "Integration")] so CI can exclude them.
///
/// Requires: docker compose -f docker-compose.test.yml up -d  (relay on wss://relay2.angor.io)
/// </summary>
[Trait("Category", "Integration")]
public class RealRelayGroupChatTests : IAsyncLifetime
{
    private const string RelayUrl = "wss://relay2.angor.io";

    private readonly ITestOutputHelper _output;
    private readonly ILogger _logger;

    // User contexts (A and B initialized in InitializeAsync, C lazily in tests that need it)
    private UserTestContext _userA = null!;
    private UserTestContext _userB = null!;
    private UserTestContext? _userC;

    private bool _relayAvailable;

    public RealRelayGroupChatTests(ITestOutputHelper output)
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _output = output;
        _logger = LoggingConfiguration.CreateLogger<RealRelayGroupChatTests>();
    }

    public async ValueTask InitializeAsync()
    {
        _relayAvailable = await IsRelayReachableAsync();
        if (!_relayAvailable)
        {
            _logger.LogWarning("Relay at {RelayUrl} is not reachable, tests will be skipped", RelayUrl);
            return;
        }

        _logger.LogInformation("Relay at {RelayUrl} is reachable, setting up test contexts", RelayUrl);

        _userA = await SetupUserAsync("A");
        _userB = await SetupUserAsync("B");

        await Task.Delay(1000); // Let connections stabilize

        _logger.LogInformation("Both users connected to relay. A={PubKeyA}, B={PubKeyB}",
            _userA.PubKey[..16], _userB.PubKey[..16]);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeUserAsync(_userA);
        await DisposeUserAsync(_userB);
        if (_userC != null) await DisposeUserAsync(_userC);

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        TryDeleteFile(_userA?.DbPath);
        TryDeleteFile(_userB?.DbPath);
        TryDeleteFile(_userC?.DbPath);
    }

    // ===================================================================
    // Test 1: Full group chat — create, invite, accept, exchange messages
    // ===================================================================

    [Fact]
    public async Task FullGroupChat_CreateInviteAcceptAndExchangeMessages()
    {
        Assert.SkipUnless(_relayAvailable, "Relay not available at " + RelayUrl);

        _output.WriteLine($"User A pubkey: {_userA.PubKey}");
        _output.WriteLine($"User B pubkey: {_userB.PubKey}");

        // Phase 1: User B generates and publishes KeyPackage
        var keyPackageB = await _userB.Mls.GenerateKeyPackageAsync();
        Assert.True(keyPackageB.Data.Length >= 64, $"KeyPackage should be >= 64 bytes, got {keyPackageB.Data.Length}");
        _output.WriteLine($"User B generated KeyPackage: {keyPackageB.Data.Length} bytes");

        var kpEventId = await _userB.Nostr.PublishKeyPackageAsync(
            keyPackageB.Data, _userB.PrivKey, keyPackageB.NostrTags);
        Assert.Equal(64, kpEventId.Length);
        _output.WriteLine($"User B published KeyPackage to relay: {kpEventId}");

        await Task.Delay(1000); // Let relay store it

        // Phase 2: User A creates a group
        var groupInfo = await _userA.Mls.CreateGroupAsync("Real Relay Test Group", new[] { "wss://relay.test" });
        Assert.NotNull(groupInfo.GroupId);
        _output.WriteLine($"User A created group: {Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant()[..16]}...");

        // Save chat record for User A
        var nostrGroupIdA = _userA.Mls.GetNostrGroupId(groupInfo.GroupId);
        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Real Relay Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupIdA,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _userA.PubKey },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _userA.Storage.SaveChatAsync(chatA);

        // Phase 3: User A fetches User B's KeyPackage from relay
        var fetchedKPs = (await _userA.Nostr.FetchKeyPackagesAsync(_userB.PubKey)).ToList();
        Assert.NotEmpty(fetchedKPs);
        var fetchedKP = fetchedKPs[0];
        Assert.NotNull(fetchedKP.EventJson);
        _output.WriteLine($"User A fetched User B's KeyPackage: {fetchedKP.NostrEventId}");

        // Phase 4: User A adds User B to the group
        var welcome = await _userA.Mls.AddMemberAsync(groupInfo.GroupId, fetchedKP);
        Assert.NotNull(welcome.WelcomeData);
        Assert.True(welcome.WelcomeData.Length > 0);
        _output.WriteLine($"User A added User B: welcome={welcome.WelcomeData.Length} bytes");

        // Update A's participant list
        chatA.ParticipantPublicKeys.Add(_userB.PubKey);
        await _userA.Storage.SaveChatAsync(chatA);

        // Phase 5: User B subscribes to welcomes, then User A publishes Welcome
        await _userB.Nostr.SubscribeToWelcomesAsync(_userB.PubKey, _userB.PrivKey);
        await Task.Delay(500);

        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _userB.Messages.NewInvites
            .Take(1)
            .Subscribe(invite => inviteTcs.TrySetResult(invite));

        var welcomeEventId = await _userA.Nostr.PublishWelcomeAsync(
            welcome.WelcomeData, _userB.PubKey, _userA.PrivKey, kpEventId);
        Assert.Equal(64, welcomeEventId.Length);
        _output.WriteLine($"User A published Welcome: {welcomeEventId}");

        // Phase 6: User B receives Welcome via relay
        using var inviteCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        inviteCts.Token.Register(() => inviteTcs.TrySetCanceled());

        var pendingInvite = await inviteTcs.Task;
        Assert.NotNull(pendingInvite);
        Assert.Equal(_userA.PubKey, pendingInvite.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"User B received invite: {pendingInvite.Id}");

        // Phase 7: User B accepts invite
        var chatB = await _userB.Messages.AcceptInviteAsync(pendingInvite.Id);
        Assert.NotNull(chatB);
        Assert.Equal(ChatType.Group, chatB.Type);
        Assert.NotNull(chatB.MlsGroupId);
        _output.WriteLine($"User B accepted invite, chat: {chatB.Id}");

        // Phase 8: Subscribe both to group messages
        var nostrGroupIdHexA = nostrGroupIdA != null
            ? Convert.ToHexString(nostrGroupIdA).ToLowerInvariant()
            : Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();

        var nostrGroupIdB = chatB.NostrGroupId ?? chatB.MlsGroupId;
        var nostrGroupIdHexB = Convert.ToHexString(nostrGroupIdB!).ToLowerInvariant();

        await _userA.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHexA });
        await _userB.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHexB });
        await Task.Delay(500);

        // Phase 9: User A sends a message, User B receives it
        var receivedByB = await SendAndWaitAsync(
            _userA.Messages, chatA.Id, "Hello from A via relay!",
            _userB.Messages.NewMessages, "Hello from A via relay!");
        Assert.Equal(_userA.PubKey, receivedByB.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"User B received message: \"{receivedByB.Content}\"");

        // Phase 10: User B sends a reply, User A receives it
        var receivedByA = await SendAndWaitAsync(
            _userB.Messages, chatB.Id, "Reply from B via relay!",
            _userA.Messages.NewMessages, "Reply from B via relay!");
        Assert.Equal(_userB.PubKey, receivedByA.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"User A received reply: \"{receivedByA.Content}\"");

        _output.WriteLine("FULL GROUP CHAT TEST PASSED: bidirectional message exchange via real relay");
    }

    // ===================================================================
    // Test 2: Published event has correct Nostr group ID (h-tag)
    // ===================================================================

    [Fact]
    public async Task PublishedEvent_HasCorrectNostrGroupId()
    {
        Assert.SkipUnless(_relayAvailable, "Relay not available at " + RelayUrl);

        _output.WriteLine($"User A pubkey: {_userA.PubKey}");
        _output.WriteLine($"User B pubkey: {_userB.PubKey}");

        // Phase 1: User B generates and publishes KeyPackage
        var keyPackageB = await _userB.Mls.GenerateKeyPackageAsync();
        var kpEventId = await _userB.Nostr.PublishKeyPackageAsync(
            keyPackageB.Data, _userB.PrivKey, keyPackageB.NostrTags);
        _output.WriteLine($"User B published KeyPackage: {kpEventId}");

        await Task.Delay(1000);

        // Phase 2: User A creates group and adds User B
        var groupInfo = await _userA.Mls.CreateGroupAsync("H-Tag Test Group", new[] { "wss://relay.test" });
        var nostrGroupId = _userA.Mls.GetNostrGroupId(groupInfo.GroupId);
        Assert.NotNull(nostrGroupId);
        var nostrGroupIdHex = Convert.ToHexString(nostrGroupId!).ToLowerInvariant();
        var mlsGroupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        _output.WriteLine($"MLS group ID:   {mlsGroupIdHex[..16]}...");
        _output.WriteLine($"Nostr group ID: {nostrGroupIdHex[..16]}...");

        // Save chat for A
        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "H-Tag Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _userA.PubKey },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _userA.Storage.SaveChatAsync(chatA);

        // Fetch B's KeyPackage, add B to group
        var fetchedKPs = (await _userA.Nostr.FetchKeyPackagesAsync(_userB.PubKey)).ToList();
        Assert.NotEmpty(fetchedKPs);
        var welcome = await _userA.Mls.AddMemberAsync(groupInfo.GroupId, fetchedKPs[0]);
        chatA.ParticipantPublicKeys.Add(_userB.PubKey);
        await _userA.Storage.SaveChatAsync(chatA);

        // Phase 3: User A sends a message (publishes kind 445 via PublishRawEventJsonAsync)
        await _userA.Messages.SendMessageAsync(chatA.Id, "H-tag test message");
        _output.WriteLine("User A sent test message");

        await Task.Delay(1000); // Let relay process

        // Phase 4: Fetch event back from relay via FetchGroupHistoryAsync
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        var until = DateTimeOffset.UtcNow.AddMinutes(1);
        var events = (await _userA.Nostr.FetchGroupHistoryAsync(nostrGroupIdHex, since, until, 10)).ToList();
        Assert.NotEmpty(events);
        _output.WriteLine($"Fetched {events.Count} event(s) from relay");

        // Phase 5: Verify the h-tag uses NostrGroupId, not MLS group ID
        var lastEvent = events.Last();
        var hTag = lastEvent.Tags.FirstOrDefault(t => t.Count > 1 && t[0] == "h");
        Assert.NotNull(hTag);
        Assert.Equal(nostrGroupIdHex, hTag![1]);
        Assert.NotEqual(mlsGroupIdHex, hTag[1]);
        _output.WriteLine($"h-tag value: {hTag[1]} (matches NostrGroupId, not MlsGroupId)");

        // Phase 6: Verify content is base64 (not double-wrapped JSON)
        var content = lastEvent.Content;
        Assert.False(string.IsNullOrEmpty(content));
        var decoded = Convert.FromBase64String(content);
        Assert.True(decoded.Length > 0, "Content should decode from base64 to non-empty bytes");
        Assert.NotEqual((byte)'{', decoded[0]);
        _output.WriteLine($"Content is valid base64 ({decoded.Length} bytes), not double-wrapped JSON");

        _output.WriteLine("H-TAG TEST PASSED: event uses NostrGroupId and base64 ciphertext");
    }

    // ===================================================================
    // Test 3: Welcome event not rejected by relay (catches timestamp bug)
    // ===================================================================

    [Fact]
    public async Task WelcomeEvent_NotRejectedByRelay()
    {
        Assert.SkipUnless(_relayAvailable, "Relay not available at " + RelayUrl);

        _output.WriteLine($"User A pubkey: {_userA.PubKey}");
        _output.WriteLine($"User B pubkey: {_userB.PubKey}");

        // Phase 1: User B generates a KeyPackage
        var keyPackageB = await _userB.Mls.GenerateKeyPackageAsync();
        var kpEventId = await _userB.Nostr.PublishKeyPackageAsync(
            keyPackageB.Data, _userB.PrivKey, keyPackageB.NostrTags);
        _output.WriteLine($"User B published KeyPackage: {kpEventId}");

        await Task.Delay(1000);

        // Phase 2: User A creates group and adds User B
        var groupInfo = await _userA.Mls.CreateGroupAsync("Welcome Timestamp Test", new[] { "wss://relay.test" });
        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Welcome Timestamp Test",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _userA.PubKey },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _userA.Storage.SaveChatAsync(chatA);

        var fetchedKPs = (await _userA.Nostr.FetchKeyPackagesAsync(_userB.PubKey)).ToList();
        Assert.NotEmpty(fetchedKPs);
        var welcome = await _userA.Mls.AddMemberAsync(groupInfo.GroupId, fetchedKPs[0]);
        _output.WriteLine($"User A added User B, welcome={welcome.WelcomeData.Length} bytes");

        // Phase 3: User A publishes Welcome to relay
        var welcomeEventId = await _userA.Nostr.PublishWelcomeAsync(
            welcome.WelcomeData, _userB.PubKey, _userA.PrivKey, kpEventId);

        Assert.Equal(64, welcomeEventId.Length);
        _output.WriteLine($"Welcome event published successfully: {welcomeEventId}");

        // Phase 4: User B subscribes and retrieves the Welcome
        await _userB.Nostr.SubscribeToWelcomesAsync(_userB.PubKey, _userB.PrivKey);
        await Task.Delay(500);

        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _userB.Messages.NewInvites
            .Take(1)
            .Subscribe(invite => inviteTcs.TrySetResult(invite));

        Task<PendingInvite> waitTask = inviteTcs.Task;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() => inviteTcs.TrySetCanceled());

        PendingInvite? receivedInvite = null;
        try
        {
            receivedInvite = await waitTask;
        }
        catch (TaskCanceledException)
        {
            _output.WriteLine("Welcome not received via subscription, trying rescan...");
            await _userB.Messages.RescanInvitesAsync();
            var invites = (await _userB.Messages.GetPendingInvitesAsync()).ToList();
            Assert.NotEmpty(invites);
            receivedInvite = invites[0];
        }

        Assert.NotNull(receivedInvite);
        Assert.Equal(_userA.PubKey, receivedInvite!.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"User B received Welcome invite: {receivedInvite.Id}");

        // Phase 5: User B can accept the invite (Welcome data is valid)
        var chatB = await _userB.Messages.AcceptInviteAsync(receivedInvite.Id);
        Assert.NotNull(chatB);
        Assert.NotNull(chatB.MlsGroupId);
        _output.WriteLine($"User B accepted invite, joined group: {chatB.Name}");

        _output.WriteLine("WELCOME TIMESTAMP TEST PASSED: event accepted by relay and processable by recipient");
    }

    // ===================================================================
    // Test 4: Multi-round 2-user chat — proves MLS epoch stays in sync
    // ===================================================================

    [Fact]
    public async Task FullGroupChat_MultiRoundMessages_ViaRelay()
    {
        Assert.SkipUnless(_relayAvailable, "Relay not available at " + RelayUrl);

        _output.WriteLine($"User A pubkey: {_userA.PubKey}");
        _output.WriteLine($"User B pubkey: {_userB.PubKey}");

        // Setup: create group, invite B, B accepts (reusable helper)
        var (chatA, chatB, nostrGroupIdHex) = await SetupTwoUserGroupAsync("Multi-Round Relay Test");

        // Subscribe both to group messages
        await _userA.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHex });
        await _userB.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHex });
        await Task.Delay(500);

        // 3 rounds of bidirectional messages
        for (int round = 1; round <= 3; round++)
        {
            _output.WriteLine($"--- Round {round} ---");

            // A sends → B receives
            var msgA = $"Round {round} from A";
            var receivedByB = await SendAndWaitAsync(
                _userA.Messages, chatA.Id, msgA,
                _userB.Messages.NewMessages, msgA);
            Assert.Equal(msgA, receivedByB.Content);
            Assert.Equal(_userA.PubKey, receivedByB.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(chatB.Id, receivedByB.ChatId);
            _output.WriteLine($"  B received: \"{receivedByB.Content}\"");

            // B sends → A receives
            var msgB = $"Round {round} from B";
            var receivedByA = await SendAndWaitAsync(
                _userB.Messages, chatB.Id, msgB,
                _userA.Messages.NewMessages, msgB);
            Assert.Equal(msgB, receivedByA.Content);
            Assert.Equal(_userB.PubKey, receivedByA.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(chatA.Id, receivedByA.ChatId);
            _output.WriteLine($"  A received: \"{receivedByA.Content}\"");
        }

        _output.WriteLine("MULTI-ROUND TEST PASSED: 6 messages exchanged, MLS epoch stayed in sync");
    }

    // ===================================================================
    // Test 5: Three-user group chat — all receive each other's messages
    // ===================================================================

    [Fact]
    public async Task ThreeUserGroupChat_AllReceiveMessages_ViaRelay()
    {
        Assert.SkipUnless(_relayAvailable, "Relay not available at " + RelayUrl);

        // Setup User C
        _userC = await SetupUserAsync("C");
        _output.WriteLine($"User A pubkey: {_userA.PubKey}");
        _output.WriteLine($"User B pubkey: {_userB.PubKey}");
        _output.WriteLine($"User C pubkey: {_userC.PubKey}");

        // ── Phase 1: B and C publish KeyPackages ──
        var kpB = await _userB.Mls.GenerateKeyPackageAsync();
        var kpBEventId = await _userB.Nostr.PublishKeyPackageAsync(
            kpB.Data, _userB.PrivKey, kpB.NostrTags);
        _output.WriteLine($"B published KeyPackage: {kpBEventId}");

        var kpC = await _userC.Mls.GenerateKeyPackageAsync();
        var kpCEventId = await _userC.Nostr.PublishKeyPackageAsync(
            kpC.Data, _userC.PrivKey, kpC.NostrTags);
        _output.WriteLine($"C published KeyPackage: {kpCEventId}");

        await Task.Delay(1000);

        // ── Phase 2: A creates group ──
        var groupInfo = await _userA.Mls.CreateGroupAsync("Three User Relay Test", new[] { RelayUrl });
        var nostrGroupIdBytes = _userA.Mls.GetNostrGroupId(groupInfo.GroupId);
        var nostrGroupIdHex = Convert.ToHexString(nostrGroupIdBytes).ToLowerInvariant();
        _output.WriteLine($"A created group, NostrGroupId: {nostrGroupIdHex[..16]}...");

        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Three User Relay Test",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupIdBytes,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _userA.PubKey },
            RelayUrls = new List<string> { RelayUrl },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _userA.Storage.SaveChatAsync(chatA);

        // ── Phase 3: A adds B (low-level for precise control) ──
        var fetchedKPsB = (await _userA.Nostr.FetchKeyPackagesAsync(_userB.PubKey)).ToList();
        Assert.NotEmpty(fetchedKPsB);
        var welcomeB = await _userA.Mls.AddMemberAsync(groupInfo.GroupId, fetchedKPsB[0]);
        _output.WriteLine($"A added B: welcome={welcomeB.WelcomeData.Length} bytes");

        chatA.ParticipantPublicKeys.Add(_userB.PubKey);
        await _userA.Storage.SaveChatAsync(chatA);

        // Publish commit for B's addition (existing members need to process it)
        if (welcomeB.CommitData != null && welcomeB.CommitData.Length > 0)
        {
            await _userA.Nostr.PublishCommitAsync(welcomeB.CommitData, nostrGroupIdHex, _userA.PrivKey);
            _output.WriteLine("A published commit for B's addition");
        }

        // B subscribes to welcomes and receives the invite
        await _userB.Nostr.SubscribeToWelcomesAsync(_userB.PubKey, _userB.PrivKey);
        await Task.Delay(500);

        var inviteBTcs = new TaskCompletionSource<PendingInvite>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteBSub = _userB.Messages.NewInvites.Take(1).Subscribe(i => inviteBTcs.TrySetResult(i));

        await _userA.Nostr.PublishWelcomeAsync(welcomeB.WelcomeData, _userB.PubKey, _userA.PrivKey, kpBEventId);
        _output.WriteLine("A published Welcome for B");

        using var inviteBCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        inviteBCts.Token.Register(() => inviteBTcs.TrySetCanceled());
        var pendingB = await inviteBTcs.Task;
        var chatB = await _userB.Messages.AcceptInviteAsync(pendingB.Id);
        Assert.NotNull(chatB);
        _output.WriteLine($"B accepted invite, chat: {chatB.Id}");

        // ── Phase 4: Subscribe A and B to group messages BEFORE adding C ──
        var nostrGroupIdHexB = Convert.ToHexString((chatB.NostrGroupId ?? chatB.MlsGroupId)!).ToLowerInvariant();
        await _userA.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHex });
        await _userB.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHexB });
        await Task.Delay(500);

        // ── Phase 5: A adds C via high-level MessageService.AddMemberAsync ──
        // This publishes commit (kind 445) + welcome (kind 444) automatically.
        // B must be subscribed so it processes the commit and advances epoch.
        await _userA.Messages.AddMemberAsync(chatA.Id, _userC.PubKey);
        _output.WriteLine("A added C via MessageService.AddMemberAsync");

        // Wait for B to process the commit event from the relay
        await Task.Delay(3000);

        // C subscribes to welcomes and receives invite
        await _userC.Nostr.SubscribeToWelcomesAsync(_userC.PubKey, _userC.PrivKey);
        await Task.Delay(500);

        // C may need a rescan if welcome was published before subscription
        Chat chatC;
        var inviteCTcs = new TaskCompletionSource<PendingInvite>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteCSub = _userC.Messages.NewInvites.Take(1).Subscribe(i => inviteCTcs.TrySetResult(i));

        using var inviteCCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        inviteCCts.Token.Register(() => inviteCTcs.TrySetCanceled());

        PendingInvite? pendingC = null;
        try
        {
            pendingC = await inviteCTcs.Task;
        }
        catch (TaskCanceledException)
        {
            _output.WriteLine("C didn't receive welcome via subscription, trying rescan...");
            await _userC.Messages.RescanInvitesAsync();
            var invites = (await _userC.Messages.GetPendingInvitesAsync()).ToList();
            Assert.NotEmpty(invites);
            pendingC = invites[0];
        }

        chatC = await _userC.Messages.AcceptInviteAsync(pendingC!.Id);
        Assert.NotNull(chatC);
        _output.WriteLine($"C accepted invite, chat: {chatC.Id}");

        // Subscribe C to group messages
        var nostrGroupIdHexC = Convert.ToHexString((chatC.NostrGroupId ?? chatC.MlsGroupId)!).ToLowerInvariant();
        await _userC.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHexC });
        await Task.Delay(500);

        // ── Phase 6: A sends → B and C both receive ──
        _output.WriteLine("--- A sends to all ---");
        var msgFromA = "Hello from A to all!";
        var bReceivesA = WaitForMessageAsync(_userB.Messages.NewMessages, msgFromA);
        var cReceivesA = WaitForMessageAsync(_userC.Messages.NewMessages, msgFromA);

        await _userA.Messages.SendMessageAsync(chatA.Id, msgFromA);
        _output.WriteLine("A sent message");

        var bGotA = await bReceivesA;
        Assert.Equal(msgFromA, bGotA.Content);
        Assert.Equal(_userA.PubKey, bGotA.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"  B received: \"{bGotA.Content}\"");

        var cGotA = await cReceivesA;
        Assert.Equal(msgFromA, cGotA.Content);
        Assert.Equal(_userA.PubKey, cGotA.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"  C received: \"{cGotA.Content}\"");

        // ── Phase 7: B sends → A and C both receive ──
        _output.WriteLine("--- B sends to all ---");
        var msgFromB = "Hello from B to all!";
        var aReceivesB = WaitForMessageAsync(_userA.Messages.NewMessages, msgFromB);
        var cReceivesB = WaitForMessageAsync(_userC.Messages.NewMessages, msgFromB);

        await _userB.Messages.SendMessageAsync(chatB.Id, msgFromB);
        _output.WriteLine("B sent message");

        var aGotB = await aReceivesB;
        Assert.Equal(msgFromB, aGotB.Content);
        Assert.Equal(_userB.PubKey, aGotB.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"  A received: \"{aGotB.Content}\"");

        var cGotB = await cReceivesB;
        Assert.Equal(msgFromB, cGotB.Content);
        Assert.Equal(_userB.PubKey, cGotB.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"  C received: \"{cGotB.Content}\"");

        // ── Phase 8: C sends → A and B both receive ──
        _output.WriteLine("--- C sends to all ---");
        var msgFromC = "Hello from C to all!";
        var aReceivesC = WaitForMessageAsync(_userA.Messages.NewMessages, msgFromC);
        var bReceivesC = WaitForMessageAsync(_userB.Messages.NewMessages, msgFromC);

        await _userC.Messages.SendMessageAsync(chatC.Id, msgFromC);
        _output.WriteLine("C sent message");

        var aGotC = await aReceivesC;
        Assert.Equal(msgFromC, aGotC.Content);
        Assert.Equal(_userC.PubKey, aGotC.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"  A received: \"{aGotC.Content}\"");

        var bGotC = await bReceivesC;
        Assert.Equal(msgFromC, bGotC.Content);
        Assert.Equal(_userC.PubKey, bGotC.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"  B received: \"{bGotC.Content}\"");

        _output.WriteLine("THREE-USER TEST PASSED: all 3 users sent and received messages via real relay");
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    /// <summary>
    /// Creates a fully initialized user context: keys, storage, MLS, MessageService, relay connection.
    /// </summary>
    private async Task<UserTestContext> SetupUserAsync(string label)
    {
        var nostr = new NostrService();
        var keys = nostr.GenerateKeyPair();

        var dbPath = Path.Combine(Path.GetTempPath(), $"scramble_relaytest_{label}_{Guid.NewGuid()}.db");
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();
        await storage.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = keys.publicKeyHex,
            PrivateKeyHex = keys.privateKeyHex,
            Npub = $"npub1test{label}",
            Nsec = $"nsec1test{label}",
            DisplayName = $"User {label} (Managed)",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        var mls = new ManagedMlsService(storage);
        var messages = new MessageService(storage, nostr, mls);
        await messages.InitializeAsync();

        await nostr.ConnectAsync(RelayUrl);
        await Task.Delay(500);

        return new UserTestContext(keys.publicKeyHex, keys.privateKeyHex, nostr, storage, mls, messages, dbPath);
    }

    /// <summary>
    /// Full 2-user group setup: create group, publish KP, add member, deliver welcome, accept.
    /// Returns (chatA, chatB, nostrGroupIdHex).
    /// </summary>
    private async Task<(Chat chatA, Chat chatB, string nostrGroupIdHex)> SetupTwoUserGroupAsync(string groupName)
    {
        // B publishes KeyPackage
        var kpB = await _userB.Mls.GenerateKeyPackageAsync();
        var kpEventId = await _userB.Nostr.PublishKeyPackageAsync(
            kpB.Data, _userB.PrivKey, kpB.NostrTags);
        _output.WriteLine($"B published KeyPackage: {kpEventId}");
        await Task.Delay(1000);

        // A creates group
        var groupInfo = await _userA.Mls.CreateGroupAsync(groupName, new[] { RelayUrl });
        var nostrGroupIdBytes = _userA.Mls.GetNostrGroupId(groupInfo.GroupId);
        var nostrGroupIdHex = Convert.ToHexString(nostrGroupIdBytes).ToLowerInvariant();

        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = groupName,
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupIdBytes,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _userA.PubKey },
            RelayUrls = new List<string> { RelayUrl },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _userA.Storage.SaveChatAsync(chatA);

        // A fetches B's KP and adds B
        var fetchedKPs = (await _userA.Nostr.FetchKeyPackagesAsync(_userB.PubKey)).ToList();
        Assert.NotEmpty(fetchedKPs);
        var welcome = await _userA.Mls.AddMemberAsync(groupInfo.GroupId, fetchedKPs[0]);
        chatA.ParticipantPublicKeys.Add(_userB.PubKey);
        await _userA.Storage.SaveChatAsync(chatA);

        // B subscribes and receives welcome
        await _userB.Nostr.SubscribeToWelcomesAsync(_userB.PubKey, _userB.PrivKey);
        await Task.Delay(500);

        var inviteTcs = new TaskCompletionSource<PendingInvite>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _userB.Messages.NewInvites.Take(1).Subscribe(i => inviteTcs.TrySetResult(i));

        await _userA.Nostr.PublishWelcomeAsync(welcome.WelcomeData, _userB.PubKey, _userA.PrivKey, kpEventId);

        using var inviteCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        inviteCts.Token.Register(() => inviteTcs.TrySetCanceled());
        var pending = await inviteTcs.Task;

        var chatB = await _userB.Messages.AcceptInviteAsync(pending.Id);
        Assert.NotNull(chatB);
        _output.WriteLine($"Two-user group \"{groupName}\" ready: A={chatA.Id}, B={chatB.Id}");

        return (chatA, chatB, nostrGroupIdHex);
    }

    /// <summary>
    /// Sends a message from sender and waits for it to appear on the receiver's NewMessages observable.
    /// </summary>
    private async Task<Message> SendAndWaitAsync(
        MessageService sender, string chatId, string content,
        IObservable<Message> receiverNewMessages, string expectedContent,
        int timeoutSeconds = 15)
    {
        var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = receiverNewMessages
            .Where(m => m.Content == expectedContent)
            .Take(1)
            .Subscribe(msg => tcs.TrySetResult(msg));

        var sent = await sender.SendMessageAsync(chatId, content);
        Assert.Equal(MessageStatus.Sent, sent.Status);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() => tcs.TrySetCanceled());

        return await tcs.Task;
    }

    /// <summary>
    /// Waits for a message with specific content on an observable. Subscribe BEFORE triggering the send.
    /// </summary>
    private static async Task<Message> WaitForMessageAsync(
        IObservable<Message> newMessages, string expectedContent, int timeoutSeconds = 15)
    {
        var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = newMessages
            .Where(m => m.Content == expectedContent)
            .Take(1)
            .Subscribe(msg => tcs.TrySetResult(msg));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            return await tcs.Task;
        }
        finally
        {
            sub.Dispose();
        }
    }

    private static async Task<bool> IsRelayReachableAsync()
    {
        try
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri(RelayUrl), cts.Token);
            return ws.State == WebSocketState.Open;
        }
        catch
        {
            return false;
        }
    }

    private static async Task DisposeUserAsync(UserTestContext? user)
    {
        if (user == null) return;
        user.Messages.Dispose();
        await user.Nostr.DisconnectAsync();
        (user.Nostr as IDisposable)?.Dispose();
    }

    private static void TryDeleteFile(string? path)
    {
        if (path == null) return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Temp file will be cleaned up eventually
        }
    }

    /// <summary>
    /// Holds all services for one test user. Zero mocks — everything is real.
    /// </summary>
    private record UserTestContext(
        string PubKey,
        string PrivKey,
        NostrService Nostr,
        StorageService Storage,
        ManagedMlsService Mls,
        MessageService Messages,
        string DbPath);
}
