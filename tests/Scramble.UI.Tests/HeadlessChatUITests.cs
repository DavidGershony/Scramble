using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Headless tests for Chat UI interactions: info panel, invite dialog,
/// recording state, file attach, and audio playback.
/// </summary>
public class HeadlessChatUITests : HeadlessTestBase
{
    private async Task<(RealTestContext Ctx, Chat Chat, ChatViewModel ChatVm)> CreateChatWithViewModel(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var groupInfo = await ctx.MlsService.CreateGroupAsync("UI Test Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "UI Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);

        var chatVm = new ChatViewModel(
            ctx.MessageService, ctx.Storage, ctx.MockNostr.Object, ctx.MlsService, ctx.MockClipboard.Object);

        chatVm.LoadChat(chat);
        Dispatcher.UIThread.RunJobs();

        return (ctx, chat, chatVm);
    }

    // --- Chat Info Panel ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ShowChatInfo_TogglesMetadataPanel(string backend)
    {
        var (ctx, chat, chatVm) = await CreateChatWithViewModel(backend);

        Assert.False(chatVm.ShowMetadataPanel);

        chatVm.ToggleMetadataPanelCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatVm.ShowMetadataPanel);

        // Toggle off
        chatVm.ToggleMetadataPanelCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.False(chatVm.ShowMetadataPanel);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ShowChatInfo_FetchesContactMetadata(string backend)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Create a DM chat so there's a contact to fetch metadata for
        var contactPubKey = "aa".PadLeft(64, 'a');
        var dmChat = await ctx.MessageService.GetOrCreateDirectMessageAsync(contactPubKey);

        ctx.MockNostr.Setup(n => n.FetchUserMetadataAsync(contactPubKey))
            .ReturnsAsync(new UserMetadata
            {
                DisplayName = "Alice",
                Name = "alice",
                About = "Hello world",
                Picture = "https://example.com/alice.png"
            });

        var chatVm = new ChatViewModel(
            ctx.MessageService, ctx.Storage, ctx.MockNostr.Object, ctx.MlsService, ctx.MockClipboard.Object);

        chatVm.LoadChat(dmChat);
        Dispatcher.UIThread.RunJobs();

        await chatVm.ShowChatInfoCommand.Execute();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatVm.ShowMetadataPanel);
        ctx.MockNostr.Verify(n => n.FetchUserMetadataAsync(contactPubKey), Times.AtLeastOnce);
    }

    // --- Invite Dialog ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ShowInviteDialog_OpensWithGroupLink(string backend)
    {
        var (ctx, chat, chatVm) = await CreateChatWithViewModel(backend);

        Assert.False(chatVm.ShowInviteDialog);

        chatVm.ShowInviteDialogCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatVm.ShowInviteDialog);
        Assert.Equal(chat.Id, chatVm.GroupInviteLink);
        Assert.Equal(string.Empty, chatVm.InvitePublicKey);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task CopyGroupLink_CopiesToClipboard(string backend)
    {
        var (ctx, chat, chatVm) = await CreateChatWithViewModel(backend);

        ctx.MockClipboard.Setup(c => c.SetTextAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        chatVm.ShowInviteDialogCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        chatVm.CopyGroupLinkCommand.Execute().Subscribe();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        ctx.MockClipboard.Verify(c => c.SetTextAsync(chat.Id), Times.Once);
        Assert.Contains("copied", chatVm.InviteSuccess?.ToLower() ?? "");
    }

    // --- Recording UI State Transitions ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task Recording_StartSetsIsRecording(string backend)
    {
        var (ctx, chat, chatVm) = await CreateChatWithViewModel(backend);

        var mockAudio = new Mock<IAudioRecordingService>();
        mockAudio.Setup(a => a.StartRecordingAsync()).Returns(Task.CompletedTask);
        mockAudio.Setup(a => a.IsRecording).Returns(true);
        ChatViewModel.AudioRecordingService = mockAudio.Object;

        Assert.False(chatVm.IsRecording);

        chatVm.ToggleRecordingCommand.Execute().Subscribe();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatVm.IsRecording);
        mockAudio.Verify(a => a.StartRecordingAsync(), Times.Once);

        ChatViewModel.AudioRecordingService = null;
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task Recording_CancelStopsWithoutSending(string backend)
    {
        var (ctx, chat, chatVm) = await CreateChatWithViewModel(backend);

        var mockAudio = new Mock<IAudioRecordingService>();
        mockAudio.Setup(a => a.StartRecordingAsync()).Returns(Task.CompletedTask);
        mockAudio.Setup(a => a.CancelRecordingAsync()).Returns(Task.CompletedTask);
        mockAudio.Setup(a => a.IsRecording).Returns(true);
        ChatViewModel.AudioRecordingService = mockAudio.Object;

        // Start recording
        chatVm.ToggleRecordingCommand.Execute().Subscribe();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatVm.IsRecording);

        // Cancel recording
        chatVm.CancelRecordingCommand.Execute().Subscribe();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.False(chatVm.IsRecording);
        mockAudio.Verify(a => a.CancelRecordingAsync(), Times.Once);

        ChatViewModel.AudioRecordingService = null;
    }

    // --- File Attach Flow ---

    [AvaloniaTheory]
    [InlineData("managed")] // Rust backend doesn't support MIP-04 media exporter secret
    public async Task AttachFile_CallsFilePickerAndUploads(string backend)
    {
        var (ctx, chat, chatVm) = await CreateChatWithViewModel(backend);

        var fileData = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03, 0x04 };
        ChatViewModel.FilePickerFunc = () =>
            Task.FromResult<(byte[] Data, string FileName, string MimeType)?>(
                (fileData, "test.png", "image/png"));

        var mockUpload = new Mock<IMediaUploadService>();
        mockUpload.Setup(u => u.UploadAsync(It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlobUploadResult { Url = "https://blossom.test/abc123", Sha256 = "abcdef1234567890" });
        ChatViewModel.MediaUploadService = mockUpload.Object;

        await chatVm.AttachFileCommand.Execute();
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        mockUpload.Verify(u => u.UploadAsync(It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        ChatViewModel.FilePickerFunc = null;
        ChatViewModel.MediaUploadService = null;
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task AttachFile_UserCancels_NoUpload(string backend)
    {
        var (ctx, chat, chatVm) = await CreateChatWithViewModel(backend);

        // File picker returns null (user cancelled)
        ChatViewModel.FilePickerFunc = () =>
            Task.FromResult<(byte[] Data, string FileName, string MimeType)?>(null);

        var mockUpload = new Mock<IMediaUploadService>();
        ChatViewModel.MediaUploadService = mockUpload.Object;

        await chatVm.AttachFileCommand.Execute();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        mockUpload.Verify(u => u.UploadAsync(It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(chatVm.IsSendingImage);

        ChatViewModel.FilePickerFunc = null;
        ChatViewModel.MediaUploadService = null;
    }

    // --- Invite Flow Bugs ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SendInvite_MlsFails_DoesNotAddParticipant(string backend)
    {
        var (ctx, chat, chatVm) = await CreateChatWithViewModel(backend);
        chatVm.SetUserContext(ctx.User.PrivateKeyHex, ctx.User.PublicKeyHex);

        // Generate an invitee keypair and publish a KeyPackage
        var nostrService = new NostrService();
        var (_, inviteePubKey, _, _) = nostrService.GenerateKeyPair();

        // Mock FetchKeyPackagesAsync to return a valid KeyPackage
        // but the MLS AddMemberAsync will fail because the KeyPackage
        // doesn't match a real MLS client — simulating the GroupNotFoundException scenario.
        // We use a fake KeyPackage that will cause MLS to throw.
        var fakeKp = KeyPackage.Create(inviteePubKey, new byte[32], 0x0001);
        ctx.MockNostr.Setup(n => n.FetchKeyPackagesAsync(inviteePubKey))
            .ReturnsAsync(new[] { fakeKp });

        var initialCount = chat.ParticipantPublicKeys.Count;

        chatVm.ShowInviteDialogCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        chatVm.InvitePublicKey = inviteePubKey;
        chatVm.SendInviteCommand.Execute().Subscribe();
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        // After MLS failure, participant should NOT be added
        var storedChat = await ctx.Storage.GetChatAsync(chat.Id);
        Assert.Equal(initialCount, storedChat!.ParticipantPublicKeys.Count);
        Assert.DoesNotContain(inviteePubKey, storedChat.ParticipantPublicKeys);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SendInvite_NoKeyPackage_ShowsError(string backend)
    {
        var (ctx, chat, chatVm) = await CreateChatWithViewModel(backend);
        chatVm.SetUserContext(ctx.User.PrivateKeyHex, ctx.User.PublicKeyHex);

        var nostrService = new NostrService();
        var (_, inviteePubKey, _, _) = nostrService.GenerateKeyPair();

        // No KeyPackages available
        ctx.MockNostr.Setup(n => n.FetchKeyPackagesAsync(inviteePubKey))
            .ReturnsAsync(Enumerable.Empty<KeyPackage>());

        chatVm.ShowInviteDialogCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        chatVm.InvitePublicKey = inviteePubKey;
        chatVm.SendInviteCommand.Execute().Subscribe();
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        // Should show error, not silently add
        Assert.NotNull(chatVm.InviteError);
        Assert.DoesNotContain(inviteePubKey, chat.ParticipantPublicKeys);
    }
}
