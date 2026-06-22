using Scramble.Core.Models;

namespace Scramble.Core.Services;

/// <summary>
/// Service for local SQLite storage operations.
/// </summary>
public interface IStorageService
{
    Task InitializeAsync();

    // User operations
    Task<User?> GetCurrentUserAsync();
    Task SaveCurrentUserAsync(User user);
    Task ClearCurrentUserAsync();
    Task<User?> GetUserByPublicKeyAsync(string publicKeyHex);
    Task<Dictionary<string, User>> GetUsersByPublicKeysAsync(IReadOnlyList<string> publicKeyHexList);
    Task SaveUserAsync(User user);
    Task<IEnumerable<User>> GetAllUsersAsync();

    // Chat operations
    Task<Chat?> GetChatAsync(string chatId);
    Task<IEnumerable<Chat>> GetAllChatsAsync();
    Task<IEnumerable<Chat>> GetArchivedChatsAsync();
    Task<Chat?> GetChatByGroupIdAsync(string groupIdHex);
    Task SaveChatAsync(Chat chat);
    Task DeleteChatAsync(string chatId);

    // Message operations
    Task<Message?> GetMessageAsync(string messageId);
    Task<Dictionary<string, Message>> GetLastMessagePerChatAsync();
    Task<IEnumerable<Message>> GetMessagesForChatAsync(string chatId, int limit = 50, int offset = 0);
    Task SaveMessageAsync(Message message);
    Task UpdateMessageStatusAsync(string messageId, MessageStatus status);
    Task UpdateMessageChatIdAsync(string messageId, string newChatId);
    Task DeleteMessageAsync(string messageId);
    Task<bool> MessageExistsByNostrEventIdAsync(string nostrEventId);
    Task<Message?> GetMessageByNostrEventIdAsync(string nostrEventId);
    Task<Message?> GetMessageByRumorEventIdAsync(string rumorEventId);

    // KeyPackage operations
    Task<KeyPackage?> GetKeyPackageAsync(string id);
    Task<KeyPackage?> GetKeyPackageByNostrEventIdAsync(string nostrEventId);
    Task<IEnumerable<KeyPackage>> GetUnusedKeyPackagesAsync(string ownerPublicKey);
    Task<IEnumerable<KeyPackage>> GetAllKeyPackagesAsync(string ownerPublicKey);
    Task SaveKeyPackageAsync(KeyPackage keyPackage);
    Task MarkKeyPackageUsedAsync(string id);
    Task UpdateKeyPackageStatusAsync(string id, KeyPackageStatus status);

    // Pending invite operations
    Task<IEnumerable<PendingInvite>> GetPendingInvitesAsync();
    Task SavePendingInviteAsync(PendingInvite invite);
    Task DeletePendingInviteAsync(string inviteId);
    Task DismissWelcomeEventAsync(string nostrEventId);
    Task<bool> IsWelcomeEventDismissedAsync(string nostrEventId);

    // Following list (NIP-02 kind 3)
    Task SaveFollowsAsync(string ownerPublicKey, IEnumerable<Follow> follows);
    Task<List<Follow>> GetFollowsAsync(string ownerPublicKey);

    // Local contacts table (union of follows + group members + manually-added)
    Task UpsertContactsAsync(string ownerPublicKey, IEnumerable<Contact> contacts);
    Task TouchContactAsync(string ownerPublicKey, string contactPublicKey);
    Task<List<Contact>> GetContactsAsync(string ownerPublicKey);

    // User relay list (NIP-65)
    Task SaveUserRelayListAsync(string publicKeyHex, IEnumerable<RelayPreference> relays);
    Task<List<RelayPreference>> GetUserRelayListAsync(string publicKeyHex);

    // Contact relay list cache (NIP-65 kind 10002, DM kind 10050, KeyPackage kind 10051)
    Task SaveContactRelayListAsync(string publicKeyHex, int kind, IEnumerable<string> relayUrls);
    Task SaveContactRelayPreferencesAsync(string publicKeyHex, IEnumerable<RelayPreference> relays);
    Task<(List<string> Urls, DateTimeOffset FetchedAt)?> GetContactRelayListAsync(string publicKeyHex, int kind);
    Task<(List<RelayPreference> Relays, DateTimeOffset FetchedAt)?> GetContactRelayPreferencesAsync(string publicKeyHex);

    // MLS state (legacy blob persistence — used only for migration)
    Task SaveMlsStateAsync(string groupId, byte[] state);
    Task<byte[]?> GetMlsStateAsync(string groupId);
    Task DeleteMlsStateAsync(string groupId);

    /// <summary>
    /// Wipe all MLS-related data: group state, key packages, pending invites, and
    /// dismissed welcome events. Preserves user identity, contacts, messages, and settings.
    /// Caller must also reset the in-memory MLS service state (IMlsService.ResetAsync).
    /// </summary>
    Task ResetMlsDataAsync();

    /// <summary>Path to the SQLite database file. Used by MLS storage provider to share the DB.</summary>
    string DatabasePath { get; }

    /// <summary>Secure storage for encrypting sensitive data at rest. May be null in tests.</summary>
    ISecureStorage? SecureStorage { get; }

    // Welcome event management
    Task UndismissWelcomeEventAsync(string nostrEventId);

    // App settings (key-value store)
    Task<string?> GetSettingAsync(string key);
    Task SaveSettingAsync(string key, string value);

    // Skipped invite count (welcomes dismissed because no key material was available)
    Task<int> GetSkippedInviteCountAsync();
    Task IncrementSkippedInviteCountAsync();
    Task ResetSkippedInviteCountAsync();
}
