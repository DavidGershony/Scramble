using Scramble.Core.Models;

namespace Scramble.Core.Services;

/// <summary>
/// Service for Nostr relay communication.
/// </summary>
public interface INostrService
{
    /// <summary>
    /// URLs of currently connected relays.
    /// </summary>
    IReadOnlyList<string> ConnectedRelayUrls { get; }

    /// <summary>
    /// URLs of all configured relays (connected or not).
    /// </summary>
    IReadOnlyList<string> ConfiguredRelayUrls { get; }

    /// <summary>
    /// Observable stream of connection status changes.
    /// </summary>
    IObservable<NostrConnectionStatus> ConnectionStatus { get; }

    /// <summary>
    /// Observable stream of sync status messages (e.g. "Syncing 70 message(s)...").
    /// Emits null when sync is complete.
    /// </summary>
    IObservable<string?> SyncStatus { get; }

    /// <summary>
    /// Observable stream of received Nostr events.
    /// </summary>
    IObservable<NostrEventReceived> Events { get; }

    /// <summary>
    /// Connect to a Nostr relay.
    /// </summary>
    Task ConnectAsync(string relayUrl);

    /// <summary>
    /// Connect to multiple relays.
    /// </summary>
    Task ConnectAsync(IEnumerable<string> relayUrls);

    /// <summary>
    /// Connect to relay(s) for bot chat DM delivery/reception only.
    /// These relays receive welcome/gift-wrap subscriptions but are excluded
    /// from group message, key package, and commit broadcasts.
    /// </summary>
    Task ConnectBotRelaysAsync(IEnumerable<string> relayUrls);

    /// <summary>
    /// Disconnect from all relays.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Reconnect a single relay by URL.
    /// </summary>
    Task ReconnectRelayAsync(string relayUrl);

    /// <summary>
    /// Disconnect and remove a single relay.
    /// </summary>
    Task DisconnectRelayAsync(string relayUrl);

    /// <summary>
    /// Generate a new Nostr keypair.
    /// </summary>
    (string privateKeyHex, string publicKeyHex, string nsec, string npub) GenerateKeyPair();

    /// <summary>
    /// Import an existing private key (nsec or hex).
    /// </summary>
    (string privateKeyHex, string publicKeyHex, string nsec, string npub) ImportPrivateKey(string privateKey);

    /// <summary>
    /// Subscribe to events by filter.
    /// </summary>
    Task SubscribeAsync(string subscriptionId, NostrFilter filter);

    /// <summary>
    /// Unsubscribe from a subscription.
    /// </summary>
    Task UnsubscribeAsync(string subscriptionId);

    /// <summary>
    /// Set an external signer for event signing (NIP-46).
    /// When set, publish methods can sign events without a private key.
    /// </summary>
    void SetExternalSigner(IExternalSigner? signer);

    /// <summary>
    /// Set the NIP-13 proof-of-work difficulty for outgoing NIP-46 events.
    /// Forwarded to the external signer if one is set. 0 = disabled.
    /// </summary>
    void SetNip46ProofOfWorkDifficulty(int bits);

    /// <summary>
    /// Whether the service can sign events — either a local private key
    /// (via <see cref="SetAuthCredentials"/>) or an external signer is available.
    /// Callers should check this before attempting publish operations to
    /// surface user-friendly errors (e.g. "signer disconnected") instead of
    /// opaque exceptions from the publish path.
    /// </summary>
    bool HasExternalSigner { get; }

    /// <summary>
    /// Set the user's private key for NIP-42 relay authentication.
    /// Call this before connecting to relays so AUTH challenges can be answered.
    /// </summary>
    void SetAuthCredentials(string? privateKeyHex);

    /// <summary>
    /// Provide a storage service for contact relay list caching.
    /// When set, relay lookups check the DB cache before hitting the network.
    /// </summary>
    void SetStorageService(IStorageService storageService);

    /// <summary>
    /// Cache-first relay list lookup. Returns cached NIP-65 if fresh (within TTL),
    /// otherwise fetches from discovery relays and caches the result.
    /// Falls back to <see cref="FetchRelayListAsync"/> when no storage is configured.
    /// </summary>
    Task<List<RelayPreference>> GetOrFetchRelayListAsync(string publicKeyHex);

    /// <summary>
    /// Cache-first DM relay list lookup (kind 10050).
    /// </summary>
    Task<List<string>> GetOrFetchDmRelayListAsync(string publicKeyHex);

    /// <summary>
    /// Cache-first KeyPackage relay list lookup (kind 10051).
    /// </summary>
    Task<List<string>> GetOrFetchKeyPackageRelayListAsync(string publicKeyHex);

    /// <summary>
    /// Connect to relay(s) for outbox model — persistent connections to contacts' relays.
    /// These relays receive welcome/gift-wrap subscriptions but are excluded
    /// from group message, key package, and commit broadcasts (like bot relays).
    /// </summary>
    Task ConnectOutboxRelaysAsync(IEnumerable<string> relayUrls);

    /// <summary>
    /// Publish a KeyPackage (kind 30443).
    /// </summary>
    /// <param name="keyPackageData">Base64-encoded KeyPackage content bytes.</param>
    /// <param name="privateKeyHex">Sender's private key (null when using external signer).</param>
    /// <param name="mdkTags">Optional MDK-provided tags for MIP-00 compliance.</param>
    Task<string> PublishKeyPackageAsync(byte[] keyPackageData, string? privateKeyHex, List<List<string>>? mdkTags = null);

    /// <summary>
    /// Publish a Welcome message (kind 444).
    /// </summary>
    /// <param name="welcomeData">MLS Welcome message data.</param>
    /// <param name="recipientPublicKey">Recipient's public key.</param>
    /// <param name="privateKeyHex">Sender's private key (null when using external signer).</param>
    /// <param name="keyPackageEventId">KeyPackage event ID referenced by this Welcome (required per MIP-02).</param>
    Task<string> PublishWelcomeAsync(byte[] welcomeData, string recipientPublicKey, string? privateKeyHex, string keyPackageEventId);

    /// <summary>
    /// Publish a commit/evolution message (kind 445).
    /// Should be published before sending Welcome messages.
    /// </summary>
    Task<string> PublishCommitAsync(byte[] commitData, string groupId, string? privateKeyHex);

    /// <summary>
    /// Publish a group message (kind 445).
    /// </summary>
    Task<string> PublishGroupMessageAsync(byte[] encryptedData, string groupId, string? privateKeyHex);

    /// <summary>
    /// Publish a pre-built signed Nostr event JSON directly to all connected relays.
    /// Returns the event ID extracted from the JSON.
    /// </summary>
    Task<string> PublishRawEventJsonAsync(byte[] eventJsonBytes);

    /// <summary>
    /// Waits for at least one relay to confirm acceptance of a published event.
    /// Returns (accepted, reason) — reason is null on success, contains rejection message or "timeout" on failure.
    /// </summary>
    Task<(bool accepted, string? reason)> WaitForRelayOkAsync(string eventId, int timeoutMs = 5000);

    /// <summary>
    /// Fetch KeyPackages for a user. Equivalent to <see cref="FetchKeyPackagesAsync(string, int)"/>
    /// with the default per-relay limit (5).
    /// </summary>
    Task<IEnumerable<KeyPackage>> FetchKeyPackagesAsync(string publicKeyHex);

    /// <summary>
    /// Fetch KeyPackages for a user with an explicit per-relay limit.
    /// </summary>
    /// <param name="publicKeyHex">The user's hex-encoded x-only Nostr public key.</param>
    /// <param name="limit">
    /// Maximum events to request from each relay. The audit flow passes a high value (e.g. 100)
    /// to surface stale slots from before the rotation fix and from multi-device deployments;
    /// inviter flows use the default <see cref="FetchKeyPackagesAsync(string)"/> overload because
    /// they only need any one usable KeyPackage.
    /// </param>
    Task<IEnumerable<KeyPackage>> FetchKeyPackagesAsync(string publicKeyHex, int limit);

    /// <summary>
    /// Fetch user metadata (kind 0) for a public key.
    /// </summary>
    Task<UserMetadata?> FetchUserMetadataAsync(string publicKeyHex);

    /// <summary>
    /// Publish user metadata (kind 0) to all connected relays.
    /// </summary>
    Task<string> PublishMetadataAsync(string name, string? displayName, string? about, string? picture, string? privateKeyHex);

    /// <summary>
    /// Subscribe to Welcome messages (NIP-59 gift-wrapped kind 1059) for a user.
    /// The private key is needed to decrypt the gift wrap.
    /// </summary>
    Task SubscribeToWelcomesAsync(string publicKeyHex, string? privateKeyHex = null);

    /// <summary>
    /// Directly fetch Welcome events (kind 444) for a user from relays.
    /// Opens fresh connections, queries, and returns results.
    /// </summary>
    Task<IEnumerable<NostrEventReceived>> FetchWelcomeEventsAsync(string publicKeyHex, string? privateKeyHex = null);

    /// <summary>
    /// Directly fetch NIP-17 DM history (kind 14 inside kind 1059 gift wraps) for a user.
    /// Opens fresh connections per relay, unwraps, and returns the inner kind 14 rumors.
    /// Used on login to restore bot/agent chats whose state may not be in the local DB
    /// (e.g. fresh device, account switch). Caller deduplicates by NostrEventId via storage.
    /// </summary>
    Task<IEnumerable<NostrEventReceived>> FetchNip17DmHistoryAsync(string publicKeyHex, string? privateKeyHex = null);

    /// <summary>
    /// Subscribe to group messages (kind 445) for specific groups.
    /// </summary>
    Task SubscribeToGroupMessagesAsync(IEnumerable<string> groupIds, DateTimeOffset? since = null);


    /// <summary>
    /// Observable stream of Welcome messages (kind 444).
    /// </summary>
    IObservable<MarmotWelcomeEvent> WelcomeMessages { get; }

    /// <summary>
    /// Observable stream of Group messages (kind 445).
    /// </summary>
    IObservable<MarmotGroupMessageEvent> GroupMessages { get; }

    /// <summary>
    /// Fetch historical group message events (kind 445) for a group from relays.
    /// Uses a time window with since/until and a limit.
    /// </summary>
    Task<IEnumerable<NostrEventReceived>> FetchGroupHistoryAsync(
        string nostrGroupIdHex, DateTimeOffset since, DateTimeOffset until, int limit = 50);

    /// <summary>
    /// Fetch a user's NIP-65 relay list (kind 10002) from discovery relays.
    /// </summary>
    Task<List<RelayPreference>> FetchRelayListAsync(string publicKeyHex);

    /// <summary>
    /// Fetch a user's NIP-02 following list (kind 3) from relays.
    /// Returns the parsed p-tag contacts with optional relay hint and petname.
    /// </summary>
    Task<List<Follow>> FetchFollowingListAsync(string publicKeyHex);

    /// <summary>
    /// Publish the current user's NIP-65 relay list (kind 10002).
    /// </summary>
    Task<string> PublishRelayListAsync(List<RelayPreference> relays, string? privateKeyHex);

    /// <summary>
    /// Publish the current user's DM relay list (kind 10050, NIP-17).
    /// These are the relays where the user wants to receive gift-wrapped DMs and Welcomes.
    /// Tags use the "relay" format: ["relay", "wss://..."].
    /// </summary>
    Task<string> PublishDmRelayListAsync(List<string> relayUrls, string? privateKeyHex);

    /// <summary>
    /// Publish the current user's KeyPackage relay list (kind 10051).
    /// These are the relays where the user publishes MLS KeyPackages.
    /// Tags use the "relay" format: ["relay", "wss://..."].
    /// </summary>
    Task<string> PublishKeyPackageRelayListAsync(List<string> relayUrls, string? privateKeyHex);

    /// <summary>
    /// Fetch a user's DM relay list (kind 10050) from discovery relays.
    /// Returns the relay URLs where the user wants to receive gift-wrapped messages.
    /// </summary>
    Task<List<string>> FetchDmRelayListAsync(string publicKeyHex);

    /// <summary>
    /// Fetch a user's KeyPackage relay list (kind 10051) from discovery relays.
    /// Returns the relay URLs where the user publishes MLS KeyPackages.
    /// </summary>
    Task<List<string>> FetchKeyPackageRelayListAsync(string publicKeyHex);

    /// <summary>
    /// Convert npub to hex public key.
    /// </summary>
    string? NpubToHex(string npub);

    /// <summary>
    /// Async NIP-44 encryption. Delegates to external signer when connected,
    /// otherwise uses local private key.
    /// </summary>
    Task<string> Nip44EncryptAsync(string plaintext, string recipientPubKey);

    /// <summary>
    /// Async NIP-44 decryption. Delegates to external signer when connected,
    /// otherwise uses local private key.
    /// </summary>
    Task<string> Nip44DecryptAsync(string ciphertext, string senderPubKey);

    /// <summary>
    /// Publish a NIP-59 gift-wrapped event.
    /// Used for NIP-17 DMs (kind 14 rumor) and other gift-wrapped protocols.
    /// </summary>
    /// <param name="targetRelayUrls">When provided, only send to these relay URLs (must be connected). When null, broadcast to all connected relays.</param>
    Task<string> PublishGiftWrapAsync(
        int rumorKind, string content, List<List<string>> rumorTags,
        string? senderPrivateKeyHex, string senderPublicKeyHex,
        string recipientPublicKeyHex, List<string>? targetRelayUrls = null);

    /// <summary>
    /// Publish a NIP-09 deletion event (kind 5) for the specified event.
    /// The deletion is signed by the current auth credentials (private key or external signer).
    /// </summary>
    /// <param name="eventId">The ID of the event to delete.</param>
    /// <param name="reason">Optional human-readable reason for deletion.</param>
    Task PublishDeletionAsync(string eventId, string? reason = null);
}

public class NostrConnectionStatus
{
    public string RelayUrl { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public string? Error { get; set; }
    public bool Removed { get; set; }
}

public class NostrEventReceived
{
    public int Kind { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<List<string>> Tags { get; set; } = new();
    public string RelayUrl { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}

public class NostrFilter
{
    public List<int>? Kinds { get; set; }
    public List<string>? Authors { get; set; }
    public List<string>? Ids { get; set; }
    public List<string>? TagP { get; set; }
    public List<string>? TagE { get; set; }
    public DateTime? Since { get; set; }
    public DateTime? Until { get; set; }
    public int? Limit { get; set; }
}

/// <summary>
/// User metadata from Nostr kind 0 events.
/// </summary>
public class UserMetadata
{
    public string PublicKeyHex { get; set; } = string.Empty;
    public string? Npub { get; set; }
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Username { get; set; }
    public string? About { get; set; }
    public string? Picture { get; set; }
    public string? Banner { get; set; }
    public string? Website { get; set; }
    public string? Nip05 { get; set; }
    public string? Lud16 { get; set; }
    public string? Lud06 { get; set; }
    public DateTime? CreatedAt { get; set; }

    public string GetDisplayName()
    {
        if (!string.IsNullOrEmpty(DisplayName))
            return DisplayName;
        if (!string.IsNullOrEmpty(Name))
            return Name;
        if (!string.IsNullOrEmpty(Username))
            return $"@{Username}";
        if (!string.IsNullOrEmpty(Npub))
            return $"{Npub[..12]}...";
        if (!string.IsNullOrEmpty(PublicKeyHex))
            return $"{PublicKeyHex[..12]}...";
        return "Unknown";
    }
}

/// <summary>
/// Marmot Welcome message event (kind 444).
/// Used to invite users to MLS groups.
/// </summary>
public class MarmotWelcomeEvent
{
    /// <summary>
    /// Nostr event ID.
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>
    /// Public key of the sender (inviter).
    /// </summary>
    public string SenderPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Public key of the recipient (invitee).
    /// </summary>
    public string RecipientPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded MLS Welcome message.
    /// </summary>
    public string WelcomeData { get; set; } = string.Empty;

    /// <summary>
    /// Group ID if specified in tags (h-tag).
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// KeyPackage event ID referenced in the e-tag (MIP-02 requirement).
    /// </summary>
    public string? KeyPackageEventId { get; set; }

    /// <summary>
    /// Relay URLs from the relays tag for locating Group Events.
    /// </summary>
    public List<string> RelayHints { get; set; } = new();

    /// <summary>
    /// When the event was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Relay URL where this event was received.
    /// </summary>
    public string RelayUrl { get; set; } = string.Empty;
}

/// <summary>
/// Marmot Group message event (kind 445).
/// Contains MLS-encrypted group messages or commits.
/// </summary>
public class MarmotGroupMessageEvent
{
    /// <summary>
    /// Nostr event ID.
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>
    /// Public key of the sender.
    /// </summary>
    public string SenderPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Group ID from tags.
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded MLS ciphertext.
    /// </summary>
    public string EncryptedData { get; set; } = string.Empty;

    /// <summary>
    /// MLS epoch if specified in tags.
    /// </summary>
    public ulong? Epoch { get; set; }

    /// <summary>
    /// When the event was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Relay URL where this event was received.
    /// </summary>
    public string RelayUrl { get; set; } = string.Empty;
}
