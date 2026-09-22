using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.ViewModels;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Headless tests for message operations: mark as read, mute, archive, delete, reply, reactions.
/// </summary>
public class HeadlessMessageOperationsTests : HeadlessTestBase
{
    private async Task<(RealTestContext Ctx, Chat Chat)> CreateGroupWithMessages(string backend, int messageCount = 3)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var groupInfo = await ctx.MlsService.CreateGroupAsync("Test Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);

        for (int i = 1; i <= messageCount; i++)
        {
            await ctx.MessageService.SendMessageAsync(chat.Id, $"Message {i}");
        }

        return (ctx, chat);
    }

    // --- Mark as Read ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task MarkAsRead_ClearsUnreadCount(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMessages(backend);

        // Simulate unread count
        chat.UnreadCount = 5;
        await ctx.Storage.SaveChatAsync(chat);

        var stored = await ctx.Storage.GetChatAsync(chat.Id);
        Assert.Equal(5, stored!.UnreadCount);

        await ctx.MessageService.MarkAsReadAsync(chat.Id);

        stored = await ctx.Storage.GetChatAsync(chat.Id);
        Assert.Equal(0, stored!.UnreadCount);
    }

    // --- Mute / Unmute ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SetMuted_TogglesCorrectly(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMessages(backend, 0);

        Assert.False(chat.IsMuted);

        await ctx.MessageService.SetMutedAsync(chat.Id, true);
        var stored = await ctx.Storage.GetChatAsync(chat.Id);
        Assert.True(stored!.IsMuted);

        await ctx.MessageService.SetMutedAsync(chat.Id, false);
        stored = await ctx.Storage.GetChatAsync(chat.Id);
        Assert.False(stored!.IsMuted);
    }

    // --- Archive ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ArchiveChat_SetsArchivedFlag(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMessages(backend, 0);

        await ctx.MessageService.ArchiveChatAsync(chat.Id);

        var stored = await ctx.Storage.GetChatAsync(chat.Id);
        Assert.True(stored!.IsArchived);
    }

    // --- Delete Message ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task DeleteMessage_RemovesFromStorage(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMessages(backend, 1);

        var messages = await ctx.Storage.GetMessagesForChatAsync(chat.Id);
        Assert.Single(messages);

        var messageId = messages.First().Id;
        await ctx.MessageService.DeleteMessageAsync(messageId);

        messages = await ctx.Storage.GetMessagesForChatAsync(chat.Id);
        Assert.Empty(messages);
    }

    // --- Send Reply ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SendReply_LinksToOriginalMessage(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMessages(backend, 1);

        var messages = await ctx.Storage.GetMessagesForChatAsync(chat.Id);
        var originalId = messages.First().Id;

        var reply = await ctx.MessageService.SendReplyAsync(chat.Id, "This is a reply", originalId);

        Assert.Equal("This is a reply", reply.Content);
        Assert.Equal(originalId, reply.ReplyToMessageId);

        // Verify persisted
        var storedMessages = await ctx.Storage.GetMessagesForChatAsync(chat.Id);
        var storedReply = storedMessages.FirstOrDefault(m => m.Id == reply.Id);
        Assert.NotNull(storedReply);
        Assert.Equal(originalId, storedReply!.ReplyToMessageId);
    }

    // --- Reactions ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task AddReaction_StoresReaction(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMessages(backend, 1);

        var messages = await ctx.Storage.GetMessagesForChatAsync(chat.Id);
        var messageId = messages.First().Id;

        await ctx.MessageService.AddReactionAsync(messageId, "👍");

        var storedMessages = await ctx.Storage.GetMessagesForChatAsync(chat.Id);
        var msg = storedMessages.First(m => m.Id == messageId);
        Assert.True(msg.Reactions.ContainsKey("👍"));
        Assert.Contains(ctx.User.PublicKeyHex, msg.Reactions["👍"]);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task RemoveReaction_DeletesReaction(string backend)
    {
        var (ctx, chat) = await CreateGroupWithMessages(backend, 1);

        var messages = await ctx.Storage.GetMessagesForChatAsync(chat.Id);
        var messageId = messages.First().Id;

        await ctx.MessageService.AddReactionAsync(messageId, "👍");
        await ctx.MessageService.RemoveReactionAsync(messageId, "👍");

        var storedMessages = await ctx.Storage.GetMessagesForChatAsync(chat.Id);
        var msg = storedMessages.First(m => m.Id == messageId);
        Assert.False(msg.Reactions.ContainsKey("👍") && msg.Reactions["👍"].Contains(ctx.User.PublicKeyHex));
    }
}
