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

    // ------------------------------------------- copying the group id from the info sheet
    //
    // The group-info sheet's Copy button reached CopyGroupLinkCommand without opening the
    // invite dialog. GroupInviteLink was only ever assigned inside
    // ShowInviteDialogCommand, so it was empty and the command took its empty-guard early
    // return: a button that did nothing and reported nothing. Reported from the phone.

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task GroupInviteLink_IsAvailableWithoutOpeningTheInviteDialog(string backend)
    {
        var (_, chat, chatVm) = await CreateChatWithViewModel(backend);

        // Deliberately NOT executing ShowInviteDialogCommand: the info sheet does not.
        Assert.Equal(chat.Id, chatVm.GroupInviteLink);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task CopyGroupLink_ReachesTheClipboardWithoutTheInviteDialog(string backend)
    {
        var (ctx, chat, chatVm) = await CreateChatWithViewModel(backend);

        await chatVm.CopyGroupLinkCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        ctx.MockClipboard.Verify(c => c.SetTextAsync(chat.Id), Times.Once);
    }

    // ------------------------------------------------- promote / demote reports an outcome
    //
    // The native head had no promote control at all, so wiring one exposed that the shared
    // command swallowed failures into the log. An admin change publishes a commit to every
    // member; "it silently did nothing" is the one outcome a user must not be left with.

    private async Task<(RealTestContext Ctx, ChatViewModel ChatVm, GroupMemberViewModel Member)>
        GroupWithAnotherMember(string backend, bool viewerIsAdmin)
    {
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var groupInfo = await ctx.MlsService.CreateGroupAsync("Admin Test", new[] { "wss://relay.test" });
        var other = "bb" + new string('1', 62);
        // A third participant is required, not decorative: ChatViewModel only treats a chat
        // as a group when it has MORE than two participants (or a description), and
        // IsCurrentUserAdmin is gated on IsGroup. With two, the admin path is unreachable.
        var third = "cc" + new string('2', 62);

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Admin Test",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { ctx.User.PublicKeyHex, other, third },
            AdminPublicKeys = viewerIsAdmin
                ? new List<string> { ctx.User.PublicKeyHex.ToLowerInvariant() }
                : new List<string> { other },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await ctx.Storage.SaveChatAsync(chat);

        var chatVm = new ChatViewModel(
            ctx.MessageService, ctx.Storage, ctx.MockNostr.Object, ctx.MlsService, ctx.MockClipboard.Object);

        // Required before LoadChat: IsCurrentUserAdmin is computed from this, so without it
        // the flag is false regardless of the admin list and the test would assert nothing.
        chatVm.SetUserContext(ctx.User.PrivateKeyHex, ctx.User.PublicKeyHex);

        chatVm.LoadChat(chat);
        Dispatcher.UIThread.RunJobs();

        var member = new GroupMemberViewModel
        {
            PublicKeyHex = other,
            DisplayName = "Other",
            IsAdmin = false,
            IsCurrentUser = false
        };

        return (ctx, chatVm, member);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ToggleAdmin_AsAdmin_AlwaysReportsAnOutcome(string backend)
    {
        var (_, chatVm, member) = await GroupWithAnotherMember(backend, viewerIsAdmin: true);
        Assert.True(chatVm.IsCurrentUserAdmin);

        await chatVm.ToggleAdminCommand.Execute(member);
        Dispatcher.UIThread.RunJobs();

        // It must have RESOLVED, not merely be non-empty. Asserting non-empty passed even
        // with the failure report deleted, because the in-progress "Promoting..." text is
        // assigned before the attempt -- a test that agreed with the bug. The in-progress
        // convention is a trailing ellipsis, so an outcome is anything that is not that.
        Assert.False(string.IsNullOrEmpty(chatVm.AdminActionStatus));

        // Still the in-progress text means the attempt reported nothing. Matching on the
        // in-progress PREFIXES rather than on a trailing ellipsis: an exception message can
        // itself contain one, which made the first version of this assertion fail against
        // correct code.
        Assert.False(
            chatVm.AdminActionStatus!.StartsWith("Promoting", StringComparison.Ordinal) ||
            chatVm.AdminActionStatus!.StartsWith("Removing admin from", StringComparison.Ordinal),
            $"admin action never resolved: {chatVm.AdminActionStatus}");
    }

    // NOT TESTED HERE: that a non-admin's toggle is refused.
    //
    // Three attempts could not construct an honest fixture for it. LoadChat refreshes the
    // admin list from real MLS state and the fixture's user created the group, so the engine
    // names them admin whatever the Chat record says; assigning IsCurrentUserAdmin=false
    // afterwards is overwritten by the async member load. A test bent far enough to pass
    // here would be asserting the bend, and a flaky test on a required gate is worse than an
    // absent one.
    //
    // The refusal itself is defended three times over: the guard in ToggleAdminCommand, the
    // control being hidden for non-admins in both heads, and the engine refusing an
    // unauthorised admin commit. Covering it properly needs a fixture where the viewer is
    // genuinely not the group's creator, which is integration-harness work.

}
