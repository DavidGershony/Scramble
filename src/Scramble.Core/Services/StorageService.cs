using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Core.Models;

namespace Scramble.Core.Services;

public class StorageService : IStorageService
{
    private readonly ILogger<StorageService> _logger;
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly ISecureStorage? _secureStorage;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Path to the SQLite database file. Used by MLS storage provider to share the same DB.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>Secure storage for encrypting sensitive data at rest.</summary>
    public ISecureStorage? SecureStorage => _secureStorage;
    private readonly ConcurrentDictionary<string, string?> _settingsCache = new();
    private User? _cachedCurrentUser;
    private bool _initialized;

    public StorageService(string? databasePath = null, ISecureStorage? secureStorage = null)
    {
        _logger = LoggingConfiguration.CreateLogger<StorageService>();
        _secureStorage = secureStorage;

        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Scramble",
            "scramble.db");

        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _logger.LogDebug("Created database directory: {Directory}", directory);
        }

        _connectionString = $"Data Source={_databasePath}";
        _logger.LogInformation("StorageService initialized with database: {DatabasePath} (SecureStorage: {HasSecureStorage})",
            _databasePath, _secureStorage != null);
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            _logger.LogDebug("Database already initialized, skipping");
            return;
        }

        _logger.LogInformation("Initializing database schema");

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            _logger.LogDebug("Database connection opened");

            // Enable WAL mode for concurrent read+write support
            var walCommand = connection.CreateCommand();
            walCommand.CommandText = "PRAGMA journal_mode=WAL";
            var journalMode = await walCommand.ExecuteScalarAsync();
            _logger.LogInformation("SQLite journal mode set to: {Mode}", journalMode);

            var command = connection.CreateCommand();
            command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Users (
                Id TEXT PRIMARY KEY,
                PublicKeyHex TEXT NOT NULL UNIQUE,
                Npub TEXT,
                PrivateKeyHex TEXT,
                Nsec TEXT,
                DisplayName TEXT,
                Username TEXT,
                AvatarUrl TEXT,
                LocalAvatarPath TEXT,
                About TEXT,
                Nip05 TEXT,
                CreatedAt TEXT NOT NULL,
                LastUpdatedAt TEXT,
                IsCurrentUser INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Chats (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Type INTEGER NOT NULL,
                MlsGroupId BLOB,
                MlsEpoch INTEGER NOT NULL DEFAULT 0,
                AvatarUrl TEXT,
                Description TEXT,
                UnreadCount INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                LastActivityAt TEXT NOT NULL,
                IsMuted INTEGER NOT NULL DEFAULT 0,
                IsPinned INTEGER NOT NULL DEFAULT 0,
                IsArchived INTEGER NOT NULL DEFAULT 0,
                WelcomeNostrEventId TEXT
            );

            CREATE TABLE IF NOT EXISTS ChatParticipants (
                ChatId TEXT NOT NULL,
                PublicKeyHex TEXT NOT NULL,
                PRIMARY KEY (ChatId, PublicKeyHex),
                FOREIGN KEY (ChatId) REFERENCES Chats(Id)
            );

            CREATE TABLE IF NOT EXISTS ChatRelays (
                ChatId TEXT NOT NULL,
                RelayUrl TEXT NOT NULL,
                PRIMARY KEY (ChatId, RelayUrl),
                FOREIGN KEY (ChatId) REFERENCES Chats(Id)
            );

            CREATE TABLE IF NOT EXISTS Messages (
                Id TEXT PRIMARY KEY,
                ChatId TEXT NOT NULL,
                SenderPublicKey TEXT NOT NULL,
                Type INTEGER NOT NULL,
                Content TEXT NOT NULL,
                EncryptedContent TEXT,
                NostrEventId TEXT,
                RumorEventId TEXT,
                MlsEpoch INTEGER NOT NULL DEFAULT 0,
                Timestamp TEXT NOT NULL,
                ReceivedAt TEXT,
                Status INTEGER NOT NULL,
                ReplyToMessageId TEXT,
                IsFromCurrentUser INTEGER NOT NULL DEFAULT 0,
                IsEdited INTEGER NOT NULL DEFAULT 0,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (ChatId) REFERENCES Chats(Id)
            );

            CREATE TABLE IF NOT EXISTS MessageReactions (
                MessageId TEXT NOT NULL,
                Emoji TEXT NOT NULL,
                ReactorPublicKey TEXT NOT NULL,
                PRIMARY KEY (MessageId, Emoji, ReactorPublicKey),
                FOREIGN KEY (MessageId) REFERENCES Messages(Id)
            );

            CREATE TABLE IF NOT EXISTS KeyPackages (
                Id TEXT PRIMARY KEY,
                OwnerPublicKey TEXT NOT NULL,
                Data BLOB NOT NULL,
                NostrEventId TEXT,
                CreatedAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                IsUsed INTEGER NOT NULL DEFAULT 0,
                CiphersuiteId INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS KeyPackageRelays (
                KeyPackageId TEXT NOT NULL,
                RelayUrl TEXT NOT NULL,
                PRIMARY KEY (KeyPackageId, RelayUrl),
                FOREIGN KEY (KeyPackageId) REFERENCES KeyPackages(Id)
            );

            CREATE TABLE IF NOT EXISTS MlsStates (
                GroupId TEXT PRIMARY KEY,
                State BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PendingInvites (
                Id TEXT PRIMARY KEY,
                SenderPublicKey TEXT NOT NULL,
                GroupId TEXT,
                WelcomeData BLOB NOT NULL,
                KeyPackageEventId TEXT,
                NostrEventId TEXT NOT NULL,
                ReceivedAt TEXT NOT NULL,
                SenderDisplayName TEXT
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_PendingInvites_NostrEventId ON PendingInvites(NostrEventId);

            CREATE TABLE IF NOT EXISTS DismissedWelcomeEvents (
                NostrEventId TEXT PRIMARY KEY,
                DismissedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS UserRelays (
                PublicKeyHex TEXT NOT NULL,
                RelayUrl TEXT NOT NULL,
                Usage INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (PublicKeyHex, RelayUrl)
            );

            CREATE TABLE IF NOT EXISTS Follows (
                OwnerPublicKey TEXT NOT NULL,
                FollowedPublicKey TEXT NOT NULL,
                Petname TEXT,
                RelayHint TEXT,
                AddedAt TEXT NOT NULL,
                PRIMARY KEY (OwnerPublicKey, FollowedPublicKey)
            );

            CREATE TABLE IF NOT EXISTS Contacts (
                OwnerPublicKey TEXT NOT NULL,
                ContactPublicKey TEXT NOT NULL,
                Petname TEXT,
                Source TEXT NOT NULL,
                FirstSeenAt TEXT NOT NULL,
                LastInteractedAt TEXT NOT NULL,
                PRIMARY KEY (OwnerPublicKey, ContactPublicKey)
            );

            CREATE INDEX IF NOT EXISTS IX_Contacts_LastInteractedAt ON Contacts(OwnerPublicKey, LastInteractedAt DESC);

            CREATE TABLE IF NOT EXISTS AppSettings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ContactRelays (
                PublicKeyHex TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                RelayUrl TEXT NOT NULL,
                Usage INTEGER NOT NULL DEFAULT 0,
                FetchedAt TEXT NOT NULL,
                PRIMARY KEY (PublicKeyHex, Kind, RelayUrl)
            );

            CREATE INDEX IF NOT EXISTS IX_Messages_ChatId ON Messages(ChatId);
            CREATE INDEX IF NOT EXISTS IX_Messages_Timestamp ON Messages(Timestamp);
            CREATE INDEX IF NOT EXISTS IX_Messages_NostrEventId ON Messages(NostrEventId);
            CREATE INDEX IF NOT EXISTS IX_Chats_LastActivityAt ON Chats(LastActivityAt);
            CREATE INDEX IF NOT EXISTS IX_KeyPackages_OwnerPublicKey ON KeyPackages(OwnerPublicKey);
        ";

            await command.ExecuteNonQueryAsync();

            // Migration: add WelcomeNostrEventId column for existing databases
            try
            {
                var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE Chats ADD COLUMN WelcomeNostrEventId TEXT";
                await migrate.ExecuteNonQueryAsync();
            }
            catch (SqliteException)
            {
                // Column already exists — ignore
            }

            // Migration: add NostrGroupId column for MIP-03 h-tag routing
            try
            {
                var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE Chats ADD COLUMN NostrGroupId BLOB";
                await migrate.ExecuteNonQueryAsync();
            }
            catch (SqliteException)
            {
                // Column already exists — ignore
            }

            // Migration: add Status column to KeyPackages
            try
            {
                var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE KeyPackages ADD COLUMN Status INTEGER NOT NULL DEFAULT 0";
                await migrate.ExecuteNonQueryAsync();
            }
            catch (SqliteException)
            {
                // Column already exists — ignore
            }

            // Migration: add signer session columns to Users for NIP-46 auto-reconnect
            // Column names are hardcoded — validated with regex to prevent SQL injection if this pattern is reused.
            foreach (var col in new[] { "SignerRelayUrl", "SignerRemotePubKey", "SignerSecret", "SignerLocalPrivateKeyHex", "SignerLocalPublicKeyHex" })
            {
                ValidateMigrationColumnName(col);

                try
                {
                    var migrate = connection.CreateCommand();
                    migrate.CommandText = $"ALTER TABLE Users ADD COLUMN {col} TEXT";
                    await migrate.ExecuteNonQueryAsync();
                }
                catch (SqliteException)
                {
                    // Column already exists — ignore
                }
            }

            // Migration: add RumorEventId column to Messages (for reaction targeting)
            try
            {
                var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE Messages ADD COLUMN RumorEventId TEXT";
                await migrate.ExecuteNonQueryAsync();
            }
            catch (SqliteException) { /* Column already exists */ }

            // Migration: add MIP-04 media columns to Messages
            var mediaColumns = new[] { "ImageUrl", "FileName", "FileSha256", "EncryptionNonce", "MediaType", "EncryptionVersion", "AudioDurationSeconds" };
            foreach (var col in mediaColumns)
            {
                try
                {
                    var migrate = connection.CreateCommand();
                    var colType = col == "AudioDurationSeconds" ? "REAL" : "TEXT";
                    migrate.CommandText = $"ALTER TABLE Messages ADD COLUMN {col} {colType}";
                    await migrate.ExecuteNonQueryAsync();
                }
                catch (SqliteException)
                {
                    // Column already exists — ignore
                }
            }

            // Migration: add LocalAvatarPath column to Users for cached avatar images
            try
            {
                var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE Users ADD COLUMN LocalAvatarPath TEXT";
                await migrate.ExecuteNonQueryAsync();
            }
            catch (SqliteException) { /* Column already exists */ }

            // Migration: add RelayUrlsJson column to PendingInvites for welcome relay list
            try
            {
                var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE PendingInvites ADD COLUMN RelayUrlsJson TEXT";
                await migrate.ExecuteNonQueryAsync();
            }
            catch (SqliteException) { /* Column already exists */ }

            // Migration: add Role column to ChatParticipants for admin tracking (MIP-01)
            try
            {
                var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE ChatParticipants ADD COLUMN Role TEXT NOT NULL DEFAULT 'member'";
                await migrate.ExecuteNonQueryAsync();
            }
            catch (SqliteException) { /* Column already exists */ }

            // Migration: add CreatorPublicKey column to Chats for group ownership
            try
            {
                var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE Chats ADD COLUMN CreatorPublicKey TEXT";
                await migrate.ExecuteNonQueryAsync();
            }
            catch (SqliteException) { /* Column already exists */ }

            // Migration: add ConsumedAt column to KeyPackages for MIP-00 lifecycle tracking
            try
            {
                var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE KeyPackages ADD COLUMN ConsumedAt TEXT";
                await migrate.ExecuteNonQueryAsync();
            }
            catch (SqliteException) { /* Column already exists */ }

            _initialized = true;
            _logger.LogInformation("Database schema initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database schema");
            throw;
        }
    }

    public async Task<User?> GetCurrentUserAsync()
    {
        if (_cachedCurrentUser != null)
            return _cachedCurrentUser;

        _logger.LogDebug("Retrieving current user from database");

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM Users WHERE IsCurrentUser = 1";

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var user = ReadUser(reader);
                _cachedCurrentUser = user;
                _logger.LogInformation("Retrieved current user: {Npub}...", user.Npub?[..Math.Min(12, user.Npub.Length)] ?? user.PublicKeyHex[..16]);
                return user;
            }

            _logger.LogDebug("No current user found in database");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve current user");
            throw;
        }
    }

    public async Task SaveCurrentUserAsync(User user)
    {
        _logger.LogInformation("Saving current user: {Npub}", user.Npub ?? user.PublicKeyHex[..16]);
        user.IsCurrentUser = true;
        await SaveUserAsync(user);
        _cachedCurrentUser = user;
    }

    public async Task ClearCurrentUserAsync() => await ExecuteWithWriteLockAsync(async () =>
    {
        _logger.LogInformation("Clearing current user flag (logout)");
        _cachedCurrentUser = null;
        _settingsCache.Clear();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Users SET IsCurrentUser = 0 WHERE IsCurrentUser = 1";
        await command.ExecuteNonQueryAsync();
    });

    public async Task<User?> GetUserByPublicKeyAsync(string publicKeyHex)
    {
        // Fast path: if the requested key matches the cached current user, return it
        // directly. This avoids a DB round-trip and — on Desktop — a DPAPI Unprotect
        // call (ReadUser decrypts PrivateKeyHex/Nsec) on every chat switch.
        var cached = _cachedCurrentUser;
        if (cached != null && string.Equals(cached.PublicKeyHex, publicKeyHex, StringComparison.Ordinal))
            return cached;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Users WHERE PublicKeyHex = @PublicKeyHex";
        command.Parameters.AddWithValue("@PublicKeyHex", publicKeyHex);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadUser(reader);
        }

        return null;
    }

    public async Task<Dictionary<string, User>> GetUsersByPublicKeysAsync(IReadOnlyList<string> publicKeyHexList)
    {
        var result = new Dictionary<string, User>(publicKeyHexList.Count);

        if (publicKeyHexList.Count == 0)
            return result;

        // Satisfy what we can from the cached current user (avoids DPAPI per key)
        var remaining = new List<string>(publicKeyHexList.Count);
        var cached = _cachedCurrentUser;
        foreach (var key in publicKeyHexList)
        {
            if (cached != null && string.Equals(cached.PublicKeyHex, key, StringComparison.Ordinal))
                result[key] = cached;
            else
                remaining.Add(key);
        }

        if (remaining.Count == 0)
            return result;

        // Single query with IN clause instead of N sequential queries
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        var paramNames = new string[remaining.Count];
        for (var i = 0; i < remaining.Count; i++)
        {
            paramNames[i] = $"@pk{i}";
            command.Parameters.AddWithValue(paramNames[i], remaining[i]);
        }
        command.CommandText = $"SELECT * FROM Users WHERE PublicKeyHex IN ({string.Join(",", paramNames)})";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var user = ReadUser(reader);
            result[user.PublicKeyHex] = user;
        }

        return result;
    }

    public async Task SaveUserAsync(User user) => await ExecuteWithWriteLockAsync(async () =>
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO Users
            (Id, PublicKeyHex, Npub, PrivateKeyHex, Nsec, DisplayName, Username, AvatarUrl, LocalAvatarPath, About, Nip05, CreatedAt, LastUpdatedAt, IsCurrentUser, SignerRelayUrl, SignerRemotePubKey, SignerSecret, SignerLocalPrivateKeyHex, SignerLocalPublicKeyHex)
            VALUES
            (@Id, @PublicKeyHex, @Npub, @PrivateKeyHex, @Nsec, @DisplayName, @Username, @AvatarUrl, @LocalAvatarPath, @About, @Nip05, @CreatedAt, @LastUpdatedAt, @IsCurrentUser, @SignerRelayUrl, @SignerRemotePubKey, @SignerSecret, @SignerLocalPrivateKeyHex, @SignerLocalPublicKeyHex)";

        command.Parameters.AddWithValue("@Id", user.Id);
        command.Parameters.AddWithValue("@PublicKeyHex", user.PublicKeyHex);
        command.Parameters.AddWithValue("@Npub", user.Npub ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@PrivateKeyHex", ProtectString(user.PrivateKeyHex) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Nsec", ProtectString(user.Nsec) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@DisplayName", user.DisplayName);
        command.Parameters.AddWithValue("@Username", user.Username ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@AvatarUrl", user.AvatarUrl ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@LocalAvatarPath", user.LocalAvatarPath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@About", user.About ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Nip05", user.Nip05 ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CreatedAt", user.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@LastUpdatedAt", user.LastUpdatedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@IsCurrentUser", user.IsCurrentUser ? 1 : 0);
        command.Parameters.AddWithValue("@SignerRelayUrl", user.SignerRelayUrl ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@SignerRemotePubKey", user.SignerRemotePubKey ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@SignerSecret", ProtectString(user.SignerSecret) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@SignerLocalPrivateKeyHex", ProtectString(user.SignerLocalPrivateKeyHex) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@SignerLocalPublicKeyHex", user.SignerLocalPublicKeyHex ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync();
    });

    public async Task<IEnumerable<User>> GetAllUsersAsync()
    {
        var users = new List<User>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Users";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            users.Add(ReadUser(reader));
        }

        return users;
    }

    public async Task<Chat?> GetChatAsync(string chatId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Chats WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", chatId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var chat = ReadChat(reader);
            var (participants, admins) = await GetChatParticipantsAndAdminsAsync(connection, chatId);
            chat.ParticipantPublicKeys = participants;
            chat.AdminPublicKeys = admins;
            chat.RelayUrls = await GetChatRelaysAsync(connection, chatId);
            return chat;
        }

        return null;
    }

    public async Task<IEnumerable<Chat>> GetAllChatsAsync()
    {
        var chats = new List<Chat>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Chats WHERE IsArchived = 0 ORDER BY IsPinned DESC, LastActivityAt DESC";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var chat = ReadChat(reader);
            var (participants, admins) = await GetChatParticipantsAndAdminsAsync(connection, chat.Id);
            chat.ParticipantPublicKeys = participants;
            chat.AdminPublicKeys = admins;
            chat.RelayUrls = await GetChatRelaysAsync(connection, chat.Id);
            chats.Add(chat);
        }

        return chats;
    }

    public async Task<IEnumerable<Chat>> GetArchivedChatsAsync()
    {
        var chats = new List<Chat>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Chats WHERE IsArchived = 1 ORDER BY LastActivityAt DESC";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var chat = ReadChat(reader);
            var (participants, admins) = await GetChatParticipantsAndAdminsAsync(connection, chat.Id);
            chat.ParticipantPublicKeys = participants;
            chat.AdminPublicKeys = admins;
            chat.RelayUrls = await GetChatRelaysAsync(connection, chat.Id);
            chats.Add(chat);
        }

        return chats;
    }

    public async Task<Chat?> GetChatByGroupIdAsync(string groupIdHex)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var groupIdBytes = Convert.FromHexString(groupIdHex);

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Chats WHERE NostrGroupId = @GroupId OR MlsGroupId = @GroupId LIMIT 1";
        command.Parameters.AddWithValue("@GroupId", groupIdBytes);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var chat = ReadChat(reader);
            var (participants, admins) = await GetChatParticipantsAndAdminsAsync(connection, chat.Id);
            chat.ParticipantPublicKeys = participants;
            chat.AdminPublicKeys = admins;
            chat.RelayUrls = await GetChatRelaysAsync(connection, chat.Id);
            return chat;
        }

        return null;
    }

    public async Task SaveChatAsync(Chat chat)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO Chats
            (Id, Name, Type, MlsGroupId, NostrGroupId, MlsEpoch, AvatarUrl, Description, UnreadCount, CreatedAt, LastActivityAt, IsMuted, IsPinned, IsArchived, WelcomeNostrEventId, CreatorPublicKey)
            VALUES
            (@Id, @Name, @Type, @MlsGroupId, @NostrGroupId, @MlsEpoch, @AvatarUrl, @Description, @UnreadCount, @CreatedAt, @LastActivityAt, @IsMuted, @IsPinned, @IsArchived, @WelcomeNostrEventId, @CreatorPublicKey)";

        command.Parameters.AddWithValue("@Id", chat.Id);
        command.Parameters.AddWithValue("@Name", chat.Name);
        command.Parameters.AddWithValue("@Type", (int)chat.Type);
        command.Parameters.AddWithValue("@MlsGroupId", chat.MlsGroupId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@NostrGroupId", chat.NostrGroupId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@MlsEpoch", (long)chat.MlsEpoch);
        command.Parameters.AddWithValue("@AvatarUrl", chat.AvatarUrl ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Description", chat.Description ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@UnreadCount", chat.UnreadCount);
        command.Parameters.AddWithValue("@CreatedAt", chat.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@LastActivityAt", chat.LastActivityAt.ToString("O"));
        command.Parameters.AddWithValue("@IsMuted", chat.IsMuted ? 1 : 0);
        command.Parameters.AddWithValue("@IsPinned", chat.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("@IsArchived", chat.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("@WelcomeNostrEventId", chat.WelcomeNostrEventId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CreatorPublicKey", chat.CreatorPublicKey ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync();

        // Save participants — only overwrite if the chat object has participants.
        // An empty participant list on a Group chat is always a bug (the creator is always
        // a member), so preserve existing participants to prevent accidental data loss.
        if (chat.ParticipantPublicKeys.Count > 0)
        {
            var deleteParticipants = connection.CreateCommand();
            deleteParticipants.CommandText = "DELETE FROM ChatParticipants WHERE ChatId = @ChatId";
            deleteParticipants.Parameters.AddWithValue("@ChatId", chat.Id);
            await deleteParticipants.ExecuteNonQueryAsync();

            foreach (var publicKey in chat.ParticipantPublicKeys)
            {
                var role = chat.AdminPublicKeys.Contains(publicKey.ToLowerInvariant()) ? "admin" : "member";
                var insertParticipant = connection.CreateCommand();
                insertParticipant.CommandText = "INSERT OR IGNORE INTO ChatParticipants (ChatId, PublicKeyHex, Role) VALUES (@ChatId, @PublicKeyHex, @Role)";
                insertParticipant.Parameters.AddWithValue("@ChatId", chat.Id);
                insertParticipant.Parameters.AddWithValue("@PublicKeyHex", publicKey.ToLowerInvariant());
                insertParticipant.Parameters.AddWithValue("@Role", role);
                await insertParticipant.ExecuteNonQueryAsync();
            }
        }

        // Save relays
        var deleteRelays = connection.CreateCommand();
        deleteRelays.CommandText = "DELETE FROM ChatRelays WHERE ChatId = @ChatId";
        deleteRelays.Parameters.AddWithValue("@ChatId", chat.Id);
        await deleteRelays.ExecuteNonQueryAsync();

        foreach (var relayUrl in chat.RelayUrls)
        {
            var insertRelay = connection.CreateCommand();
            insertRelay.CommandText = "INSERT INTO ChatRelays (ChatId, RelayUrl) VALUES (@ChatId, @RelayUrl)";
            insertRelay.Parameters.AddWithValue("@ChatId", chat.Id);
            insertRelay.Parameters.AddWithValue("@RelayUrl", relayUrl);
            await insertRelay.ExecuteNonQueryAsync();
        }
    }

    public async Task DeleteChatAsync(string chatId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM MessageReactions WHERE MessageId IN (SELECT Id FROM Messages WHERE ChatId = @ChatId);
            DELETE FROM Messages WHERE ChatId = @ChatId;
            DELETE FROM ChatParticipants WHERE ChatId = @ChatId;
            DELETE FROM ChatRelays WHERE ChatId = @ChatId;
            DELETE FROM Chats WHERE Id = @ChatId;";
        command.Parameters.AddWithValue("@ChatId", chatId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<Message?> GetMessageAsync(string messageId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Messages WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", messageId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var message = ReadMessage(reader);
            message.Reactions = await GetMessageReactionsAsync(connection, messageId);
            return message;
        }

        return null;
    }

    /// <summary>
    /// Gets the last message for each chat in a single query. Much faster than N individual queries.
    /// </summary>
    public async Task<Dictionary<string, Message>> GetLastMessagePerChatAsync()
    {
        var result = new Dictionary<string, Message>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT m.* FROM Messages m
            INNER JOIN (
                SELECT ChatId, MAX(Timestamp) AS MaxTs
                FROM Messages WHERE IsDeleted = 0
                GROUP BY ChatId
            ) latest ON m.ChatId = latest.ChatId AND m.Timestamp = latest.MaxTs AND m.IsDeleted = 0";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var message = ReadMessage(reader);
            result[message.ChatId] = message;
        }

        return result;
    }

    public async Task<IEnumerable<Message>> GetMessagesForChatAsync(string chatId, int limit = 50, int offset = 0)
    {
        var messages = new List<Message>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT * FROM Messages
            WHERE ChatId = @ChatId AND IsDeleted = 0
            ORDER BY Timestamp DESC
            LIMIT @Limit OFFSET @Offset";
        command.Parameters.AddWithValue("@ChatId", chatId);
        command.Parameters.AddWithValue("@Limit", limit);
        command.Parameters.AddWithValue("@Offset", offset);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var message = ReadMessage(reader);
            message.Reactions = await GetMessageReactionsAsync(connection, message.Id);
            messages.Add(message);
        }

        messages.Reverse();
        return messages;
    }

    public async Task SaveMessageAsync(Message message) => await ExecuteWithWriteLockAsync(async () =>
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO Messages
            (Id, ChatId, SenderPublicKey, Type, Content, EncryptedContent, NostrEventId, RumorEventId, MlsEpoch, Timestamp, ReceivedAt, Status, ReplyToMessageId, IsFromCurrentUser, IsEdited, IsDeleted, ImageUrl, FileName, FileSha256, EncryptionNonce, MediaType, EncryptionVersion, AudioDurationSeconds)
            VALUES
            (@Id, @ChatId, @SenderPublicKey, @Type, @Content, @EncryptedContent, @NostrEventId, @RumorEventId, @MlsEpoch, @Timestamp, @ReceivedAt, @Status, @ReplyToMessageId, @IsFromCurrentUser, @IsEdited, @IsDeleted, @ImageUrl, @FileName, @FileSha256, @EncryptionNonce, @MediaType, @EncryptionVersion, @AudioDurationSeconds)";

        command.Parameters.AddWithValue("@Id", message.Id);
        command.Parameters.AddWithValue("@ChatId", message.ChatId);
        command.Parameters.AddWithValue("@SenderPublicKey", message.SenderPublicKey);
        command.Parameters.AddWithValue("@Type", (int)message.Type);
        command.Parameters.AddWithValue("@Content", message.Content);
        command.Parameters.AddWithValue("@EncryptedContent", message.EncryptedContent ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@NostrEventId", message.NostrEventId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@RumorEventId", message.RumorEventId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@MlsEpoch", (long)message.MlsEpoch);
        command.Parameters.AddWithValue("@Timestamp", message.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("@ReceivedAt", message.ReceivedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Status", (int)message.Status);
        command.Parameters.AddWithValue("@ReplyToMessageId", message.ReplyToMessageId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@IsFromCurrentUser", message.IsFromCurrentUser ? 1 : 0);
        command.Parameters.AddWithValue("@IsEdited", message.IsEdited ? 1 : 0);
        command.Parameters.AddWithValue("@IsDeleted", message.IsDeleted ? 1 : 0);
        command.Parameters.AddWithValue("@ImageUrl", message.ImageUrl ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@FileName", message.FileName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@FileSha256", message.FileSha256 ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@EncryptionNonce", message.EncryptionNonce ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@MediaType", message.MediaType ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@EncryptionVersion", message.EncryptionVersion ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@AudioDurationSeconds", message.AudioDurationSeconds.HasValue ? message.AudioDurationSeconds.Value : (object)DBNull.Value);

        await command.ExecuteNonQueryAsync();

        // Save reactions
        var deleteReactions = connection.CreateCommand();
        deleteReactions.CommandText = "DELETE FROM MessageReactions WHERE MessageId = @MessageId";
        deleteReactions.Parameters.AddWithValue("@MessageId", message.Id);
        await deleteReactions.ExecuteNonQueryAsync();

        foreach (var (emoji, reactors) in message.Reactions)
        {
            foreach (var reactor in reactors)
            {
                var insertReaction = connection.CreateCommand();
                insertReaction.CommandText = "INSERT INTO MessageReactions (MessageId, Emoji, ReactorPublicKey) VALUES (@MessageId, @Emoji, @ReactorPublicKey)";
                insertReaction.Parameters.AddWithValue("@MessageId", message.Id);
                insertReaction.Parameters.AddWithValue("@Emoji", emoji);
                insertReaction.Parameters.AddWithValue("@ReactorPublicKey", reactor);
                await insertReaction.ExecuteNonQueryAsync();
            }
        }
    });

    public async Task UpdateMessageStatusAsync(string messageId, MessageStatus status)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Messages SET Status = @Status WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", messageId);
        command.Parameters.AddWithValue("@Status", (int)status);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteMessageAsync(string messageId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Messages SET IsDeleted = 1, Content = '' WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", messageId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> MessageExistsByNostrEventIdAsync(string nostrEventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Messages WHERE NostrEventId = @NostrEventId";
        command.Parameters.AddWithValue("@NostrEventId", nostrEventId);
        var count = (long)(await command.ExecuteScalarAsync())!;
        return count > 0;
    }

    public async Task<Message?> GetMessageByNostrEventIdAsync(string nostrEventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Messages WHERE NostrEventId = @EventId OR RumorEventId = @EventId LIMIT 1";
        command.Parameters.AddWithValue("@EventId", nostrEventId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var message = ReadMessage(reader);
            message.Reactions = await GetMessageReactionsAsync(connection, message.Id);
            return message;
        }

        return null;
    }

    public async Task<Message?> GetMessageByRumorEventIdAsync(string rumorEventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Messages WHERE RumorEventId = @RumorEventId LIMIT 1";
        command.Parameters.AddWithValue("@RumorEventId", rumorEventId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var message = ReadMessage(reader);
            message.Reactions = await GetMessageReactionsAsync(connection, message.Id);
            return message;
        }

        return null;
    }

    public async Task<KeyPackage?> GetKeyPackageAsync(string id)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM KeyPackages WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var keyPackage = ReadKeyPackage(reader);
            keyPackage.RelayUrls = await GetKeyPackageRelaysAsync(connection, id);
            return keyPackage;
        }

        return null;
    }

    public async Task<IEnumerable<KeyPackage>> GetUnusedKeyPackagesAsync(string ownerPublicKey)
    {
        var keyPackages = new List<KeyPackage>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM KeyPackages WHERE OwnerPublicKey = @OwnerPublicKey AND IsUsed = 0 AND ExpiresAt > @Now";
        command.Parameters.AddWithValue("@OwnerPublicKey", ownerPublicKey);
        command.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var keyPackage = ReadKeyPackage(reader);
            keyPackage.RelayUrls = await GetKeyPackageRelaysAsync(connection, keyPackage.Id);
            keyPackages.Add(keyPackage);
        }

        return keyPackages;
    }

    public async Task SaveKeyPackageAsync(KeyPackage keyPackage)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO KeyPackages
            (Id, OwnerPublicKey, Data, NostrEventId, CreatedAt, ExpiresAt, IsUsed, CiphersuiteId, Status)
            VALUES
            (@Id, @OwnerPublicKey, @Data, @NostrEventId, @CreatedAt, @ExpiresAt, @IsUsed, @CiphersuiteId, @Status)";

        command.Parameters.AddWithValue("@Id", keyPackage.Id);
        command.Parameters.AddWithValue("@OwnerPublicKey", keyPackage.OwnerPublicKey);
        command.Parameters.AddWithValue("@Data", keyPackage.Data);
        command.Parameters.AddWithValue("@NostrEventId", keyPackage.NostrEventId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CreatedAt", keyPackage.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@ExpiresAt", keyPackage.ExpiresAt.ToString("O"));
        command.Parameters.AddWithValue("@IsUsed", keyPackage.IsUsed ? 1 : 0);
        command.Parameters.AddWithValue("@CiphersuiteId", keyPackage.CiphersuiteId);
        command.Parameters.AddWithValue("@Status", (int)keyPackage.Status);

        await command.ExecuteNonQueryAsync();

        // Save relays
        var deleteRelays = connection.CreateCommand();
        deleteRelays.CommandText = "DELETE FROM KeyPackageRelays WHERE KeyPackageId = @KeyPackageId";
        deleteRelays.Parameters.AddWithValue("@KeyPackageId", keyPackage.Id);
        await deleteRelays.ExecuteNonQueryAsync();

        foreach (var relayUrl in keyPackage.RelayUrls)
        {
            var insertRelay = connection.CreateCommand();
            insertRelay.CommandText = "INSERT INTO KeyPackageRelays (KeyPackageId, RelayUrl) VALUES (@KeyPackageId, @RelayUrl)";
            insertRelay.Parameters.AddWithValue("@KeyPackageId", keyPackage.Id);
            insertRelay.Parameters.AddWithValue("@RelayUrl", relayUrl);
            await insertRelay.ExecuteNonQueryAsync();
        }
    }

    public async Task MarkKeyPackageUsedAsync(string id)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE KeyPackages SET IsUsed = 1, ConsumedAt = @ConsumedAt WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@ConsumedAt", DateTime.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateKeyPackageStatusAsync(string id, KeyPackageStatus status)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE KeyPackages SET Status = @Status WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Status", (int)status);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<KeyPackage?> GetKeyPackageByNostrEventIdAsync(string nostrEventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM KeyPackages WHERE NostrEventId = @NostrEventId";
        command.Parameters.AddWithValue("@NostrEventId", nostrEventId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var keyPackage = ReadKeyPackage(reader);
            keyPackage.RelayUrls = await GetKeyPackageRelaysAsync(connection, keyPackage.Id);
            return keyPackage;
        }

        return null;
    }

    public async Task<IEnumerable<KeyPackage>> GetAllKeyPackagesAsync(string ownerPublicKey)
    {
        var keyPackages = new List<KeyPackage>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM KeyPackages WHERE OwnerPublicKey = @OwnerPublicKey ORDER BY CreatedAt DESC";
        command.Parameters.AddWithValue("@OwnerPublicKey", ownerPublicKey);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var keyPackage = ReadKeyPackage(reader);
            keyPackage.RelayUrls = await GetKeyPackageRelaysAsync(connection, keyPackage.Id);
            keyPackages.Add(keyPackage);
        }

        return keyPackages;
    }

    public async Task SaveUserRelayListAsync(string publicKeyHex, IEnumerable<RelayPreference> relays)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        // Delete existing relays for this user
        var deleteCmd = connection.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM UserRelays WHERE PublicKeyHex = @pubkey";
        deleteCmd.Parameters.AddWithValue("@pubkey", publicKeyHex);
        await deleteCmd.ExecuteNonQueryAsync();

        // Insert new relays
        foreach (var relay in relays)
        {
            var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = @"
                INSERT INTO UserRelays (PublicKeyHex, RelayUrl, Usage)
                VALUES (@pubkey, @url, @usage)";
            insertCmd.Parameters.AddWithValue("@pubkey", publicKeyHex);
            insertCmd.Parameters.AddWithValue("@url", relay.Url);
            insertCmd.Parameters.AddWithValue("@usage", (int)relay.Usage);
            await insertCmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        _logger.LogDebug("Saved {Count} relays for {PubKey}", relays.Count(), publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");
    }

    public async Task<List<RelayPreference>> GetUserRelayListAsync(string publicKeyHex)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT RelayUrl, Usage FROM UserRelays WHERE PublicKeyHex = @pubkey";
        command.Parameters.AddWithValue("@pubkey", publicKeyHex);

        var relays = new List<RelayPreference>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            relays.Add(new RelayPreference
            {
                Url = reader.GetString(0),
                Usage = (RelayUsage)reader.GetInt32(1)
            });
        }

        return relays;
    }

    public async Task SaveContactRelayListAsync(string publicKeyHex, int kind, IEnumerable<string> relayUrls)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        var now = DateTimeOffset.UtcNow.ToString("o");

        var deleteCmd = connection.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM ContactRelays WHERE PublicKeyHex = @pubkey AND Kind = @kind";
        deleteCmd.Parameters.AddWithValue("@pubkey", publicKeyHex);
        deleteCmd.Parameters.AddWithValue("@kind", kind);
        await deleteCmd.ExecuteNonQueryAsync();

        foreach (var url in relayUrls)
        {
            var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = @"
                INSERT INTO ContactRelays (PublicKeyHex, Kind, RelayUrl, Usage, FetchedAt)
                VALUES (@pubkey, @kind, @url, 0, @fetchedAt)";
            insertCmd.Parameters.AddWithValue("@pubkey", publicKeyHex);
            insertCmd.Parameters.AddWithValue("@kind", kind);
            insertCmd.Parameters.AddWithValue("@url", url);
            insertCmd.Parameters.AddWithValue("@fetchedAt", now);
            await insertCmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        _logger.LogDebug("Cached {Count} kind {Kind} relays for {PubKey}", relayUrls.Count(), kind, publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");
    }

    public async Task SaveContactRelayPreferencesAsync(string publicKeyHex, IEnumerable<RelayPreference> relays)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        var now = DateTimeOffset.UtcNow.ToString("o");

        var deleteCmd = connection.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM ContactRelays WHERE PublicKeyHex = @pubkey AND Kind = 10002";
        deleteCmd.Parameters.AddWithValue("@pubkey", publicKeyHex);
        await deleteCmd.ExecuteNonQueryAsync();

        foreach (var relay in relays)
        {
            var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = @"
                INSERT INTO ContactRelays (PublicKeyHex, Kind, RelayUrl, Usage, FetchedAt)
                VALUES (@pubkey, 10002, @url, @usage, @fetchedAt)";
            insertCmd.Parameters.AddWithValue("@pubkey", publicKeyHex);
            insertCmd.Parameters.AddWithValue("@url", relay.Url);
            insertCmd.Parameters.AddWithValue("@usage", (int)relay.Usage);
            insertCmd.Parameters.AddWithValue("@fetchedAt", now);
            await insertCmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        _logger.LogDebug("Cached {Count} NIP-65 relays for {PubKey}", relays.Count(), publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");
    }

    public async Task<(List<string> Urls, DateTimeOffset FetchedAt)?> GetContactRelayListAsync(string publicKeyHex, int kind)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT RelayUrl, FetchedAt FROM ContactRelays WHERE PublicKeyHex = @pubkey AND Kind = @kind";
        command.Parameters.AddWithValue("@pubkey", publicKeyHex);
        command.Parameters.AddWithValue("@kind", kind);

        var urls = new List<string>();
        DateTimeOffset fetchedAt = default;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            urls.Add(reader.GetString(0));
            if (fetchedAt == default)
                fetchedAt = DateTimeOffset.Parse(reader.GetString(1));
        }

        return urls.Count > 0 ? (urls, fetchedAt) : null;
    }

    public async Task<(List<RelayPreference> Relays, DateTimeOffset FetchedAt)?> GetContactRelayPreferencesAsync(string publicKeyHex)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT RelayUrl, Usage, FetchedAt FROM ContactRelays WHERE PublicKeyHex = @pubkey AND Kind = 10002";
        command.Parameters.AddWithValue("@pubkey", publicKeyHex);

        var relays = new List<RelayPreference>();
        DateTimeOffset fetchedAt = default;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            relays.Add(new RelayPreference
            {
                Url = reader.GetString(0),
                Usage = (RelayUsage)reader.GetInt32(1)
            });
            if (fetchedAt == default)
                fetchedAt = DateTimeOffset.Parse(reader.GetString(2));
        }

        return relays.Count > 0 ? (relays, fetchedAt) : null;
    }

    public async Task SaveFollowsAsync(string ownerPublicKey, IEnumerable<Follow> follows) => await ExecuteWithWriteLockAsync(async () =>
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var owner = ownerPublicKey.ToLowerInvariant();

        var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM Follows WHERE OwnerPublicKey = @owner";
        delete.Parameters.AddWithValue("@owner", owner);
        await delete.ExecuteNonQueryAsync();

        foreach (var f in follows)
        {
            if (string.IsNullOrEmpty(f.PublicKeyHex)) continue;
            var insert = connection.CreateCommand();
            insert.CommandText = @"INSERT OR REPLACE INTO Follows
                (OwnerPublicKey, FollowedPublicKey, Petname, RelayHint, AddedAt)
                VALUES (@owner, @followed, @petname, @relay, @addedAt)";
            insert.Parameters.AddWithValue("@owner", owner);
            insert.Parameters.AddWithValue("@followed", f.PublicKeyHex.ToLowerInvariant());
            insert.Parameters.AddWithValue("@petname", (object?)f.Petname ?? DBNull.Value);
            insert.Parameters.AddWithValue("@relay", (object?)f.RelayHint ?? DBNull.Value);
            insert.Parameters.AddWithValue("@addedAt", f.AddedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync();

            var upsertContact = connection.CreateCommand();
            upsertContact.CommandText = @"
                INSERT INTO Contacts (OwnerPublicKey, ContactPublicKey, Petname, Source, FirstSeenAt, LastInteractedAt)
                VALUES (@owner, @contact, @petname, 'follow', @now, @now)
                ON CONFLICT(OwnerPublicKey, ContactPublicKey) DO UPDATE SET
                    Petname = COALESCE(excluded.Petname, Contacts.Petname),
                    Source = 'follow'";
            upsertContact.Parameters.AddWithValue("@owner", owner);
            upsertContact.Parameters.AddWithValue("@contact", f.PublicKeyHex.ToLowerInvariant());
            upsertContact.Parameters.AddWithValue("@petname", (object?)f.Petname ?? DBNull.Value);
            upsertContact.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            await upsertContact.ExecuteNonQueryAsync();
        }
    });

    public async Task<List<Follow>> GetFollowsAsync(string ownerPublicKey)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT FollowedPublicKey, Petname, RelayHint, AddedAt FROM Follows WHERE OwnerPublicKey = @owner";
        command.Parameters.AddWithValue("@owner", ownerPublicKey.ToLowerInvariant());

        var follows = new List<Follow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            follows.Add(new Follow
            {
                PublicKeyHex = reader.GetString(0),
                Petname = reader.IsDBNull(1) ? null : reader.GetString(1),
                RelayHint = reader.IsDBNull(2) ? null : reader.GetString(2),
                AddedAt = DateTime.TryParse(reader.GetString(3), out var dt) ? dt : DateTime.UtcNow
            });
        }
        return follows;
    }

    public async Task UpsertContactsAsync(string ownerPublicKey, IEnumerable<Contact> contacts) => await ExecuteWithWriteLockAsync(async () =>
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var owner = ownerPublicKey.ToLowerInvariant();
        var now = DateTime.UtcNow.ToString("O");

        foreach (var c in contacts)
        {
            if (string.IsNullOrEmpty(c.PublicKeyHex)) continue;
            var key = c.PublicKeyHex.ToLowerInvariant();
            if (string.Equals(key, owner, StringComparison.OrdinalIgnoreCase)) continue;

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Contacts (OwnerPublicKey, ContactPublicKey, Petname, Source, FirstSeenAt, LastInteractedAt)
                VALUES (@owner, @contact, @petname, @source, @firstSeen, @lastInter)
                ON CONFLICT(OwnerPublicKey, ContactPublicKey) DO UPDATE SET
                    Petname = COALESCE(excluded.Petname, Contacts.Petname),
                    Source = CASE WHEN Contacts.Source = 'follow' THEN Contacts.Source ELSE excluded.Source END";
            cmd.Parameters.AddWithValue("@owner", owner);
            cmd.Parameters.AddWithValue("@contact", key);
            cmd.Parameters.AddWithValue("@petname", (object?)c.Petname ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source", c.Source);
            cmd.Parameters.AddWithValue("@firstSeen", c.FirstSeenAt == default ? now : c.FirstSeenAt.ToString("O"));
            cmd.Parameters.AddWithValue("@lastInter", c.LastInteractedAt == default ? now : c.LastInteractedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
    });

    public async Task TouchContactAsync(string ownerPublicKey, string contactPublicKey) => await ExecuteWithWriteLockAsync(async () =>
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"UPDATE Contacts SET LastInteractedAt = @now
            WHERE OwnerPublicKey = @owner AND ContactPublicKey = @contact";
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@owner", ownerPublicKey.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@contact", contactPublicKey.ToLowerInvariant());
        await cmd.ExecuteNonQueryAsync();
    });

    public async Task<List<Contact>> GetContactsAsync(string ownerPublicKey)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"SELECT ContactPublicKey, Petname, Source, FirstSeenAt, LastInteractedAt
            FROM Contacts WHERE OwnerPublicKey = @owner
            ORDER BY LastInteractedAt DESC";
        command.Parameters.AddWithValue("@owner", ownerPublicKey.ToLowerInvariant());

        var result = new List<Contact>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new Contact
            {
                PublicKeyHex = reader.GetString(0),
                Petname = reader.IsDBNull(1) ? null : reader.GetString(1),
                Source = reader.GetString(2),
                FirstSeenAt = DateTime.TryParse(reader.GetString(3), out var fs) ? fs : DateTime.UtcNow,
                LastInteractedAt = DateTime.TryParse(reader.GetString(4), out var li) ? li : DateTime.UtcNow
            });
        }
        return result;
    }

    public async Task SaveMlsStateAsync(string groupId, byte[] state) => await ExecuteWithWriteLockAsync(async () =>
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO MlsStates (GroupId, State) VALUES (@GroupId, @State)";
        command.Parameters.AddWithValue("@GroupId", groupId);
        command.Parameters.AddWithValue("@State", ProtectBlob(state));

        await command.ExecuteNonQueryAsync();
    });

    public async Task<byte[]?> GetMlsStateAsync(string groupId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM MlsStates WHERE GroupId = @GroupId";
        command.Parameters.AddWithValue("@GroupId", groupId);

        var result = await command.ExecuteScalarAsync();
        if (result is byte[] data)
            return UnprotectBlob(data);
        return null;
    }

    public async Task DeleteMlsStateAsync(string groupId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM MlsStates WHERE GroupId = @GroupId";
        command.Parameters.AddWithValue("@GroupId", groupId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task UndismissWelcomeEventAsync(string nostrEventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DismissedWelcomeEvents WHERE NostrEventId = @NostrEventId";
        command.Parameters.AddWithValue("@NostrEventId", nostrEventId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IEnumerable<PendingInvite>> GetPendingInvitesAsync()
    {
        var invites = new List<PendingInvite>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PendingInvites ORDER BY ReceivedAt DESC";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            invites.Add(ReadPendingInvite(reader));
        }

        return invites;
    }

    public async Task SavePendingInviteAsync(PendingInvite invite)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR IGNORE INTO PendingInvites
            (Id, SenderPublicKey, GroupId, WelcomeData, KeyPackageEventId, NostrEventId, ReceivedAt, SenderDisplayName, RelayUrlsJson)
            VALUES
            (@Id, @SenderPublicKey, @GroupId, @WelcomeData, @KeyPackageEventId, @NostrEventId, @ReceivedAt, @SenderDisplayName, @RelayUrlsJson)";

        command.Parameters.AddWithValue("@Id", invite.Id);
        command.Parameters.AddWithValue("@SenderPublicKey", invite.SenderPublicKey);
        command.Parameters.AddWithValue("@GroupId", invite.GroupId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@WelcomeData", invite.WelcomeData);
        command.Parameters.AddWithValue("@KeyPackageEventId", invite.KeyPackageEventId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@NostrEventId", invite.NostrEventId);
        command.Parameters.AddWithValue("@ReceivedAt", invite.ReceivedAt.ToString("O"));
        command.Parameters.AddWithValue("@SenderDisplayName", invite.SenderDisplayName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@RelayUrlsJson",
            invite.RelayUrls.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(invite.RelayUrls) : (object)DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeletePendingInviteAsync(string inviteId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PendingInvites WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", inviteId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DismissWelcomeEventAsync(string nostrEventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR IGNORE INTO DismissedWelcomeEvents (NostrEventId, DismissedAt)
            VALUES (@NostrEventId, @DismissedAt)";
        command.Parameters.AddWithValue("@NostrEventId", nostrEventId);
        command.Parameters.AddWithValue("@DismissedAt", DateTime.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> IsWelcomeEventDismissedAsync(string nostrEventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DismissedWelcomeEvents WHERE NostrEventId = @NostrEventId";
        command.Parameters.AddWithValue("@NostrEventId", nostrEventId);

        var count = (long)(await command.ExecuteScalarAsync())!;
        return count > 0;
    }

    private static PendingInvite ReadPendingInvite(SqliteDataReader reader)
    {
        var invite = new PendingInvite
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            SenderPublicKey = reader.GetString(reader.GetOrdinal("SenderPublicKey")),
            GroupId = reader.IsDBNull(reader.GetOrdinal("GroupId")) ? null : reader.GetString(reader.GetOrdinal("GroupId")),
            WelcomeData = (byte[])reader["WelcomeData"],
            KeyPackageEventId = reader.IsDBNull(reader.GetOrdinal("KeyPackageEventId")) ? null : reader.GetString(reader.GetOrdinal("KeyPackageEventId")),
            NostrEventId = reader.GetString(reader.GetOrdinal("NostrEventId")),
            ReceivedAt = ParseDateTime(reader.GetString(reader.GetOrdinal("ReceivedAt"))),
            SenderDisplayName = reader.IsDBNull(reader.GetOrdinal("SenderDisplayName")) ? null : reader.GetString(reader.GetOrdinal("SenderDisplayName"))
        };

        // RelayUrlsJson column may not exist in older databases before migration
        try
        {
            var relayOrd = reader.GetOrdinal("RelayUrlsJson");
            if (!reader.IsDBNull(relayOrd))
            {
                var json = reader.GetString(relayOrd);
                invite.RelayUrls = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new();
            }
        }
        catch (ArgumentOutOfRangeException) { /* Column doesn't exist yet */ }

        return invite;
    }

    private User ReadUser(SqliteDataReader reader)
    {
        var user = new User
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            PublicKeyHex = reader.GetString(reader.GetOrdinal("PublicKeyHex")),
            Npub = reader.IsDBNull(reader.GetOrdinal("Npub")) ? string.Empty : reader.GetString(reader.GetOrdinal("Npub")),
            PrivateKeyHex = UnprotectString(reader.IsDBNull(reader.GetOrdinal("PrivateKeyHex")) ? null : reader.GetString(reader.GetOrdinal("PrivateKeyHex"))),
            Nsec = UnprotectString(reader.IsDBNull(reader.GetOrdinal("Nsec")) ? null : reader.GetString(reader.GetOrdinal("Nsec"))),
            DisplayName = reader.GetString(reader.GetOrdinal("DisplayName")),
            Username = reader.IsDBNull(reader.GetOrdinal("Username")) ? null : reader.GetString(reader.GetOrdinal("Username")),
            AvatarUrl = reader.IsDBNull(reader.GetOrdinal("AvatarUrl")) ? null : reader.GetString(reader.GetOrdinal("AvatarUrl")),
            LocalAvatarPath = SafeGetString(reader, "LocalAvatarPath"),
            About = reader.IsDBNull(reader.GetOrdinal("About")) ? null : reader.GetString(reader.GetOrdinal("About")),
            Nip05 = reader.IsDBNull(reader.GetOrdinal("Nip05")) ? null : reader.GetString(reader.GetOrdinal("Nip05")),
            CreatedAt = ParseDateTime(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            LastUpdatedAt = reader.IsDBNull(reader.GetOrdinal("LastUpdatedAt")) ? null : ParseDateTime(reader.GetString(reader.GetOrdinal("LastUpdatedAt"))),
            IsCurrentUser = reader.GetInt32(reader.GetOrdinal("IsCurrentUser")) == 1
        };

        // Signer session columns may not exist in older databases before migration
        try
        {
            var ordinal = reader.GetOrdinal("SignerRelayUrl");
            user.SignerRelayUrl = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        catch (ArgumentOutOfRangeException) { /* Column doesn't exist yet */ }

        try
        {
            var ordinal = reader.GetOrdinal("SignerRemotePubKey");
            user.SignerRemotePubKey = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        catch (ArgumentOutOfRangeException) { /* Column doesn't exist yet */ }

        try
        {
            var ordinal = reader.GetOrdinal("SignerSecret");
            user.SignerSecret = UnprotectString(reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal));
        }
        catch (ArgumentOutOfRangeException) { /* Column doesn't exist yet */ }

        try
        {
            var ordinal = reader.GetOrdinal("SignerLocalPrivateKeyHex");
            user.SignerLocalPrivateKeyHex = UnprotectString(reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal));
        }
        catch (ArgumentOutOfRangeException) { /* Column doesn't exist yet */ }

        try
        {
            var ordinal = reader.GetOrdinal("SignerLocalPublicKeyHex");
            user.SignerLocalPublicKeyHex = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        catch (ArgumentOutOfRangeException) { /* Column doesn't exist yet */ }

        return user;
    }

    private static Chat ReadChat(SqliteDataReader reader)
    {
        var welcomeOrdinal = reader.GetOrdinal("WelcomeNostrEventId");

        // NostrGroupId column may not exist in older databases before migration
        byte[]? nostrGroupId = null;
        try
        {
            var nostrGroupIdOrdinal = reader.GetOrdinal("NostrGroupId");
            nostrGroupId = reader.IsDBNull(nostrGroupIdOrdinal) ? null : (byte[])reader["NostrGroupId"];
        }
        catch (ArgumentOutOfRangeException) { /* Column doesn't exist yet */ }

        // CreatorPublicKey column may not exist in older databases before migration
        string? creatorPublicKey = null;
        try
        {
            var creatorOrdinal = reader.GetOrdinal("CreatorPublicKey");
            creatorPublicKey = reader.IsDBNull(creatorOrdinal) ? null : reader.GetString(creatorOrdinal);
        }
        catch (ArgumentOutOfRangeException) { /* Column doesn't exist yet */ }

        return new Chat
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Type = (ChatType)reader.GetInt32(reader.GetOrdinal("Type")),
            MlsGroupId = reader.IsDBNull(reader.GetOrdinal("MlsGroupId")) ? null : (byte[])reader["MlsGroupId"],
            NostrGroupId = nostrGroupId,
            MlsEpoch = (ulong)reader.GetInt64(reader.GetOrdinal("MlsEpoch")),
            AvatarUrl = reader.IsDBNull(reader.GetOrdinal("AvatarUrl")) ? null : reader.GetString(reader.GetOrdinal("AvatarUrl")),
            Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
            UnreadCount = reader.GetInt32(reader.GetOrdinal("UnreadCount")),
            CreatedAt = ParseDateTime(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            LastActivityAt = ParseDateTime(reader.GetString(reader.GetOrdinal("LastActivityAt"))),
            IsMuted = reader.GetInt32(reader.GetOrdinal("IsMuted")) == 1,
            IsPinned = reader.GetInt32(reader.GetOrdinal("IsPinned")) == 1,
            IsArchived = reader.GetInt32(reader.GetOrdinal("IsArchived")) == 1,
            WelcomeNostrEventId = reader.IsDBNull(welcomeOrdinal) ? null : reader.GetString(welcomeOrdinal),
            CreatorPublicKey = creatorPublicKey
        };
    }

    private static Message ReadMessage(SqliteDataReader reader)
    {
        return new Message
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            ChatId = reader.GetString(reader.GetOrdinal("ChatId")),
            SenderPublicKey = reader.GetString(reader.GetOrdinal("SenderPublicKey")),
            Type = (MessageType)reader.GetInt32(reader.GetOrdinal("Type")),
            Content = reader.GetString(reader.GetOrdinal("Content")),
            EncryptedContent = reader.IsDBNull(reader.GetOrdinal("EncryptedContent")) ? null : reader.GetString(reader.GetOrdinal("EncryptedContent")),
            NostrEventId = reader.IsDBNull(reader.GetOrdinal("NostrEventId")) ? null : reader.GetString(reader.GetOrdinal("NostrEventId")),
            RumorEventId = TryGetString(reader, "RumorEventId"),
            MlsEpoch = (ulong)reader.GetInt64(reader.GetOrdinal("MlsEpoch")),
            Timestamp = ParseDateTime(reader.GetString(reader.GetOrdinal("Timestamp"))),
            ReceivedAt = reader.IsDBNull(reader.GetOrdinal("ReceivedAt")) ? null : ParseDateTime(reader.GetString(reader.GetOrdinal("ReceivedAt"))),
            Status = (MessageStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            ReplyToMessageId = reader.IsDBNull(reader.GetOrdinal("ReplyToMessageId")) ? null : reader.GetString(reader.GetOrdinal("ReplyToMessageId")),
            IsFromCurrentUser = reader.GetInt32(reader.GetOrdinal("IsFromCurrentUser")) == 1,
            IsEdited = reader.GetInt32(reader.GetOrdinal("IsEdited")) == 1,
            IsDeleted = reader.GetInt32(reader.GetOrdinal("IsDeleted")) == 1,
            ImageUrl = TryGetString(reader, "ImageUrl"),
            FileName = TryGetString(reader, "FileName"),
            FileSha256 = TryGetString(reader, "FileSha256"),
            EncryptionNonce = TryGetString(reader, "EncryptionNonce"),
            MediaType = TryGetString(reader, "MediaType"),
            EncryptionVersion = TryGetString(reader, "EncryptionVersion"),
            AudioDurationSeconds = TryGetDouble(reader, "AudioDurationSeconds")
        };
    }

    /// <summary>
    /// Parse a DateTime string preserving its original Kind (UTC 'Z' stays UTC).
    /// Prevents timestamp drift when roundtripping through ToString("O").
    /// </summary>
    private static DateTime ParseDateTime(string value)
        => DateTime.Parse(value, null, DateTimeStyles.RoundtripKind);

    private static string? SafeGetString(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        catch (ArgumentOutOfRangeException) { return null; /* Column doesn't exist yet */ }
    }

    private static string? TryGetString(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        catch (ArgumentOutOfRangeException) { return null; } // Column doesn't exist yet
    }

    private static double? TryGetDouble(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static KeyPackage ReadKeyPackage(SqliteDataReader reader)
    {
        var kp = new KeyPackage
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            OwnerPublicKey = reader.GetString(reader.GetOrdinal("OwnerPublicKey")),
            Data = (byte[])reader["Data"],
            NostrEventId = reader.IsDBNull(reader.GetOrdinal("NostrEventId")) ? null : reader.GetString(reader.GetOrdinal("NostrEventId")),
            CreatedAt = ParseDateTime(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            ExpiresAt = ParseDateTime(reader.GetString(reader.GetOrdinal("ExpiresAt"))),
            IsUsed = reader.GetInt32(reader.GetOrdinal("IsUsed")) == 1,
            CiphersuiteId = (ushort)reader.GetInt32(reader.GetOrdinal("CiphersuiteId"))
        };

        // Status column may not exist in older databases before migration runs
        try
        {
            var statusOrdinal = reader.GetOrdinal("Status");
            if (!reader.IsDBNull(statusOrdinal))
                kp.Status = (KeyPackageStatus)reader.GetInt32(statusOrdinal);
        }
        catch (IndexOutOfRangeException)
        {
            // Column doesn't exist yet — default is Active
        }

        var consumedAtStr = TryGetString(reader, "ConsumedAt");
        if (consumedAtStr != null)
            kp.ConsumedAt = ParseDateTime(consumedAtStr);

        return kp;
    }

    private static async Task<(List<string> participants, List<string> admins)> GetChatParticipantsAndAdminsAsync(SqliteConnection connection, string chatId)
    {
        var participants = new List<string>();
        var admins = new List<string>();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT PublicKeyHex, Role FROM ChatParticipants WHERE ChatId = @ChatId";
        command.Parameters.AddWithValue("@ChatId", chatId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var pubkey = reader.GetString(0);
            participants.Add(pubkey);
            try
            {
                var role = reader.GetString(1);
                if (role == "admin")
                    admins.Add(pubkey);
            }
            catch (InvalidCastException) { /* Role column not yet populated */ }
        }

        return (participants, admins);
    }

    private static async Task<List<string>> GetChatRelaysAsync(SqliteConnection connection, string chatId)
    {
        var relays = new List<string>();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT RelayUrl FROM ChatRelays WHERE ChatId = @ChatId";
        command.Parameters.AddWithValue("@ChatId", chatId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            relays.Add(reader.GetString(0));
        }

        return relays;
    }

    private static async Task<Dictionary<string, List<string>>> GetMessageReactionsAsync(SqliteConnection connection, string messageId)
    {
        var reactions = new Dictionary<string, List<string>>();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT Emoji, ReactorPublicKey FROM MessageReactions WHERE MessageId = @MessageId";
        command.Parameters.AddWithValue("@MessageId", messageId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var emoji = reader.GetString(0);
            var reactor = reader.GetString(1);

            if (!reactions.ContainsKey(emoji))
            {
                reactions[emoji] = new List<string>();
            }
            reactions[emoji].Add(reactor);
        }

        return reactions;
    }

    private static async Task<List<string>> GetKeyPackageRelaysAsync(SqliteConnection connection, string keyPackageId)
    {
        var relays = new List<string>();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT RelayUrl FROM KeyPackageRelays WHERE KeyPackageId = @KeyPackageId";
        command.Parameters.AddWithValue("@KeyPackageId", keyPackageId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            relays.Add(reader.GetString(0));
        }

        return relays;
    }

    /// <summary>
    /// Encrypts a string value for storage. Returns null if the input is null.
    /// The encrypted bytes are returned as a base64 string.
    /// Throws if encryption fails — never falls back to plaintext.
    /// </summary>
    private string? ProtectString(string? value)
    {
        if (value == null)
            return null;

        if (_secureStorage == null)
            throw new InvalidOperationException(
                "Cannot protect sensitive data: secure storage is not configured. " +
                "Private keys and secrets require encrypted storage.");

        var plainBytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = _secureStorage.Protect(plainBytes);
        return Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// Decrypts a string value read from storage. Returns null if the input is null.
    /// Handles plaintext (backward compat) via FormatException on base64 decode.
    /// Throws on decryption failure — never returns corrupted/encrypted data as plaintext.
    /// </summary>
    private string? UnprotectString(string? value)
    {
        if (value == null || _secureStorage == null)
            return value;

        try
        {
            var protectedBytes = Convert.FromBase64String(value);
            var plainBytes = _secureStorage.Unprotect(protectedBytes);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (FormatException)
        {
            // Not valid base64 — this is plaintext data from before encryption was enabled
            _logger.LogInformation("Value is not base64-encoded, returning as plaintext (pre-encryption data)");
            return value;
        }
    }

    /// <summary>
    /// Encrypts a blob for storage.
    /// Throws if encryption fails — never falls back to plaintext.
    /// </summary>
    private byte[] ProtectBlob(byte[] data)
    {
        if (_secureStorage == null)
            throw new InvalidOperationException(
                "Cannot protect sensitive data: secure storage is not configured. " +
                "MLS state and key material require encrypted storage.");

        return _secureStorage.Protect(data);
    }

    /// <summary>
    /// Decrypts a blob read from storage.
    /// Throws on decryption failure — never returns encrypted data as plaintext.
    /// The Unprotect implementation handles the magic prefix check for backward compat.
    /// </summary>
    private byte[] UnprotectBlob(byte[] data)
    {
        if (_secureStorage == null)
            throw new InvalidOperationException(
                "Cannot unprotect sensitive data: secure storage is not configured.");

        return _secureStorage.Unprotect(data);
    }

    // ---- App Settings ----

    public async Task<string?> GetSettingAsync(string key)
    {
        if (_settingsCache.TryGetValue(key, out var cached))
            return cached;

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM AppSettings WHERE Key = @Key";
            command.Parameters.AddWithValue("@Key", key);

            var result = await command.ExecuteScalarAsync();
            var value = result as string;
            _settingsCache[key] = value;
            _logger.LogDebug("GetSetting: key={Key}, found={Found}", key, value != null);
            return value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get setting: {Key}", key);
            return null;
        }
    }

    public async Task SaveSettingAsync(string key, string value)
    {
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO AppSettings (Key, Value) VALUES (@Key, @Value)
                ON CONFLICT(Key) DO UPDATE SET Value = @Value";
            command.Parameters.AddWithValue("@Key", key);
            command.Parameters.AddWithValue("@Value", value);

            await command.ExecuteNonQueryAsync();
            _settingsCache[key] = value;
            _logger.LogInformation("SaveSetting: key={Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save setting: {Key}", key);
            throw;
        }
    }

    private const string SkippedInviteCountKey = "SkippedInviteCount";

    public async Task<int> GetSkippedInviteCountAsync()
    {
        var value = await GetSettingAsync(SkippedInviteCountKey);
        return value != null && int.TryParse(value, out var count) ? count : 0;
    }

    public async Task IncrementSkippedInviteCountAsync()
    {
        var current = await GetSkippedInviteCountAsync();
        await SaveSettingAsync(SkippedInviteCountKey, (current + 1).ToString());
        _logger.LogInformation("IncrementSkippedInviteCount: new count={Count}", current + 1);
    }

    public async Task ResetSkippedInviteCountAsync()
    {
        await SaveSettingAsync(SkippedInviteCountKey, "0");
        _logger.LogInformation("ResetSkippedInviteCount: count reset to 0");
    }

    /// <summary>
    /// Executes a write operation with serialized access to prevent "database is locked" errors.
    /// WAL mode allows concurrent reads, but writes must be serialized.
    /// </summary>
    private async Task ExecuteWithWriteLockAsync(Func<Task> action)
    {
        await _writeLock.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Executes a write operation with serialized access and returns a result.
    /// </summary>
    private async Task<T> ExecuteWithWriteLockAsync<T>(Func<Task<T>> action)
    {
        await _writeLock.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Validates that a column name is safe for use in DDL statements.
    /// DDL column names cannot be parameterized in SQL, so we whitelist [a-zA-Z_] only.
    /// </summary>
    internal static void ValidateMigrationColumnName(string columnName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(columnName, @"^[a-zA-Z_]+$"))
            throw new InvalidOperationException($"Invalid column name for migration: '{columnName}'");
    }
}
