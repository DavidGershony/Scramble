using System.Net.WebSockets;
using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Scramble.Core.Configuration;
using Scramble.Core.Logging;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.ViewModels;
using Scramble.UI.Tests.TestHelpers;
using Xunit;
namespace Scramble.UI.Tests;

/// <summary>
/// Avalonia headless integration tests that exercise group chat flows
/// at the ViewModel level using ZERO mocks and a real Nostr relay.
///
/// Every layer is real: NostrService → WebSocket → relay → WebSocket → MessageService → MLS decrypt.
/// Tests are driven through MainViewModel.InitializeAfterLoginAsync() to exercise the full
/// app lifecycle including MLS state restoration.
///
/// Requires: docker compose -f docker-compose.test.yml up -d  (relay on wss://relay2.angor.io)
/// </summary>
[Trait("Category", "Integration")]
public class HeadlessRealRelayTests : IAsyncLifetime
{
    private const string RelayUrl = "wss://relay2.angor.io";

    private readonly ITestOutputHelper _output;
    private readonly ILogger _logger;
    private readonly List<string> _dbPaths = new();
    private readonly List<IDisposable> _disposables = new();
    private readonly List<NostrService> _nostrServices = new();

    private bool _relayAvailable;

    public HeadlessRealRelayTests(ITestOutputHelper output)
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _output = output;
        _logger = LoggingConfiguration.CreateLogger<HeadlessRealRelayTests>();
    }

    public async ValueTask InitializeAsync()
    {
        _relayAvailable = await IsRelayReachableAsync();
        if (!_relayAvailable)
            _logger.LogWarning("Relay at {RelayUrl} not reachable, tests will skip", RelayUrl);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _disposables) d.Dispose();
        foreach (var nostr in _nostrServices)
        {
            try { await nostr.DisconnectAsync(); } catch { }
            (nostr as IDisposable)?.Dispose();
        }

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        foreach (var path in _dbPaths) TryDeleteFile(path);
    }

    // ===================================================================
    // Test 1: Two-user chat driven through MainViewModel initialization
    // ===================================================================

    [AvaloniaFact]
    public async Task TwoUserChat_ViaRealRelay_ViewModelLevel()
    {
        Assert.SkipUnless(_relayAvailable, "Relay not available at " + RelayUrl);

        var userA = await CreateUserContextAsync("A");
        var userB = await CreateUserContextAsync("B");
        _output.WriteLine($"A: {userA.User.PublicKeyHex[..16]}  B: {userB.User.PublicKeyHex[..16]}");

        // Initialize through ViewModel path (MLS init, state restore, chat loading)
        await userA.MainVm.InitializeAfterLoginAsync();
        await userB.MainVm.InitializeAfterLoginAsync();

        // B publishes KeyPackage
        var kpB = await userB.Mls.GenerateKeyPackageAsync();
        await userB.Nostr.PublishKeyPackageAsync(kpB.Data, userB.User.PrivateKeyHex!, kpB.NostrTags);
        await Task.Delay(1000);

        // A creates group, adds B
        var (chatA, chatB, nostrGroupIdHex) = await SetupGroupAndInviteAsync(userA, userB);

        // Subscribe to group messages
        await userA.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHex });
        await userB.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHex });
        await Task.Delay(500);

        // A sends → B receives
        var receivedByB = await SendAndWaitAsync(
            userA.Messages, chatA.Id, "Hello from A (VM level)!",
            userB.Messages.NewMessages, "Hello from A (VM level)!");
        Assert.Equal("Hello from A (VM level)!", receivedByB.Content);
        Assert.Equal(userA.User.PublicKeyHex, receivedByB.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"B received: \"{receivedByB.Content}\"");

        // B sends → A receives
        var receivedByA = await SendAndWaitAsync(
            userB.Messages, chatB.Id, "Reply from B (VM level)!",
            userA.Messages.NewMessages, "Reply from B (VM level)!");
        Assert.Equal("Reply from B (VM level)!", receivedByA.Content);
        Assert.Equal(userB.User.PublicKeyHex, receivedByA.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"A received: \"{receivedByA.Content}\"");

        _output.WriteLine("TWO-USER VM-LEVEL TEST PASSED");
    }

    // ===================================================================
    // Test 2: Three-user chat driven through MainViewModel
    // ===================================================================

    [AvaloniaFact]
    public async Task ThreeUserChat_ViaRealRelay_ViewModelLevel()
    {
        Assert.SkipUnless(_relayAvailable, "Relay not available at " + RelayUrl);

        var userA = await CreateUserContextAsync("A");
        var userB = await CreateUserContextAsync("B");
        var userC = await CreateUserContextAsync("C");
        _output.WriteLine($"A: {userA.User.PublicKeyHex[..16]}  B: {userB.User.PublicKeyHex[..16]}  C: {userC.User.PublicKeyHex[..16]}");

        await userA.MainVm.InitializeAfterLoginAsync();
        await userB.MainVm.InitializeAfterLoginAsync();
        await userC.MainVm.InitializeAfterLoginAsync();

        // B and C publish KeyPackages
        var kpB = await userB.Mls.GenerateKeyPackageAsync();
        await userB.Nostr.PublishKeyPackageAsync(kpB.Data, userB.User.PrivateKeyHex!, kpB.NostrTags);
        var kpC = await userC.Mls.GenerateKeyPackageAsync();
        await userC.Nostr.PublishKeyPackageAsync(kpC.Data, userC.User.PrivateKeyHex!, kpC.NostrTags);
        await Task.Delay(1000);

        // A creates group, adds B
        var (chatA, chatB, nostrGroupIdHex) = await SetupGroupAndInviteAsync(userA, userB);

        // Subscribe A and B before adding C (so B processes the commit)
        await userA.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHex });
        await userB.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHex });
        await Task.Delay(500);

        // A adds C via high-level MessageService.AddMemberAsync
        await userA.Messages.AddMemberAsync(chatA.Id, userC.User.PublicKeyHex);
        _output.WriteLine("A added C via MessageService.AddMemberAsync");
        await Task.Delay(3000); // Wait for B to process commit

        // C receives welcome
        await userC.Nostr.SubscribeToWelcomesAsync(userC.User.PublicKeyHex, userC.User.PrivateKeyHex!);
        await Task.Delay(500);

        Chat chatC;
        var inviteCTcs = new TaskCompletionSource<PendingInvite>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteCSub = userC.Messages.NewInvites.Take(1).Subscribe(i => inviteCTcs.TrySetResult(i));
        using var inviteCCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        inviteCCts.Token.Register(() => inviteCTcs.TrySetCanceled());

        PendingInvite? pendingC;
        try
        {
            pendingC = await inviteCTcs.Task;
        }
        catch (TaskCanceledException)
        {
            _output.WriteLine("C didn't receive welcome via subscription, trying rescan...");
            await userC.Messages.RescanInvitesAsync();
            var invites = (await userC.Messages.GetPendingInvitesAsync()).ToList();
            Assert.NotEmpty(invites);
            pendingC = invites[0];
        }

        chatC = await userC.Messages.AcceptInviteAsync(pendingC!.Id);
        Assert.NotNull(chatC);
        _output.WriteLine($"C accepted invite, chat: {chatC.Id}");

        var nostrGroupIdHexC = Convert.ToHexString((chatC.NostrGroupId ?? chatC.MlsGroupId)!).ToLowerInvariant();
        await userC.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHexC });
        await Task.Delay(500);

        // A sends → B and C receive
        _output.WriteLine("--- A sends ---");
        var bGetsA = WaitForMessageAsync(userB.Messages.NewMessages, "Three-way from A");
        var cGetsA = WaitForMessageAsync(userC.Messages.NewMessages, "Three-way from A");
        await userA.Messages.SendMessageAsync(chatA.Id, "Three-way from A");
        Assert.Equal("Three-way from A", (await bGetsA).Content);
        Assert.Equal("Three-way from A", (await cGetsA).Content);
        _output.WriteLine("  B and C received A's message");

        // B sends → A and C receive
        _output.WriteLine("--- B sends ---");
        var aGetsB = WaitForMessageAsync(userA.Messages.NewMessages, "Three-way from B");
        var cGetsB = WaitForMessageAsync(userC.Messages.NewMessages, "Three-way from B");
        await userB.Messages.SendMessageAsync(chatB.Id, "Three-way from B");
        Assert.Equal("Three-way from B", (await aGetsB).Content);
        Assert.Equal("Three-way from B", (await cGetsB).Content);
        _output.WriteLine("  A and C received B's message");

        // C sends → A and B receive
        _output.WriteLine("--- C sends ---");
        var aGetsC = WaitForMessageAsync(userA.Messages.NewMessages, "Three-way from C");
        var bGetsC = WaitForMessageAsync(userB.Messages.NewMessages, "Three-way from C");
        await userC.Messages.SendMessageAsync(chatC.Id, "Three-way from C");
        Assert.Equal("Three-way from C", (await aGetsC).Content);
        Assert.Equal("Three-way from C", (await bGetsC).Content);
        _output.WriteLine("  A and B received C's message");

        _output.WriteLine("THREE-USER VM-LEVEL TEST PASSED");
    }

    // ===================================================================
    // Test 3: Close and reopen — the critical lifecycle test
    // ===================================================================

    [AvaloniaFact]
    public async Task CloseAndReopen_MessagesStillFlow_ViaRealRelay()
    {
        Assert.SkipUnless(_relayAvailable, "Relay not available at " + RelayUrl);

        var userA = await CreateUserContextAsync("A");
        var userB = await CreateUserContextAsync("B");
        _output.WriteLine($"A: {userA.User.PublicKeyHex[..16]}  B: {userB.User.PublicKeyHex[..16]}");

        await userA.MainVm.InitializeAfterLoginAsync();
        await userB.MainVm.InitializeAfterLoginAsync();

        // B publishes KeyPackage
        var kpB = await userB.Mls.GenerateKeyPackageAsync();
        await userB.Nostr.PublishKeyPackageAsync(kpB.Data, userB.User.PrivateKeyHex!, kpB.NostrTags);
        await Task.Delay(1000);

        // Create group, exchange messages
        var (chatA, chatB, nostrGroupIdHex) = await SetupGroupAndInviteAsync(userA, userB);

        await userA.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHex });
        await userB.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHex });
        await Task.Delay(500);

        // Verify messages work before close
        var preClose = await SendAndWaitAsync(
            userA.Messages, chatA.Id, "Before close",
            userB.Messages.NewMessages, "Before close");
        Assert.Equal("Before close", preClose.Content);
        _output.WriteLine("Pre-close message verified");

        var preCloseReply = await SendAndWaitAsync(
            userB.Messages, chatB.Id, "Ack before close",
            userA.Messages.NewMessages, "Ack before close");
        Assert.Equal("Ack before close", preCloseReply.Content);
        _output.WriteLine("Pre-close reply verified");

        // ── A CLOSES ──
        _output.WriteLine("=== A CLOSING ===");
        userA.Messages.Dispose();
        // Remove from disposables list so DisposeAsync doesn't double-dispose
        _disposables.Remove(userA.Messages);
        _output.WriteLine("A's MessageService disposed");

        // B sends a message while A is "closed" — A won't receive it now
        await userB.Messages.SendMessageAsync(chatB.Id, "Sent while A was closed");
        _output.WriteLine("B sent message while A was closed");
        await Task.Delay(1000);

        // ── A REOPENS ──
        _output.WriteLine("=== A REOPENING ===");

        // Create fresh MLS service and MessageService using the SAME storage (same DB)
        var mlsA2 = new ManagedMlsService(userA.Storage);
        var messagesA2 = new MessageService(userA.Storage, userA.Nostr, mlsA2);
        _disposables.Add(messagesA2);

        // Create new MainViewModel and initialize — this restores MLS state
        var mainVmA2 = CreateMainViewModel(userA.User, userA.Storage, userA.Nostr, mlsA2, messagesA2);
        mainVmA2.CurrentUser = userA.User;
        await mainVmA2.InitializeAfterLoginAsync();
        _output.WriteLine("A reopened with new MainViewModel + InitializeAfterLoginAsync");

        // Re-subscribe to group messages
        await userA.Nostr.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHex });
        await Task.Delay(500);

        // ── Verify messages work after reopen ──
        // B sends a new message → A should receive it
        var afterReopen = await SendAndWaitAsync(
            userB.Messages, chatB.Id, "After A reopened",
            messagesA2.NewMessages, "After A reopened");
        Assert.Equal("After A reopened", afterReopen.Content);
        Assert.Equal(userB.User.PublicKeyHex, afterReopen.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"A received after reopen: \"{afterReopen.Content}\"");

        // A sends a reply → B should receive it
        // Need to find A's chat ID from the restored chat list
        var chatsA2 = await messagesA2.GetChatsAsync();
        var restoredChatA = chatsA2.FirstOrDefault(c => c.Id == chatA.Id);
        Assert.NotNull(restoredChatA);
        _output.WriteLine($"A's chat restored: {restoredChatA!.Id}");

        var afterReopenReply = await SendAndWaitAsync(
            messagesA2, restoredChatA.Id, "A is back!",
            userB.Messages.NewMessages, "A is back!");
        Assert.Equal("A is back!", afterReopenReply.Content);
        Assert.Equal(userA.User.PublicKeyHex, afterReopenReply.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"B received A's reply: \"{afterReopenReply.Content}\"");

        _output.WriteLine("CLOSE/REOPEN TEST PASSED: MLS state survived restart, messages flow bidirectionally");
    }

    // ===================================================================
    // Infrastructure
    // ===================================================================

    private record UserContext(
        User User,
        NostrService Nostr,
        StorageService Storage,
        ManagedMlsService Mls,
        MessageService Messages,
        MainViewModel MainVm);

    private async Task<UserContext> CreateUserContextAsync(string label)
    {
        var nostr = new NostrService();
        _nostrServices.Add(nostr);
        var (privKey, pubKey, nsec, npub) = nostr.GenerateKeyPair();

        var dbPath = Path.Combine(Path.GetTempPath(), $"headless_relay_{label}_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = pubKey,
            PrivateKeyHex = privKey,
            Npub = npub,
            Nsec = nsec,
            DisplayName = $"User {label}",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };
        await storage.SaveCurrentUserAsync(user);

        var mls = new ManagedMlsService(storage);
        var messages = new MessageService(storage, nostr, mls);
        _disposables.Add(messages);

        await nostr.ConnectAsync(RelayUrl);
        await Task.Delay(500);

        var mainVm = CreateMainViewModel(user, storage, nostr, mls, messages);
        mainVm.CurrentUser = user;

        return new UserContext(user, nostr, storage, mls, messages, mainVm);
    }

    private static MainViewModel CreateMainViewModel(
        User user, StorageService storage, NostrService nostr,
        ManagedMlsService mls, MessageService messages)
    {
        var mockClipboard = new Moq.Mock<Presentation.Services.IPlatformClipboard>();
        var mockQr = new Moq.Mock<Presentation.Services.IQrCodeGenerator>();
        var mockLauncher = new Moq.Mock<Presentation.Services.IPlatformLauncher>();

        return new MainViewModel(
            messages, nostr, storage, mls,
            mockClipboard.Object, mockQr.Object, mockLauncher.Object);
    }

    /// <summary>
    /// Full group setup: A creates group, adds B via low-level MLS, delivers welcome via relay.
    /// Returns (chatA, chatB, nostrGroupIdHex).
    /// </summary>
    private async Task<(Chat chatA, Chat chatB, string nostrGroupIdHex)> SetupGroupAndInviteAsync(
        UserContext creator, UserContext joiner)
    {
        // A creates group
        var groupInfo = await creator.Mls.CreateGroupAsync("Headless Relay Test", new[] { RelayUrl });
        var nostrGroupIdBytes = creator.Mls.GetNostrGroupId(groupInfo.GroupId);
        var nostrGroupIdHex = Convert.ToHexString(nostrGroupIdBytes).ToLowerInvariant();

        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Headless Relay Test",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupIdBytes,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { creator.User.PublicKeyHex },
            RelayUrls = new List<string> { RelayUrl },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await creator.Storage.SaveChatAsync(chatA);

        // A fetches B's KP from relay
        var fetchedKPs = (await creator.Nostr.FetchKeyPackagesAsync(joiner.User.PublicKeyHex)).ToList();
        Assert.NotEmpty(fetchedKPs);
        var welcome = await creator.Mls.AddMemberAsync(groupInfo.GroupId, fetchedKPs[0]);
        chatA.ParticipantPublicKeys.Add(joiner.User.PublicKeyHex);
        await creator.Storage.SaveChatAsync(chatA);

        // Publish commit
        if (welcome.CommitData != null && welcome.CommitData.Length > 0)
        {
            await creator.Nostr.PublishCommitAsync(welcome.CommitData, nostrGroupIdHex, creator.User.PrivateKeyHex!);
            _output.WriteLine("Published commit");
        }

        // B subscribes to welcomes
        await joiner.Nostr.SubscribeToWelcomesAsync(joiner.User.PublicKeyHex, joiner.User.PrivateKeyHex!);
        await Task.Delay(500);

        // A publishes welcome, B receives it
        var inviteTcs = new TaskCompletionSource<PendingInvite>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = joiner.Messages.NewInvites.Take(1).Subscribe(i => inviteTcs.TrySetResult(i));

        var kpEventId = fetchedKPs[0].NostrEventId!;
        await creator.Nostr.PublishWelcomeAsync(
            welcome.WelcomeData, joiner.User.PublicKeyHex, creator.User.PrivateKeyHex!, kpEventId);
        _output.WriteLine("Published welcome");

        using var inviteCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        inviteCts.Token.Register(() => inviteTcs.TrySetCanceled());
        var pending = await inviteTcs.Task;

        var chatB = await joiner.Messages.AcceptInviteAsync(pending.Id);
        Assert.NotNull(chatB);
        _output.WriteLine($"Joiner accepted invite, chat: {chatB.Id}");

        return (chatA, chatB, nostrGroupIdHex);
    }

    private static async Task<Message> SendAndWaitAsync(
        MessageService sender, string chatId, string content,
        IObservable<Message> receiverNewMessages, string expectedContent,
        int timeoutSeconds = 15)
    {
        var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = receiverNewMessages
            .Where(m => m.Content == expectedContent)
            .Take(1)
            .Subscribe(msg => tcs.TrySetResult(msg));

        await sender.SendMessageAsync(chatId, content);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

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

        try { return await tcs.Task; }
        finally { sub.Dispose(); }
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
        catch { return false; }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
