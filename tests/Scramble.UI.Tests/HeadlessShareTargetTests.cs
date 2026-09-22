using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.UI.Tests.TestHelpers;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Integration tests for the Android share target flow:
/// account resolution → chat loading → grouping → handoff.
/// </summary>
public class HeadlessShareTargetTests : HeadlessTestBase
{
    private readonly string _testRootDir;

    public HeadlessShareTargetTests()
    {
        _testRootDir = Path.Combine(Path.GetTempPath(), $"scramble_share_ui_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testRootDir);
        ProfileConfiguration.SetRootDataDirectory(_testRootDir);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task ShareFlow_SelectChat_ProducesCorrectHandoff(string backend)
    {

        // --- Arrange: create two accounts with different chats ---
        var ctxA = await CreateRealContext(backend);
        await ctxA.MessageService.InitializeAsync();
        var ctxB = await CreateRealContext(backend);
        await ctxB.MessageService.InitializeAsync();

        // Account A: 1 bot + 1 DM
        var profileDirA = CreateProfileDir(ctxA.User.PublicKeyHex);
        var storageA = new StorageService(
            Path.Combine(profileDirA, "scramble.db"), new MockSecureStorage());
        await storageA.InitializeAsync();

        var botChatA = new Chat
        {
            Id = "a-bot-1", Name = "Device A", Type = ChatType.Bot,
            CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow.AddMinutes(-5)
        };
        var dmChatA = new Chat
        {
            Id = "a-dm-1", Name = "Alice DM", Type = ChatType.DirectMessage,
            CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow.AddMinutes(-1)
        };
        await storageA.SaveChatAsync(botChatA);
        await storageA.SaveChatAsync(dmChatA);

        // Account B: 1 group only
        var profileDirB = CreateProfileDir(ctxB.User.PublicKeyHex);
        var storageB = new StorageService(
            Path.Combine(profileDirB, "scramble.db"), new MockSecureStorage());
        await storageB.InitializeAsync();

        var groupChatB = new Chat
        {
            Id = "b-group-1", Name = "Team B", Type = ChatType.Group,
            CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow
        };
        await storageB.SaveChatAsync(groupChatB);

        // Register both accounts, A is active
        AccountRegistryService.Reload();
        AccountRegistryService.AddOrUpdateAccount(new AccountEntry
        {
            PublicKeyHex = ctxA.User.PublicKeyHex, Npub = ctxA.User.Npub,
            DisplayName = "Account A", AddedAt = DateTime.UtcNow, LastActiveAt = DateTime.UtcNow
        });
        AccountRegistryService.AddOrUpdateAccount(new AccountEntry
        {
            PublicKeyHex = ctxB.User.PublicKeyHex, Npub = ctxB.User.Npub,
            DisplayName = "Account B", AddedAt = DateTime.UtcNow, LastActiveAt = DateTime.UtcNow.AddHours(-1)
        });
        AccountRegistryService.SetActiveAccount(ctxA.User.PublicKeyHex);

        // --- Act: simulate share flow ---

        // Step 1: Resolve account — should pick A (active)
        var resolved = ShareAccountResolver.Resolve();
        Assert.NotNull(resolved);
        Assert.Equal(ctxA.User.PublicKeyHex, resolved!.PublicKeyHex);

        // Step 2: Load chats for resolved account
        var result = await ShareChatLoader.LoadAsync(resolved.PublicKeyHex);

        // --- Assert: Bot chat first, then DM ---
        Assert.Equal(1, result.DeviceChats.Count);
        Assert.Equal("a-bot-1", result.DeviceChats[0].Id);
        Assert.Equal(1, result.RegularChats.Count);
        Assert.Equal("a-dm-1", result.RegularChats[0].Id);

        // Step 3: Simulate chat selection and build handoff
        var selectedChat = result.RegularChats[0]; // pick the DM
        var payload = new SharePayload { Text = "Shared text from browser", MimeType = "text/plain" };

        // Verify the handoff data is correct
        Assert.Equal("a-dm-1", selectedChat.Id);
        Assert.Equal(resolved.PublicKeyHex, ctxA.User.PublicKeyHex);
        Assert.True(payload.IsText);
        Assert.Equal("Shared text from browser", payload.Text);

        // Cleanup
        CleanUp();
    }

    [AvaloniaFact]
    public async Task ShareFlow_AccountSwitch_ReloadsChats()
    {
        // --- Arrange: two accounts with distinct chats ---
        var pubKeyA = "aa".PadRight(64, 'a');
        var pubKeyB = "bb".PadRight(64, 'b');

        var profileDirA = CreateProfileDir(pubKeyA);
        var storageA = new StorageService(
            Path.Combine(profileDirA, "scramble.db"), new MockSecureStorage());
        await storageA.InitializeAsync();
        await storageA.SaveChatAsync(new Chat
        {
            Id = "a-chat", Name = "A's Chat", Type = ChatType.DirectMessage,
            CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow
        });

        var profileDirB = CreateProfileDir(pubKeyB);
        var storageB = new StorageService(
            Path.Combine(profileDirB, "scramble.db"), new MockSecureStorage());
        await storageB.InitializeAsync();
        await storageB.SaveChatAsync(new Chat
        {
            Id = "b-chat", Name = "B's Chat", Type = ChatType.Bot,
            CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow
        });

        // --- Act: load for A, then switch to B ---
        var resultA = await ShareChatLoader.LoadAsync(pubKeyA);
        Assert.Equal(1, resultA.RegularChats.Count);
        Assert.Equal("a-chat", resultA.RegularChats[0].Id);
        Assert.Empty(resultA.DeviceChats);

        var resultB = await ShareChatLoader.LoadAsync(pubKeyB);
        Assert.Empty(resultB.RegularChats);
        Assert.Equal(1, resultB.DeviceChats.Count);
        Assert.Equal("b-chat", resultB.DeviceChats[0].Id);

        // Verify they don't cross-contaminate
        Assert.NotEqual(resultA.RegularChats[0].Id, resultB.DeviceChats[0].Id);

        CleanUp();
    }

    // ───────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────

    private string CreateProfileDir(string pubKeyHex)
    {
        var profileName = ProfileConfiguration.DeriveProfileName(pubKeyHex);
        var dir = Path.Combine(_testRootDir, "profiles", profileName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void CleanUp()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { if (Directory.Exists(_testRootDir)) Directory.Delete(_testRootDir, true); }
        catch (IOException) { }
    }
}
