using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.Services;
using Scramble.UI.Tests.TestHelpers;
using Scramble.Presentation.ViewModels;
using Scramble.UI.Views;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Headless Avalonia integration tests using real MLS services (Rust or Managed)
/// instead of mocked IMlsService. Uses real StorageService and MessageService
/// with a mocked INostrService (no relay needed).
///
/// Each test runs with both "rust" and "managed" backends via [AvaloniaTheory].
/// </summary>
public class HeadlessRealMlsIntegrationTests : HeadlessTestBase
{

    // ═══════════════════════════════════════════════════════════════════
    // Test 1: Login flow triggers main UI visibility
    // ═══════════════════════════════════════════════════════════════════

    [InlineData("rust")]
    [InlineData("managed")]
    [AvaloniaTheory]
    public async Task LoginFlow_SetsIsLoggedIn_MainUIBecomesVisible(string backend)
    {
        if (backend == "rust" && !NativeDllAvailable()) return; // Skip when native DLL absent
        var ctx = await CreateRealContext(backend, saveUser: false);

        var mainVm = new MainViewModel(ctx.MessageService, ctx.MockNostr.Object, ctx.Storage, ctx.MlsService, ctx.MockClipboard.Object, ctx.MockQrGenerator.Object, ctx.MockLauncher.Object);
        Dispatcher.UIThread.RunJobs();

        Assert.False(mainVm.IsLoggedIn);

        // Save user to storage and simulate login
        await ctx.Storage.SaveCurrentUserAsync(ctx.User);
        mainVm.CurrentUser = ctx.User;
        mainVm.IsLoggedIn = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(mainVm.IsLoggedIn);
        Assert.Equal(ctx.User, mainVm.CurrentUser);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 2: MainWindow renders and login view is visible when not logged in
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task MainWindow_WhenNotLoggedIn_LoginViewIsVisible(string backend)
    {
        if (backend == "rust" && !NativeDllAvailable()) return;
        var ctx = await CreateRealContext(backend, saveUser: false);

        var mainVm = new MainViewModel(ctx.MessageService, ctx.MockNostr.Object, ctx.Storage, ctx.MlsService, ctx.MockClipboard.Object, ctx.MockQrGenerator.Object, ctx.MockLauncher.Object);
        var window = new MainWindow { DataContext = mainVm };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(mainVm.IsLoggedIn);

        window.Close();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 3: Pending invites appear in ChatListViewModel via observable
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaTheory(Skip = "Obsolete: since commit 706fd66, MessageService filters welcomes via CanProcessWelcomeAsync before saving a PendingInvite. This test pushes random bytes which are now correctly rejected. Needs a real MLS key-package + welcome flow to reinstate.")]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task PendingInvite_ArrivesViaObservable_AppearsInChatList(string backend)
    {
        if (backend == "rust" && !NativeDllAvailable()) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(chatListVm.PendingInvites);

        // Simulate a welcome event arriving via the mocked NostrService
        var invite = new PendingInvite
        {
            Id = "invite-1",
            SenderPublicKey = "cc".PadLeft(64, 'c'),
            GroupId = "deadbeef",
            WelcomeData = new byte[64],
            NostrEventId = "event123",
            ReceivedAt = DateTime.UtcNow
        };

        // Deliver via the real MessageService's NewInvites observable
        // by pushing a kind-444 event through the mocked NostrService events
        var fakeWelcomeEventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var welcomeEvent = new NostrEventReceived
        {
            Kind = 444,
            EventId = fakeWelcomeEventId,
            PublicKey = "cc".PadLeft(64, 'c'),
            Content = Convert.ToBase64String(new byte[64]),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "p", ctx.User.PublicKeyHex },
                new() { "h", "deadbeef" }
            },
            RelayUrl = "wss://test.relay"
        };

        ctx.EventsSubject.OnNext(welcomeEvent);
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(chatListVm.PendingInvites);
        Assert.Equal(1, chatListVm.PendingInviteCount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 4: Chat selection loads chat in ChatViewModel
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ChatSelection_LoadsChatInChatViewModel(string backend)
    {
        if (backend == "rust" && !NativeDllAvailable()) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var mainVm = new MainViewModel(ctx.MessageService, ctx.MockNostr.Object, ctx.Storage, ctx.MlsService, ctx.MockClipboard.Object, ctx.MockQrGenerator.Object, ctx.MockLauncher.Object);
        Dispatcher.UIThread.RunJobs();

        var chat = new Chat
        {
            Id = "chat-select-test",
            Name = "Selected Chat",
            Type = ChatType.Group,
            MlsGroupId = new byte[16],
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);

        var chatItem = new ChatItemViewModel(chat);
        mainVm.ChatListViewModel.Chats.Add(chatItem);
        mainVm.ChatListViewModel.SelectedChat = chatItem;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Selected Chat", mainVm.ChatViewModel.ChatName);
        Assert.True(mainVm.ChatViewModel.HasChat);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 5: New Group dialog opens and group name is bindable
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task NewGroupDialog_OpensAndBindsGroupName(string backend)
    {
        if (backend == "rust" && !NativeDllAvailable()) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);

        Assert.False(chatListVm.ShowNewChatDialog);

        chatListVm.NewChatCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatListVm.ShowNewChatDialog);

        chatListVm.NewChatName = "My Test Group";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("My Test Group", chatListVm.NewChatName);

        chatListVm.CancelNewChatCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.False(chatListVm.ShowNewChatDialog);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 6: Settings view navigation
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SettingsNavigation_TogglesCurrentView(string backend)
    {
        if (backend == "rust" && !NativeDllAvailable()) return;
        var ctx = await CreateRealContext(backend);

        var mainVm = new MainViewModel(ctx.MessageService, ctx.MockNostr.Object, ctx.Storage, ctx.MlsService, ctx.MockClipboard.Object, ctx.MockQrGenerator.Object, ctx.MockLauncher.Object);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(mainVm.CurrentView);

        mainVm.ShowSettingsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(mainVm.CurrentView);
        Assert.IsType<SettingsViewModel>(mainVm.CurrentView);

        mainVm.ShowChatsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(mainVm.CurrentView);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 7: Full flow - Login → Create Group → See it in chat list
    //         (exercises REAL MLS group creation)
    // ═══════════════════════════════════════════════════════════════════

    [InlineData("rust")]
    [InlineData("managed")]
    [AvaloniaTheory]
    public async Task FullFlow_Login_CreateGroup_AppearsInChatList(string backend)
    {
        if (backend == "rust" && !NativeDllAvailable()) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // CreateNewChatAsync requires SelectableRelays (populated from ConnectedRelayUrls).
        ctx.MockNostr.Setup(n => n.ConnectedRelayUrls).Returns(new List<string> { "wss://relay.test" });

        var mainVm = new MainViewModel(ctx.MessageService, ctx.MockNostr.Object, ctx.Storage, ctx.MlsService, ctx.MockClipboard.Object, ctx.MockQrGenerator.Object, ctx.MockLauncher.Object);
        Dispatcher.UIThread.RunJobs();

        // Login
        mainVm.CurrentUser = ctx.User;
        mainVm.IsLoggedIn = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(mainVm.IsLoggedIn);

        // ShellViewModel normally drives this post-login initialization;
        // tests bypass the shell, so trigger it explicitly.
        await mainVm.InitializeAfterLoginAsync();
        Dispatcher.UIThread.RunJobs();

        // Open new group dialog (triggers PopulateSelectableRelays).
        await mainVm.ChatListViewModel.NewChatCommand.Execute();
        Dispatcher.UIThread.RunJobs();
        Assert.True(mainVm.ChatListViewModel.ShowNewChatDialog);

        // Add a participant — CreateNewChatAsync early-returns otherwise. The
        // mocked FetchKeyPackagesAsync returns empty, so the invite will fail
        // and end up in inviteErrors, but the group itself is still created
        // and inserted into Chats (the assertion target).
        mainVm.ChatListViewModel.NewChatParticipants.Add(
            new FollowContactViewModel("cc".PadLeft(64, 'c'), "npub1other", null, null, null));
        mainVm.ChatListViewModel.NewChatName = "Real MLS Group";
        // Description flips ChatItemViewModel.IsGroup to true even when invites
        // fail and the participant list ends up with just the creator.
        mainVm.ChatListViewModel.NewChatDescription = "real-mls integration test group";
        Dispatcher.UIThread.RunJobs();

        await mainVm.ChatListViewModel.CreateChatCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        // Group should appear in chat list (real MLS group was created).
        Assert.NotEmpty(mainVm.ChatListViewModel.Chats);
        Assert.Contains(mainVm.ChatListViewModel.Chats, c => c.Name == "Real MLS Group");
        Assert.True(mainVm.ChatListViewModel.Chats.First(c => c.Name == "Real MLS Group").IsGroup);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 8: Reset Group removes chat from list and triggers rescan
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ResetGroup_RemovesChatFromList(string backend)
    {
        if (backend == "rust" && !NativeDllAvailable()) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Create a real group via MLS
        var groupInfo = await ctx.MlsService.CreateGroupAsync("Corrupted Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Corrupted Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            WelcomeNostrEventId = "welcome-event-abc123",
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        await chatListVm.LoadChatsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(chatListVm.Chats);
        Assert.Equal("Corrupted Group", chatListVm.Chats[0].Name);

        // Trigger reset dialog
        var chatItem = chatListVm.Chats[0];
        await chatListVm.ResetGroupCommand.Execute(chatItem);
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatListVm.ShowResetGroupDialog);
        Assert.Equal("Corrupted Group", chatListVm.GroupToReset?.Name);

        // Confirm the reset
        await chatListVm.ConfirmResetGroupCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(chatListVm.Chats);
        Assert.False(chatListVm.ShowResetGroupDialog);
        Assert.Null(chatListVm.GroupToReset);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 9: Cancel Reset Group keeps chat in list
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task CancelResetGroup_KeepsChatInList(string backend)
    {
        if (backend == "rust" && !NativeDllAvailable()) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var groupInfo = await ctx.MlsService.CreateGroupAsync("Keep This Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Keep This Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        await chatListVm.LoadChatsAsync();
        Dispatcher.UIThread.RunJobs();

        var chatItem = chatListVm.Chats[0];
        await chatListVm.ResetGroupCommand.Execute(chatItem);
        Dispatcher.UIThread.RunJobs();
        Assert.True(chatListVm.ShowResetGroupDialog);

        await chatListVm.CancelResetGroupCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(chatListVm.Chats);
        Assert.Equal("Keep This Group", chatListVm.Chats[0].Name);
        Assert.False(chatListVm.ShowResetGroupDialog);
        Assert.Null(chatListVm.GroupToReset);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 10: Decryption error surfaces status message
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task DecryptionError_SurfacesStatusMessage(string backend)
    {
        if (backend == "rust" && !NativeDllAvailable()) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        Dispatcher.UIThread.RunJobs();

        // Create a group and a chat so we can simulate a bad message
        var groupInfo = await ctx.MlsService.CreateGroupAsync("Broken Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Broken Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);

        // Deliver an undecryptable kind-445 message to trigger a decryption error
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        var badMessageEvent = new NostrEventReceived
        {
            Kind = 445,
            EventId = "bad_" + Guid.NewGuid().ToString("N"),
            PublicKey = "ee".PadLeft(64, 'e'),
            Content = Convert.ToBase64String(new byte[64]), // garbage ciphertext
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", groupIdHex } },
            RelayUrl = "wss://test.relay"
        };

        ctx.EventsSubject.OnNext(badMessageEvent);
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        // Status message should mention the group name and reset
        Assert.NotNull(chatListVm.StatusMessage);
        Assert.Contains("Broken Group", chatListVm.StatusMessage);
        Assert.Contains("reset", chatListVm.StatusMessage.ToLower());
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 11: ChatListView renders with pending invites (view-level)
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaTheory(Skip = "Obsolete: since commit 706fd66, MessageService filters welcomes via CanProcessWelcomeAsync before saving a PendingInvite. This test pushes random bytes which are now correctly rejected. Needs a real MLS key-package + welcome flow to reinstate.")]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ChatListView_RendersPendingInvites(string backend)
    {
        if (backend == "rust" && !NativeDllAvailable()) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        var chatListView = new ChatListView { DataContext = chatListVm };
        var window = new Window { Content = chatListView, Width = 400, Height = 600 };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Deliver a welcome event through the real service pipeline
        var fakeWelcomeEventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var welcomeEvent = new NostrEventReceived
        {
            Kind = 444,
            EventId = fakeWelcomeEventId,
            PublicKey = "dd".PadLeft(64, 'd'),
            Content = Convert.ToBase64String(new byte[64]),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "p", ctx.User.PublicKeyHex },
                new() { "h", "abcd1234" }
            },
            RelayUrl = "wss://test.relay"
        };

        ctx.EventsSubject.OnNext(welcomeEvent);
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(chatListVm.PendingInvites);

        window.Close();
    }

}
