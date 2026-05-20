namespace Scramble.Core.Models;

/// <summary>
/// Status of a KeyPackage relative to local key material availability.
/// </summary>
public enum KeyPackageStatus
{
    /// <summary>We have the private key material locally and can process Welcomes encrypted to this KeyPackage.</summary>
    Active,
    /// <summary>The KeyPackage exists on relays but the private key material is not available locally.</summary>
    Lost,
    /// <summary>The KeyPackage has expired (past its ExpiresAt date).</summary>
    Expired
}

/// <summary>
/// Represents an MLS KeyPackage for group membership.
/// </summary>
public class KeyPackage
{
    /// <summary>
    /// Cipher suites that Scramble can currently process.
    /// Other suites are displayed but marked as unsupported.
    /// </summary>
    public static readonly HashSet<ushort> SupportedCipherSuites = new() { 0x0001, 0x0003 };

    /// <summary>
    /// Human-readable names for known cipher suites (RFC 9420 + ts-mls extensions).
    /// </summary>
    public static readonly Dictionary<ushort, string> CipherSuiteNames = new()
    {
        { 0x0001, "X25519 / AES-128-GCM / SHA-256 / Ed25519" },
        { 0x0002, "P-256 / AES-128-GCM / SHA-256 / P-256" },
        { 0x0003, "X25519 / ChaCha20-Poly1305 / SHA-256 / Ed25519" },
        { 0x0004, "X448 / AES-256-GCM / SHA-512 / Ed448" },
        { 0x0005, "P-521 / AES-256-GCM / SHA-512 / P-521" },
        { 0x0006, "X448 / ChaCha20-Poly1305 / SHA-512 / Ed448" },
        { 0x0007, "P-384 / AES-256-GCM / SHA-384 / P-384" },
    };

    /// <summary>
    /// Whether this KeyPackage uses a cipher suite that Scramble supports.
    /// </summary>
    public bool IsCipherSuiteSupported => SupportedCipherSuites.Contains(CiphersuiteId);

    /// <summary>
    /// Human-readable cipher suite name, or "Unknown (0xNNNN)" for unrecognized suites.
    /// </summary>
    public string CipherSuiteName => CipherSuiteNames.TryGetValue(CiphersuiteId, out var name)
        ? name
        : $"Unknown (0x{CiphersuiteId:x4})";

    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Nostr public key of the owner (hex format).
    /// </summary>
    public string OwnerPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Serialized MLS KeyPackage bytes (decoded from base64 content).
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// The full JSON-serialized Nostr event (kind 30443) containing this KeyPackage.
    /// Required for MLS processing.
    /// </summary>
    public string? EventJson { get; set; }

    /// <summary>
    /// Nostr event ID where this KeyPackage was published (kind 30443).
    /// </summary>
    public string? NostrEventId { get; set; }

    /// <summary>
    /// When the KeyPackage was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the KeyPackage expires (should be rotated before this).
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Whether this KeyPackage has been used (can only be used once).
    /// </summary>
    public bool IsUsed { get; set; }

    /// <summary>
    /// When this KeyPackage was consumed by a Welcome message. Null if not yet consumed.
    /// For last-resort KPs, init_key material is retained for 24 h after this timestamp
    /// before being zeroized (MIP-00 §"Deletion Timing").
    /// </summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>
    /// MLS ciphersuite identifier.
    /// </summary>
    public ushort CiphersuiteId { get; set; }

    /// <summary>
    /// Whether we have the local private key material to process Welcomes encrypted to this KeyPackage.
    /// </summary>
    public KeyPackageStatus Status { get; set; } = KeyPackageStatus.Active;

    /// <summary>
    /// The d-tag (slot ID) from the kind 30443 addressable event.
    /// Each device uses a unique, stable slot ID so multiple devices can publish
    /// KeyPackages under the same Nostr identity without overwriting each other.
    /// </summary>
    public string? SlotId { get; set; }

    /// <summary>
    /// Relay URLs where this KeyPackage was published.
    /// </summary>
    public List<string> RelayUrls { get; set; } = new();

    /// <summary>
    /// Nostr tags provided by MDK for MIP-00 compliance.
    /// These should be used when publishing the KeyPackage event.
    /// </summary>
    public List<List<string>> NostrTags { get; set; } = new();

    /// <summary>
    /// Creates a new KeyPackage with default expiration (30 days).
    /// </summary>
    public static KeyPackage Create(string ownerPublicKey, byte[] data, ushort ciphersuiteId = 0x0001)
    {
        return new KeyPackage
        {
            Id = Guid.NewGuid().ToString(),
            OwnerPublicKey = ownerPublicKey,
            Data = data,
            CiphersuiteId = ciphersuiteId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
    }
}
