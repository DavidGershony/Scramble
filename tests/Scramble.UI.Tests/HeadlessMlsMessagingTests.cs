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
/// Headless tests for MLS-encrypted messaging: send, receive, and multi-group switching.
/// Uses two real MLS contexts for cross-user encrypt/decrypt.
/// </summary>
public class HeadlessMlsMessagingTests : HeadlessTestBase
{
    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SendMessage_InGroup_EncryptsViaRealMls(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Create a real MLS group
        var groupInfo = await ctx.MlsService.CreateGroupAsync("Encrypt Test", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Encrypt Test",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);

        // Send a message through MessageService — this exercises real MLS encryption
        var message = await ctx.MessageService.SendMessageAsync(chat.Id, "Hello encrypted world!");

        Assert.Equal(MessageStatus.Sent, message.Status);
        Assert.Equal("Hello encrypted world!", message.Content);
        Assert.NotNull(message.NostrEventId);

        // Verify PublishRawEventJsonAsync was called with encrypted event JSON bytes
        ctx.MockNostr.Verify(n => n.PublishRawEventJsonAsync(
            It.Is<byte[]>(data => data.Length > 0)), Moq.Times.Once);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ReceiveGroupMessage_DecryptsAndAppearsInChat(string backend)
    {
        if (ShouldSkip(backend)) return;

        // Two users: Alice creates group, adds Bob, Bob sends message, Alice decrypts
        var alice = await CreateRealContext(backend);
        var bob = await CreateRealContext(backend);

        await alice.MessageService.InitializeAsync();
        await bob.MlsService.InitializeAsync(bob.User.PrivateKeyHex, bob.User.PublicKeyHex);

        // Alice creates group
        var groupInfo = await alice.MlsService.CreateGroupAsync("Two User Chat", new[] { "wss://relay.test" });
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();

        // Bob generates KeyPackage
        var bobKp = await bob.MlsService.GenerateKeyPackageAsync();
        await PrepareKeyPackageForAddMemberAsync(bobKp, bob);

        // Alice adds Bob
        var welcome = await alice.MlsService.StageAddMemberAsync(groupInfo.GroupId, bobKp);
        await alice.MlsService.MergeStagedAsync(groupInfo.GroupId);

        // Bob processes Welcome
        var fakeWelcomeEventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var bobGroupInfo = await bob.MlsService.ProcessWelcomeAsync(welcome.WelcomeData, fakeWelcomeEventId);

        // Save chat for Alice
        var aliceChat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Two User Chat",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { alice.User.PublicKeyHex, bob.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await alice.Storage.SaveChatAsync(aliceChat);

        // Bob encrypts a message
        var encryptedData = await bob.MlsService.EncryptMessageAsync(bobGroupInfo.GroupId, "Hello from Bob!");

        // Set up ChatViewModel to observe new messages (without MainViewModel login flow
        // which would re-initialize MLS and lose the group state)
        var chatVm = new ChatViewModel(alice.MessageService, alice.Storage,
            alice.MockNostr.Object, alice.MlsService, alice.MockClipboard.Object);
        chatVm.LoadChat(aliceChat);
        Dispatcher.UIThread.RunJobs();

        // Deliver encrypted message to Alice via NostrService events
        var messageEvent = new NostrEventReceived
        {
            Kind = 445,
            EventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            PublicKey = bob.User.PublicKeyHex,
            Content = Convert.ToBase64String(encryptedData),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", groupIdHex } },
            RelayUrl = "wss://test.relay"
        };

        alice.EventsSubject.OnNext(messageEvent);
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        // Message should be decrypted and visible
        Assert.NotEmpty(chatVm.Messages);
        var received = chatVm.Messages.First(m => m.Content == "Hello from Bob!");
        Assert.Equal("Hello from Bob!", received.Content);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task MultipleGroups_SwitchBetween_LoadsCorrectMessages(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Create two groups
        var group1Info = await ctx.MlsService.CreateGroupAsync("Group Alpha", new[] { "wss://relay.test" });
        var group2Info = await ctx.MlsService.CreateGroupAsync("Group Beta", new[] { "wss://relay.test" });

        var chat1 = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Group Alpha",
            Type = ChatType.Group,
            MlsGroupId = group1Info.GroupId,
            MlsEpoch = group1Info.Epoch,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        var chat2 = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Group Beta",
            Type = ChatType.Group,
            MlsGroupId = group2Info.GroupId,
            MlsEpoch = group2Info.Epoch,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat1);
        await ctx.Storage.SaveChatAsync(chat2);

        // Send messages to each group
        await ctx.MessageService.SendMessageAsync(chat1.Id, "Alpha message 1");
        await ctx.MessageService.SendMessageAsync(chat1.Id, "Alpha message 2");
        await ctx.MessageService.SendMessageAsync(chat2.Id, "Beta message 1");

        var mainVm = CreateMainViewModel(ctx);
        Dispatcher.UIThread.RunJobs();
        mainVm.CurrentUser = ctx.User;
        mainVm.IsLoggedIn = true;
        Dispatcher.UIThread.RunJobs();

        // Load chats
        await mainVm.ChatListViewModel.LoadChatsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, mainVm.ChatListViewModel.Chats.Count);

        // Select Group Alpha
        var alphaItem = mainVm.ChatListViewModel.Chats.First(c => c.Name == "Group Alpha");
        mainVm.ChatListViewModel.SelectedChat = alphaItem;
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Group Alpha", mainVm.ChatViewModel.ChatName);
        Assert.Equal(2, mainVm.ChatViewModel.Messages.Count);
        Assert.Contains(mainVm.ChatViewModel.Messages, m => m.Content == "Alpha message 1");
        Assert.Contains(mainVm.ChatViewModel.Messages, m => m.Content == "Alpha message 2");

        // Switch to Group Beta
        var betaItem = mainVm.ChatListViewModel.Chats.First(c => c.Name == "Group Beta");
        mainVm.ChatListViewModel.SelectedChat = betaItem;
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Group Beta", mainVm.ChatViewModel.ChatName);
        Assert.Single(mainVm.ChatViewModel.Messages);
        Assert.Equal("Beta message 1", mainVm.ChatViewModel.Messages[0].Content);
    }
}
