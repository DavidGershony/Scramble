using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Headless tests for voice messages, media messages, fetch profile, and load older messages.
/// </summary>
public class HeadlessMessagingExtendedTests : HeadlessTestBase
{
    private async Task<(RealTestContext Ctx, Chat Chat)> CreateGroupWithMls(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var groupInfo = await ctx.MlsService.CreateGroupAsync("Messaging Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Messaging Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);

        return (ctx, chat);
    }

    // --- Send Voice Message ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SendVoiceMessage_SavesAudioMessage(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMls(backend);

        await ctx.MessageService.SendVoiceMessageAsync(
            chat.Id,
            content: "",
            mediaUrl: "https://blossom.test/voice123",
            sha256Hex: "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            nonceHex: "0102030405060708090a0b0c",
            mimeType: "audio/ogg",
            filename: "voice.ogg",
            durationSeconds: 5.2);

        var messages = await ctx.Storage.GetMessagesForChatAsync(chat.Id);
        Assert.Single(messages);

        var msg = messages.First();
        Assert.Equal(MessageType.Audio, msg.Type);
        Assert.Equal("https://blossom.test/voice123", msg.ImageUrl);
        Assert.Equal("audio/ogg", msg.MediaType);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SendVoiceMessage_PublishesEncryptedEvent(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMls(backend);

        await ctx.MessageService.SendVoiceMessageAsync(
            chat.Id,
            content: "",
            mediaUrl: "https://blossom.test/voice456",
            sha256Hex: "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            nonceHex: "0102030405060708090a0b0c",
            mimeType: "audio/ogg",
            filename: "voice.ogg",
            durationSeconds: 3.0);

        ctx.MockNostr.Verify(n => n.PublishRawEventJsonAsync(It.IsAny<byte[]>()), Times.Once);
    }

    // --- Send Media Message ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SendMediaMessage_Image_SavesCorrectType(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMls(backend);

        await ctx.MessageService.SendMediaMessageAsync(
            chat.Id,
            content: "",
            mediaUrl: "https://blossom.test/img789",
            sha256Hex: "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            nonceHex: "0102030405060708090a0b0c",
            mimeType: "image/png",
            filename: "photo.png",
            messageType: MessageType.Image);

        var messages = await ctx.Storage.GetMessagesForChatAsync(chat.Id);
        Assert.Single(messages);

        var msg = messages.First();
        Assert.Equal(MessageType.Image, msg.Type);
        Assert.Equal("https://blossom.test/img789", msg.ImageUrl);
        Assert.Equal("image/png", msg.MediaType);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SendMediaMessage_File_SavesCorrectType(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMls(backend);

        await ctx.MessageService.SendMediaMessageAsync(
            chat.Id,
            content: "",
            mediaUrl: "https://blossom.test/file001",
            sha256Hex: "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            nonceHex: "0102030405060708090a0b0c",
            mimeType: "application/pdf",
            filename: "document.pdf",
            messageType: MessageType.File);

        var messages = await ctx.Storage.GetMessagesForChatAsync(chat.Id);
        Assert.Single(messages);

        var msg = messages.First();
        Assert.Equal(MessageType.File, msg.Type);
        Assert.Equal("application/pdf", msg.MediaType);
        Assert.Equal("document.pdf", msg.FileName);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SendMediaMessage_PublishesEncryptedEvent(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMls(backend);

        await ctx.MessageService.SendMediaMessageAsync(
            chat.Id,
            content: "",
            mediaUrl: "https://blossom.test/media001",
            sha256Hex: "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            nonceHex: "0102030405060708090a0b0c",
            mimeType: "image/jpeg",
            filename: "photo.jpg",
            messageType: MessageType.Image);

        ctx.MockNostr.Verify(n => n.PublishRawEventJsonAsync(It.IsAny<byte[]>()), Times.Once);
    }

    // --- Fetch & Cache Profile ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task FetchAndCacheProfile_ReturnsMetadata(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var targetPubKey = "aa".PadLeft(64, 'a');
        ctx.MockNostr.Setup(n => n.FetchUserMetadataAsync(targetPubKey))
            .ReturnsAsync((UserMetadata?)new UserMetadata
            {
                PublicKeyHex = targetPubKey,
                DisplayName = "Alice",
                Name = "alice",
                About = "Test user",
                Picture = "https://example.com/alice.png"
            });

        var metadata = await ctx.MessageService.FetchAndCacheProfileAsync(targetPubKey);

        Assert.NotNull(metadata);
        Assert.Equal("Alice", metadata!.DisplayName);
        Assert.Equal("alice", metadata.Name);
        Assert.Equal("Test user", metadata.About);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task FetchAndCacheProfile_NullOnFetchFailure(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var targetPubKey = "bb".PadLeft(64, 'b');
        ctx.MockNostr.Setup(n => n.FetchUserMetadataAsync(targetPubKey))
            .Returns(Task.FromException<UserMetadata?>(new Exception("Network error")));

        var metadata = await ctx.MessageService.FetchAndCacheProfileAsync(targetPubKey);

        Assert.Null(metadata);
    }

    // --- Load Older Messages ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task LoadOlderMessages_ReturnsResult(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMls(backend);

        // Send some messages first
        await ctx.MessageService.SendMessageAsync(chat.Id, "Message 1");
        await ctx.MessageService.SendMessageAsync(chat.Id, "Message 2");

        var result = await ctx.MessageService.LoadOlderMessagesAsync(
            chat.Id, DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.IsType<LoadOlderMessagesResult>(result);
        // Result may have 0 messages from relay (mock returns nothing), but should not throw
    }
}
