using Scramble.Core.Models;

namespace Scramble.Core.Services;

/// <summary>
/// Service for MLS group management operations.
/// </summary>
public interface IMlsService
{
    /// <summary>
    /// Initialize the MLS client with a Nostr identity.
    /// </summary>
    Task InitializeAsync(string privateKeyHex, string publicKeyHex);

    /// <summary>
    /// Resets the MLS service state, clearing signing keys, stored KeyPackages, and groups.
    /// Must be called on logout so re-initialization uses the new user's identity.
    /// </summary>
    Task ResetAsync();

    /// <summary>
    /// Generate a new KeyPackage for group invitations.
    /// </summary>
    Task<KeyPackage> GenerateKeyPackageAsync();

    /// <summary>
    /// Create a new MLS group. Relay URLs are required — the group's MarmotGroupData
    /// extension includes them so other members know where to publish/subscribe.
    /// </summary>
    Task<MlsGroupInfo> CreateGroupAsync(string groupName, string[] relayUrls);

    /// <summary>
    /// Add a member to a group using their KeyPackage.
    /// Auto-merges immediately — prefer <see cref="StageAddMemberAsync"/> for MIP-03 compliance.
    /// </summary>
    Task<MlsWelcome> AddMemberAsync(byte[] groupId, KeyPackage keyPackage);

    /// <summary>
    /// Stage an add-member commit without advancing local MLS state.
    /// Returns the same MlsWelcome data but the epoch is NOT advanced until
    /// <see cref="MergeStagedAsync"/> is called after relay confirmation.
    /// </summary>
    Task<MlsWelcome> StageAddMemberAsync(byte[] groupId, KeyPackage keyPackage);

    /// <summary>
    /// Merge a previously staged commit, advancing local MLS state.
    /// Call after the relay has confirmed the commit (MIP-03 step 3).
    /// </summary>
    Task MergeStagedAsync(byte[] groupId);

    /// <summary>
    /// Clear a previously staged commit without applying it.
    /// Call when the commit publish failed.
    /// </summary>
    Task ClearStagedAsync(byte[] groupId);

    /// <summary>
    /// Stage a remove-member commit without advancing local MLS state.
    /// </summary>
    Task<byte[]> StageRemoveMemberAsync(byte[] groupId, string memberPublicKey);

    /// <summary>
    /// Returns true if there is a pending (staged) commit for the group.
    /// </summary>
    bool HasPendingCommit(byte[] groupId);

    /// <summary>
    /// Process a Welcome message to join a group.
    /// </summary>
    /// <param name="welcomeData">The welcome rumor data from AddMember.</param>
    /// <param name="wrapperEventId">The kind-444 Nostr event ID that wrapped this welcome.</param>
    Task<MlsGroupInfo> ProcessWelcomeAsync(byte[] welcomeData, string wrapperEventId);

    /// <summary>
    /// Check whether we have the key material needed to process a Welcome message,
    /// without committing to joining the group.
    /// Returns false immediately if no KeyPackages are stored locally.
    /// The Rust backend always returns true (cannot check without attempting).
    /// </summary>
    Task<bool> CanProcessWelcomeAsync(byte[] welcomeData);

    /// <summary>
    /// Encrypt a message for a group.
    /// </summary>
    Task<byte[]> EncryptMessageAsync(byte[] groupId, string plaintext, List<List<string>>? rumorTags = null);

    /// <summary>
    /// Encrypt a reaction event (kind 7) targeting a specific message.
    /// </summary>
    Task<byte[]> EncryptReactionAsync(byte[] groupId, string emoji, string targetRumorEventId);

    /// <summary>
    /// The rumor event ID from the most recent EncryptMessageAsync call.
    /// Used to store alongside the message for reaction targeting.
    /// </summary>
    string? LastEncryptedRumorEventId { get; }

    /// <summary>
    /// Decrypt a message from a group.
    /// </summary>
    /// <param name="groupId">The MLS group identifier.</param>
    /// <param name="ciphertext">The encrypted message bytes.</param>
    /// <param name="nostrEventId">Optional Nostr event ID for MIP-03 tiebreaker resolution.</param>
    /// <param name="nostrCreatedAt">Optional Nostr event created_at for MIP-03 tiebreaker resolution.</param>
    Task<MlsDecryptedMessage> DecryptMessageAsync(byte[] groupId, byte[] ciphertext,
        string? nostrEventId = null, DateTimeOffset? nostrCreatedAt = null);

    /// <summary>
    /// Process a commit message (member changes, key updates).
    /// </summary>
    Task ProcessCommitAsync(byte[] groupId, byte[] commitData);

    /// <summary>
    /// Update keys for forward secrecy.
    /// </summary>
    Task<byte[]> UpdateKeysAsync(byte[] groupId);

    /// <summary>
    /// Remove a member from a group.
    /// </summary>
    Task<byte[]> RemoveMemberAsync(byte[] groupId, string memberPublicKey);

    /// <summary>
    /// Get current group state.
    /// </summary>
    Task<MlsGroupInfo?> GetGroupInfoAsync(byte[] groupId);

    /// <summary>
    /// Export group state for persistence.
    /// </summary>
    Task<byte[]> ExportGroupStateAsync(byte[] groupId);

    /// <summary>
    /// Import group state from persistence.
    /// </summary>
    Task ImportGroupStateAsync(byte[] groupId, byte[] state);

    /// <summary>
    /// Export service-level state (signing keys, stored key packages) for persistence.
    /// Returns null if not supported or no state to export.
    /// </summary>
    Task<byte[]?> ExportServiceStateAsync();

    /// <summary>
    /// Import service-level state from persistence.
    /// </summary>
    Task ImportServiceStateAsync(byte[] state);

    /// <summary>
    /// Get the Nostr group ID (from 0xF2EE extension) for a given MLS group.
    /// This is the value used in the 'h' tag for kind 445 events.
    /// Returns null only if the backend doesn't support MarmotGroupData (e.g., Rust MDK).
    /// The managed C# backend always returns a value or throws.
    /// </summary>
    byte[]? GetNostrGroupId(byte[] groupId);

    /// <summary>
    /// Returns the admin public keys (as lowercase hex strings) from the 0xF2EE extension for a group.
    /// Returns an empty list if the extension is missing or has no admins.
    /// </summary>
    List<string> GetAdminPubkeys(byte[] groupId);

    /// <summary>
    /// Stage a commit that updates the group's admin public keys in the 0xF2EE extension.
    /// Returns the commit bytes for MIP-03 publish-then-merge flow.
    /// Call <see cref="MergeStagedAsync"/> after the relay confirms the commit.
    /// </summary>
    /// <param name="groupId">The MLS group identifier.</param>
    /// <param name="adminPubkeysHex">The new admin public keys (lowercase hex strings).</param>
    /// <returns>The commit bytes to encrypt and publish as a kind 445 event.</returns>
    Task<byte[]> StageUpdateAdminPubkeysAsync(byte[] groupId, List<string> adminPubkeysHex);

    /// <summary>
    /// Get the number of KeyPackages for which we have private key material stored locally.
    /// </summary>
    int GetStoredKeyPackageCount();

    /// <summary>
    /// Check if we have the private key material for a given KeyPackage (by its serialized TLS bytes).
    /// Used by the KeyPackage audit to determine if a relay-published KeyPackage can still be used.
    /// </summary>
    bool HasKeyMaterialForKeyPackage(byte[] keyPackageData);

    /// <summary>
    /// Set the Nostr event signer used for building signed kind 445 events.
    /// When set, EncryptMessageAsync delegates signing to this signer instead of using the local private key.
    /// </summary>
    void SetNostrEventSigner(INostrEventSigner signer);

    /// <summary>
    /// Get the MIP-04 media exporter secret for a group.
    /// This is MLS-Exporter("marmot", "encrypted-media", 32) from the current epoch.
    /// Used for deriving per-file encryption keys for MIP-04 media.
    /// </summary>
    byte[] GetMediaExporterSecret(byte[] groupId);

    /// <summary>
    /// Get the local device's KeyPackage slot ID (d-tag for kind 30443 events).
    /// Returns null if no KeyPackage has been published yet on this device.
    /// Used for multi-device detection: KPs from the same pubkey with a different
    /// slot ID belong to a peer device.
    /// </summary>
    string? GetLocalKeyPackageSlotId();

    /// <summary>
    /// Try to reconcile the local slot ID from relay-fetched KeyPackages.
    /// When the local slot ID is null (v3 state migration or pre-multi-device),
    /// matches locally stored KP bytes against relay KPs to adopt the correct
    /// d-tag. This prevents the device from appearing as a "new peer" to itself.
    /// Returns true if a slot ID was adopted, false if already set or no match found.
    /// </summary>
    bool TryReconcileSlotId(IEnumerable<KeyPackage> relayKeyPackages);

    /// <summary>
    /// MIP-03 encrypt raw commit/proposal bytes using the group's current exporter secret
    /// and wrap in a signed kind 445 event with ephemeral key.
    /// Must be called BEFORE the local state advances (use pre-commit exporter secret).
    /// </summary>
    Task<byte[]> EncryptCommitAsync(byte[] groupId, byte[] commitData);
}

public class MlsGroupInfo
{
    public byte[] GroupId { get; set; } = Array.Empty<byte>();
    public string GroupName { get; set; } = string.Empty;
    public ulong Epoch { get; set; }
    public List<string> MemberPublicKeys { get; set; } = new();
}

public class MlsWelcome
{
    public byte[] WelcomeData { get; set; } = Array.Empty<byte>();
    public string RecipientPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Commit/evolution event data to publish as kind 445 before sending the welcome.
    /// </summary>
    public byte[]? CommitData { get; set; }

    /// <summary>
    /// The KeyPackage event ID that was used for the invitation.
    /// Required for the Welcome event's 'e' tag.
    /// </summary>
    public string? KeyPackageEventId { get; set; }
}

public class MlsDecryptedMessage
{
    public string SenderPublicKey { get; set; } = string.Empty;
    public string Plaintext { get; set; } = string.Empty;
    public string? RumorJson { get; set; }
    public ulong Epoch { get; set; }

    /// <summary>
    /// True if this was a commit message (epoch transition), not an application message.
    /// When true, the MLS group state has been updated but there is no user-visible content.
    /// </summary>
    public bool IsCommit { get; set; }

    /// <summary>
    /// Image URL extracted from imeta tags (MIP-04).
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// MIME type from imeta tags (e.g. "image/jpeg").
    /// </summary>
    public string? MediaType { get; set; }

    /// <summary>
    /// Original filename from imeta tags.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// SHA-256 hash of the original plaintext file (hex, from imeta tag).
    /// Used for MIP-04 key derivation and integrity verification.
    /// </summary>
    public string? FileSha256 { get; set; }

    /// <summary>
    /// Encryption nonce (hex, from imeta tag).
    /// Used for MIP-04 ChaCha20-Poly1305 decryption.
    /// </summary>
    public string? EncryptionNonce { get; set; }

    /// <summary>
    /// MIP-04 encryption version (from imeta tag, e.g. "2").
    /// </summary>
    public string? EncryptionVersion { get; set; }

    /// <summary>
    /// The event ID of the inner rumor event (used by other clients for reaction targeting).
    /// </summary>
    public string? RumorEventId { get; set; }

    /// <summary>
    /// The Nostr kind of the inner rumor event (e.g. 9 for regular message, 7 for reaction).
    /// </summary>
    public int RumorKind { get; set; } = 9;

    /// <summary>
    /// For reaction events (kind 7): the Nostr event ID of the message being reacted to.
    /// </summary>
    public string? ReactionTargetEventId { get; set; }

    /// <summary>
    /// For reply messages: the rumor event ID of the message being replied to.
    /// Populated from an "e" tag with "reply" marker in the inner rumor event.
    /// </summary>
    public string? ReplyToRumorEventId { get; set; }

    /// <summary>
    /// For reaction events (kind 7): the emoji (e.g. "👍"). NIP-25 "+" maps to 👍, "-" to 👎.
    /// </summary>
    public string? ReactionEmoji { get; set; }
}
