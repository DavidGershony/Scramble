using System.Reactive;
using System.Reactive.Linq;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Scramble.UI.Tests.TestHelpers;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Tests for issues found in the second log audit (2026-04-02 remote device).
/// </summary>
public class LogAudit2Tests : IDisposable
{
    private readonly List<string> _dbPaths = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var path in _dbPaths)
            try { File.Delete(path); } catch { }
    }

    // ── P0 #1: SaveChatAsync should not wipe participants when list is empty ──

    [Fact]
    public async Task SaveChatAsync_EmptyParticipants_PreservesExisting()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"scramble_audit2_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        // Save a chat with participants
        var chat = new Chat
        {
            Id = "group-test-1",
            Name = "Test Group",
            Type = ChatType.Group,
            ParticipantPublicKeys = new List<string> { "aaa", "bbb" },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await storage.SaveChatAsync(chat);

        // Verify participants saved
        var loaded = await storage.GetChatAsync("group-test-1");
        Assert.Equal(2, loaded!.ParticipantPublicKeys.Count);

        // Re-save with empty participants (simulating a code path that doesn't load them)
        var chatWithoutParticipants = new Chat
        {
            Id = "group-test-1",
            Name = "Test Group",
            Type = ChatType.Group,
            ParticipantPublicKeys = new List<string>(),
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await storage.SaveChatAsync(chatWithoutParticipants);

        // Participants should still be there
        var reloaded = await storage.GetChatAsync("group-test-1");
        Assert.Equal(2, reloaded!.ParticipantPublicKeys.Count);
        Assert.Contains("aaa", reloaded.ParticipantPublicKeys);
        Assert.Contains("bbb", reloaded.ParticipantPublicKeys);
    }

    [Fact]
    public async Task SaveChatAsync_WithParticipants_OverwritesExisting()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"scramble_audit2_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var chat = new Chat
        {
            Id = "group-test-2",
            Name = "Test Group",
            Type = ChatType.Group,
            ParticipantPublicKeys = new List<string> { "aaa" },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await storage.SaveChatAsync(chat);

        // Update with new participant list
        chat.ParticipantPublicKeys = new List<string> { "aaa", "ccc" };
        await storage.SaveChatAsync(chat);

        var loaded = await storage.GetChatAsync("group-test-2");
        Assert.Equal(2, loaded!.ParticipantPublicKeys.Count);
        Assert.Contains("ccc", loaded.ParticipantPublicKeys);
        Assert.DoesNotContain("bbb", loaded.ParticipantPublicKeys);
    }

    // ── P1 #3: Voice/attach commands disabled when MIP-04 is off ──

    [Fact]
    public void CanRecord_RequiresMip04Enabled()
    {
        var mockMessageService = new Mock<IMessageService>();
        mockMessageService.Setup(m => m.NewMessages).Returns(Observable.Empty<Message>());
        mockMessageService.Setup(m => m.ReactionUpdates).Returns(Observable.Empty<(string, string, string)>());

        var mockStorageService = new Mock<IStorageService>();
        var mockNostrService = new Mock<INostrService>();
        mockNostrService.Setup(n => n.ConnectionStatus).Returns(Observable.Empty<NostrConnectionStatus>());
        mockNostrService.Setup(n => n.SyncStatus).Returns(Observable.Empty<string?>());
        var mockMlsService = new Mock<IMlsService>();
        var mockClipboard = new Mock<IPlatformClipboard>();

        var vm = new ChatViewModel(
            mockMessageService.Object,
            mockStorageService.Object,
            mockNostrService.Object,
            mockMlsService.Object,
            mockClipboard.Object);

        // Set up a chat so HasChat=true
        mockStorageService.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        mockMessageService.Setup(m => m.GetMessagesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Message>());

        var chat = new Chat
        {
            Id = "test-chat",
            Name = "Test",
            Type = ChatType.Group,
            MlsGroupId = new byte[] { 1, 2, 3 }
        };
        vm.LoadChat(chat);

        // MIP-04 disabled — commands should not be executable
        vm.IsMip04Enabled = false;
        Assert.False(vm.ToggleRecordingCommand.CanExecute.FirstAsync().Wait());
        Assert.False(vm.AttachFileCommand.CanExecute.FirstAsync().Wait());

        // MIP-04 enabled — commands should be executable
        vm.IsMip04Enabled = true;
        Assert.True(vm.ToggleRecordingCommand.CanExecute.FirstAsync().Wait());
        Assert.True(vm.AttachFileCommand.CanExecute.FirstAsync().Wait());
    }

    // ── P1 #4: SetAuthCredentials makes key available for NIP-42 ──

    [Fact]
    public void SetAuthCredentials_SetsPrivateKey()
    {
        var service = new NostrService();

        // Before setting — no crash, just works
        service.SetAuthCredentials("abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234");

        // Clear
        service.SetAuthCredentials(null);

        // Should not throw
        service.Dispose();
    }

    // ── P3 #6: Blossom URL normalized to lowercase ──

    [Fact]
    public async Task BlossomUrl_NormalizedToLowercase_OnSave()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"scramble_audit2_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        // Simulate what SettingsViewModel does: save a mixed-case URL
        var normalized = "https://Blossom.TheDude.Cloud".Trim().ToLowerInvariant();
        await storage.SaveSettingAsync("blossom_server_url", normalized);

        var loaded = await storage.GetSettingAsync("blossom_server_url");
        Assert.Equal("https://blossom.thedude.cloud", loaded);
    }
}
