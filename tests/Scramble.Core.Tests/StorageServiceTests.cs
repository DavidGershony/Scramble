using Microsoft.Data.Sqlite;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Core.Tests.TestHelpers;
using Xunit;

namespace Scramble.Core.Tests;

public class StorageServiceTests : IAsyncLifetime
{
    private readonly string _testDbPath;
    private readonly StorageService _storageService;

    public StorageServiceTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"scramble_test_{Guid.NewGuid()}.db");
        _storageService = new StorageService(_testDbPath, new MockSecureStorage());
    }

    public async ValueTask InitializeAsync()
    {
        await _storageService.InitializeAsync();
    }

    public ValueTask DisposeAsync()
    {
        // Clear SQLite connection pool to release file handles
        SqliteConnection.ClearAllPools();

        // Give the system time to release the file
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
        }
        catch (IOException)
        {
            // Ignore if file is still locked - it's in temp folder anyway
        }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SaveAndRetrieveUser_ShouldWork()
    {
        // Arrange
        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = "a".PadLeft(64, 'a'),
            Npub = "npub1test",
            DisplayName = "Test User",
            CreatedAt = DateTime.UtcNow
        };

        // Act
        await _storageService.SaveUserAsync(user);
        var retrieved = await _storageService.GetUserByPublicKeyAsync(user.PublicKeyHex);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(user.Id, retrieved.Id);
        Assert.Equal(user.DisplayName, retrieved.DisplayName);
        Assert.Equal(user.PublicKeyHex, retrieved.PublicKeyHex);
    }

    [Fact]
    public async Task SaveAndRetrieveCurrentUser_ShouldWork()
    {
        // Arrange
        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = "b".PadLeft(64, 'b'),
            PrivateKeyHex = "c".PadLeft(64, 'c'),
            Npub = "npub1current",
            Nsec = "nsec1current",
            DisplayName = "Current User",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };

        // Act
        await _storageService.SaveCurrentUserAsync(user);
        var retrieved = await _storageService.GetCurrentUserAsync();

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(user.Id, retrieved.Id);
        Assert.True(retrieved.IsCurrentUser);
        Assert.Equal(user.PrivateKeyHex, retrieved.PrivateKeyHex);
    }

    [Fact]
    public async Task SaveAndRetrieveChat_ShouldWork()
    {
        // Arrange
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Chat",
            Type = ChatType.Group,
            ParticipantPublicKeys = new List<string> { "pub1", "pub2" },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        // Act
        await _storageService.SaveChatAsync(chat);
        var retrieved = await _storageService.GetChatAsync(chat.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(chat.Id, retrieved.Id);
        Assert.Equal(chat.Name, retrieved.Name);
        Assert.Equal(ChatType.Group, retrieved.Type);
        Assert.Equal(2, retrieved.ParticipantPublicKeys.Count);
    }

    [Fact]
    public async Task SaveAndRetrieveMessage_ShouldWork()
    {
        // Arrange
        var chatId = Guid.NewGuid().ToString();
        var chat = new Chat
        {
            Id = chatId,
            Name = "Message Test Chat",
            Type = ChatType.DirectMessage,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageService.SaveChatAsync(chat);

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ChatId = chatId,
            SenderPublicKey = "sender123",
            Content = "Hello, World!",
            Type = MessageType.Text,
            Status = MessageStatus.Sent,
            Timestamp = DateTime.UtcNow
        };

        // Act
        await _storageService.SaveMessageAsync(message);
        var messages = await _storageService.GetMessagesForChatAsync(chatId);

        // Assert
        Assert.Single(messages);
        Assert.Equal(message.Content, messages.First().Content);
    }

    [Fact]
    public async Task GetAllChats_ShouldReturnOrderedByLastActivity()
    {
        // Arrange
        var chat1 = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Older Chat",
            Type = ChatType.DirectMessage,
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            LastActivityAt = DateTime.UtcNow.AddHours(-2)
        };

        var chat2 = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Newer Chat",
            Type = ChatType.DirectMessage,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            LastActivityAt = DateTime.UtcNow.AddHours(-1)
        };

        var chat3 = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Pinned Chat",
            Type = ChatType.DirectMessage,
            IsPinned = true,
            CreatedAt = DateTime.UtcNow.AddHours(-3),
            LastActivityAt = DateTime.UtcNow.AddHours(-3)
        };

        await _storageService.SaveChatAsync(chat1);
        await _storageService.SaveChatAsync(chat2);
        await _storageService.SaveChatAsync(chat3);

        // Act
        var chats = (await _storageService.GetAllChatsAsync()).ToList();

        // Assert
        Assert.Equal(3, chats.Count);
        Assert.Equal("Pinned Chat", chats[0].Name); // Pinned first
        Assert.Equal("Newer Chat", chats[1].Name);  // Then by activity
        Assert.Equal("Older Chat", chats[2].Name);
    }

    [Fact]
    public async Task SaveAndRetrieveKeyPackage_ShouldWork()
    {
        // Arrange
        var keyPackage = new KeyPackage
        {
            Id = Guid.NewGuid().ToString(),
            OwnerPublicKey = "owner123",
            Data = new byte[] { 1, 2, 3, 4, 5 },
            CiphersuiteId = 0x0001,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            RelayUrls = new List<string> { "wss://relay1.test", "wss://relay2.test" }
        };

        // Act
        await _storageService.SaveKeyPackageAsync(keyPackage);
        var retrieved = await _storageService.GetKeyPackageAsync(keyPackage.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(keyPackage.OwnerPublicKey, retrieved.OwnerPublicKey);
        Assert.Equal(keyPackage.Data, retrieved.Data);
        Assert.Equal(2, retrieved.RelayUrls.Count);
    }

    [Theory]
    [InlineData("ValidColumn")]
    [InlineData("Signer_Relay_Url")]
    [InlineData("column_name")]
    public void ValidateMigrationColumnName_ValidNames_DoesNotThrow(string name)
    {
        StorageService.ValidateMigrationColumnName(name);
    }

    [Theory]
    [InlineData("col; DROP TABLE Users--")]
    [InlineData("col' OR '1'='1")]
    [InlineData("col)")]
    [InlineData("col name")]
    [InlineData("col123")]
    [InlineData("")]
    [InlineData("col-name")]
    public void ValidateMigrationColumnName_SqlInjection_Throws(string name)
    {
        Assert.Throws<InvalidOperationException>(
            () => StorageService.ValidateMigrationColumnName(name));
    }

    // --- Admin role persistence tests ---

    [Fact]
    public async Task SaveChat_AdminPublicKeys_PersistedAsRoles()
    {
        var chat = new Chat
        {
            Id = "admin-test-1",
            Name = "Admin Test Group",
            Type = ChatType.Group,
            ParticipantPublicKeys = new List<string> { "aaa111", "bbb222", "ccc333" },
            AdminPublicKeys = new List<string> { "aaa111" },
            CreatorPublicKey = "aaa111",
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        await _storageService.SaveChatAsync(chat);

        var loaded = await _storageService.GetChatAsync("admin-test-1");
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded.ParticipantPublicKeys.Count);
        Assert.Single(loaded.AdminPublicKeys);
        Assert.Contains("aaa111", loaded.AdminPublicKeys);
        Assert.DoesNotContain("bbb222", loaded.AdminPublicKeys);
    }

    [Fact]
    public async Task SaveChat_CreatorPublicKey_Persisted()
    {
        var chat = new Chat
        {
            Id = "creator-test-1",
            Name = "Creator Test",
            Type = ChatType.Group,
            ParticipantPublicKeys = new List<string> { "abc123" },
            AdminPublicKeys = new List<string> { "abc123" },
            CreatorPublicKey = "abc123",
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        await _storageService.SaveChatAsync(chat);

        var loaded = await _storageService.GetChatAsync("creator-test-1");
        Assert.NotNull(loaded);
        Assert.Equal("abc123", loaded.CreatorPublicKey);
    }

    [Fact]
    public async Task SaveChat_NoAdmins_AllRolesAreMember()
    {
        var chat = new Chat
        {
            Id = "no-admin-test",
            Name = "No Admin Group",
            Type = ChatType.Group,
            ParticipantPublicKeys = new List<string> { "aaa111", "bbb222" },
            AdminPublicKeys = new List<string>(),
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        await _storageService.SaveChatAsync(chat);

        var loaded = await _storageService.GetChatAsync("no-admin-test");
        Assert.NotNull(loaded);
        Assert.Empty(loaded.AdminPublicKeys);
    }

    [Fact]
    public async Task SaveChat_UpdateAdminList_Persisted()
    {
        var chat = new Chat
        {
            Id = "update-admin-test",
            Name = "Update Admin Test",
            Type = ChatType.Group,
            ParticipantPublicKeys = new List<string> { "aaa111", "bbb222" },
            AdminPublicKeys = new List<string> { "aaa111" },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        await _storageService.SaveChatAsync(chat);

        // Promote bbb222 to admin
        chat.AdminPublicKeys.Add("bbb222");
        await _storageService.SaveChatAsync(chat);

        var loaded = await _storageService.GetChatAsync("update-admin-test");
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.AdminPublicKeys.Count);
        Assert.Contains("aaa111", loaded.AdminPublicKeys);
        Assert.Contains("bbb222", loaded.AdminPublicKeys);
    }

    [Fact]
    public async Task SaveChat_IsOutOfSync_SurvivesRoundtrip()
    {
        var chat = new Chat
        {
            Id = "outofsync-roundtrip",
            Name = "Sync Test",
            Type = ChatType.Group,
            IsOutOfSync = true,
            IsResyncPending = false,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageService.SaveChatAsync(chat);

        var loaded = await _storageService.GetChatAsync("outofsync-roundtrip");
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsOutOfSync, "IsOutOfSync should survive DB roundtrip");
        Assert.False(loaded.IsResyncPending, "IsResyncPending should survive DB roundtrip as false");
    }

    [Fact]
    public async Task SaveChat_IsResyncPending_SurvivesRoundtrip()
    {
        var chat = new Chat
        {
            Id = "resyncpending-roundtrip",
            Name = "Resync Test",
            Type = ChatType.Group,
            IsOutOfSync = true,
            IsResyncPending = true,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageService.SaveChatAsync(chat);

        var loaded = await _storageService.GetChatAsync("resyncpending-roundtrip");
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsOutOfSync, "IsOutOfSync should be true");
        Assert.True(loaded.IsResyncPending, "IsResyncPending should survive DB roundtrip as true");
    }

    [Fact]
    public async Task SaveChat_ClearingFlags_SurvivesRoundtrip()
    {
        // Save with flags true
        var chat = new Chat
        {
            Id = "clear-flags-roundtrip",
            Name = "Clear Test",
            Type = ChatType.Group,
            IsOutOfSync = true,
            IsResyncPending = true,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageService.SaveChatAsync(chat);

        // Clear flags and re-save
        chat.IsOutOfSync = false;
        chat.IsResyncPending = false;
        await _storageService.SaveChatAsync(chat);

        var loaded = await _storageService.GetChatAsync("clear-flags-roundtrip");
        Assert.NotNull(loaded);
        Assert.False(loaded!.IsOutOfSync, "IsOutOfSync should be cleared after re-save");
        Assert.False(loaded.IsResyncPending, "IsResyncPending should be cleared after re-save");
    }
}
