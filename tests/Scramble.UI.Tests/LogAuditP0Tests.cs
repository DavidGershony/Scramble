using System.Net;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Moq;
using Moq.Protected;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Tests for P0 issues found in the 2026-04-02 log audit.
/// </summary>
public class LogAuditP0Tests
{
    // ── P0 #1: Voice message should not crash when MlsGroupId is null ──

    [Fact]
    public async Task SendVoiceMessage_NullMlsGroupId_DoesNotThrow()
    {
        // Arrange
        var mockMessageService = new Mock<IMessageService>();
        mockMessageService.Setup(m => m.NewMessages).Returns(Observable.Empty<Message>());
        mockMessageService.Setup(m => m.ReactionUpdates).Returns(Observable.Empty<(string, string, string)>());
        mockMessageService.Setup(m => m.GetMessagesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Message>());

        var mockStorageService = new Mock<IStorageService>();
        mockStorageService.Setup(s => s.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((string?)null);

        var mockNostrService = new Mock<INostrService>();
        mockNostrService.Setup(n => n.ConnectionStatus)
            .Returns(Observable.Empty<NostrConnectionStatus>());
        mockNostrService.Setup(n => n.SyncStatus).Returns(Observable.Empty<string?>());

        var mockMlsService = new Mock<IMlsService>();
        var mockClipboard = new Mock<IPlatformClipboard>();

        var vm = new ChatViewModel(
            mockMessageService.Object,
            mockStorageService.Object,
            mockNostrService.Object,
            mockMlsService.Object,
            mockClipboard.Object);

        // Bot chat with no MlsGroupId — this is the bug trigger
        var botChat = new Chat
        {
            Id = "bot-chat-1",
            Name = "Test Bot",
            Type = ChatType.Bot,
            MlsGroupId = null
        };

        vm.LoadChat(botChat);

        // Mock audio service
        var mockAudio = new Mock<IAudioRecordingService>();
        mockAudio.Setup(a => a.StopRecordingAsync()).ReturnsAsync(new AudioRecordingResult
        {
            PcmData = new byte[48000],
            SampleRate = 48000,
            Channels = 1,
            Duration = TimeSpan.FromSeconds(1)
        });
        ChatViewModel.AudioRecordingService = mockAudio.Object;

        // Act: simulate pressing stop on a recording
        vm.IsRecording = true;

        var exception = await Record.ExceptionAsync(async () =>
        {
            vm.ToggleRecordingCommand.Execute(Unit.Default);
            await Task.Delay(500);
        });

        // Assert: no crash, and GetMediaExporterSecret was never called
        Assert.Null(exception);
        mockMlsService.Verify(
            m => m.GetMediaExporterSecret(It.IsAny<byte[]>()),
            Times.Never);
    }

    // ── P0 #2: Blossom upload should fall back to default server on 401 ──

    [Fact]
    public async Task BlossomUpload_401OnConfiguredServer_FallsBackToDefault()
    {
        var mockHandler = new Mock<HttpMessageHandler>();

        // Custom server: 401 (return new instance each call to avoid disposal issues)
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.RequestUri!.Host == "blossom.thedude.cloud"),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("Pubkey not authorized by any storage rule")
            }));

        // Default server: success
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.RequestUri!.Host == "blossom.primal.net"),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"sha256":"abcd1234","url":"https://blossom.primal.net/abcd1234","size":100}""",
                    Encoding.UTF8, "application/json")
            }));

        var httpClient = new HttpClient(mockHandler.Object) { Timeout = TimeSpan.FromSeconds(10) };
        var service = new BlossomUploadService(externalSigner: null, httpClient: httpClient);
        service.BlossomServerUrl = "https://blossom.thedude.cloud";

        var privKeyHex = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";

        // Act
        var result = await service.UploadAsync(new byte[] { 1, 2, 3, 4 }, privKeyHex, "audio/opus");

        // Assert: succeeded via fallback to default
        Assert.NotNull(result);
        Assert.Contains("blossom.primal.net", result.Url);
    }

    [Fact]
    public async Task BlossomUpload_BothServersFail_Throws()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("Unauthorized")
            }));

        var httpClient = new HttpClient(mockHandler.Object) { Timeout = TimeSpan.FromSeconds(10) };
        var service = new BlossomUploadService(externalSigner: null, httpClient: httpClient);
        service.BlossomServerUrl = "https://blossom.thedude.cloud";

        var privKeyHex = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync(new byte[] { 1, 2, 3, 4 }, privKeyHex, "audio/opus"));
    }

    [Fact]
    public async Task BlossomUpload_AlreadyOnDefault_NoFallbackAttempted()
    {
        var callCount = 0;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("Unauthorized")
                });
            });

        var httpClient = new HttpClient(mockHandler.Object) { Timeout = TimeSpan.FromSeconds(10) };
        var service = new BlossomUploadService(externalSigner: null, httpClient: httpClient);
        // Leave as default — no fallback should happen

        var privKeyHex = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync(new byte[] { 1, 2, 3, 4 }, privKeyHex, "audio/opus"));

        // 1 call only (auth-first when credentials present), not 2+ (no fallback to same server)
        Assert.Equal(1, callCount);
    }
}
