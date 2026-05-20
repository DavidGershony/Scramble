using System.Reactive;
using Scramble.Core.Models;

namespace Scramble.Core.Services;

/// <summary>
/// Service for message handling and chat operations.
/// </summary>
public interface IMessageService
{
    /// <summary>
    /// Observable stream of new messages.
    /// </summary>
    IObservable<Message> NewMessages { get; }

    /// <summary>
    /// Observable stream of message status updates.
    /// </summary>
    IObservable<(string MessageId, MessageStatus Status)> MessageStatusUpdates { get; }

    /// <summary>
    /// Observable stream of chat updates.
    /// </summary>
    IObservable<Chat> ChatUpdates { get; }

    /// <summary>
    /// Observable stream of new pending invites.
    /// </summary>
    IObservable<PendingInvite> NewInvites { get; }

    /// <summary>
    /// Fires each time a Welcome event is skipped because no key material was available.
    /// Subscribers can use this to update the skipped invite count in the UI.
    /// </summary>
    IObservable<Unit> SkippedInvites { get; }

    /// <summary>
    /// Observable stream of reaction updates (messageId, emoji, reactorPublicKey).
    /// </summary>
    IObservable<(string MessageId, string Emoji, string ReactorPublicKey)> ReactionUpdates { get; }

    /// <summary>
    /// Observable stream of MLS decryption errors (e.g. corrupted state after restart).
    /// </summary>
    IObservable<MlsDecryptionError> DecryptionErrors { get; }

    /// <summary>
    /// Initialize the message service.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Get all chats for the current user.
    /// </summary>
    Task<IEnumerable<Chat>> GetChatsAsync();

    /// <summary>
    /// Get a specific chat.
    /// </summary>
    Task<Chat?> GetChatAsync(string chatId);

    /// <summary>
    /// Get messages for a chat.
    /// </summary>
    Task<IEnumerable<Message>> GetMessagesAsync(string chatId, int limit = 50, int offset = 0);

    /// <summary>
    /// Send a text message.
    /// </summary>
    Task<Message> SendMessageAsync(string chatId, string content);

    /// <summary>
    /// Send a voice message with encrypted audio uploaded to Blossom.
    /// </summary>
    Task SendVoiceMessageAsync(string chatId, string content, string mediaUrl,
        string sha256Hex, string nonceHex, string mimeType, string filename, double durationSeconds);

    /// <summary>
    /// Send a media message (image, file) with encrypted data uploaded to Blossom.
    /// </summary>
    Task SendMediaMessageAsync(string chatId, string content, string mediaUrl,
        string sha256Hex, string nonceHex, string mimeType, string filename, MessageType messageType);

    /// <summary>
    /// Send a reply to a message.
    /// </summary>
    Task<Message> SendReplyAsync(string chatId, string content, string replyToMessageId);

    /// <summary>
    /// Create a new group chat.
    /// </summary>
    /// <param name="name">Group display name.</param>
    /// <param name="memberPublicKeys">Initial members (excluding the creator).</param>
    /// <param name="relayUrlsOverride">
    /// Optional override for the relay URLs advertised in MLS group state and Welcomes.
    /// When null, defaults to the currently connected relay URLs. Intended for tests
    /// where the URL reachable by remote peers (e.g. another container) differs from
    /// the URL the creator connects to (e.g. localhost vs docker-DNS hostname).
    /// </param>
    Task<Chat> CreateGroupAsync(string name, IEnumerable<string> memberPublicKeys, IEnumerable<string>? relayUrlsOverride = null);

    /// <summary>
    /// Create or get a direct message chat with a user.
    /// </summary>
    Task<Chat> GetOrCreateDirectMessageAsync(string recipientPublicKey);

    /// <summary>
    /// Create or get a bot chat (NIP-17 DMs) with a bot by public key.
    /// </summary>
    /// <param name="botPublicKey">The bot's hex public key.</param>
    /// <param name="relayUrls">Optional relay URLs to associate with this chat for targeted message delivery.</param>
    Task<Chat> GetOrCreateBotChatAsync(string botPublicKey, List<string>? relayUrls = null);

    /// <summary>
    /// Get or create the internal device-sync group ("Private Notes").
    /// Created automatically on login — used to sync metadata between devices
    /// and verify device liveness via invite acceptance.
    /// </summary>
    Task<Chat> GetOrCreateDeviceSyncGroupAsync();

    /// <summary>
    /// Invite a peer device to the device-sync group.
    /// The peer is expected to auto-accept within 3 days to prove the device is alive.
    /// </summary>
    /// <param name="peerKeyPackage">The peer device's KeyPackage.</param>
    /// <param name="syncChatId">The chat ID of the device-sync group.</param>
    Task InvitePeerToSyncGroupAsync(KeyPackage peerKeyPackage, string syncChatId);

    /// <summary>
    /// Publish dummy KeyPackage events to the private relay to obfuscate the real
    /// device count. Requires a configured private relay URL and an opt-in feature flag.
    /// Uses deterministic slot IDs derived from the private key so all devices
    /// can independently compute and filter them out.
    /// Skipped when the feature is disabled, no private relay is configured,
    /// or for external signer users (no private key available).
    /// </summary>
    /// <param name="privateRelayUrl">The private relay URL to target. Only this relay
    /// will appear in the dummy KP relay tags.</param>
    Task PublishDummyKeyPackagesAsync(string privateRelayUrl);

    /// <summary>
    /// Add a member to a group.
    /// </summary>
    Task AddMemberAsync(string chatId, string memberPublicKey);

    /// <summary>
    /// Add a peer device (same Nostr identity, different MLS leaf) to all groups
    /// where the current device is a member. Uses a specific KeyPackage from the
    /// peer device rather than fetching the newest KP for the pubkey.
    /// Only auto-adds to groups where the current user is admin. Non-admin groups
    /// are skipped and reported in the result so the UI can inform the user.
    /// </summary>
    /// <param name="peerKeyPackage">The peer device's KeyPackage (different slot ID from local device).</param>
    Task<PeerDeviceAddResult> AddPeerDeviceToGroupsAsync(KeyPackage peerKeyPackage);

    /// <summary>
    /// Remove a member from a group.
    /// </summary>
    Task RemoveMemberAsync(string chatId, string memberPublicKey);

    /// <summary>
    /// Leave a group chat.
    /// </summary>
    Task LeaveGroupAsync(string chatId);

    /// <summary>
    /// Mark messages as read.
    /// </summary>
    Task MarkAsReadAsync(string chatId);

    /// <summary>
    /// Add a reaction to a message.
    /// </summary>
    Task AddReactionAsync(string messageId, string emoji);

    /// <summary>
    /// Send a reaction to the MLS group and update local storage.
    /// </summary>
    Task SendReactionAsync(string messageId, string emoji);

    /// <summary>
    /// Remove a reaction from a message.
    /// </summary>
    Task RemoveReactionAsync(string messageId, string emoji);

    /// <summary>
    /// Delete a message.
    /// </summary>
    Task DeleteMessageAsync(string messageId);

    /// <summary>
    /// Archive a chat (hide from main list, keep receiving messages).
    /// </summary>
    Task ArchiveChatAsync(string chatId);

    /// <summary>
    /// Unarchive a chat (move back to main list).
    /// </summary>
    Task UnarchiveChatAsync(string chatId);

    /// <summary>
    /// Mute a chat (suppress notifications).
    /// </summary>
    Task MuteChatAsync(string chatId);

    /// <summary>
    /// Unmute a chat.
    /// </summary>
    Task UnmuteChatAsync(string chatId);

    /// <summary>
    /// Mute/unmute a chat.
    /// </summary>
    Task SetMutedAsync(string chatId, bool muted);

    /// <summary>
    /// Get all pending group invites.
    /// </summary>
    Task<IEnumerable<PendingInvite>> GetPendingInvitesAsync();

    /// <summary>
    /// Accept a pending invite — processes MLS welcome and creates the chat.
    /// </summary>
    Task<Chat> AcceptInviteAsync(string inviteId);

    /// <summary>
    /// Decline a pending invite — discards it.
    /// </summary>
    Task DeclineInviteAsync(string inviteId);

    /// <summary>
    /// Rescan relays for Welcome messages (kind 444) and reload pending invites.
    /// </summary>
    Task RescanInvitesAsync();

    /// <summary>
    /// Reset a group's MLS state and delete the chat, allowing re-join via welcome rescan.
    /// </summary>
    Task ResetGroupAsync(string chatId);

    /// <summary>
    /// Audit KeyPackages: fetch own KeyPackages from relays, check which ones
    /// have local private key material, and mark lost ones.
    /// Returns (total on relays, active with keys, lost without keys).
    /// </summary>
    Task<KeyPackageAuditResult> AuditKeyPackagesAsync();

    /// <summary>
    /// Generates and publishes a fresh KeyPackage if the local DB has no unused ones.
    /// No-op when the user already has at least one available KeyPackage. Safe to call
    /// from post-login init or after consuming an invite — failures are logged, not thrown.
    /// </summary>
    Task AutoPublishKeyPackageIfNeededAsync();

    /// <summary>
    /// Post a device-presence announcement to the DeviceSync group ("Private Notes").
    /// Sends a system message of the form "[Device] {osName} · {shortSlotId} online".
    /// No-op if the DeviceSync group does not exist yet or has no slot ID.
    /// </summary>
    /// <param name="osName">Human-readable OS name, e.g. "Windows", "Android", "macOS".</param>
    Task AnnounceDeviceToSyncGroupAsync(string osName);

    /// <summary>
    /// Rotate the local KeyPackage and post a structured resync request to the DeviceSync
    /// group so other devices of the same identity can re-invite this device to the
    /// affected group. Sets <see cref="Chat.IsResyncPending"/> on the chat.
    /// </summary>
    Task RequestResyncAsync(string chatId);

    /// <summary>
    /// Fetches NIP-17 (kind 14) DM history from relays and routes each unwrapped event
    /// through the normal incoming-message path so bot/agent chats are auto-created and
    /// missed messages persisted. Idempotent — dedup by NostrEventId. Used on login to
    /// restore the Agents (DVM) tab without requiring the user to re-add devices manually.
    /// </summary>
    Task RescanNip17ChatsAsync();

    /// <summary>
    /// Load older messages from relays for the given chat, going back from the fetchBoundary.
    /// Uses a 2-day sliding window with retry. Returns decrypted messages and the new boundary.
    /// </summary>
    Task<LoadOlderMessagesResult> LoadOlderMessagesAsync(string chatId, DateTimeOffset fetchBoundary);

    /// <summary>
    /// Fetches kind 0 metadata for the given public key, caches it as a User in the database,
    /// and returns the metadata. Returns null if fetch fails. Existing user fields (private keys,
    /// signer details) are preserved on update.
    /// </summary>
    Task<UserMetadata?> FetchAndCacheProfileAsync(string publicKeyHex);

    /// <summary>
    /// Returns cached metadata (memory or DB) immediately, then fires a background relay fetch.
    /// If the relay returns updated metadata, the DB and memory cache are updated and a
    /// <see cref="MetadataUpdated"/> notification is emitted so the UI can refresh.
    /// Returns null only if nothing is cached and the background fetch hasn't completed yet.
    /// </summary>
    Task<UserMetadata?> GetCachedOrFetchProfileAsync(string publicKeyHex);

    /// <summary>
    /// Fires each time a background metadata refresh returns updated profile data.
    /// Subscribers can use this to refresh display names, avatars, etc.
    /// </summary>
    IObservable<UserMetadata> MetadataUpdated { get; }
}

public class KeyPackageAuditResult
{
    public int TotalOnRelays { get; set; }
    public int ActiveWithKeys { get; set; }
    public int Lost { get; set; }
    public int Expired { get; set; }
    public List<string> LostEventIds { get; set; } = new();
}

public class LoadOlderMessagesResult
{
    public List<Message> Messages { get; set; } = new();
    public DateTimeOffset NewBoundary { get; set; }
    public bool HasMore { get; set; } = true;
}

/// <summary>
/// Result of adding a peer device to groups, distinguishing admin (auto-added)
/// from non-admin (skipped) groups.
/// </summary>
public class PeerDeviceAddResult
{
    /// <summary>Number of admin groups where the peer device was successfully added.</summary>
    public int AddedCount { get; set; }

    /// <summary>Names of non-admin groups that were skipped (user should ask the admin).</summary>
    public List<string> SkippedNonAdminGroups { get; set; } = new();

    /// <summary>Detailed info for each skipped non-admin group (for device-add requests).</summary>
    public List<SkippedGroupInfo> SkippedGroupDetails { get; set; } = new();

    /// <summary>Number of groups where the add failed due to an error.</summary>
    public int FailedCount { get; set; }
}

/// <summary>
/// Information about a non-admin group that was skipped during peer device addition.
/// Used to send device-add requests to group admins.
/// </summary>
public class SkippedGroupInfo
{
    /// <summary>Display name of the group.</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>Chat ID (for local reference).</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Admin public keys (hex) for this group.</summary>
    public List<string> AdminPubkeys { get; set; } = new();
}
