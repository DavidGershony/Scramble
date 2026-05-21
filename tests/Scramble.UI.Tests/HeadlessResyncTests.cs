using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.ViewModels;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Tests for the out-of-sync / resync feature:
///
/// Production changes covered:
///   - MessageService.RequestResyncAsync — posts resync request to DeviceSync, sets IsResyncPending
///   - MessageService.AnnounceDeviceToSyncGroupAsync — posts device announcement to DeviceSync
///   - ChatViewModel.IsOutOfSync / IsResyncPending — bound from Chat model
///   - ChatViewModel.ResyncCommand — calls RequestResyncAsync
///   - ChatViewModel ChatUpdates subscription — updates VM flags on chat updates
///   - StorageService — persists IsOutOfSync/IsResyncPending in Chats table
///
/// IsOutOfSync and IsResyncPending are persisted in the SQLite Chats table.
/// MarkAsReadAsync reloads the chat from DB but preserves these flags because
/// they are now persisted alongside other chat properties.
/// </summary>
public class HeadlessResyncTests : HeadlessTestBase
{
    // ──────────────────────────────────────────────────────────────
    //  Helper: create a context with a DeviceSync group + a separate group chat.
    //  Also generates a KeyPackage so the MLS service has a SlotId
    //  (required by RequestResyncAsync).
    // ──────────────────────────────────────────────────────────────

    private async Task<(RealTestContext Ctx, Chat SyncChat, Chat TargetChat)> CreateContextWithSyncGroup(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Generate a KP — needed so MLS service has a SlotId for RequestResyncAsync
        await ctx.MlsService.GenerateKeyPackageAsync();

        // Create the DeviceSync group (single-member group for multi-device sync)
        var syncGroupInfo = await ctx.MlsService.CreateGroupAsync("DeviceSync", new[] { "wss://relay.test" });
        var syncChat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "DeviceSync",
            Type = ChatType.DeviceSync,
            MlsGroupId = syncGroupInfo.GroupId,
            MlsEpoch = syncGroupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(syncChat);
        await ctx.Storage.SaveSettingAsync("device_sync_chat_id", syncChat.Id);

        // Create a separate target group (the one that becomes out-of-sync)
        var targetGroupInfo = await ctx.MlsService.CreateGroupAsync("Target Group", new[] { "wss://relay.test" });
        var nostrGroupId = ctx.MlsService.GetNostrGroupId(targetGroupInfo.GroupId);
        var targetChat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Target Group",
            Type = ChatType.Group,
            MlsGroupId = targetGroupInfo.GroupId,
            MlsEpoch = targetGroupInfo.Epoch,
            NostrGroupId = nostrGroupId,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(targetChat);

        return (ctx, syncChat, targetChat);
    }

    // ══════════════════════════════════════════════════════════════
    //  MessageService integration tests
    // ══════════════════════════════════════════════════════════════

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task RequestResyncAsync_EmitsChatUpdateWithIsResyncPending(string backend)
    {
        if (ShouldSkip(backend)) return;
        var (ctx, syncChat, targetChat) = await CreateContextWithSyncGroup(backend);

        // Subscribe to ChatUpdates before calling RequestResync
        Chat? receivedUpdate = null;
        using var sub = ctx.MessageService.ChatUpdates
            .Where(c => c.Id == targetChat.Id && c.IsResyncPending)
            .Take(1)
            .Subscribe(c => receivedUpdate = c);

        // Precondition
        Assert.False(targetChat.IsResyncPending);

        // Act
        await ctx.MessageService.RequestResyncAsync(targetChat.Id);

        // Assert: ChatUpdates should have emitted with IsResyncPending = true
        Assert.NotNull(receivedUpdate);
        Assert.True(receivedUpdate!.IsResyncPending,
            "ChatUpdates emission from RequestResyncAsync should have IsResyncPending = true");
    }

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task RequestResyncAsync_PublishesMessageToSyncGroup(string backend)
    {
        if (ShouldSkip(backend)) return;
        var (ctx, syncChat, targetChat) = await CreateContextWithSyncGroup(backend);

        // Act
        await ctx.MessageService.RequestResyncAsync(targetChat.Id);

        // Assert: an encrypted message was published (PublishRawEventJsonAsync for the MLS ciphertext)
        ctx.MockNostr.Verify(n => n.PublishRawEventJsonAsync(It.IsAny<byte[]>()), Times.AtLeastOnce,
            "RequestResyncAsync should send a [ResyncRequest] message to the DeviceSync group");
    }

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task AnnounceDeviceToSyncGroupAsync_PublishesMessage(string backend)
    {
        if (ShouldSkip(backend)) return;
        var (ctx, syncChat, _) = await CreateContextWithSyncGroup(backend);

        // Act
        await ctx.MessageService.AnnounceDeviceToSyncGroupAsync("Windows");

        // Assert: a message was published to the sync group
        ctx.MockNostr.Verify(n => n.PublishRawEventJsonAsync(It.IsAny<byte[]>()), Times.AtLeastOnce,
            "AnnounceDeviceToSyncGroupAsync should send a [Device] message to the DeviceSync group");
    }

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task AnnounceDeviceToSyncGroupAsync_NoSyncGroup_DoesNotThrow(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // No DeviceSync group exists (no setting saved) — should be a no-op
        await ctx.MessageService.AnnounceDeviceToSyncGroupAsync("Android");

        // No exception + no publish
        ctx.MockNostr.Verify(n => n.PublishRawEventJsonAsync(It.IsAny<byte[]>()), Times.Never);
    }

    // ══════════════════════════════════════════════════════════════
    //  ChatViewModel tests
    // ══════════════════════════════════════════════════════════════

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task ChatViewModel_LoadChat_SetsOutOfSyncFlags(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Save the chat to DB with flags set — they are now persisted.
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Broken Group",
            Type = ChatType.Group,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            IsOutOfSync = true,
            IsResyncPending = true
        };
        await ctx.Storage.SaveChatAsync(chat);

        var chatVm = new ChatViewModel(
            ctx.MessageService, ctx.Storage, ctx.MockNostr.Object,
            ctx.MlsService, ctx.MockClipboard.Object);

        // Act: LoadChat sets flags synchronously from the Chat object.
        chatVm.LoadChat(chat);

        Assert.True(chatVm.IsOutOfSync, "LoadChat should set IsOutOfSync from chat model");
        Assert.True(chatVm.IsResyncPending, "LoadChat should set IsResyncPending from chat model");

        // Wait for MarkAsReadAsync fire-and-forget to complete (it reloads from DB).
        // With flags persisted, the reloaded chat retains IsOutOfSync/IsResyncPending=true,
        // so the ChatUpdates emission should NOT clobber the VM flags.
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatVm.IsOutOfSync,
            "IsOutOfSync must survive MarkAsReadAsync (flags are now persisted in DB)");
        Assert.True(chatVm.IsResyncPending,
            "IsResyncPending must survive MarkAsReadAsync (flags are now persisted in DB)");
    }

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task ChatViewModel_LoadChat_ClearFlags_WhenNotOutOfSync(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var groupInfo = await ctx.MlsService.CreateGroupAsync("Good Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Good Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            IsOutOfSync = false,
            IsResyncPending = false
        };
        await ctx.Storage.SaveChatAsync(chat);

        var chatVm = new ChatViewModel(
            ctx.MessageService, ctx.Storage, ctx.MockNostr.Object,
            ctx.MlsService, ctx.MockClipboard.Object);

        // Act
        chatVm.LoadChat(chat);

        // Assert
        Assert.False(chatVm.IsOutOfSync);
        Assert.False(chatVm.IsResyncPending);
    }

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task ChatViewModel_ChatUpdates_UpdatesResyncPending(string backend)
    {
        if (ShouldSkip(backend)) return;
        var (ctx, _, targetChat) = await CreateContextWithSyncGroup(backend);

        var chatVm = new ChatViewModel(
            ctx.MessageService, ctx.Storage, ctx.MockNostr.Object,
            ctx.MlsService, ctx.MockClipboard.Object);

        // Load the chat initially (not out of sync)
        chatVm.LoadChat(targetChat);

        Assert.False(chatVm.IsResyncPending);

        // Act: RequestResyncAsync emits a ChatUpdate with IsResyncPending = true
        await ctx.MessageService.RequestResyncAsync(targetChat.Id);

        // The ChatUpdates subscription uses ObserveOn(MainThreadScheduler),
        // so we need to process the pending UI jobs
        Dispatcher.UIThread.RunJobs();

        // Assert: the VM should have picked up the ChatUpdate
        Assert.True(chatVm.IsResyncPending,
            "ChatUpdates subscription should update IsResyncPending on the VM");
    }

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task ChatViewModel_ResyncCommand_SetsIsResyncPending(string backend)
    {
        if (ShouldSkip(backend)) return;
        var (ctx, syncChat, targetChat) = await CreateContextWithSyncGroup(backend);

        var chatVm = new ChatViewModel(
            ctx.MessageService, ctx.Storage, ctx.MockNostr.Object,
            ctx.MlsService, ctx.MockClipboard.Object);

        chatVm.LoadChat(targetChat);

        Assert.False(chatVm.IsResyncPending);

        // Act: execute the ResyncCommand (it sets IsResyncPending = true optimistically,
        // then calls RequestResyncAsync which emits a ChatUpdate)
        await chatVm.ResyncCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        // Assert: the VM should show resync pending (set optimistically by the command handler)
        Assert.True(chatVm.IsResyncPending,
            "ResyncCommand should set IsResyncPending on the VM");
    }
}
