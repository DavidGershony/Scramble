namespace Scramble.Core.Models;

/// <summary>
/// Represents a chat conversation (group or DM).
/// </summary>
public class Chat
{
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Chat display name (group name or contact name for DMs).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a group chat or direct message.
    /// </summary>
    public ChatType Type { get; set; } = ChatType.DirectMessage;

    /// <summary>
    /// MLS group ID for encrypted group chats (used for MLS encrypt/decrypt operations).
    /// </summary>
    public byte[]? MlsGroupId { get; set; }

    /// <summary>
    /// Nostr group ID from the MLS 0xF2EE extension (used for kind 445 h-tag and relay subscriptions).
    /// Distinct from MlsGroupId — this is what the Nostr protocol uses for message routing.
    /// </summary>
    public byte[]? NostrGroupId { get; set; }

    /// <summary>
    /// Current MLS epoch number for the group.
    /// </summary>
    public ulong MlsEpoch { get; set; }

    /// <summary>
    /// List of participant public keys (hex format).
    /// </summary>
    public List<string> ParticipantPublicKeys { get; set; } = new();

    /// <summary>
    /// Avatar/group picture URL.
    /// </summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Group description (for group chats).
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The last message in this chat (for display in chat list).
    /// </summary>
    public Message? LastMessage { get; set; }

    /// <summary>
    /// Number of unread messages.
    /// </summary>
    public int UnreadCount { get; set; }

    /// <summary>
    /// When the chat was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last activity time (for sorting).
    /// </summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the chat is muted.
    /// </summary>
    public bool IsMuted { get; set; }

    /// <summary>
    /// Whether the chat is pinned to the top.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// Whether the chat is archived.
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// Nostr relay URLs associated with this chat.
    /// </summary>
    public List<string> RelayUrls { get; set; } = new();

    /// <summary>
    /// The Nostr event ID of the Welcome (kind 444) that created this chat.
    /// Used by group reset to un-dismiss the welcome and allow re-joining.
    /// </summary>
    public string? WelcomeNostrEventId { get; set; }

    /// <summary>
    /// Public key of the group creator (from MIP-01 AdminPubkeys at creation time).
    /// </summary>
    public string? CreatorPublicKey { get; set; }

    /// <summary>
    /// Public keys of group admins (from MIP-01 0xF2EE extension).
    /// </summary>
    public List<string> AdminPublicKeys { get; set; } = new();

    /// <summary>
    /// Set when this device can no longer decrypt messages due to missed MLS commits.
    /// Cleared when the device successfully rejoins via a new Welcome.
    /// </summary>
    public bool IsOutOfSync { get; set; }

    /// <summary>
    /// Set after <see cref="IsOutOfSync"/> when the device has sent a resync request
    /// to the DeviceSync group and is waiting for another device to re-invite it.
    /// </summary>
    public bool IsResyncPending { get; set; }
}

public enum ChatType
{
    DirectMessage,
    Group,
    Bot,
    /// <summary>
    /// Internal device-sync group ("Private Notes"). Created automatically on login,
    /// used to sync metadata between devices and verify device liveness.
    /// Hidden from the chat list unless the user opts in via Settings.
    /// </summary>
    DeviceSync
}
