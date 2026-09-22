using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.ViewModels;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Headless tests for chat management: delete, search/filter, unread count, and relay status.
/// </summary>
public class HeadlessChatManagementTests : HeadlessTestBase
{
    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task DeleteChat_RemovesFromList(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Create a group with real MLS
        var groupInfo = await ctx.MlsService.CreateGroupAsync("Doomed Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Doomed Group",
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

        Assert.Single(chatListVm.Chats);

        // Trigger delete dialog
        var chatItem = chatListVm.Chats[0];
        chatListVm.DeleteChatCommand.Execute(chatItem).Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatListVm.ShowDeleteChatDialog);
        Assert.Equal("Doomed Group", chatListVm.ChatToDelete?.Name);

        // Confirm delete
        await chatListVm.ConfirmDeleteChatCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(chatListVm.Chats);
        Assert.False(chatListVm.ShowDeleteChatDialog);
        Assert.Null(chatListVm.ChatToDelete);

        // Verify deleted from storage
        var storedChats = await ctx.Storage.GetAllChatsAsync();
        Assert.Empty(storedChats);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ChatSearch_FiltersChats(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Create multiple groups
        var group1 = await ctx.MlsService.CreateGroupAsync("Alpha Team", new[] { "wss://relay.test" });
        var group2 = await ctx.MlsService.CreateGroupAsync("Beta Squad", new[] { "wss://relay.test" });
        var group3 = await ctx.MlsService.CreateGroupAsync("Alpha Force", new[] { "wss://relay.test" });

        foreach (var (info, name) in new[] { (group1, "Alpha Team"), (group2, "Beta Squad"), (group3, "Alpha Force") })
        {
            await ctx.Storage.SaveChatAsync(new Chat
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Type = ChatType.Group,
                MlsGroupId = info.GroupId,
                MlsEpoch = info.Epoch,
                ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            });
        }

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        await chatListVm.LoadChatsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, chatListVm.Chats.Count);
        Assert.True(chatListVm.Chats.All(c => c.IsVisible));

        // Search for "Alpha" — filter uses 300ms throttle, so wait for it
        chatListVm.SearchText = "Alpha";
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        var visibleChats = chatListVm.Chats.Where(c => c.IsVisible).ToList();
        Assert.Equal(2, visibleChats.Count);
        Assert.All(visibleChats, c => Assert.Contains("Alpha", c.Name));

        // Clear search
        chatListVm.SearchText = "";
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatListVm.Chats.All(c => c.IsVisible));
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task UnreadCount_IncrementsOnNewMessage(string backend)
    {

        // Two users: Alice and Bob in a group, Bob sends, Alice's unread increments
        var alice = await CreateRealContext(backend);
        var bob = await CreateRealContext(backend);

        await alice.MessageService.InitializeAsync();
        await bob.MlsService.InitializeAsync(bob.User.PrivateKeyHex, bob.User.PublicKeyHex);

        // Alice creates group, adds Bob
        var groupInfo = await alice.MlsService.CreateGroupAsync("Unread Test", new[] { "wss://relay.test" });
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        var bobKp = await bob.MlsService.GenerateKeyPackageAsync();
        await PrepareKeyPackageForAddMemberAsync(bobKp, bob);
        var welcome = await alice.MlsService.StageAddMemberAsync(groupInfo.GroupId, bobKp);
        await alice.MlsService.MergeStagedAsync(groupInfo.GroupId);
        var fakeEventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await bob.MlsService.ProcessWelcomeAsync(welcome.WelcomeData, fakeEventId);

        var aliceChat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Unread Test",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            UnreadCount = 0,
            ParticipantPublicKeys = new List<string> { alice.User.PublicKeyHex, bob.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await alice.Storage.SaveChatAsync(aliceChat);

        // Bob encrypts a message
        var encrypted = await bob.MlsService.EncryptMessageAsync(groupInfo.GroupId, "New message!");

        // Deliver to Alice
        var msgEvent = new NostrEventReceived
        {
            Kind = 445,
            EventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            PublicKey = bob.User.PublicKeyHex,
            Content = Convert.ToBase64String(encrypted),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", groupIdHex } },
            RelayUrl = "wss://test.relay"
        };

        alice.EventsSubject.OnNext(msgEvent);
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        // Verify unread count in storage
        var updatedChat = await alice.Storage.GetChatAsync(aliceChat.Id);
        Assert.NotNull(updatedChat);
        Assert.Equal(1, updatedChat!.UnreadCount);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task LoadMoreMessages_PaginatesCorrectly(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Create group and send several messages
        var groupInfo = await ctx.MlsService.CreateGroupAsync("Pagination Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Pagination Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);

        // Send 5 messages
        for (int i = 1; i <= 5; i++)
        {
            await ctx.MessageService.SendMessageAsync(chat.Id, $"Message {i}");
        }

        var mainVm = CreateMainViewModel(ctx);
        Dispatcher.UIThread.RunJobs();
        mainVm.CurrentUser = ctx.User;
        mainVm.IsLoggedIn = true;
        Dispatcher.UIThread.RunJobs();

        // Load chat — should load initial batch of messages
        mainVm.ChatListViewModel.Chats.Add(new ChatItemViewModel(chat));
        mainVm.ChatListViewModel.SelectedChat = mainVm.ChatListViewModel.Chats[0];
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(5, mainVm.ChatViewModel.Messages.Count);
        Assert.Contains(mainVm.ChatViewModel.Messages, m => m.Content == "Message 1");
        Assert.Contains(mainVm.ChatViewModel.Messages, m => m.Content == "Message 5");
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ContactMetadataPanel_OpensAndShowsInfo(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Mock metadata for a contact
        var contactPubKey = "ff".PadLeft(64, 'f');
        ctx.MockNostr.Setup(n => n.FetchUserMetadataAsync(contactPubKey))
            .ReturnsAsync(new UserMetadata
            {
                DisplayName = "Contact Person",
                About = "A test contact",
                Name = "contact_person"
            });

        var groupInfo = await ctx.MlsService.CreateGroupAsync("Metadata Test", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Metadata Test",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex, contactPubKey },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);

        var mainVm = CreateMainViewModel(ctx);
        Dispatcher.UIThread.RunJobs();
        mainVm.CurrentUser = ctx.User;
        mainVm.IsLoggedIn = true;
        Dispatcher.UIThread.RunJobs();

        mainVm.ChatListViewModel.Chats.Add(new ChatItemViewModel(chat));
        mainVm.ChatListViewModel.SelectedChat = mainVm.ChatListViewModel.Chats[0];
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.False(mainVm.ChatViewModel.ShowMetadataPanel);

        // Toggle metadata panel
        mainVm.ChatViewModel.ToggleMetadataPanelCommand.Execute().Subscribe();
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        Assert.True(mainVm.ChatViewModel.ShowMetadataPanel);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task LoadChats_UppercaseParticipantKeys_NotMarkedOrphan(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Simulate what happens on mobile: MLS-derived participant keys are UPPERCASE
        // (from Convert.ToHexString) but user's PublicKeyHex is lowercase.
        var uppercasePubKey = ctx.User.PublicKeyHex.ToUpperInvariant();

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Group",
            Type = ChatType.Group,
            ParticipantPublicKeys = new List<string> { uppercasePubKey, "AABBCCDD11223344" },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        await chatListVm.LoadChatsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(chatListVm.Chats);
        Assert.False(chatListVm.Chats[0].NeedsRepair,
            $"Chat should NOT be orphaned when participant key '{uppercasePubKey}' matches user key '{ctx.User.PublicKeyHex}' (case-insensitive)");
    }
}
