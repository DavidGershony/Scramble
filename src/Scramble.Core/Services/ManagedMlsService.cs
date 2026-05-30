using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using Scramble.Core.Logging;
using Scramble.Core.Models;
using MarmotCs.Core;
using MarmotCs.Core.Results;
using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using MarmotCs.Protocol.Crypto;
using MarmotCs.Protocol.Mip00;
using MarmotCs.Protocol.Mip03;

namespace Scramble.Core.Services;

/// <summary>
/// IMlsService implementation backed by the pure C# marmut-mdk library.
/// Uses Mdk&lt;EncryptedSqliteStorageProvider&gt; for MLS group management.
/// MLS state is persisted directly to SQLite via the storage provider —
/// no blob export/import needed.
/// </summary>
public class ManagedMlsService : IMlsService
{
    private readonly ILogger<ManagedMlsService> _logger;
    private readonly ICipherSuite _cipherSuite = new CipherSuite0x0001();
    private readonly IStorageService? _storageService;

    private Mdk<EncryptedSqliteStorageProvider>? _mdk;
    private EncryptedSqliteStorageProvider? _storageProvider;
    private string? _publicKeyHex;
    private string? _privateKeyHex;
    private byte[]? _identity;
    private byte[]? _signingPrivateKey;
    private byte[]? _signingPublicKey;

    public string? LastEncryptedRumorEventId { get; private set; }

    /// <summary>
    /// Private key material for a stored KeyPackage.
    /// MLS allows multiple KeyPackages per user (RFC 9420 Section 16.8).
    /// Each can only be used for one Welcome, so we keep several available.
    /// </summary>
    private class StoredKeyPackageMaterial
    {
        public byte[] KeyPackageBytes { get; init; } = Array.Empty<byte>();
        public byte[] InitPrivateKey { get; init; } = Array.Empty<byte>();
        public byte[] HpkePrivateKey { get; init; } = Array.Empty<byte>();
        public bool IsLastResort { get; init; }
        // Timestamp when this last-resort KP was consumed by a Welcome. Key material is
        // retained for 24 h after consumption so that concurrent senders who fetched the
        // same KP before rotation propagated can still complete their Welcomes.
        // Null = not yet consumed. After 24 h the material is zeroized (MIP-00 §"Deletion Timing").
        public DateTimeOffset? ConsumedAt { get; set; }
    }

    // All stored KeyPackages with their private keys (supports multiple).
    // IMPORTANT: All reads/writes must be performed under lock(_kpLock).
    private readonly List<StoredKeyPackageMaterial> _storedKeyPackages = new();
    private readonly object _kpLock = new();

    // Stable d-tag (slot ID) for kind 30443 KeyPackage events. MIP-00 requires this to be
    // generated once per slot and reused on every rotation so the relay replaces the previous
    // KeyPackage in place rather than accumulating new addressable-event slots. Persisted in the
    // service state and lazily generated on first KeyPackage publish.
    private string? _keyPackageSlotId;

    // Optional external signer for kind 445 events (Amber / NIP-46)
    private INostrEventSigner? _nostrEventSigner;

    // Prevents concurrent InitializeAsync calls from racing
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private const string ServiceStateKey = "__service__";
    private const byte ServiceStateVersion = 4;
    private const byte ServiceStateVersion5 = 5;
    private const byte ServiceStateVersion1 = 1;
    private const byte ServiceStateVersion2 = 2;
    private const byte ServiceStateVersion3 = 3;

    public ManagedMlsService(IStorageService? storageService = null)
    {
        _storageService = storageService;
        _logger = LoggingConfiguration.CreateLogger<ManagedMlsService>();
        _logger.LogInformation("ManagedMlsService instance created (C# MDK backend, persistence={HasStorage})",
            storageService != null);
    }

    public async Task InitializeAsync(string privateKeyHex, string publicKeyHex)
    {
        await _initLock.WaitAsync();
        try
        {
            // Guard against double-initialization clobbering restored state
            if (_mdk != null)
            {
                _logger.LogDebug("ManagedMlsService already initialized, skipping");
                return;
            }

            _logger.LogInformation("Initializing managed MLS service");
            _publicKeyHex = publicKeyHex;
            _privateKeyHex = privateKeyHex;
            _identity = Convert.FromHexString(publicKeyHex);

            // Try to restore signing keys and KeyPackages from persisted service state first.
            // Track restore outcome with three states:
            //   restoredKeys = true  → DB had valid state, keys + KPs restored
            //   importFailed = true  → DB had state but import threw (transient or data issue)
            //   neither              → no prior state in DB (first launch or post-logout)
            // The distinction matters: on import failure we must NOT overwrite the DB
            // (the state may be recoverable on next restart), whereas on "no prior state"
            // we safely persist the freshly generated keys.
            bool restoredKeys = false;
            bool importFailed = false;
            if (_storageService != null)
            {
                try
                {
                    var serviceState = await _storageService.GetMlsStateAsync(ServiceStateKey);
                    if (serviceState != null && serviceState.Length > 0)
                    {
                        await ImportServiceStateAsync(serviceState);
                        restoredKeys = _signingPrivateKey != null && _signingPublicKey != null;
                        _logger.LogInformation("Restored MLS service state from persistence (signingKey={HasKey}, storedKeyPackages={Count})",
                            restoredKeys, _storedKeyPackages.Count);
                    }
                    else if (serviceState != null)
                    {
                        // Zero-byte blob: treat as "no prior state" rather than feeding it to the
                        // TLS decoder and tripping a noisy "Insufficient data" stack trace.
                        _logger.LogDebug("Persisted MLS service state is empty; will generate new keys");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to restore MLS service state, will generate new keys");
                    importFailed = true;

                    // ImportServiceStateAsync is now transactional (parses into temps,
                    // commits only on full success), so instance fields should be clean.
                    // Belt-and-suspenders: explicitly clear any signing keys to ensure
                    // restoredKeys stays false and new keys are generated below.
                    _signingPrivateKey = null;
                    _signingPublicKey = null;
                    _keyPackageSlotId = null;
                }
            }

            // Generate new Ed25519 signing keys only if we couldn't restore them
            if (!restoredKeys)
            {
                (_signingPrivateKey, _signingPublicKey) = _cipherSuite.GenerateSignatureKeyPair();
                _logger.LogInformation("Generated new MLS signing keys");
            }

            // Use the main scramble.db for MLS state with a "mls_" table prefix
            // to avoid name collisions with Scramble's own tables.
            var baseDbPath = _storageService?.DatabasePath ?? ":memory:";
            var connStr = baseDbPath == ":memory:"
                ? "Data Source=:memory:"
                : $"Data Source={baseDbPath}";
            var secureStorage = _storageService?.SecureStorage
                ?? throw new InvalidOperationException(
                    "SecureStorage is required for MLS data protection. " +
                    "Ensure a platform-specific ISecureStorage implementation is registered.");
            _storageProvider = new EncryptedSqliteStorageProvider(connStr, secureStorage, tablePrefix: "mls_");

            _mdk = new MdkBuilder<EncryptedSqliteStorageProvider>()
                .WithStorage(_storageProvider)
                .WithConfig(MdkConfig.Default)
                .Build();

            _logger.LogInformation("Managed MLS service initialized successfully");

            // Restore group states from the MlsStates table.
            // The MDK's in-memory cache is empty after Build() — groups persisted
            // by PersistGroupStateAsync must be explicitly re-imported.
            if (_storageService != null)
            {
                try
                {
                    var allChats = await _storageService.GetAllChatsAsync();
                    var groupCount = 0;
                    foreach (var chat in allChats.Where(c => (c.Type == ChatType.Group || c.Type == ChatType.DeviceSync) && c.MlsGroupId != null))
                    {
                        try
                        {
                            var hex = Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();
                            var state = await _storageService.GetMlsStateAsync(hex);
                            if (state != null)
                            {
                                _mdk.ImportGroupState(chat.MlsGroupId!, state);
                                groupCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to restore MLS state for group {ChatId}", chat.Id);
                        }
                    }
                    if (groupCount > 0)
                        _logger.LogInformation("Restored {Count} MLS group(s) from persistence", groupCount);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate chats for MLS group state restoration");
                }
            }

            // Save service state only when we genuinely have no prior state (first
            // launch or post-logout). If the import failed (importFailed == true), we
            // must NOT overwrite the DB — the persisted state may be recoverable on
            // the next restart (e.g., transient DB lock, Keystore not yet ready).
            // Overwriting here would permanently destroy the KeyPackage private keys.
            if (!restoredKeys && !importFailed)
            {
                await SaveServiceStateAsync();
            }
            else if (importFailed)
            {
                _logger.LogWarning("Skipping SaveServiceStateAsync to preserve existing DB state after import failure");
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    public Task ResetAsync()
    {
        _initLock.Wait();
        try
        {
            _logger.LogInformation("Resetting MLS service state (in-memory only, DB preserved)");
            _mdk = null;
            _publicKeyHex = null;
            _privateKeyHex = null;

            // Zero sensitive key material before releasing references
            if (_identity != null) CryptographicOperations.ZeroMemory(_identity);
            if (_signingPrivateKey != null) CryptographicOperations.ZeroMemory(_signingPrivateKey);
            if (_signingPublicKey != null) CryptographicOperations.ZeroMemory(_signingPublicKey);
            lock (_kpLock)
            {
                foreach (var kp in _storedKeyPackages)
                {
                    CryptographicOperations.ZeroMemory(kp.InitPrivateKey);
                    CryptographicOperations.ZeroMemory(kp.HpkePrivateKey);
                }
                _storedKeyPackages.Clear();
            }

            _identity = null;
            _signingPrivateKey = null;
            _signingPublicKey = null;
            _keyPackageSlotId = null;
            _nostrEventSigner = null;

            // NOTE: We deliberately do NOT wipe the persisted DB state here.
            // ResetAsync is called during account switches and logout, but the
            // ShellViewModel design preserves per-profile data so users can switch
            // back later. The DB retains KeyPackage private keys so Welcomes sent
            // while the user is switched away can still be processed on return.
            //
            // For an explicit full wipe, use StorageService.ResetMlsDataAsync()
            // (exposed via the "Reset MLS Data" button in Settings).
        }
        finally
        {
            _initLock.Release();
        }

        return Task.CompletedTask;
    }

    public async Task<KeyPackage> GenerateKeyPackageAsync()
    {
        EnsureInitialized();
        PruneExpiredConsumedKeyPackages();

        // Call MlsGroup.CreateKeyPackage directly to capture initPriv/hpkePriv.
        // Advertise support for required extensions:
        //   0x000A = LastResort (RFC 9420 Section 17.3) — required by Rust MDK
        //   0xF2EE = NostrGroupData (marmot extension for group metadata)
        var mlsKp = MlsGroup.CreateKeyPackage(
            _cipherSuite, _identity!, _signingPrivateKey!, _signingPublicKey!,
            out var initPrivateKey, out var hpkePrivateKey,
            supportedExtensionTypes: new ushort[] { 0x000A, 0xF2EE });

        // Store for later ProcessWelcomeAsync (add to list, don't overwrite)
        byte[] kpBytes = TlsCodec.Serialize(writer => mlsKp.WriteTo(writer));
        lock (_kpLock)
        {
            _storedKeyPackages.Add(new StoredKeyPackageMaterial
            {
                KeyPackageBytes = kpBytes,
                InitPrivateKey = initPrivateKey,
                HpkePrivateKey = hpkePrivateKey,
                IsLastResort = true
            });
        }

        // Build Nostr event tags using the protocol builder.
        // Lazily generate the per-identity KeyPackage slot ID on first publish, then reuse it for
        // every rotation so the kind 30443 addressable event is replaced in place on the relay
        // instead of accumulating fresh slots (MIP-00 KeyPackage Lifecycle Management).
        if (string.IsNullOrEmpty(_keyPackageSlotId))
        {
            _keyPackageSlotId = KeyPackageSlotId.GenerateNew();
            _logger.LogInformation("Generated new KeyPackage slot ID for identity {Identity}",
                _publicKeyHex![..Math.Min(16, _publicKeyHex!.Length)]);
        }

        var (content, tags) = KeyPackageEventBuilder.BuildKeyPackageEvent(
            kpBytes, _publicKeyHex!, Array.Empty<string>(),
            supportedExtensionTypes: new ushort[] { 0x000A, 0xF2EE },
            slotId: _keyPackageSlotId);

        // Convert string[][] tags to List<List<string>> for Scramble's KeyPackage model
        var nostrTags = tags.Select(t => t.ToList()).ToList();

        var keyPackage = KeyPackage.Create(_publicKeyHex!, kpBytes, 0x0001);
        keyPackage.NostrTags = nostrTags;

        _logger.LogInformation(
            "Generated KeyPackage with {TagCount} tags, {Len} bytes (managed)",
            nostrTags.Count, kpBytes.Length);

        await SaveServiceStateAsync();

        return keyPackage;
    }

    public async Task<MlsGroupInfo> CreateGroupAsync(string groupName, string[] relayUrls)
    {
        EnsureInitialized();

        if (relayUrls.Length == 0)
            throw new ArgumentException("At least one relay URL is required for group creation.", nameof(relayUrls));

        _logger.LogInformation("CreateGroup: creating MLS group '{GroupName}' with {RelayCount} relays (managed)",
            groupName, relayUrls.Length);

        var result = await _mdk!.CreateGroupAsync(
            _identity!, _signingPrivateKey!, _signingPublicKey!,
            groupName, relayUrls);

        var groupId = result.Group.Id.Value;
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();

        _logger.LogInformation("CreateGroup: created group {GroupId}, epoch={Epoch} (managed)",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], result.Group.Epoch);

        await PersistGroupStateAsync(groupId);

        return new MlsGroupInfo
        {
            GroupId = groupId,
            GroupName = groupName,
            Epoch = result.Group.Epoch,
            MemberPublicKeys = new List<string> { _publicKeyHex! }
        };
    }

    public async Task<MlsWelcome> AddMemberAsync(byte[] groupId, KeyPackage keyPackage)
    {
        EnsureInitialized();

        if (string.IsNullOrEmpty(keyPackage.EventJson))
            throw new InvalidOperationException("KeyPackage is missing the Nostr event JSON required for MLS processing");

        // Extract raw KeyPackage bytes from the base64-encoded event content.
        byte[] kpBytes;
        try
        {
            using var doc = JsonDocument.Parse(keyPackage.EventJson);
            var root = doc.RootElement;

            var content = root.GetProperty("content").GetString()
                ?? throw new InvalidOperationException("KeyPackage event has null content");

            kpBytes = Convert.FromBase64String(content);
            if (kpBytes.Length == 0)
                throw new InvalidOperationException("KeyPackage content decoded to empty bytes");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to parse KeyPackage event JSON", ex);
        }

        // Capture the pre-commit exporter secret BEFORE AddMembersAsync advances the epoch.
        // MIP-03: commits must be encrypted with the current epoch's exporter secret
        // so that existing members (at the current epoch) can decrypt them.
        var preCommitExporterSecret = _mdk!.GetExporterSecret(groupId);

        var result = await _mdk!.AddMembersAsync(groupId, new[] { kpBytes });


        // MIP-02 requires the Welcome to be wrapped in an MLSMessage container:
        //   MLSMessage { version=mls10(0x0001), wire_format=mls_welcome(0x0003), Welcome }
        // The C# MDK produces raw Welcome TLS bytes, so we prepend the 4-byte header.
        var rawWelcome = result.WelcomeBytes ?? Array.Empty<byte>();
        var mlsMessage = WrapWelcomeInMlsMessage(rawWelcome);

        // Wrap commit in MlsMessage envelope (same as Welcome wrapping but with wire_format=mls_private_message)
        // Without this, OpenMLS can't parse the raw PrivateMessage TLS bytes.
        var wrappedCommit = WrapPrivateMessageInMlsMessage(result.CommitMessageBytes);
        _logger.LogInformation("AddMember: commit bytes first 8: {Hex}, len={Len}",
            Convert.ToHexString(wrappedCommit[..Math.Min(8, wrappedCommit.Length)]).ToLowerInvariant(),
            wrappedCommit.Length);

        // MIP-03 encrypt the commit with the PRE-commit exporter secret
        var mip03EncryptedBase64 = GroupEventEncryption.Encrypt(wrappedCommit, preCommitExporterSecret);
        var mip03Encrypted = Convert.FromBase64String(mip03EncryptedBase64);

        await PersistGroupStateAsync(groupId);

        return new MlsWelcome
        {
            WelcomeData = mlsMessage,
            CommitData = mip03Encrypted,  // Now MIP-03 encrypted, not raw
            RecipientPublicKey = keyPackage.OwnerPublicKey,
            KeyPackageEventId = keyPackage.NostrEventId
        };
    }

    public async Task<MlsGroupInfo> ProcessWelcomeAsync(byte[] welcomeData, string wrapperEventId)
    {
        EnsureInitialized();

        PruneExpiredConsumedKeyPackages();

        // Snapshot under lock for iteration — the loop body does async MLS work,
        // so we can't hold the lock across it.
        StoredKeyPackageMaterial[] kpSnapshot;
        lock (_kpLock)
        {
            if (_storedKeyPackages.Count == 0)
                throw new InvalidOperationException("No stored KeyPackage data. Call GenerateKeyPackageAsync first.");
            kpSnapshot = _storedKeyPackages.ToArray();
        }

        _logger.LogInformation("ProcessWelcomeAsync: wrapperEventId={EventId}, welcomeData={Len} bytes, {KpCount} stored KeyPackages (managed)",
            wrapperEventId[..Math.Min(16, wrapperEventId.Length)], welcomeData.Length, kpSnapshot.Length);

        // The welcome data may be either:
        // 1. Raw TLS bytes (from managed/C# MDK sender) — ready to use directly
        // 2. UTF-8 JSON (from Rust/native MDK sender) — a Nostr rumor event (or array)
        //    where the actual MLS Welcome binary is base64-encoded in the "content" field
        var mlsWelcomeBytes = ExtractMlsWelcomeBytes(welcomeData);
        _logger.LogInformation("ProcessWelcome: extracted MLS bytes ({Len} bytes), first 16: {Hex}",
            mlsWelcomeBytes.Length,
            Convert.ToHexString(mlsWelcomeBytes[..Math.Min(16, mlsWelcomeBytes.Length)]));

        // Try each stored KeyPackage — the Welcome is encrypted to one specific KeyPackage.
        // MLS allows clients to publish multiple KeyPackages (RFC 9420 Section 16.8).
        // Randomize attempt order to prevent timing side-channel leaking which index matched.
        Exception? lastError = null;
        var indices = Enumerable.Range(0, kpSnapshot.Length).ToList();
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
        foreach (var i in indices)
        {
            var kp = kpSnapshot[i];
            try
            {
                _logger.LogDebug("ProcessWelcome: trying stored KeyPackage {Index}/{Total} ({Len} bytes)",
                    i + 1, kpSnapshot.Length, kp.KeyPackageBytes.Length);

                var preview = await _mdk!.PreviewWelcomeAsync(
                    mlsWelcomeBytes,
                    kp.KeyPackageBytes,
                    kp.InitPrivateKey,
                    kp.HpkePrivateKey,
                    _signingPrivateKey!);

                var group = await _mdk.AcceptWelcomeAsync(
                    preview.WelcomeId,
                    kp.KeyPackageBytes,
                    kp.InitPrivateKey,
                    kp.HpkePrivateKey,
                    _signingPrivateKey!);

                _logger.LogInformation("ProcessWelcome: matched stored KeyPackage {Index}/{Total}",
                    i + 1, kpSnapshot.Length);

                // MIP-00 §"Rotating KeyPackages": SHOULD rotate after successfully joining.
                // MIP-02 §"Processing Requirements" step 5: publish fresh kind:30443 under same d-tag.
                // MIP-02 §"Processing Requirements" step 6: securely delete init_key.
                //
                // Non-last-resort KPs: zeroize immediately.
                // Last-resort KPs (RFC 9420 §17.3 / MIP-00 §"Deletion Timing"): mark consumed and
                // retain key material for 24 h so concurrent senders who fetched the same KP before
                // rotation propagated can still get their Welcome processed. PruneExpiredConsumedKeyPackages
                // removes the material once the grace window elapses.
                if (!kp.IsLastResort)
                {
                    _logger.LogInformation("ProcessWelcome: matched KeyPackage {Index}/{Total}, zeroizing consumed init_key (MIP-02 step 6)",
                        i + 1, kpSnapshot.Length);
                    CryptographicOperations.ZeroMemory(kp.InitPrivateKey);
                    CryptographicOperations.ZeroMemory(kp.HpkePrivateKey);
                    lock (_kpLock)
                    {
                        _storedKeyPackages.Remove(kp);
                    }
                }
                else
                {
                    kp.ConsumedAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation(
                        "ProcessWelcome: marked last-resort KeyPackage {Index}/{Total} consumed at {Time} — init_key retained for 24 h grace window (MIP-00)",
                        i + 1, kpSnapshot.Length, kp.ConsumedAt.Value.UtcDateTime);
                }

                await PersistGroupStateAsync(preview.GroupId);
                await SaveServiceStateAsync();

                return new MlsGroupInfo
                {
                    GroupId = preview.GroupId,
                    GroupName = preview.GroupName,
                    Epoch = group.Epoch,
                    MemberPublicKeys = preview.MemberIdentities.Select(k => k.ToLowerInvariant()).ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug("ProcessWelcome: KeyPackage {Index} did not match: {Error}",
                    i + 1, ex.Message);
                lastError = ex;
            }
        }

        int currentCount;
        lock (_kpLock) { currentCount = _storedKeyPackages.Count; }
        throw new InvalidOperationException(
            $"None of the {currentCount} stored KeyPackages match this Welcome. " +
            "The private key material for the targeted KeyPackage may have been lost.",
            lastError);
    }

    public async Task<bool> CanProcessWelcomeAsync(byte[] welcomeData)
    {
        // Snapshot under lock — the loop body does async MLS work
        StoredKeyPackageMaterial[] kpSnapshot;
        lock (_kpLock)
        {
            if (_storedKeyPackages.Count == 0)
            {
                _logger.LogInformation("CanProcessWelcome: no stored KeyPackages — cannot process welcome");
                return false;
            }
            kpSnapshot = _storedKeyPackages.ToArray();
        }

        var mlsWelcomeBytes = ExtractMlsWelcomeBytes(welcomeData);
        _logger.LogDebug("CanProcessWelcome: checking {KpCount} stored KeyPackage(s) against welcome ({Len} bytes)",
            kpSnapshot.Length, mlsWelcomeBytes.Length);

        foreach (var kp in kpSnapshot)
        {
            try
            {
                await _mdk!.PreviewWelcomeAsync(
                    mlsWelcomeBytes,
                    kp.KeyPackageBytes,
                    kp.InitPrivateKey,
                    kp.HpkePrivateKey,
                    _signingPrivateKey!);

                _logger.LogInformation("CanProcessWelcome: found matching KeyPackage — welcome is processable");
                return true;
            }
            catch
            {
                // This key didn't match — try the next one
            }
        }

        _logger.LogInformation("CanProcessWelcome: no stored KeyPackage matched — welcome cannot be processed on this device");
        return false;
    }

    public async Task<byte[]> EncryptMessageAsync(byte[] groupId, string plaintext, List<List<string>>? rumorTags = null)
    {
        EnsureInitialized();

        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        _logger.LogDebug("EncryptMessage: group={GroupId}, plaintext length={Len}, tags={TagCount} (managed)",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], plaintext.Length, rumorTags?.Count ?? 0);

        // Match the Rust MDK flow:
        // 1. Wrap plaintext in a Nostr rumor event (kind 9) with optional tags
        // 2. MLS-encrypt the rumor → raw TLS bytes
        // 3. MIP-03 encrypt with exporter_secret (ChaCha20-Poly1305)
        // 4. Build a signed Nostr event (kind 445) with base64 content and h-tag

        // Step 1: Create rumor event JSON (kind 9, pubkey, content, tags)
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rumorTagsList = rumorTags ?? new List<List<string>>();
        var rumorId = ComputeRumorEventId(_publicKeyHex!, now, kind: 9, rumorTagsList, plaintext);
        LastEncryptedRumorEventId = rumorId;
        var escapedContent = JsonSerializer.Serialize(plaintext);
        var rumorTagsJson = rumorTagsList.Count > 0
            ? JsonSerializer.Serialize(rumorTagsList)
            : "[]";
        var rumorJson = $"{{\"id\":\"{rumorId}\",\"pubkey\":\"{_publicKeyHex}\",\"created_at\":{now},\"kind\":9,\"tags\":{rumorTagsJson},\"content\":{escapedContent}}}";

        // Step 2: MLS-encrypt the rumor
        var mlsBytes = await _mdk!.CreateMessageAsync(groupId, Encoding.UTF8.GetBytes(rumorJson));

        // Step 3: MIP-03 encrypt + build kind 445 content and tags via GroupEventBuilder
        var exporterSecret = _mdk!.GetExporterSecret(groupId);
        _logger.LogInformation("EncryptMessage: exporter secret (epoch={Epoch}): {Secret}",
            (await _mdk.GetGroupAsync(groupId))?.Epoch,
            Convert.ToHexString(exporterSecret).ToLowerInvariant());
        var nostrGroupId = GetNostrGroupId(groupId);
        var (base64Content, mip03Tags) = GroupEventBuilder.BuildGroupEvent(mlsBytes, nostrGroupId, exporterSecret);

        // Step 4: Build signed kind 445 Nostr event with exporter-derived key (MIP-03)
        // The key is derived from the group's exporter secret so group members
        // can verify the event was signed by someone who knows the group secret.
        // Sender identity is inside the MLS-encrypted rumor, not in the Nostr event pubkey.
        var tags = mip03Tags.Select(t => t.ToList()).ToList();
        var eventJson = BuildEphemeralSignedEvent(445, base64Content, tags, exporterSecret);

        _logger.LogDebug("EncryptMessage: produced {Len} bytes event JSON (managed)", eventJson.Length);

        await PersistGroupStateAsync(groupId);

        return eventJson;
    }

    public async Task<byte[]> EncryptReactionAsync(byte[] groupId, string emoji, string targetRumorEventId)
    {
        EnsureInitialized();

        // Map emoji to NIP-25 content for interop
        var content = emoji switch
        {
            "\U0001F44D" => "+", // 👍
            "\U0001F44E" => "-", // 👎
            _ => emoji
        };

        _logger.LogInformation("EncryptReaction: emoji={Emoji}, target={Target}", emoji, targetRumorEventId[..Math.Min(16, targetRumorEventId.Length)]);

        // Build kind 7 rumor with "e" tag pointing to target message
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var reactionTagsList = new List<List<string>> { new() { "e", targetRumorEventId } };
        var rumorId = ComputeRumorEventId(_publicKeyHex!, now, kind: 7, reactionTagsList, content);
        LastEncryptedRumorEventId = rumorId;
        var escapedContent = JsonSerializer.Serialize(content);
        var reactionTags = JsonSerializer.Serialize(reactionTagsList);
        var rumorJson = $"{{\"id\":\"{rumorId}\",\"pubkey\":\"{_publicKeyHex}\",\"created_at\":{now},\"kind\":7,\"tags\":{reactionTags},\"content\":{escapedContent}}}";

        // MLS-encrypt, MIP-03 encrypt, build kind 445 event (same as EncryptMessageAsync)
        var mlsBytes = await _mdk!.CreateMessageAsync(groupId, Encoding.UTF8.GetBytes(rumorJson));
        var exporterSecret = _mdk!.GetExporterSecret(groupId);
        var nostrGroupId = GetNostrGroupId(groupId);
        var (base64Content, mip03Tags) = GroupEventBuilder.BuildGroupEvent(mlsBytes, nostrGroupId, exporterSecret);

        var tags = mip03Tags.Select(t => t.ToList()).ToList();
        var eventJson = BuildEphemeralSignedEvent(445, base64Content, tags, exporterSecret);

        await PersistGroupStateAsync(groupId);

        return eventJson;
    }

    public async Task<MlsDecryptedMessage> DecryptMessageAsync(byte[] groupId, byte[] ciphertext,
        string? nostrEventId = null, DateTimeOffset? nostrCreatedAt = null)
    {
        EnsureInitialized();

        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        _logger.LogDebug("DecryptMessage: group={GroupId}, ciphertext={Len} bytes (managed)",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], ciphertext.Length);

        // Ciphertext may be:
        // 1. A JSON Nostr event (from our own EncryptMessageAsync) with base64 content
        // 2. Raw MIP-03 ChaCha20-Poly1305 encrypted bytes (from web app / relay)
        // 3. Raw MLS PrivateMessage bytes (internal tests)
        bool isFromNostrEvent = ciphertext.Length > 0 && (ciphertext[0] == (byte)'{' || ciphertext[0] == (byte)'[');
        var payloadBytes = ExtractMlsMessageBytes(ciphertext);

        // MIP-03: All relay messages (both JSON events and raw bytes) are
        // ChaCha20-Poly1305 encrypted with the group's MLS exporter secret.
        // Always apply MIP-03 decryption unless the bytes are already a valid MLS message.
        // MLS wire format: version 0x0001 followed by wire_format:
        //   0x0001 = PublicMessage (commits/proposals)
        //   0x0002 = PrivateMessage (application messages)
        bool isRawMlsMessage = payloadBytes.Length >= 4 &&
            payloadBytes[0] == 0x00 && payloadBytes[1] == 0x01 &&
            payloadBytes[2] == 0x00 && (payloadBytes[3] == 0x01 || payloadBytes[3] == 0x02);

        byte[] mlsBytes;
        if (!isRawMlsMessage)
        {
            _logger.LogDebug("DecryptMessage: applying MIP-03 ChaCha20-Poly1305 decryption ({Len} bytes)",
                payloadBytes.Length);
            _logger.LogInformation("DecryptMessage: MIP-03 ciphertext first 32 bytes: {Hex}",
                Convert.ToHexString(payloadBytes[..Math.Min(32, payloadBytes.Length)]).ToLowerInvariant());
            var exporterSecret = _mdk!.GetExporterSecret(groupId);
            _logger.LogInformation("DecryptMessage: exporter secret (epoch={Epoch}): {Secret}",
                (await _mdk.GetGroupAsync(groupId))?.Epoch,
                Convert.ToHexString(exporterSecret).ToLowerInvariant());
            mlsBytes = GroupEventEncryption.Decrypt(Convert.ToBase64String(payloadBytes), exporterSecret);
            _logger.LogDebug("DecryptMessage: MIP-03 decrypted to {Len} bytes, first 32: {Hex}",
                mlsBytes.Length,
                Convert.ToHexString(mlsBytes[..Math.Min(32, mlsBytes.Length)]));
        }
        else
        {
            mlsBytes = payloadBytes;
        }

        // Use real Nostr event ID for deduplication and MIP-03 tiebreaker, or synthetic if not available
        var eventId = nostrEventId ?? Guid.NewGuid().ToString("N");
        var result = await _mdk!.ProcessMessageAsync(groupId, mlsBytes, eventId, nostrCreatedAt);
        await PersistGroupStateAsync(groupId);

        if (result is ApplicationMessageResult appMsg)
        {
            var senderHex = Convert.ToHexString(appMsg.Message.SenderIdentity).ToLowerInvariant();
            var plaintext = Encoding.UTF8.GetString(appMsg.Message.Content);
            var rumorJsonFull = plaintext; // Preserve full rumor JSON before content extraction

            // The Rust MDK wraps messages as Nostr rumor events (JSON with "content" field).
            // Extract the actual message text from the rumor event if present.
            // Also parse imeta tags for image messages (MIP-04).
            string? imageUrl = null;
            string? mediaType = null;
            string? fileName = null;
            string? fileSha256 = null;
            string? encryptionNonce = null;
            string? encryptionVersion = null;
            int rumorKind = 9;
            string? rumorEventId = null;
            string? reactionTargetEventId = null;
            string? reactionEmoji = null;
            string? replyToRumorEventId = null;

            if (plaintext.Length > 0 && plaintext[0] == '{')
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(plaintext);

                    // MIP-03 §"Application Messages": "clients MUST verify the MLS sender matches
                    // the inner event's pubkey" — reject messages where inner rumor pubkey doesn't
                    // match the MLS-authenticated sender identity.
                    if (doc.RootElement.TryGetProperty("pubkey", out var rumorPubkeyProp))
                    {
                        var rumorPubkey = rumorPubkeyProp.GetString();
                        if (rumorPubkey != null && !string.Equals(rumorPubkey, senderHex, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning(
                                "DecryptMessage: MIP-03 sender verification FAILED — MLS sender {MlsSender} != rumor pubkey {RumorPubkey}. Dropping message.",
                                senderHex, rumorPubkey);
                            throw new InvalidOperationException(
                                $"MIP-03 inner sender verification failed: MLS sender '{senderHex}' does not match rumor pubkey '{rumorPubkey}'. Possible spoofing attempt.");
                        }
                    }

                    if (doc.RootElement.TryGetProperty("content", out var contentProp))
                    {
                        var extracted = contentProp.GetString();
                        if (extracted != null)
                        {
                            _logger.LogDebug("DecryptMessage: extracted content from rumor event ({RumorLen} bytes → {ContentLen} chars)",
                                plaintext.Length, extracted.Length);
                            plaintext = extracted;
                        }
                    }

                    // Extract rumor id and kind
                    if (doc.RootElement.TryGetProperty("id", out var idProp))
                    {
                        rumorEventId = idProp.GetString();
                    }
                    if (doc.RootElement.TryGetProperty("kind", out var kindProp))
                    {
                        rumorKind = kindProp.GetInt32();
                    }

                    // Parse tags from the rumor event
                    if (doc.RootElement.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var tag in tagsProp.EnumerateArray())
                        {
                            if (tag.GetArrayLength() < 2) continue;
                            var tagName = tag[0].GetString();

                            // Parse "e" tags — check for marker to distinguish replies from reactions
                            if (tagName == "e")
                            {
                                var marker = tag.GetArrayLength() >= 4 ? tag[3].GetString() : null;
                                if (marker == "reply")
                                {
                                    replyToRumorEventId = tag[1].GetString();
                                }
                                else
                                {
                                    reactionTargetEventId = tag[1].GetString();
                                }
                            }

                            // NIP-22 "q" tag — used by White Noise and other clients for replies/quotes
                            if (tagName == "q" && replyToRumorEventId == null)
                            {
                                replyToRumorEventId = tag[1].GetString();
                            }

                            // Parse imeta tags for image metadata (MIP-04)
                            if (tagName == "imeta")
                            {
                                _logger.LogDebug("DecryptMessage: found imeta tag with {Count} entries", tag.GetArrayLength() - 1);

                                for (var i = 1; i < tag.GetArrayLength(); i++)
                                {
                                    var entry = tag[i].GetString();
                                    if (string.IsNullOrEmpty(entry)) continue;

                                    if (entry.StartsWith("url "))
                                        imageUrl = entry.Substring(4);
                                    else if (entry.StartsWith("m "))
                                        mediaType = entry.Substring(2);
                                    else if (entry.StartsWith("filename "))
                                        fileName = entry.Substring(9);
                                    else if (entry.StartsWith("x "))
                                        fileSha256 = entry.Substring(2);
                                    else if (entry.StartsWith("n ") && entry.Length <= 26)
                                        encryptionNonce = entry.Substring(2);
                                    else if (entry.StartsWith("nonce "))
                                        encryptionNonce = entry.Substring(6);
                                    else if (entry.StartsWith("v "))
                                        encryptionVersion = entry.Substring(2);
                                    else if (entry.StartsWith("encryption-version "))
                                        encryptionVersion = entry.Substring(19);
                                }

                                if (imageUrl != null)
                                {
                                    _logger.LogInformation("DecryptMessage: extracted imeta - url={Url}, type={MediaType}, filename={FileName}, sha256={Sha256}, nonce={Nonce}, version={Version}",
                                        imageUrl, mediaType ?? "(none)", fileName ?? "(none)",
                                        fileSha256?[..Math.Min(16, fileSha256?.Length ?? 0)] ?? "(none)",
                                        encryptionNonce?[..Math.Min(16, encryptionNonce?.Length ?? 0)] ?? "(none)",
                                        encryptionVersion ?? "(none)");
                                }
                            }
                        }
                    }

                    // Determine reaction emoji from content (NIP-25 convention)
                    if (rumorKind == 7 && reactionTargetEventId != null)
                    {
                        reactionEmoji = plaintext switch
                        {
                            "+" => "\U0001F44D", // 👍
                            "-" => "\U0001F44E", // 👎
                            _ => plaintext
                        };
                        _logger.LogInformation("DecryptMessage: detected reaction kind=7, emoji={Emoji}, target={Target}",
                            reactionEmoji, reactionTargetEventId[..Math.Min(16, reactionTargetEventId.Length)]);
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogDebug(ex, "DecryptMessage: plaintext starts with '{{' but is not valid JSON, using raw value");
                }
            }

            _logger.LogDebug("DecryptMessage: sender={Sender}, epoch={Epoch}, plaintext length={Len} (managed)",
                senderHex[..Math.Min(16, senderHex.Length)], appMsg.Message.Epoch, plaintext.Length);

    
            return new MlsDecryptedMessage
            {
                SenderPublicKey = senderHex,
                Plaintext = plaintext,
                RumorJson = rumorJsonFull,
                Epoch = appMsg.Message.Epoch,
                ImageUrl = imageUrl,
                MediaType = mediaType,
                FileName = fileName,
                FileSha256 = fileSha256,
                EncryptionNonce = encryptionNonce,
                EncryptionVersion = encryptionVersion,
                RumorEventId = rumorEventId,
                RumorKind = rumorKind,
                ReactionTargetEventId = reactionTargetEventId,
                ReactionEmoji = reactionEmoji,
                ReplyToRumorEventId = replyToRumorEventId
            };
        }

        // Handle commit messages — these advance the epoch but carry no user-visible content.
        // This happens when another member adds/removes a user or updates keys.
        if (result is CommitResult commitResult)
        {
            _logger.LogInformation("DecryptMessage: processed commit for group {GroupId}, epoch advanced (managed)",
                groupIdHex[..Math.Min(16, groupIdHex.Length)]);
                return new MlsDecryptedMessage
            {
                IsCommit = true,
                Epoch = 0, // Epoch info not directly available from CommitResult
                SenderPublicKey = string.Empty,
                Plaintext = string.Empty
            };
        }

        var reason = result is UnprocessableResult ur ? ur.Reason : result.GetType().Name;
        throw new InvalidOperationException($"Expected ApplicationMessageResult or CommitResult but got {result.GetType().Name}: {reason}");
    }

    public async Task ProcessCommitAsync(byte[] groupId, byte[] commitData)
    {
        EnsureInitialized();

        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        _logger.LogInformation("ProcessCommit: group={GroupId}, commit={Len} bytes (managed)",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], commitData.Length);

        var eventId = Guid.NewGuid().ToString("N");
        var result = await _mdk!.ProcessMessageAsync(groupId, commitData, eventId);

        if (result is CommitResult)
        {
            _logger.LogInformation("ProcessCommit: success for group {GroupId} (managed)",
                groupIdHex[..Math.Min(16, groupIdHex.Length)]);
            await PersistGroupStateAsync(groupId);
            return;
        }

        if (result is UnprocessableResult unprocessable)
        {
            throw new InvalidOperationException($"Failed to process commit: {unprocessable.Reason}");
        }
    }

    public async Task<byte[]> UpdateKeysAsync(byte[] groupId)
    {
        EnsureInitialized();

        var result = await _mdk!.SelfUpdateAsync(groupId);
        await PersistGroupStateAsync(groupId);

        return result.CommitMessageBytes;
    }

    public async Task<byte[]> RemoveMemberAsync(byte[] groupId, string memberPublicKey)
    {
        EnsureInitialized();

        // Find the leaf index for this member's identity
        var members = await _mdk!.GetMembersAsync(groupId);
        var memberHexUpper = memberPublicKey.ToUpperInvariant();
        var member = members.FirstOrDefault(m =>
            m.identityHex.Equals(memberHexUpper, StringComparison.OrdinalIgnoreCase));

        if (member.identityHex == null)
            throw new InvalidOperationException($"Member {memberPublicKey} not found in group");

        var result = await _mdk.RemoveMembersAsync(groupId, new[] { member.leafIndex });


        return result.CommitMessageBytes;
    }

    public async Task<MlsWelcome> StageAddMemberAsync(byte[] groupId, KeyPackage keyPackage)
    {
        EnsureInitialized();

        if (string.IsNullOrEmpty(keyPackage.EventJson))
            throw new InvalidOperationException("KeyPackage is missing the Nostr event JSON required for MLS processing");

        byte[] kpBytes;
        try
        {
            using var doc = JsonDocument.Parse(keyPackage.EventJson);
            var root = doc.RootElement;
            var content = root.GetProperty("content").GetString()
                ?? throw new InvalidOperationException("KeyPackage event has null content");
            kpBytes = Convert.FromBase64String(content);
            if (kpBytes.Length == 0)
                throw new InvalidOperationException("KeyPackage content decoded to empty bytes");
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to parse KeyPackage event JSON", ex);
        }

        // Capture pre-commit exporter secret BEFORE staging (MIP-03: encrypt with current epoch's key)
        var preCommitExporterSecret = _mdk!.GetExporterSecret(groupId);

        // Stage — does NOT advance epoch
        var result = await _mdk.StageAddMembersAsync(groupId, new[] { kpBytes });

        var rawWelcome = result.WelcomeBytes ?? Array.Empty<byte>();
        var mlsMessage = WrapWelcomeInMlsMessage(rawWelcome);

        // The commit bytes from Stage* are already in MLSMessage envelope
        // Encrypt commit with pre-commit exporter secret (MIP-03)
        var mip03EncryptedBase64 = GroupEventEncryption.Encrypt(result.CommitMessageBytes, preCommitExporterSecret);
        var mip03Encrypted = Convert.FromBase64String(mip03EncryptedBase64);

        return new MlsWelcome
        {
            WelcomeData = mlsMessage,
            CommitData = mip03Encrypted,
            RecipientPublicKey = keyPackage.OwnerPublicKey,
            KeyPackageEventId = keyPackage.NostrEventId
        };
    }

    public async Task MergeStagedAsync(byte[] groupId)
    {
        EnsureInitialized();
        await _mdk!.MergeStagedCommitAsync(groupId);
        await PersistGroupStateAsync(groupId);
    }

    public Task ClearStagedAsync(byte[] groupId)
    {
        EnsureInitialized();
        _mdk!.ClearPendingCommit(groupId);
        return Task.CompletedTask;
    }

    public async Task<byte[]> StageRemoveMemberAsync(byte[] groupId, string memberPublicKey)
    {
        EnsureInitialized();

        var members = await _mdk!.GetMembersAsync(groupId);
        var memberHexUpper = memberPublicKey.ToUpperInvariant();
        var member = members.FirstOrDefault(m =>
            m.identityHex.Equals(memberHexUpper, StringComparison.OrdinalIgnoreCase));

        if (member.identityHex == null)
            throw new InvalidOperationException($"Member {memberPublicKey} not found in group");

        var result = await _mdk.StageRemoveMembersAsync(groupId, new[] { member.leafIndex });

        return result.CommitMessageBytes;
    }

    public bool HasPendingCommit(byte[] groupId)
    {
        EnsureInitialized();
        return _mdk!.HasPendingCommit(groupId);
    }

    public async Task<MlsGroupInfo?> GetGroupInfoAsync(byte[] groupId)
    {
        EnsureInitialized();

        var group = await _mdk!.GetGroupAsync(groupId);
        if (group == null) return null;

        var members = await _mdk.GetMembersAsync(groupId);

        return new MlsGroupInfo
        {
            GroupId = groupId,
            GroupName = group.Name,
            Epoch = group.Epoch,
            MemberPublicKeys = members.Select(m => m.identityHex.ToLowerInvariant()).ToList()
        };
    }

    public Task<byte[]> ExportGroupStateAsync(byte[] groupId)
    {
        EnsureInitialized();

        var stateBytes = _mdk!.ExportGroupState(groupId);
        return Task.FromResult(stateBytes);
    }

    public Task ImportGroupStateAsync(byte[] groupId, byte[] state)
    {
        EnsureInitialized();

        _mdk!.ImportGroupState(groupId, state);
        _logger.LogInformation("Imported MLS group state for {GroupId}", Convert.ToHexString(groupId).ToLowerInvariant());
        return Task.CompletedTask;
    }

    public Task<byte[]?> ExportServiceStateAsync()
    {
        if (_signingPrivateKey == null || _signingPublicKey == null)
            return Task.FromResult<byte[]?>(null);

        // Snapshot KeyPackages under lock to avoid racing with Add/Remove/Clear
        // on other threads (GenerateKeyPackageAsync, ProcessWelcomeAsync, ImportServiceStateAsync).
        StoredKeyPackageMaterial[] kpSnapshot;
        lock (_kpLock)
        {
            kpSnapshot = _storedKeyPackages.ToArray();
        }

        var stateBytes = TlsCodec.Serialize(writer =>
        {
            writer.WriteUint8(ServiceStateVersion5);
            // v5: identity + signing keys + KeyPackage slot ID + stored KeyPackages with ConsumedAt.
            // Identical to v4 except each KP entry gains a length-prefixed ConsumedAt field:
            //   empty bytes  = not consumed
            //   8 bytes BE   = Unix seconds (int64) when the KP was consumed by a Welcome
            writer.WriteOpaqueV(_identity ?? Array.Empty<byte>());
            writer.WriteOpaqueV(_signingPrivateKey);
            writer.WriteOpaqueV(_signingPublicKey);

            // Slot ID may be null if no KeyPackage has been generated yet on this identity.
            // Encode as length-prefixed UTF-8; empty string means "not yet generated".
            var slotBytes = string.IsNullOrEmpty(_keyPackageSlotId)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(_keyPackageSlotId);
            writer.WriteOpaqueV(slotBytes);

            // Write count of stored KeyPackages, then each one (from snapshot)
            writer.WriteUint16((ushort)kpSnapshot.Length);
            foreach (var kp in kpSnapshot)
            {
                writer.WriteOpaqueV(kp.KeyPackageBytes);
                writer.WriteOpaqueV(kp.InitPrivateKey);
                writer.WriteOpaqueV(kp.HpkePrivateKey);
                var consumedAtBytes = kp.ConsumedAt.HasValue
                    ? EncodeBigEndianInt64(kp.ConsumedAt.Value.ToUnixTimeSeconds())
                    : Array.Empty<byte>();
                writer.WriteOpaqueV(consumedAtBytes);
            }
        });

        return Task.FromResult<byte[]?>(stateBytes);
    }

    public Task ImportServiceStateAsync(byte[] state)
    {
        // Defensive guard: an empty buffer is "no state to import", not a parse error.
        // Keeps the TLS decoder from throwing a noisy "Insufficient data" stack trace
        // when the storage layer returns an empty blob (multi-account first-use, etc.).
        if (state == null || state.Length == 0)
            return Task.CompletedTask;

        var reader = new TlsReader(state);
        byte version = reader.ReadUint8();

        // ── Fully transactional parsing ──
        // ALL fields are parsed into local temporaries first. Instance fields are
        // only committed once parsing succeeds completely. This prevents the
        // dangerous partial-import scenario where _signingPrivateKey is set but
        // _storedKeyPackages remains empty (e.g., TLS data truncated or corrupted
        // after the signing-key bytes). Without this, the caller's catch block in
        // InitializeAsync would generate new signing keys and call
        // SaveServiceStateAsync(), permanently overwriting the DB with zero KPs.
        byte[]? tempSigningPrivateKey = null;
        byte[]? tempSigningPublicKey = null;
        string? tempKeyPackageSlotId = null;
        var parsed = new List<StoredKeyPackageMaterial>();

        if (version == ServiceStateVersion5)
        {
            // v5 format: identity + signing keys + KeyPackage slot ID + KeyPackages with ConsumedAt
            var storedIdentity = reader.ReadOpaqueV();

            if (_identity != null && !storedIdentity.SequenceEqual(_identity))
            {
                _logger.LogWarning("Persisted MLS state identity mismatch (stored={Stored}, current={Current}). Discarding stale state.",
                    Convert.ToHexString(storedIdentity).ToLowerInvariant()[..Math.Min(16, storedIdentity.Length * 2)],
                    Convert.ToHexString(_identity).ToLowerInvariant()[..Math.Min(16, _identity.Length * 2)]);
                return Task.CompletedTask;
            }

            tempSigningPrivateKey = reader.ReadOpaqueV();
            tempSigningPublicKey = reader.ReadOpaqueV();

            var slotBytes = reader.ReadOpaqueV();
            tempKeyPackageSlotId = slotBytes.Length == 0 ? null : Encoding.UTF8.GetString(slotBytes);

            ushort count = reader.ReadUint16();
            for (int i = 0; i < count; i++)
            {
                var kpBytes = reader.ReadOpaqueV();
                var initPriv = reader.ReadOpaqueV();
                var hpkePriv = reader.ReadOpaqueV();
                var consumedAtBytes = reader.ReadOpaqueV();
                DateTimeOffset? consumedAt = consumedAtBytes.Length == 8
                    ? DateTimeOffset.FromUnixTimeSeconds(DecodeBigEndianInt64(consumedAtBytes))
                    : null;
                parsed.Add(new StoredKeyPackageMaterial
                {
                    KeyPackageBytes = kpBytes,
                    InitPrivateKey = initPriv,
                    HpkePrivateKey = hpkePriv,
                    IsLastResort = true,
                    ConsumedAt = consumedAt
                });
            }
        }
        else if (version == ServiceStateVersion)
        {
            // v4 format: identity + signing keys + KeyPackage slot ID + KeyPackages (no ConsumedAt)
            // Migrate: set ConsumedAt = null for all KPs (treat as freshly published).
            var storedIdentity = reader.ReadOpaqueV();

            // Verify identity matches current user
            if (_identity != null && !storedIdentity.SequenceEqual(_identity))
            {
                _logger.LogWarning("Persisted MLS state identity mismatch (stored={Stored}, current={Current}). Discarding stale state.",
                    Convert.ToHexString(storedIdentity).ToLowerInvariant()[..Math.Min(16, storedIdentity.Length * 2)],
                    Convert.ToHexString(_identity).ToLowerInvariant()[..Math.Min(16, _identity.Length * 2)]);
                return Task.CompletedTask; // Don't restore — will generate fresh keys
            }

            tempSigningPrivateKey = reader.ReadOpaqueV();
            tempSigningPublicKey = reader.ReadOpaqueV();

            // Slot ID: empty bytes means "not yet generated"; the next GenerateKeyPackageAsync
            // call will lazily produce one.
            var slotBytes = reader.ReadOpaqueV();
            tempKeyPackageSlotId = slotBytes.Length == 0 ? null : Encoding.UTF8.GetString(slotBytes);

            ushort count = reader.ReadUint16();
            for (int i = 0; i < count; i++)
            {
                parsed.Add(new StoredKeyPackageMaterial
                {
                    KeyPackageBytes = reader.ReadOpaqueV(),
                    InitPrivateKey = reader.ReadOpaqueV(),
                    HpkePrivateKey = reader.ReadOpaqueV(),
                    IsLastResort = true,
                    ConsumedAt = null
                });
            }

            _logger.LogInformation("Migrated MLS service state v4 -> v5 (ConsumedAt = null for all KPs)");
        }
        else if (version == ServiceStateVersion3)
        {
            // v3 format: identity + signing keys + KeyPackages (no slot ID).
            // Migrate by reading v3 fields; the next GenerateKeyPackageAsync call will lazily
            // mint a slot ID on first use, and the next SaveServiceStateAsync will persist as v4.
            // NOTE: any KeyPackages already published under random d-tags before this upgrade
            // will remain orphaned on relays until they expire; this is acceptable per MIP-00
            // (no NIP-09 deletion required for KeyPackage rotation).
            var storedIdentity = reader.ReadOpaqueV();

            if (_identity != null && !storedIdentity.SequenceEqual(_identity))
            {
                _logger.LogWarning("Persisted MLS state identity mismatch (stored={Stored}, current={Current}). Discarding stale state.",
                    Convert.ToHexString(storedIdentity).ToLowerInvariant()[..Math.Min(16, storedIdentity.Length * 2)],
                    Convert.ToHexString(_identity).ToLowerInvariant()[..Math.Min(16, _identity.Length * 2)]);
                return Task.CompletedTask;
            }

            tempSigningPrivateKey = reader.ReadOpaqueV();
            tempSigningPublicKey = reader.ReadOpaqueV();
            tempKeyPackageSlotId = null; // will be lazily generated on next publish

            ushort count = reader.ReadUint16();
            for (int i = 0; i < count; i++)
            {
                parsed.Add(new StoredKeyPackageMaterial
                {
                    KeyPackageBytes = reader.ReadOpaqueV(),
                    InitPrivateKey = reader.ReadOpaqueV(),
                    HpkePrivateKey = reader.ReadOpaqueV(),
                    IsLastResort = true // All KPs from this implementation include LastResort extension
                });
            }

            _logger.LogInformation("Migrated MLS service state v3 -> v4 (slot ID will be generated on next KeyPackage publish)");
        }
        else if (version == ServiceStateVersion1)
        {
            // v1 format: signing keys + single optional KeyPackage (no identity — discard)
            _logger.LogWarning("Discarding v1 MLS state (no identity verification). Will regenerate keys.");
            return Task.CompletedTask;
        }
        else if (version == ServiceStateVersion2)
        {
            // v2 format: signing keys + KeyPackage list (no identity — discard)
            _logger.LogWarning("Discarding v2 MLS state (no identity verification). Will regenerate keys.");
            return Task.CompletedTask;
        }
        else
        {
            throw new InvalidOperationException($"Unsupported service state version: {version}");
        }

        // ── Commit phase: parsing succeeded — apply all fields atomically ──
        _signingPrivateKey = tempSigningPrivateKey;
        _signingPublicKey = tempSigningPublicKey;
        _keyPackageSlotId = tempKeyPackageSlotId;

        // Atomically swap under lock — no window where _storedKeyPackages is empty
        lock (_kpLock)
        {
            _storedKeyPackages.Clear();
            _storedKeyPackages.AddRange(parsed);
        }

        // Remove KPs whose 24 h retention window expired while the app was closed
        PruneExpiredConsumedKeyPackages();

        _logger.LogInformation("Restored MLS service state (signingKey={Len} bytes, storedKeyPackages={Count}, slotId={HasSlot})",
            _signingPrivateKey!.Length, _storedKeyPackages.Count, _keyPackageSlotId != null);
        return Task.CompletedTask;
    }

    public byte[] GetNostrGroupId(byte[] groupId)
    {
        EnsureInitialized();
        var nostrGroupId = _mdk!.GetNostrGroupId(groupId);
        if (nostrGroupId == null)
        {
            var hex = Convert.ToHexString(groupId).ToLowerInvariant();
            _logger.LogError("GetNostrGroupId: NostrGroupData extension (0xF2EE) not found in GroupContext for group {GroupId}. " +
                "This means the MarmotGroupData extension was not preserved across epochs.", hex[..Math.Min(16, hex.Length)]);
            throw new InvalidOperationException(
                $"NostrGroupId not found for group {hex[..Math.Min(16, hex.Length)]}. " +
                "The MarmotGroupData extension (0xF2EE) is missing from the GroupContext.");
        }
        return nostrGroupId;
    }

    /// <summary>
    /// Returns the admin public keys (as hex strings) from the 0xF2EE extension for a group.
    /// </summary>
    public List<string> GetAdminPubkeys(byte[] groupId)
    {
        EnsureInitialized();
        try
        {
            var groupData = _mdk!.GetNostrGroupData(groupId);
            if (groupData == null || groupData.AdminPubkeys.Length == 0)
                return new List<string>();

            var admins = new List<string>();
            for (int i = 0; i < groupData.AdminPubkeys.Length; i += 32)
            {
                var pubkey = new byte[32];
                Array.Copy(groupData.AdminPubkeys, i, pubkey, 0, 32);
                admins.Add(Convert.ToHexString(pubkey).ToLowerInvariant());
            }
            return admins;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract admin pubkeys for group");
            return new List<string>();
        }
    }

    // TODO: DIAGNOSTIC ONLY — remove after cross-impl epoch divergence is resolved
    /// <summary>
    /// Get the MIP-03 exporter secret for a group (for diagnostic/comparison purposes).
    /// </summary>
    public byte[] GetExporterSecret(byte[] groupId)
    {
        EnsureInitialized();
        return _mdk!.GetExporterSecret(groupId);
    }

    public int GetStoredKeyPackageCount()
    {
        lock (_kpLock) { return _storedKeyPackages.Count; }
    }

    public bool HasKeyMaterialForKeyPackage(byte[] keyPackageData)
    {
        lock (_kpLock)
        {
            return _storedKeyPackages.Any(kp =>
                kp.KeyPackageBytes.AsSpan().SequenceEqual(keyPackageData.AsSpan()));
        }
    }

    public void SetNostrEventSigner(INostrEventSigner signer)
    {
        _nostrEventSigner = signer;
        _logger.LogInformation("Nostr event signer set: {SignerType}", signer.GetType().Name);
    }

    public byte[] GetMediaExporterSecret(byte[] groupId)
    {
        EnsureInitialized();

        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        _logger.LogDebug("GetMediaExporterSecret: group={GroupId}", groupIdHex[..Math.Min(16, groupIdHex.Length)]);

        var mediaContext = Encoding.UTF8.GetBytes("encrypted-media");
        var result = _mdk!.GetExporterSecret(groupId, "marmot", mediaContext, 32);

        _logger.LogInformation("GetMediaExporterSecret: derived 32-byte secret for group {GroupId}",
            groupIdHex[..Math.Min(16, groupIdHex.Length)]);

        return result;
    }

    public string? GetLocalKeyPackageSlotId() => _keyPackageSlotId;

    public bool TryReconcileSlotId(IEnumerable<KeyPackage> relayKeyPackages)
    {
        if (!string.IsNullOrEmpty(_keyPackageSlotId))
        {
            _logger.LogDebug("TryReconcileSlotId: slot ID already set ({SlotId}), no reconciliation needed",
                _keyPackageSlotId[..Math.Min(16, _keyPackageSlotId.Length)]);
            return false;
        }

        int kpCount;
        lock (_kpLock) { kpCount = _storedKeyPackages.Count; }
        if (kpCount == 0)
        {
            _logger.LogDebug("TryReconcileSlotId: no stored KeyPackages locally, cannot match");
            return false;
        }

        // Find a relay KP whose MLS bytes match one of our locally stored KPs.
        // This identifies our own published KP and lets us adopt its d-tag (slot ID).
        foreach (var relayKp in relayKeyPackages)
        {
            if (string.IsNullOrEmpty(relayKp.SlotId) || relayKp.Data == null || relayKp.Data.Length == 0)
                continue;

            if (HasKeyMaterialForKeyPackage(relayKp.Data))
            {
                _keyPackageSlotId = relayKp.SlotId;
                _logger.LogInformation(
                    "TryReconcileSlotId: adopted slot ID {SlotId} from relay KP (matched local key material)",
                    _keyPackageSlotId[..Math.Min(16, _keyPackageSlotId.Length)]);

                // Persist the updated state so the slot ID survives restart
                _ = PersistReconciledSlotIdAsync();

                return true;
            }
        }

        _logger.LogDebug("TryReconcileSlotId: no relay KP matched locally stored key material");
        return false;
    }

    // ---- Persistence helpers ----

    private async Task PersistReconciledSlotIdAsync()
    {
        try { await SaveServiceStateAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist reconciled slot ID"); }
    }

    private async Task SaveServiceStateAsync()
    {
        if (_storageService == null) return;

        try
        {
            var state = await ExportServiceStateAsync();
            if (state != null)
            {
                await _storageService.SaveMlsStateAsync(ServiceStateKey, state);
                _logger.LogDebug("Saved MLS service state ({Len} bytes)", state.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save MLS service state");
        }
    }

    /// <summary>
    /// Extracts raw MLS Welcome TLS bytes from whatever format the welcome data arrives in.
    /// Welcome data can be:
    /// 1. MLSMessage-wrapped TLS bytes (from any MIP-02 compliant sender) — strip 4-byte header
    /// 2. Raw Welcome TLS bytes (legacy C# MDK) — use directly
    /// 3. UTF-8 JSON rumor event (from Rust/native MDK via MarmotWrapper) — extract base64 content
    /// The C# MDK's internal parser expects raw Welcome bytes (without MLSMessage header).
    /// </summary>
    private byte[] ExtractMlsWelcomeBytes(byte[] welcomeData)
    {
        if (welcomeData.Length == 0)
            throw new InvalidOperationException("Welcome data is empty.");

        byte first = welcomeData[0];

        // JSON starts with '{' (0x7B) or '[' (0x5B) — Rust/native MDK rumor event format
        if (first == (byte)'{' || first == (byte)'[')
        {
            _logger.LogInformation("ProcessWelcome: detected JSON welcome data from Rust/native MDK, extracting MLS Welcome bytes");
            return ExtractFromJsonRumor(welcomeData);
        }

        // Binary TLS data — check if it's MLSMessage-wrapped (MIP-02 compliant)
        // MLSMessage header: version=0x0001 (2 bytes) + wire_format=0x0003 (2 bytes)
        if (welcomeData.Length >= 4 &&
            welcomeData[0] == 0x00 && welcomeData[1] == 0x01 &&  // version = mls10
            welcomeData[2] == 0x00 && welcomeData[3] == 0x03)    // wire_format = mls_welcome
        {
            _logger.LogDebug("ProcessWelcome: stripping MLSMessage header from {Len} bytes", welcomeData.Length);
            return welcomeData[4..]; // Strip the 4-byte MLSMessage header
        }

        // Raw Welcome TLS bytes (legacy format without MLSMessage wrapper)
        _logger.LogDebug("ProcessWelcome: welcome data appears to be raw TLS ({Len} bytes)", welcomeData.Length);
        return welcomeData;
    }

    private byte[] ExtractFromJsonRumor(byte[] welcomeData)
    {
        try
        {
            var json = Encoding.UTF8.GetString(welcomeData);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // May be an array of rumor events — extract the first one
            JsonElement rumorEvent;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    throw new InvalidOperationException("Welcome JSON array is empty.");
                rumorEvent = root[0];
                _logger.LogDebug("ProcessWelcome: extracted first rumor event from array of {Count}", root.GetArrayLength());
            }
            else
            {
                rumorEvent = root;
            }

            // Extract the "content" field which contains base64-encoded MLSMessage(Welcome)
            var content = rumorEvent.GetProperty("content").GetString()
                ?? throw new InvalidOperationException("Rumor event has null content field.");

            var mlsBytes = Convert.FromBase64String(content);
            if (mlsBytes.Length == 0)
                throw new InvalidOperationException("Rumor event content decoded to empty bytes.");

            _logger.LogInformation(
                "ProcessWelcome: extracted {Len} bytes from JSON rumor event content",
                mlsBytes.Length);

            // The content may be MLSMessage-wrapped — strip the header if present
            if (mlsBytes.Length >= 4 &&
                mlsBytes[0] == 0x00 && mlsBytes[1] == 0x01 &&
                mlsBytes[2] == 0x00 && mlsBytes[3] == 0x03)
            {
                _logger.LogDebug("ProcessWelcome: stripping MLSMessage header from extracted bytes");
                return mlsBytes[4..];
            }

            return mlsBytes;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to extract MLS Welcome bytes from JSON welcome data", ex);
        }
    }

    /// <summary>
    /// Extracts raw MLS message bytes from whatever format the ciphertext arrives in.
    /// Handles JSON Nostr events (from Rust/native MDK) and raw TLS bytes (from C# MDK).
    /// </summary>
    private byte[] ExtractMlsMessageBytes(byte[] ciphertext)
    {
        if (ciphertext.Length == 0)
            return ciphertext;

        byte first = ciphertext[0];

        // JSON object or array — Rust/native MDK Nostr event format
        if (first == (byte)'{' || first == (byte)'[')
        {
            _logger.LogDebug("DecryptMessage: detected JSON message data, extracting MLS bytes");
            try
            {
                var json = Encoding.UTF8.GetString(ciphertext);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var content = root.GetProperty("content").GetString()
                    ?? throw new InvalidOperationException("Message event has null content field.");

                return Convert.FromBase64String(content);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to extract MLS message bytes from JSON event", ex);
            }
        }

        // Raw TLS bytes — pass through
        return ciphertext;
    }

    /// <summary>
    /// Wraps raw Welcome TLS bytes in an MLSMessage container per MIP-02:
    /// MLSMessage { version=mls10(0x0001), wire_format=mls_welcome(0x0003), Welcome }
    /// </summary>
    private static byte[] WrapWelcomeInMlsMessage(byte[] rawWelcome)
    {
        if (rawWelcome.Length == 0) return rawWelcome;

        // Don't double-wrap if already has MLSMessage header
        if (rawWelcome.Length >= 4 &&
            rawWelcome[0] == 0x00 && rawWelcome[1] == 0x01 &&
            rawWelcome[2] == 0x00 && rawWelcome[3] == 0x03)
        {
            return rawWelcome;
        }

        var result = new byte[4 + rawWelcome.Length];
        result[0] = 0x00; result[1] = 0x01; // ProtocolVersion = mls10 (1)
        result[2] = 0x00; result[3] = 0x03; // WireFormat = mls_welcome (3)
        Buffer.BlockCopy(rawWelcome, 0, result, 4, rawWelcome.Length);
        return result;
    }

    /// <summary>
    /// Wraps raw PrivateMessage TLS bytes in an MLSMessage container.
    /// Required for cross-implementation interop — OpenMLS expects the 4-byte header.
    /// </summary>
    private static byte[] WrapPrivateMessageInMlsMessage(byte[] rawPrivateMessage)
    {
        if (rawPrivateMessage.Length == 0) return rawPrivateMessage;

        // Don't double-wrap if already has MLSMessage header
        if (rawPrivateMessage.Length >= 4 &&
            rawPrivateMessage[0] == 0x00 && rawPrivateMessage[1] == 0x01 &&
            rawPrivateMessage[2] == 0x00 && rawPrivateMessage[3] == 0x02)
        {
            return rawPrivateMessage;
        }

        var result = new byte[4 + rawPrivateMessage.Length];
        result[0] = 0x00; result[1] = 0x01; // ProtocolVersion = mls10 (1)
        result[2] = 0x00; result[3] = 0x02; // WireFormat = mls_private_message (2)
        Buffer.BlockCopy(rawPrivateMessage, 0, result, 4, rawPrivateMessage.Length);
        return result;
    }

    /// <summary>
    /// Builds a signed Nostr event JSON (as UTF-8 bytes) matching the Rust MDK output format.
    /// Delegates to INostrEventSigner when available; otherwise falls back to local
    /// NIP-01 serialization, SHA-256 event ID, BIP-340 Schnorr signature.
    /// </summary>
    private async Task<byte[]> BuildSignedNostrEventAsync(int kind, string content, List<List<string>> tags)
    {
        // Delegate to injected signer when available
        if (_nostrEventSigner != null)
        {
            _logger.LogDebug("BuildSignedNostrEventAsync: delegating kind {Kind} signing to {SignerType}",
                kind, _nostrEventSigner.GetType().Name);
            return await _nostrEventSigner.SignEventAsync(kind, content, tags, _publicKeyHex!);
        }

        // Fallback: sign locally with private key (backward compatibility)
        _logger.LogDebug("BuildSignedNostrEventAsync: no signer set, using local private key for kind {Kind}", kind);
        var privateKeyBytes = Convert.FromHexString(_privateKeyHex!);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // NIP-01: event ID = SHA256([0,pubkey,created_at,kind,tags,content])
        string serializedForId;
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(0);
                writer.WriteStringValue(_publicKeyHex);
                writer.WriteNumberValue(createdAt);
                writer.WriteNumberValue(kind);
                writer.WriteStartArray();
                foreach (var tag in tags)
                {
                    writer.WriteStartArray();
                    foreach (var v in tag) writer.WriteStringValue(v);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteStringValue(content);
                writer.WriteEndArray();
            }
            serializedForId = Encoding.UTF8.GetString(stream.ToArray());
        }

        var eventIdBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serializedForId));
        var eventId = Convert.ToHexString(eventIdBytes).ToLowerInvariant();

        // BIP-340 Schnorr signature
        if (!Context.Instance.TryCreateECPrivKey(privateKeyBytes, out var ecPrivKey) || ecPrivKey is null)
            throw new InvalidOperationException("Invalid Nostr private key for signing");
        var sig = ecPrivKey.SignBIP340(eventIdBytes);
        var sigBytes = new byte[64];
        sig.WriteToSpan(sigBytes);
        var sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();

        // Build the event JSON object (NOT wrapped in ["EVENT",...] relay message)
        using var eventStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(eventStream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", eventId);
            writer.WriteString("pubkey", _publicKeyHex);
            writer.WriteNumber("created_at", createdAt);
            writer.WriteNumber("kind", kind);
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in tags)
            {
                writer.WriteStartArray();
                foreach (var v in tag) writer.WriteStringValue(v);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteString("content", content);
            writer.WriteString("sig", sigHex);
            writer.WriteEndObject();
        }
        return eventStream.ToArray();
    }

    public Task<byte[]> EncryptCommitAsync(byte[] groupId, byte[] mip03EncryptedCommitData)
    {
        EnsureInitialized();
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        _logger.LogDebug("EncryptCommit: wrapping {Len} bytes MIP-03 encrypted commit in kind 445 event",
            mip03EncryptedCommitData.Length);

        // CommitData is ALREADY MIP-03 encrypted by AddMemberAsync.
        // Just wrap in base64 + ephemeral-signed kind 445 event.
        var base64Content = Convert.ToBase64String(mip03EncryptedCommitData);

        var hTagValue = Convert.ToHexString(GetNostrGroupId(groupId)).ToLowerInvariant();
        var tags = new List<List<string>> { new() { "h", hTagValue }, new() { "encoding", "base64" } };
        var exporterSecret = _mdk!.GetExporterSecret(groupId);
        var eventJson = BuildEphemeralSignedEvent(445, base64Content, tags, exporterSecret);

        _logger.LogDebug("EncryptCommit: produced {Len} bytes event JSON", eventJson.Length);
        return Task.FromResult(eventJson);
    }

    /// <summary>
    /// Computes a NIP-01 event id for an MLS rumor event (kind 9 message or
    /// kind 7 reaction) before MLS-encrypting it.
    ///
    /// MDK 0.8.0 (PR #287) calls verify_id() on every rumor on both send and
    /// receive: it recomputes <c>SHA256(JSON.serialize([0,pubkey,created_at,kind,tags,content]))</c>
    /// and silently drops the message if the id field doesn't match. Earlier
    /// MDK versions accepted any id; that's why we used to slot in a random
    /// Guid here. Using the canonical NIP-01 id keeps us correct against
    /// both old and new MDK and ensures that reaction/reply "e" tags
    /// referencing this rumor stay valid across the wire.
    /// </summary>
    private static string ComputeRumorEventId(
        string pubkeyHex, long createdAt, int kind, List<List<string>> tags, string content)
    {
        var serialized = NostrService.SerializeForEventId(pubkeyHex, createdAt, kind, tags, content);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Signs a Nostr event with a randomly generated ephemeral keypair.
    /// Used for kind 445 group messages per MIP-03 to prevent linking messages to user identity.
    /// </summary>
    private byte[] BuildEphemeralSignedEvent(int kind, string content, List<List<string>> tags,
        byte[]? exporterSecret = null)
    {
        // MIP-03: Derive ephemeral keypair from the group's exporter secret so group members
        // can verify the event was signed by someone who knows the group secret.
        // Falls back to random key if exporter secret is not available.
        byte[] ephemeralPrivKey;
        if (exporterSecret != null)
        {
            (ephemeralPrivKey, _) = ExporterSecretKeyDerivation.DeriveKeyPair(exporterSecret);
        }
        else
        {
            ephemeralPrivKey = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(ephemeralPrivKey);
        }

        if (!Context.Instance.TryCreateECPrivKey(ephemeralPrivKey, out var ecPrivKey) || ecPrivKey is null)
            throw new InvalidOperationException("Failed to create ephemeral signing key");
        var ephemeralPubKey = ecPrivKey.CreateXOnlyPubKey();
        var ephPubBytes = new byte[32];
        ephemeralPubKey.WriteToSpan(ephPubBytes);
        var ephPubHex = Convert.ToHexString(ephPubBytes).ToLowerInvariant();

        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // NIP-01 event ID
        var serialized = NostrService.SerializeForEventId(ephPubHex, createdAt, kind, tags, content);
        var eventIdBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        var eventId = Convert.ToHexString(eventIdBytes).ToLowerInvariant();

        // BIP-340 Schnorr signature with ephemeral key
        var sig = ecPrivKey.SignBIP340(eventIdBytes);
        var sigBytes = new byte[64];
        sig.WriteToSpan(sigBytes);
        var sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();

        // Build event JSON
        using var eventStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(eventStream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", eventId);
            writer.WriteString("pubkey", ephPubHex);
            writer.WriteNumber("created_at", createdAt);
            writer.WriteNumber("kind", kind);
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in tags)
            {
                writer.WriteStartArray();
                foreach (var v in tag) writer.WriteStringValue(v);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteString("content", content);
            writer.WriteString("sig", sigHex);
            writer.WriteEndObject();
        }

        _logger.LogDebug("BuildEphemeralSignedEvent: kind {Kind}, ephemeral pubkey {PubKey}",
            kind, ephPubHex[..16]);

        return eventStream.ToArray();
    }

    /// <summary>
    /// Exports the current MLS group state and saves it to the MlsStates table.
    /// Called after every state-changing operation so the group survives app restart.
    /// </summary>
    private async Task PersistGroupStateAsync(byte[] groupId)
    {
        if (_storageService == null) return;

        try
        {
            var hex = Convert.ToHexString(groupId).ToLowerInvariant();
            var state = _mdk!.ExportGroupState(groupId);
            await _storageService.SaveMlsStateAsync(hex, state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist MLS state for group");
        }
    }

    /// <summary>
    /// Zeroizes and removes consumed last-resort KeyPackages whose 24 h MIP-00 grace window has elapsed.
    /// Called on app startup (after state restore), before generating a new KP, and before processing Welcomes.
    /// </summary>
    private void PruneExpiredConsumedKeyPackages()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        lock (_kpLock)
        {
            for (int i = _storedKeyPackages.Count - 1; i >= 0; i--)
            {
                var kp = _storedKeyPackages[i];
                if (kp.ConsumedAt.HasValue && kp.ConsumedAt.Value < cutoff)
                {
                    _logger.LogInformation(
                        "PruneExpiredConsumedKeyPackages: zeroizing last-resort init_key consumed at {Time} (24 h grace elapsed, MIP-00)",
                        kp.ConsumedAt.Value.UtcDateTime);
                    CryptographicOperations.ZeroMemory(kp.InitPrivateKey);
                    CryptographicOperations.ZeroMemory(kp.HpkePrivateKey);
                    _storedKeyPackages.RemoveAt(i);
                }
            }
        }
    }

    private static byte[] EncodeBigEndianInt64(long value) =>
        new byte[]
        {
            (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32),
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        };

    private static long DecodeBigEndianInt64(byte[] bytes) =>
        (long)bytes[0] << 56 | (long)bytes[1] << 48 | (long)bytes[2] << 40 | (long)bytes[3] << 32
        | (long)bytes[4] << 24 | (long)bytes[5] << 16 | (long)bytes[6] << 8 | (long)bytes[7];

    private void EnsureInitialized()
    {
        if (_mdk == null)
            throw new InvalidOperationException("MLS service not initialized. Call InitializeAsync first.");
    }
}
