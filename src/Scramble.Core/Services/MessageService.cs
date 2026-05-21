using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Core.Crypto;
using Scramble.Core.Models;
using MarmotCs.Protocol.Mip02;

namespace Scramble.Core.Services;

public class MessageService : IMessageService, IDisposable
{
    private readonly ILogger<MessageService> _logger;
    private readonly IStorageService _storageService;
    private readonly INostrService _nostrService;
    private readonly IMlsService _mlsService;

    private readonly Subject<Message> _newMessages = new();
    private readonly Subject<(string MessageId, MessageStatus Status)> _messageStatusUpdates = new();
    private readonly Subject<Chat> _chatUpdates = new();
    private readonly Subject<PendingInvite> _newInvites = new();
    private readonly Subject<MlsDecryptionError> _decryptionErrors = new();
    private readonly Subject<(string MessageId, string Emoji, string ReactorPublicKey)> _reactionUpdates = new();
    private readonly Subject<Unit> _skippedInvites = new();

    private User? _currentUser;
    private IDisposable? _eventSubscription;
    private bool _disposed;

    // MIP-02 §"Self-Update Timing": MUST perform self-update within 24h of joining.
    // Tracks groupIdHex -> joinTime for groups that haven't self-updated yet.
    private readonly ConcurrentDictionary<string, DateTime> _pendingSelfUpdates = new();
    private Timer? _selfUpdateTimer;

    // MIP-00 §"Rotating KeyPackages": proactively rotate KPs that have expired (>30 days old)
    // even when no Welcome has arrived to trigger AutoPublishKeyPackageIfNeededAsync.
    private Timer? _kpRotationTimer;

    // Deduplicates concurrent background relay fetches for the same pubkey.
    private readonly ConcurrentDictionary<string, byte> _inflightMetadataFetches = new();
    private readonly Subject<UserMetadata> _metadataUpdated = new();

    public IObservable<Message> NewMessages => _newMessages.AsObservable();
    public IObservable<(string MessageId, MessageStatus Status)> MessageStatusUpdates => _messageStatusUpdates.AsObservable();
    public IObservable<Chat> ChatUpdates => _chatUpdates.AsObservable();
    public IObservable<PendingInvite> NewInvites => _newInvites.AsObservable();
    public IObservable<MlsDecryptionError> DecryptionErrors => _decryptionErrors.AsObservable();
    public IObservable<(string MessageId, string Emoji, string ReactorPublicKey)> ReactionUpdates => _reactionUpdates.AsObservable();
    public IObservable<Unit> SkippedInvites => _skippedInvites.AsObservable();
    public IObservable<UserMetadata> MetadataUpdated => _metadataUpdated.AsObservable();

    public MessageService(IStorageService storageService, INostrService nostrService, IMlsService mlsService)
    {
        _logger = LoggingConfiguration.CreateLogger<MessageService>();
        _storageService = storageService;
        _nostrService = nostrService;
        _mlsService = mlsService;
    }

    public async Task InitializeAsync()
    {
        await _storageService.InitializeAsync();

        _currentUser = await _storageService.GetCurrentUserAsync();

        if (_currentUser != null && !string.IsNullOrEmpty(_currentUser.PublicKeyHex))
        {
            try
            {
                var mlsPrivateKey = _currentUser.PrivateKeyHex ?? new string('0', 64);
                await _mlsService.InitializeAsync(mlsPrivateKey, _currentUser.PublicKeyHex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MLS initialization failed in MessageService. MLS operations will be unavailable.");
            }

            // Subscribe to Nostr events (kind 444 Welcome, etc.)
            // Dispose any previous subscription to prevent duplicate event processing
            // (e.g., if InitializeAsync is called more than once)
            _eventSubscription?.Dispose();
            _eventSubscription = _nostrService.Events.Subscribe(OnNostrEventReceived);

            // MIP-02 §"Self-Update Timing": start periodic check for groups needing self-update
            _selfUpdateTimer?.Dispose();
            _selfUpdateTimer = new Timer(OnSelfUpdateTimerTick, null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));

            // MIP-00: proactive KP rotation — fires 10 min after login then hourly.
            // AutoPublishKeyPackageIfNeededAsync already handles rotation after each Welcome;
            // this catches devices that haven't received a Welcome in >30 days (KP expired).
            _kpRotationTimer?.Dispose();
            _kpRotationTimer = new Timer(OnKpRotationTimerTick, null,
                TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));

            _logger.LogInformation("MessageService initialized for {PubKey}",
                _currentUser.PublicKeyHex[..Math.Min(16, _currentUser.PublicKeyHex.Length)]);
        }
    }

    public async Task<IEnumerable<Chat>> GetChatsAsync()
    {
        var chats = await _storageService.GetAllChatsAsync();

        // Batch-load last message for all chats in a single query
        var lastMessages = await _storageService.GetLastMessagePerChatAsync();
        foreach (var chat in chats)
        {
            chat.LastMessage = lastMessages.GetValueOrDefault(chat.Id);
        }

        return chats;
    }

    public async Task<Chat?> GetChatAsync(string chatId)
    {
        return await _storageService.GetChatAsync(chatId);
    }

    public async Task<IEnumerable<Message>> GetMessagesAsync(string chatId, int limit = 50, int offset = 0)
    {
        var messages = await _storageService.GetMessagesForChatAsync(chatId, limit, offset);

        // Batch-resolve all unique senders in a single SQL query instead of N sequential queries
        var uniqueSenderKeys = messages.Select(m => m.SenderPublicKey).Distinct().ToList();
        var senderLookup = await _storageService.GetUsersByPublicKeysAsync(uniqueSenderKeys);

        // Build a lookup of loaded messages so reply resolution can avoid extra DB queries
        var messageList = messages.ToList();
        var messageLookup = messageList.ToDictionary(m => m.Id);

        var unknownKeys = new List<string>();
        foreach (var message in messageList)
        {
            message.Sender = senderLookup.GetValueOrDefault(message.SenderPublicKey);
            message.IsFromCurrentUser = message.SenderPublicKey == _currentUser?.PublicKeyHex;
            if (message.Sender == null && !message.IsFromCurrentUser)
            {
                unknownKeys.Add(message.SenderPublicKey);
            }

            // Resolve ReplyToMessage so the UI can show the reply quote
            if (!string.IsNullOrEmpty(message.ReplyToMessageId) && message.ReplyToMessage == null)
            {
                // Try the in-memory batch first, fall back to DB
                if (messageLookup.TryGetValue(message.ReplyToMessageId, out var replyTarget))
                {
                    message.ReplyToMessage = replyTarget;
                }
                else
                {
                    message.ReplyToMessage = await _storageService.GetMessageAsync(message.ReplyToMessageId);
                }

                // Resolve the reply target's sender for display
                if (message.ReplyToMessage != null && message.ReplyToMessage.Sender == null)
                {
                    message.ReplyToMessage.Sender = senderLookup.GetValueOrDefault(message.ReplyToMessage.SenderPublicKey)
                        ?? await _storageService.GetUserByPublicKeyAsync(message.ReplyToMessage.SenderPublicKey);
                }
            }
        }

        // Background-fetch profiles for any unknown senders (e.g. from before caching was added)
        if (unknownKeys.Count > 0)
        {
            _ = BackgroundFetchProfilesAsync(unknownKeys);
        }

        return messageList;
    }

    private async Task BackgroundFetchProfilesAsync(List<string> unknownKeys)
    {
        try
        {
            foreach (var pubKey in unknownKeys.Distinct())
            {
                try
                {
                    await FetchAndCacheProfileAsync(pubKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background profile fetch failed for {PubKey}", pubKey[..Math.Min(16, pubKey.Length)]);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BackgroundFetchProfilesAsync failed");
        }
    }

    public async Task<Message> SendMessageAsync(string chatId, string content)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ChatId = chatId,
            SenderPublicKey = _currentUser.PublicKeyHex,
            Sender = _currentUser,
            Type = MessageType.Text,
            Content = content,
            Timestamp = DateTime.UtcNow,
            Status = MessageStatus.Pending,
            IsFromCurrentUser = true
        };

        // Save locally first
        await _storageService.SaveMessageAsync(message);
        _newMessages.OnNext(message);

        try
        {
            // Encrypt and send via MLS if group chat
            if (chat.MlsGroupId != null)
            {
                var groupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
                _logger.LogInformation("SendMessage: encrypting for group {GroupId}, content length={Len}",
                    groupIdHex[..Math.Min(16, groupIdHex.Length)], content.Length);

                // EncryptMessageAsync returns a complete signed kind 445 Nostr event JSON
                // with the correct h-tag (NostrGroupId from 0xF2EE extension).
                // Publish it directly — do NOT wrap in another event.
                var eventJsonBytes = await _mlsService.EncryptMessageAsync(chat.MlsGroupId, content);
                _logger.LogDebug("SendMessage: encrypted to {Len} bytes, publishing kind 445", eventJsonBytes.Length);

                message.RumorEventId = _mlsService.LastEncryptedRumorEventId;
                message.NostrEventId = await _nostrService.PublishRawEventJsonAsync(eventJsonBytes);
                _logger.LogInformation("SendMessage: published kind 445 event {EventId}, rumorId={RumorId}",
                    message.NostrEventId, message.RumorEventId?[..Math.Min(16, message.RumorEventId?.Length ?? 0)]);
            }
            else if (chat.Type == ChatType.Bot)
            {
                // Bot chat - use NIP-17 gift-wrapped DMs (kind 14 rumor in kind 1059)
                var botPublicKey = chat.ParticipantPublicKeys
                    .FirstOrDefault(pk => pk != _currentUser.PublicKeyHex);

                if (!string.IsNullOrEmpty(botPublicKey))
                {
                    _logger.LogInformation("SendMessage: creating NIP-17 gift wrap for bot {Bot}",
                        botPublicKey[..Math.Min(16, botPublicKey.Length)]);

                    var rumorTags = new List<List<string>>
                    {
                        new() { "p", botPublicKey }
                    };

                    var botRelays = chat.RelayUrls.Count > 0 ? chat.RelayUrls : null;
                    message.NostrEventId = await _nostrService.PublishGiftWrapAsync(
                        rumorKind: 14,
                        content: content,
                        rumorTags: rumorTags,
                        senderPrivateKeyHex: _currentUser.PrivateKeyHex,
                        senderPublicKeyHex: _currentUser.PublicKeyHex,
                        recipientPublicKeyHex: botPublicKey,
                        targetRelayUrls: botRelays);

                    _logger.LogInformation("SendMessage: published gift-wrapped bot DM {EventId} to {Relays}",
                        message.NostrEventId[..Math.Min(16, message.NostrEventId.Length)],
                        botRelays != null ? string.Join(", ", botRelays) : "all relays");
                }
            }
            else
            {
                // Direct message - use NIP-44
                var recipientPublicKey = chat.ParticipantPublicKeys
                    .FirstOrDefault(pk => pk != _currentUser.PublicKeyHex);

                if (!string.IsNullOrEmpty(recipientPublicKey))
                {
                    _logger.LogInformation("SendMessage: encrypting DM for {Recipient}",
                        recipientPublicKey[..Math.Min(16, recipientPublicKey.Length)]);
                    var encryptedContent = await _nostrService.Nip44EncryptAsync(content, recipientPublicKey);
                    message.EncryptedContent = encryptedContent;
                }
            }

            message.Status = MessageStatus.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendMessage: failed to encrypt/publish message for chat {ChatId}", chatId);
            message.Status = MessageStatus.Failed;
        }

        await _storageService.SaveMessageAsync(message);
        _messageStatusUpdates.OnNext((message.Id, message.Status));

        // Update chat last activity
        chat.LastActivityAt = DateTime.UtcNow;
        chat.LastMessage = message;
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);

        return message;
    }

    public async Task SendVoiceMessageAsync(string chatId, string content, string mediaUrl,
        string sha256Hex, string nonceHex, string mimeType, string filename, double durationSeconds)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ChatId = chatId,
            SenderPublicKey = _currentUser.PublicKeyHex,
            Sender = _currentUser,
            Type = MessageType.Audio,
            Content = content,
            ImageUrl = mediaUrl,
            FileName = filename,
            FileSha256 = sha256Hex,
            EncryptionNonce = nonceHex,
            MediaType = mimeType,
            EncryptionVersion = "mip04-v2",
            AudioDurationSeconds = durationSeconds,
            Timestamp = DateTime.UtcNow,
            Status = MessageStatus.Pending,
            IsFromCurrentUser = true
        };

        await _storageService.SaveMessageAsync(message);
        _newMessages.OnNext(message);

        try
        {
            if (chat.MlsGroupId != null)
            {
                // MIP-04 v2 imeta tags for voice messages
                var imetaTags = new List<List<string>>
                {
                    new() { "imeta",
                        $"url {mediaUrl}",
                        $"m {mimeType}",
                        $"x {sha256Hex}",
                        $"n {nonceHex}",
                        $"v mip04-v2",
                        $"filename {filename}",
                        $"duration {durationSeconds:F1}" }
                };
                var eventJsonBytes = await _mlsService.EncryptMessageAsync(chat.MlsGroupId, "", imetaTags);
                message.RumorEventId = _mlsService.LastEncryptedRumorEventId;
                message.NostrEventId = await _nostrService.PublishRawEventJsonAsync(eventJsonBytes);
                _logger.LogInformation("Voice message published with imeta tags: event {EventId}", message.NostrEventId);
            }

            message.Status = MessageStatus.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send voice message for chat {ChatId}", chatId);
            message.Status = MessageStatus.Failed;
        }

        await _storageService.SaveMessageAsync(message);
        _messageStatusUpdates.OnNext((message.Id, message.Status));

        chat.LastActivityAt = DateTime.UtcNow;
        chat.LastMessage = message;
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);
    }

    public async Task SendMediaMessageAsync(string chatId, string content, string mediaUrl,
        string sha256Hex, string nonceHex, string mimeType, string filename, MessageType messageType)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ChatId = chatId,
            SenderPublicKey = _currentUser.PublicKeyHex,
            Sender = _currentUser,
            Type = messageType,
            Content = content,
            ImageUrl = mediaUrl,
            FileName = filename,
            FileSha256 = sha256Hex,
            EncryptionNonce = nonceHex,
            MediaType = mimeType,
            EncryptionVersion = "mip04-v2",
            Timestamp = DateTime.UtcNow,
            Status = MessageStatus.Pending,
            IsFromCurrentUser = true
        };

        await _storageService.SaveMessageAsync(message);
        _newMessages.OnNext(message);

        try
        {
            if (chat.MlsGroupId != null)
            {
                // Build imeta tags for MIP-04 media metadata so cross-impl clients
                // can download and decrypt the file
                // MIP-04 v2 imeta field names: n=nonce, v=version, filename=filename
                var imetaTags = new List<List<string>>
                {
                    new() { "imeta",
                        $"url {mediaUrl}",
                        $"m {mimeType}",
                        $"x {sha256Hex}",
                        $"n {nonceHex}",
                        $"v mip04-v2",
                        $"filename {filename}" }
                };
                // Content is empty per MIP-04 — all metadata is in the imeta tag
                var eventJsonBytes = await _mlsService.EncryptMessageAsync(chat.MlsGroupId, "", imetaTags);
                message.RumorEventId = _mlsService.LastEncryptedRumorEventId;
                message.NostrEventId = await _nostrService.PublishRawEventJsonAsync(eventJsonBytes);
                _logger.LogInformation("Media message published with imeta tags: event {EventId}", message.NostrEventId);
            }
            message.Status = MessageStatus.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send media message for chat {ChatId}", chatId);
            message.Status = MessageStatus.Failed;
        }

        await _storageService.SaveMessageAsync(message);
        _messageStatusUpdates.OnNext((message.Id, message.Status));

        chat.LastActivityAt = DateTime.UtcNow;
        chat.LastMessage = message;
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);
    }

    public async Task<Message> SendReplyAsync(string chatId, string content, string replyToMessageId)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        var replyToMessage = await _storageService.GetMessageAsync(replyToMessageId);

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ChatId = chatId,
            SenderPublicKey = _currentUser.PublicKeyHex,
            Sender = _currentUser,
            Type = MessageType.Text,
            Content = content,
            Timestamp = DateTime.UtcNow,
            Status = MessageStatus.Pending,
            IsFromCurrentUser = true,
            ReplyToMessageId = replyToMessageId,
            ReplyToMessage = replyToMessage
        };

        await _storageService.SaveMessageAsync(message);
        _newMessages.OnNext(message);

        try
        {
            if (chat.MlsGroupId != null)
            {
                // Build reply "e" tag with "reply" marker per NIP-10.
                // Use RumorEventId if available (for targeting within MLS), otherwise NostrEventId.
                var targetEventId = replyToMessage?.RumorEventId ?? replyToMessage?.NostrEventId ?? replyToMessageId;
                var replyTags = new List<List<string>>
                {
                    new() { "e", targetEventId, "", "reply" }
                };

                _logger.LogInformation("SendReply: encrypting reply to {Target} for group, content length={Len}",
                    targetEventId[..Math.Min(16, targetEventId.Length)], content.Length);

                var eventJsonBytes = await _mlsService.EncryptMessageAsync(chat.MlsGroupId, content, replyTags);
                message.RumorEventId = _mlsService.LastEncryptedRumorEventId;
                message.NostrEventId = await _nostrService.PublishRawEventJsonAsync(eventJsonBytes);

                _logger.LogInformation("SendReply: published kind 445 event {EventId}", message.NostrEventId);
            }
            else if (chat.Type == ChatType.Bot)
            {
                var botPublicKey = chat.ParticipantPublicKeys
                    .FirstOrDefault(pk => pk != _currentUser.PublicKeyHex);

                if (!string.IsNullOrEmpty(botPublicKey))
                {
                    var targetEventId = replyToMessage?.NostrEventId ?? replyToMessageId;
                    var rumorTags = new List<List<string>>
                    {
                        new() { "p", botPublicKey },
                        new() { "e", targetEventId, "", "reply" }
                    };

                    var botRelays = chat.RelayUrls.Count > 0 ? chat.RelayUrls : null;
                    message.NostrEventId = await _nostrService.PublishGiftWrapAsync(
                        rumorKind: 14,
                        content: content,
                        rumorTags: rumorTags,
                        senderPrivateKeyHex: _currentUser.PrivateKeyHex,
                        senderPublicKeyHex: _currentUser.PublicKeyHex,
                        recipientPublicKeyHex: botPublicKey,
                        targetRelayUrls: botRelays);
                }
            }

            message.Status = MessageStatus.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendReply: failed to encrypt/publish reply for chat {ChatId}", chatId);
            message.Status = MessageStatus.Failed;
        }

        await _storageService.SaveMessageAsync(message);
        _messageStatusUpdates.OnNext((message.Id, message.Status));

        chat.LastActivityAt = DateTime.UtcNow;
        chat.LastMessage = message;
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);

        return message;
    }

    public async Task<Chat> CreateGroupAsync(string name, IEnumerable<string> memberPublicKeys, IEnumerable<string>? relayUrlsOverride = null)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        _logger.LogInformation("CreateGroup: creating group '{Name}' with {MemberCount} initial members",
            name, memberPublicKeys.Count());

        // Create MLS group with connected relay URLs (or test override).
        var relayUrls = relayUrlsOverride?.ToArray() ?? _nostrService.ConnectedRelayUrls.ToArray();
        if (relayUrls.Length == 0)
            throw new InvalidOperationException("Cannot create group: no relays connected");
        var groupInfo = await _mlsService.CreateGroupAsync(name, relayUrls);
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        _logger.LogInformation("CreateGroup: MLS group created {GroupId}, epoch={Epoch}",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], groupInfo.Epoch);

        // Extract admin pubkeys from 0xF2EE extension; fall back to creator
        var adminPubkeys = _mlsService.GetAdminPubkeys(groupInfo.GroupId);
        if (adminPubkeys.Count == 0)
            adminPubkeys = new List<string> { _currentUser.PublicKeyHex.ToLowerInvariant() };

        // Get the NostrGroupId from the 0xF2EE extension for relay subscriptions
        byte[]? nostrGroupId = null;
        try { nostrGroupId = _mlsService.GetNostrGroupId(groupInfo.GroupId); }
        catch { _logger.LogWarning("CreateGroup: NostrGroupId not available from 0xF2EE extension"); }

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex },
            AdminPublicKeys = adminPubkeys,
            CreatorPublicKey = _currentUser.PublicKeyHex,
            RelayUrls = relayUrls.ToList(),
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        await _storageService.SaveChatAsync(chat);
        _logger.LogInformation("CreateGroup: saved chat {ChatId} for group '{Name}'", chat.Id, name);

        // Add members
        foreach (var memberPublicKey in memberPublicKeys)
        {
            if (memberPublicKey != _currentUser.PublicKeyHex)
            {
                await AddMemberAsync(chat.Id, memberPublicKey);
            }
        }

        _chatUpdates.OnNext(chat);
        return chat;
    }

    /// <summary>
    /// Internal group name prefix used to identify device-sync groups in MLS state.
    /// Checked during welcome processing to trigger auto-accept.
    /// </summary>
    internal const string DeviceSyncGroupNamePrefix = "__device_sync__";

    public async Task<Chat> GetOrCreateDeviceSyncGroupAsync()
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        // Check if we already have a device-sync group
        var syncChatId = await _storageService.GetSettingAsync("device_sync_chat_id");
        if (!string.IsNullOrEmpty(syncChatId))
        {
            var existing = await _storageService.GetChatAsync(syncChatId);
            if (existing != null)
            {
                _logger.LogInformation("GetOrCreateDeviceSyncGroup: found existing sync group {ChatId}", syncChatId);
                return existing;
            }
            // Chat was deleted but setting remains — recreate
            _logger.LogWarning("GetOrCreateDeviceSyncGroup: sync chat {ChatId} not found in storage, recreating", syncChatId);
        }

        // Also check by ChatType in case the setting was lost
        var allChats = await _storageService.GetAllChatsAsync();
        var existingSync = allChats.FirstOrDefault(c => c.Type == ChatType.DeviceSync);
        if (existingSync != null)
        {
            await _storageService.SaveSettingAsync("device_sync_chat_id", existingSync.Id);
            _logger.LogInformation("GetOrCreateDeviceSyncGroup: recovered sync group {ChatId} from chat list", existingSync.Id);
            return existingSync;
        }

        // Create a new MLS group for device sync
        // Include the sync relay (if configured) to ensure all devices can discover each other
        var relayList = _nostrService.ConnectedRelayUrls.ToList();
        var syncRelayUrl = await _storageService.GetSettingAsync("sync_relay_url");
        if (!string.IsNullOrEmpty(syncRelayUrl) &&
            !relayList.Any(r => string.Equals(r.TrimEnd('/'), syncRelayUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            relayList.Insert(0, syncRelayUrl); // prioritize sync relay
        }
        var relayUrls = relayList.ToArray();
        if (relayUrls.Length == 0)
            throw new InvalidOperationException("Cannot create device-sync group: no relays connected");

        var groupInfo = await _mlsService.CreateGroupAsync(DeviceSyncGroupNamePrefix, relayUrls);
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        _logger.LogInformation("GetOrCreateDeviceSyncGroup: MLS group created {GroupId}", groupIdHex[..Math.Min(16, groupIdHex.Length)]);

        byte[]? nostrGroupId = null;
        try { nostrGroupId = _mlsService.GetNostrGroupId(groupInfo.GroupId); }
        catch { _logger.LogWarning("GetOrCreateDeviceSyncGroup: NostrGroupId not available"); }

        var adminPubkeys = _mlsService.GetAdminPubkeys(groupInfo.GroupId);
        if (adminPubkeys.Count == 0)
            adminPubkeys = new List<string> { _currentUser.PublicKeyHex.ToLowerInvariant() };

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Private Notes",
            Type = ChatType.DeviceSync,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex },
            AdminPublicKeys = adminPubkeys,
            CreatorPublicKey = _currentUser.PublicKeyHex,
            RelayUrls = relayUrls.ToList(),
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            IsPinned = true
        };

        await _storageService.SaveChatAsync(chat);
        await _storageService.SaveSettingAsync("device_sync_chat_id", chat.Id);
        _logger.LogInformation("GetOrCreateDeviceSyncGroup: created sync chat {ChatId}", chat.Id);

        return chat;
    }

    public async Task InvitePeerToSyncGroupAsync(KeyPackage peerKeyPackage, string syncChatId)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("Not logged in");

        var chat = await _storageService.GetChatAsync(syncChatId)
            ?? throw new ArgumentException("Sync group chat not found", nameof(syncChatId));

        if (chat.MlsGroupId == null)
            throw new InvalidOperationException("Sync group has no MLS group ID");

        var groupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
        _logger.LogInformation("InvitePeerToSyncGroup: inviting peer (slot={SlotId}) to sync group {GroupId}",
            peerKeyPackage.SlotId?[..Math.Min(16, peerKeyPackage.SlotId?.Length ?? 0)] ?? "none",
            groupIdHex[..Math.Min(16, groupIdHex.Length)]);

        // Stage the add-member commit with the specific peer device KP
        var staged = await _mlsService.StageAddMemberAsync(chat.MlsGroupId, peerKeyPackage);

        // Publish commit and wait for relay confirmation
        var nostrGroupId = _mlsService.GetNostrGroupId(chat.MlsGroupId);
        var commitGroupId = nostrGroupId != null
            ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
            : groupIdHex;

        if (staged.CommitData != null && staged.CommitData.Length > 0)
        {
            var commitEventId = await _nostrService.PublishCommitAsync(
                staged.CommitData, commitGroupId, _currentUser.PrivateKeyHex);
            _logger.LogInformation("InvitePeerToSyncGroup: commit confirmed, event {EventId}", commitEventId);

            await _mlsService.MergeStagedAsync(chat.MlsGroupId);

            // Mark commit as processed
            await _storageService.SaveMessageAsync(new Message
            {
                Id = Guid.NewGuid().ToString(),
                ChatId = syncChatId,
                Content = "[peer device added to sync group]",
                SenderPublicKey = _currentUser.PublicKeyHex,
                NostrEventId = commitEventId,
                Timestamp = DateTime.UtcNow,
                Type = MessageType.System,
                Status = MessageStatus.Sent
            });

            // Send Welcome to peer device
            var welcomeEventId = await _nostrService.PublishWelcomeAsync(
                staged.WelcomeData, staged.RecipientPublicKey, _currentUser.PrivateKeyHex,
                staged.KeyPackageEventId);
            _logger.LogInformation("InvitePeerToSyncGroup: Welcome sent, event {EventId}", welcomeEventId);

            // Track invite timestamp for 3-day liveness window
            await _storageService.SaveSettingAsync(
                $"sync_invite_{peerKeyPackage.SlotId}", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        }
    }

    /// <summary>
    /// Number of dummy KeyPackage events to publish per login.
    /// Chosen to be plausible (a user might have 2-6 real devices).
    /// </summary>
    internal const int DummyKeyPackageCount = 4;

    /// <summary>
    /// Deterministically computes dummy slot IDs from the user's private key.
    /// Any device with the same private key produces the same set, so no
    /// cross-device sync is needed to know which slot IDs are dummies.
    /// Returns empty set if privateKeyHex is null/empty (external signer).
    /// </summary>
    public static HashSet<string> ComputeDummySlotIds(string? privateKeyHex, int count = DummyKeyPackageCount)
    {
        if (string.IsNullOrEmpty(privateKeyHex))
            return new HashSet<string>();

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var hmac = new HMACSHA256(Convert.FromHexString(privateKeyHex));
        for (int i = 0; i < count; i++)
        {
            var input = System.Text.Encoding.UTF8.GetBytes($"dummy_kp_{i}");
            var hash = hmac.ComputeHash(input);
            // Slot IDs are 32-byte hex strings (64 chars) — HMACSHA256 output is exactly 32 bytes
            result.Add(Convert.ToHexString(hash).ToLowerInvariant());
        }
        return result;
    }

    /// <summary>
    /// Publish dummy KeyPackage events to the private relay to mask the real device count.
    /// Each dummy uses a deterministic slot ID (derived from the private key)
    /// and random opaque content that looks like a valid KeyPackage to observers.
    /// Only publishes to the specified private relay — not broadcast to all relays.
    /// Skipped for external signer users (no private key).
    /// </summary>
    public async Task PublishDummyKeyPackagesAsync(string privateRelayUrl)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        if (string.IsNullOrEmpty(privateRelayUrl))
        {
            _logger.LogInformation("PublishDummyKeyPackages: skipping — no private relay configured");
            return;
        }

        if (string.IsNullOrEmpty(_currentUser.PrivateKeyHex))
        {
            _logger.LogInformation("PublishDummyKeyPackages: skipping — no private key (external signer)");
            return;
        }

        var dummySlotIds = ComputeDummySlotIds(_currentUser.PrivateKeyHex);

        var rng = RandomNumberGenerator.Create();
        int published = 0;

        foreach (var slotId in dummySlotIds)
        {
            try
            {
                // Generate random bytes that look like a KeyPackage (200-300 bytes)
                var fakeSize = 200 + (int)(slotId.GetHashCode() & 0x7F); // deterministic-ish size per slot
                if (fakeSize < 200) fakeSize = 200;
                if (fakeSize > 350) fakeSize = 350;
                var fakeData = new byte[fakeSize];
                rng.GetBytes(fakeData);

                // Build realistic MIP-00 tags — relay tag points only to the private relay
                var tags = new List<List<string>>
                {
                    new() { "d", slotId },
                    new() { "ciphersuite", "0x0001" },
                    new() { "mls_protocol_version", "mls10" },
                    new() { "relays", privateRelayUrl },
                };

                await _nostrService.PublishKeyPackageAsync(fakeData, _currentUser.PrivateKeyHex, tags);
                published++;
            }
            catch (Exception ex)
            {
                // Non-fatal: dummy KPs are best-effort
                _logger.LogDebug(ex, "PublishDummyKeyPackages: failed for slot {SlotId}", slotId[..Math.Min(8, slotId.Length)]);
            }
        }

        _logger.LogInformation("PublishDummyKeyPackages: published {Count}/{Total} dummy KeyPackages to private relay",
            published, dummySlotIds.Count);
    }

    public async Task<Chat> GetOrCreateDirectMessageAsync(string recipientPublicKey)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        // Check if DM chat already exists
        var chats = await _storageService.GetAllChatsAsync();
        var existingChat = chats.FirstOrDefault(c =>
            c.Type == ChatType.DirectMessage &&
            c.ParticipantPublicKeys.Contains(recipientPublicKey));

        if (existingChat != null)
            return existingChat;

        // Get recipient user info
        var recipient = await _storageService.GetUserByPublicKeyAsync(recipientPublicKey);
        var chatName = recipient?.GetDisplayNameOrNpub() ?? "Anonymous";

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = chatName,
            Type = ChatType.DirectMessage,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, recipientPublicKey },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);

        return chat;
    }

    /// <summary>
    /// Finds the best bot chat for an incoming kind 14 message, or creates one when no match exists.
    ///
    /// DVMs frequently sign their responses with a different key than the one the user registered,
    /// which would otherwise create a second orphan chat containing only the reply.  We use two
    /// ordered fallback strategies before resorting to creating a new chat:
    ///
    ///   1. Exact key match  — the normal path; existing chat already contains senderPublicKey.
    ///   2. Relay + recency  — the response arrived on a relay that is configured on an existing bot
    ///      chat, AND the last message in that chat was sent by the current user (awaiting a reply)
    ///      within the past hour.  Very accurate when relay URLs match.
    ///   3. Time-only        — no relay URL clue; the most recently messaged bot chat where the
    ///      user sent the last message, within a 30-minute window.  Covers DVMs that send from
    ///      a different relay than configured.
    ///
    /// When a fallback match is found the response key is added to that chat's participants so
    /// all subsequent messages from the same key route correctly via the exact-match path.
    /// </summary>
    private async Task<Chat> FindChatForIncomingBotMessageAsync(
        string senderPublicKey, string responseRelayUrl, DateTime messageTimestamp)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var allChats = (await _storageService.GetAllChatsAsync()).ToList();
        var botChats = allChats.Where(c => c.Type == ChatType.Bot).ToList();

        // 1. Exact key match — fastest and most reliable path.
        var exactMatch = botChats.FirstOrDefault(c =>
            c.ParticipantPublicKeys.Contains(senderPublicKey));
        if (exactMatch != null)
            return exactMatch;

        // GetAllChatsAsync does not populate Chat.LastMessage; the relay-match and time-fallback
        // predicates below depend on it, so hydrate from the last-message snapshot before evaluating.
        if (botChats.Count > 0)
        {
            var lastMessages = await _storageService.GetLastMessagePerChatAsync();
            foreach (var c in botChats)
                c.LastMessage = lastMessages.GetValueOrDefault(c.Id);
        }

        // 2. Relay + recency match: response came from a relay we recognise for a specific bot chat
        //    that is currently waiting for a reply (last message was from the current user).
        if (!string.IsNullOrEmpty(responseRelayUrl))
        {
            var relayNorm = responseRelayUrl.TrimEnd('/');
            var relayMatch = botChats
                .Where(c =>
                    c.LastMessage?.IsFromCurrentUser == true &&
                    (messageTimestamp - c.LastActivityAt).TotalHours <= 1 &&
                    c.RelayUrls.Any(r =>
                        string.Equals(r.TrimEnd('/'), relayNorm, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(c => c.LastActivityAt)
                .FirstOrDefault();

            if (relayMatch != null)
            {
                _logger.LogInformation(
                    "FindChatForIncomingBot: relay match — routing response from {Sender} to existing chat {Chat}",
                    senderPublicKey[..Math.Min(16, senderPublicKey.Length)],
                    relayMatch.Id[..Math.Min(8, relayMatch.Id.Length)]);

                if (!relayMatch.ParticipantPublicKeys.Contains(senderPublicKey))
                {
                    relayMatch.ParticipantPublicKeys.Add(senderPublicKey);
                    await _storageService.SaveChatAsync(relayMatch);
                }
                return relayMatch;
            }
        }

        // 3. Time-only fallback: pick the most recently messaged bot chat that is awaiting a reply,
        //    within a 30-minute window.  Less precise than the relay match, but handles DVMs that
        //    respond from a relay the user did not explicitly configure.
        var timeMatch = botChats
            .Where(c =>
                c.LastMessage?.IsFromCurrentUser == true &&
                (messageTimestamp - c.LastActivityAt).TotalMinutes <= 30)
            .OrderByDescending(c => c.LastActivityAt)
            .FirstOrDefault();

        if (timeMatch != null)
        {
            _logger.LogInformation(
                "FindChatForIncomingBot: time fallback — routing response from {Sender} to existing chat {Chat}",
                senderPublicKey[..Math.Min(16, senderPublicKey.Length)],
                timeMatch.Id[..Math.Min(8, timeMatch.Id.Length)]);

            if (!timeMatch.ParticipantPublicKeys.Contains(senderPublicKey))
            {
                timeMatch.ParticipantPublicKeys.Add(senderPublicKey);
                await _storageService.SaveChatAsync(timeMatch);
            }
            return timeMatch;
        }

        // 4. No suitable existing chat found — create a new one (original behaviour).
        return await GetOrCreateBotChatAsync(senderPublicKey);
    }

    public async Task<Chat> GetOrCreateBotChatAsync(string botPublicKey, List<string>? relayUrls = null)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        // Check if bot chat already exists
        var chats = await _storageService.GetAllChatsAsync();
        var existingChat = chats.FirstOrDefault(c =>
            c.Type == ChatType.Bot &&
            c.ParticipantPublicKeys.Contains(botPublicKey));

        if (existingChat != null)
        {
            // Update relay URLs if provided and chat had none
            if (relayUrls is { Count: > 0 } && existingChat.RelayUrls.Count == 0)
            {
                existingChat.RelayUrls = relayUrls;
                await _storageService.SaveChatAsync(existingChat);
                _logger.LogInformation("Updated relay URLs for existing bot chat {ChatId}: {Relays}",
                    existingChat.Id[..Math.Min(8, existingChat.Id.Length)], string.Join(", ", relayUrls));
            }
            return existingChat;
        }

        // Get bot profile for display name
        var botUser = await _storageService.GetUserByPublicKeyAsync(botPublicKey);
        var chatName = botUser?.GetDisplayNameOrNpub() ?? "Anonymous";

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = chatName,
            Type = ChatType.Bot,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, botPublicKey },
            RelayUrls = relayUrls ?? new List<string>(),
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);

        _logger.LogInformation("Created bot chat {ChatId} with {RelayCount} relay(s)",
            chat.Id[..Math.Min(8, chat.Id.Length)], chat.RelayUrls.Count);

        return chat;
    }

    public async Task AddMemberAsync(string chatId, string memberPublicKey)
    {
        _logger.LogInformation("AddMember: adding {Member} to chat {ChatId}",
            memberPublicKey[..Math.Min(16, memberPublicKey.Length)], chatId);

        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        if (chat.MlsGroupId == null)
            throw new InvalidOperationException("Cannot add member to non-MLS chat");

        var groupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();

        // Fetch ALL of the member's KeyPackages — each represents a separate device.
        // In MLS, each device must be added individually with its own Commit + Welcome
        // so that every device can join the group (multi-device support).
        _logger.LogDebug("AddMember: fetching KeyPackages for {Member}", memberPublicKey[..Math.Min(16, memberPublicKey.Length)]);
        var keyPackages = await _nostrService.FetchKeyPackagesAsync(memberPublicKey);
        var supportedKps = keyPackages.Where(kp => kp.IsCipherSuiteSupported).ToList();

        if (supportedKps.Count == 0)
            throw new InvalidOperationException(
                keyPackages.Any()
                    ? $"No KeyPackage with a supported cipher suite found for member (found {keyPackages.Count()} with unsupported suites)"
                    : "No KeyPackage found for member");

        // Deduplicate: keep only the latest KeyPackage per device (SlotId / d-tag).
        // Kind 30443 is an addressable event — each device publishes under a unique,
        // stable d-tag. Relays should return only the latest per d-tag, but we
        // guard against duplicates here (multiple relays, timing, etc.).
        // KPs without a SlotId are treated as unique (legacy single-device publishes).
        var latestPerDevice = supportedKps
            .GroupBy(kp => kp.SlotId ?? kp.NostrEventId ?? Guid.NewGuid().ToString())
            .Select(g => g.OrderByDescending(kp => kp.CreatedAt).First())
            .ToList();

        _logger.LogInformation("AddMember: found {Total} supported KP(s), {Devices} unique device(s) for {Member}",
            supportedKps.Count, latestPerDevice.Count, memberPublicKey[..Math.Min(16, memberPublicKey.Length)]);

        var addedCount = 0;
        foreach (var keyPackage in latestPerDevice)
        {
            _logger.LogInformation("AddMember: adding KP {KpId} (slot={SlotId}), {Len} bytes",
                keyPackage.NostrEventId?[..Math.Min(16, keyPackage.NostrEventId?.Length ?? 0)] ?? "none",
                keyPackage.SlotId?[..Math.Min(16, keyPackage.SlotId?.Length ?? 0)] ?? "none",
                keyPackage.Data.Length);

            // MIP-03 §"Commit Message Race Conditions" — sending steps 1-4:
            // 1. Stage the commit (do NOT advance local MLS state yet)
            var staged = await _mlsService.StageAddMemberAsync(chat.MlsGroupId, keyPackage);
            _logger.LogInformation("AddMember: staged commit, welcome={WelcomeLen} bytes, commit={CommitLen} bytes",
                staged.WelcomeData.Length, staged.CommitData?.Length ?? 0);

            try
            {
                // 2. Publish commit and wait for relay confirmation
                if (_currentUser != null && staged.CommitData != null && staged.CommitData.Length > 0)
                {
                    var nostrGroupId = _mlsService.GetNostrGroupId(chat.MlsGroupId);
                    var commitGroupId = nostrGroupId != null
                        ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
                        : groupIdHex;
                    var commitEventId = await _nostrService.PublishCommitAsync(
                        staged.CommitData, commitGroupId, _currentUser.PrivateKeyHex);
                    // ↑ throws PublishUnconfirmedException on no-OK (Phase 3)
                    _logger.LogInformation("AddMember: commit confirmed by relay, event {EventId}", commitEventId);

                    // 3. Only NOW advance local MLS state (MIP-03 step 3)
                    await _mlsService.MergeStagedAsync(chat.MlsGroupId);
                    _logger.LogInformation("AddMember: local MLS state merged to new epoch");

                    // Mark commit as processed to prevent re-processing our own relay echo
                    await _storageService.SaveMessageAsync(new Message
                    {
                        Id = Guid.NewGuid().ToString(),
                        ChatId = chatId,
                        Content = "[commit]",
                        SenderPublicKey = _currentUser.PublicKeyHex,
                        NostrEventId = commitEventId,
                        Timestamp = DateTime.UtcNow,
                        Type = MessageType.System,
                        Status = MessageStatus.Sent
                    });

                    // 4. Only THEN send the Welcome (MIP-02 §"Timing Requirements")
                    var welcomeEventId = await _nostrService.PublishWelcomeAsync(
                        staged.WelcomeData, staged.RecipientPublicKey, _currentUser.PrivateKeyHex,
                        staged.KeyPackageEventId);
                    _logger.LogInformation("AddMember: published Welcome event {EventId} for {Recipient} (slot={SlotId})",
                        welcomeEventId, staged.RecipientPublicKey[..Math.Min(16, staged.RecipientPublicKey.Length)],
                        keyPackage.SlotId?[..Math.Min(16, keyPackage.SlotId?.Length ?? 0)] ?? "none");

                    addedCount++;
                }
            }
            catch (PublishUnconfirmedException ex) when (!_mlsService.HasPendingCommit(chat.MlsGroupId))
            {
                // Commit was confirmed but Welcome failed — recoverable state.
                // Local MLS state already advanced; this device can be re-invited.
                // Continue adding remaining devices instead of aborting all.
                _logger.LogWarning(ex, "AddMember: Welcome publish failed after commit was confirmed for KP {KpId} — continuing",
                    keyPackage.NostrEventId?[..Math.Min(16, keyPackage.NostrEventId?.Length ?? 0)] ?? "none");
            }
            catch (PublishUnconfirmedException ex) when (_mlsService.HasPendingCommit(chat.MlsGroupId))
            {
                // Commit was NOT confirmed — rollback local MLS state
                _logger.LogWarning(ex, "AddMember: commit publish failed for KP {KpId}, rolling back staged commit — continuing",
                    keyPackage.NostrEventId?[..Math.Min(16, keyPackage.NostrEventId?.Length ?? 0)] ?? "none");
                await _mlsService.ClearStagedAsync(chat.MlsGroupId);
            }
            catch (Exception ex)
            {
                // Unexpected error — clear staged state and continue with remaining devices
                _logger.LogWarning(ex, "AddMember: failed to add KP {KpId} — clearing staged state and continuing",
                    keyPackage.NostrEventId?[..Math.Min(16, keyPackage.NostrEventId?.Length ?? 0)] ?? "none");
                try { await _mlsService.ClearStagedAsync(chat.MlsGroupId); } catch { }
            }
        }

        if (addedCount == 0)
            throw new InvalidOperationException($"Failed to add any device for member {memberPublicKey[..Math.Min(16, memberPublicKey.Length)]}");

        // Update chat participants (once for the member, regardless of how many devices were added)
        if (!chat.ParticipantPublicKeys.Any(p => string.Equals(p, memberPublicKey, StringComparison.OrdinalIgnoreCase)))
        {
            chat.ParticipantPublicKeys.Add(memberPublicKey);
            await _storageService.SaveChatAsync(chat);
            _chatUpdates.OnNext(chat);
            _logger.LogInformation("AddMember: updated participants for group {GroupId}, now {Count} members ({DeviceCount} device(s) added)",
                groupIdHex[..Math.Min(16, groupIdHex.Length)], chat.ParticipantPublicKeys.Count, addedCount);
        }
    }

    public async Task<PeerDeviceAddResult> AddPeerDeviceToGroupsAsync(KeyPackage peerKeyPackage)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("Not logged in");

        var result = new PeerDeviceAddResult();

        _logger.LogInformation("AddPeerDevice: adding peer device (slot={SlotId}) to admin groups",
            peerKeyPackage.SlotId?[..Math.Min(16, peerKeyPackage.SlotId?.Length ?? 0)] ?? "none");

        var chats = await _storageService.GetAllChatsAsync();
        var groupChats = chats
            .Where(c => c.Type == ChatType.Group && c.MlsGroupId != null && c.MlsGroupId.Length > 0)
            .ToList();

        if (groupChats.Count == 0)
        {
            _logger.LogInformation("AddPeerDevice: no group chats to add peer device to");
            return result;
        }

        foreach (var chat in groupChats)
        {
            try
            {
                var groupIdHex = Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();

                // Only auto-add to groups where this device is admin.
                // For non-admin groups, the group admin should add the new device.
                var adminPubkeys = _mlsService.GetAdminPubkeys(chat.MlsGroupId!);
                var isAdmin = adminPubkeys.Any(pk =>
                    string.Equals(pk, _currentUser.PublicKeyHex, StringComparison.OrdinalIgnoreCase));

                if (!isAdmin)
                {
                    _logger.LogInformation("AddPeerDevice: skipping non-admin group {GroupId} ({Name})",
                        groupIdHex[..Math.Min(16, groupIdHex.Length)], chat.Name);
                    result.SkippedNonAdminGroups.Add(chat.Name ?? groupIdHex[..Math.Min(16, groupIdHex.Length)]);
                    result.SkippedGroupDetails.Add(new SkippedGroupInfo
                    {
                        GroupName = chat.Name ?? groupIdHex[..Math.Min(16, groupIdHex.Length)],
                        ChatId = chat.Id,
                        AdminPubkeys = adminPubkeys.ToList(),
                    });
                    continue;
                }

                _logger.LogInformation("AddPeerDevice: adding to admin group {GroupId} ({Name})",
                    groupIdHex[..Math.Min(16, groupIdHex.Length)], chat.Name);

                // Stage the add-member commit with the specific peer device KP
                var staged = await _mlsService.StageAddMemberAsync(chat.MlsGroupId!, peerKeyPackage);

                // Publish commit and wait for relay confirmation
                var nostrGroupId = _mlsService.GetNostrGroupId(chat.MlsGroupId!);
                var commitGroupId = nostrGroupId != null
                    ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
                    : groupIdHex;

                if (staged.CommitData != null && staged.CommitData.Length > 0)
                {
                    var commitEventId = await _nostrService.PublishCommitAsync(
                        staged.CommitData, commitGroupId, _currentUser.PrivateKeyHex);
                    _logger.LogInformation("AddPeerDevice: commit confirmed for group {GroupId}, event {EventId}",
                        groupIdHex[..Math.Min(16, groupIdHex.Length)], commitEventId);

                    // Advance local MLS state
                    await _mlsService.MergeStagedAsync(chat.MlsGroupId!);

                    // Mark commit as processed
                    await _storageService.SaveMessageAsync(new Message
                    {
                        Id = Guid.NewGuid().ToString(),
                        ChatId = chat.Id,
                        Content = "[peer device added]",
                        SenderPublicKey = _currentUser.PublicKeyHex,
                        NostrEventId = commitEventId,
                        Timestamp = DateTime.UtcNow,
                        Type = MessageType.System,
                        Status = MessageStatus.Sent
                    });

                    // Send Welcome to peer device
                    var welcomeEventId = await _nostrService.PublishWelcomeAsync(
                        staged.WelcomeData, staged.RecipientPublicKey, _currentUser.PrivateKeyHex,
                        staged.KeyPackageEventId);
                    _logger.LogInformation("AddPeerDevice: Welcome sent for group {GroupId}, event {EventId}",
                        groupIdHex[..Math.Min(16, groupIdHex.Length)], welcomeEventId);

                    result.AddedCount++;
                }
            }
            catch (Exception ex)
            {
                var groupIdHex = Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();
                _logger.LogWarning(ex, "AddPeerDevice: failed to add peer device to group {GroupId} — skipping",
                    groupIdHex[..Math.Min(16, groupIdHex.Length)]);
                result.FailedCount++;

                // Attempt to clear staged commit if it exists
                try { await _mlsService.ClearStagedAsync(chat.MlsGroupId!); }
                catch { /* best-effort cleanup */ }
            }
        }

        _logger.LogInformation("AddPeerDevice: added={Added}, skipped={Skipped} non-admin, failed={Failed}",
            result.AddedCount, result.SkippedNonAdminGroups.Count, result.FailedCount);
        return result;
    }

    public async Task RemoveMemberAsync(string chatId, string memberPublicKey)
    {
        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        if (chat.MlsGroupId == null)
            throw new InvalidOperationException("Cannot remove member from non-MLS chat");

        // MIP-03 §"Commit Message Race Conditions" — stage, publish, merge
        var commitData = await _mlsService.StageRemoveMemberAsync(chat.MlsGroupId, memberPublicKey);

        try
        {
            if (_currentUser != null)
            {
                var groupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
                await _nostrService.PublishGroupMessageAsync(commitData, groupIdHex, _currentUser.PrivateKeyHex);
                // ↑ throws PublishUnconfirmedException on no-OK

                await _mlsService.MergeStagedAsync(chat.MlsGroupId);
            }

            chat.ParticipantPublicKeys.Remove(memberPublicKey);
            await _storageService.SaveChatAsync(chat);
            _chatUpdates.OnNext(chat);
        }
        catch (PublishUnconfirmedException ex)
        {
            _logger.LogWarning(ex, "RemoveMember: commit publish failed, rolling back");
            await _mlsService.ClearStagedAsync(chat.MlsGroupId);
            throw;
        }
    }

    public async Task LeaveGroupAsync(string chatId)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        if (chat.MlsGroupId == null)
            throw new InvalidOperationException("Cannot leave a non-MLS chat");

        var groupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
        _logger.LogInformation("LeaveGroup: leaving chat {ChatId}, group {GroupId}",
            chatId, groupIdHex[..Math.Min(16, groupIdHex.Length)]);

        // MLS does not allow self-removal via Commit (RFC 9420).
        // Clean up locally: delete MLS state, chat, and messages.
        // Other members will keep encrypting for us until they remove us.
        await _storageService.DeleteMlsStateAsync(groupIdHex);
        await _storageService.DeleteChatAsync(chatId);
        _chatUpdates.OnNext(chat);

        _logger.LogInformation("LeaveGroup: local state for chat {ChatId} cleaned up", chatId);
    }

    public async Task MarkAsReadAsync(string chatId)
    {
        var chat = await _storageService.GetChatAsync(chatId);
        if (chat != null)
        {
            chat.UnreadCount = 0;
            await _storageService.SaveChatAsync(chat);
            _chatUpdates.OnNext(chat);
        }
    }

    public async Task AddReactionAsync(string messageId, string emoji)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var message = await _storageService.GetMessageAsync(messageId);
        if (message != null)
        {
            if (!message.Reactions.ContainsKey(emoji))
            {
                message.Reactions[emoji] = new List<string>();
            }

            if (!message.Reactions[emoji].Contains(_currentUser.PublicKeyHex))
            {
                message.Reactions[emoji].Add(_currentUser.PublicKeyHex);
                await _storageService.SaveMessageAsync(message);
            }
        }
    }

    public async Task SendReactionAsync(string messageId, string emoji)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var message = await _storageService.GetMessageAsync(messageId);
        if (message == null)
        {
            _logger.LogWarning("SendReaction: message {MessageId} not found", messageId);
            return;
        }

        var targetRumorId = message.RumorEventId ?? message.NostrEventId;
        if (string.IsNullOrEmpty(targetRumorId))
        {
            _logger.LogWarning("SendReaction: message {MessageId} has no event ID to target", messageId);
            return;
        }

        var chat = await _storageService.GetChatAsync(message.ChatId);
        if (chat?.MlsGroupId == null)
        {
            _logger.LogWarning("SendReaction: chat {ChatId} has no MLS group", message.ChatId);
            return;
        }

        try
        {
            var eventJsonBytes = await _mlsService.EncryptReactionAsync(chat.MlsGroupId, emoji, targetRumorId);
            await _nostrService.PublishRawEventJsonAsync(eventJsonBytes);
            _logger.LogInformation("SendReaction: published {Emoji} reaction on message {MessageId}", emoji, messageId);

            // Update local storage
            await AddReactionAsync(messageId, emoji);
            _reactionUpdates.OnNext((messageId, emoji, _currentUser.PublicKeyHex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendReaction: failed to send {Emoji} reaction on message {MessageId}", emoji, messageId);
        }
    }

    public async Task RemoveReactionAsync(string messageId, string emoji)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var message = await _storageService.GetMessageAsync(messageId);
        if (message != null && message.Reactions.ContainsKey(emoji))
        {
            message.Reactions[emoji].Remove(_currentUser.PublicKeyHex);
            if (message.Reactions[emoji].Count == 0)
            {
                message.Reactions.Remove(emoji);
            }
            await _storageService.SaveMessageAsync(message);
        }
    }

    public async Task DeleteMessageAsync(string messageId)
    {
        await _storageService.DeleteMessageAsync(messageId);
    }

    public async Task ArchiveChatAsync(string chatId)
    {
        var chat = await _storageService.GetChatAsync(chatId);
        if (chat != null)
        {
            chat.IsArchived = true;
            await _storageService.SaveChatAsync(chat);
            _chatUpdates.OnNext(chat);
        }
    }

    public async Task UnarchiveChatAsync(string chatId)
    {
        var chat = await _storageService.GetChatAsync(chatId);
        if (chat != null)
        {
            chat.IsArchived = false;
            await _storageService.SaveChatAsync(chat);
            _chatUpdates.OnNext(chat);
        }
    }

    public Task MuteChatAsync(string chatId) => SetMutedAsync(chatId, true);

    public Task UnmuteChatAsync(string chatId) => SetMutedAsync(chatId, false);

    public async Task SetMutedAsync(string chatId, bool muted)
    {
        var chat = await _storageService.GetChatAsync(chatId);
        if (chat != null)
        {
            chat.IsMuted = muted;
            await _storageService.SaveChatAsync(chat);
            _chatUpdates.OnNext(chat);
        }
    }

    private async void OnNostrEventReceived(NostrEventReceived nostrEvent)
    {
        try
        {
            _logger.LogInformation("OnNostrEventReceived: kind {Kind} event {EventId} from {Relay}",
                nostrEvent.Kind, nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)], nostrEvent.RelayUrl);

            switch (nostrEvent.Kind)
            {
                case 14: // NIP-17 DM (bot chat)
                    await HandleBotMessageEventAsync(nostrEvent);
                    break;
                case 443: // KeyPackage
                    await HandleKeyPackageEventAsync(nostrEvent);
                    break;
                case 444: // Welcome
                    await HandleWelcomeEventAsync(nostrEvent);
                    break;
                case 445: // Group message
                    await HandleGroupMessageEventAsync(nostrEvent);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Nostr event kind {Kind}", nostrEvent.Kind);
        }
    }

    private async Task HandleBotMessageEventAsync(NostrEventReceived nostrEvent)
    {
        _logger.LogInformation("HandleBotMessage: kind 14 DM from {Sender}",
            nostrEvent.PublicKey[..Math.Min(16, nostrEvent.PublicKey.Length)]);

        if (_currentUser == null) return;

        var senderPubKey = nostrEvent.PublicKey;
        var isSelfSent = string.Equals(senderPubKey, _currentUser.PublicKeyHex, StringComparison.OrdinalIgnoreCase);

        // For self-sent messages (NIP-59 wraps a copy of every outgoing DM to the
        // sender too, so it can be picked up on other devices), the bot chat is
        // with the RECIPIENT, not with us. Pull the recipient from the first p tag.
        string otherPartyPubKey;
        if (isSelfSent)
        {
            var pTag = nostrEvent.Tags.FirstOrDefault(t =>
                t.Count >= 2 && string.Equals(t[0], "p", StringComparison.Ordinal));
            if (pTag == null)
            {
                _logger.LogDebug("HandleBotMessage: self-sent kind 14 has no p tag — skipping");
                return;
            }
            otherPartyPubKey = pTag[1];
            if (string.IsNullOrEmpty(otherPartyPubKey) || otherPartyPubKey.Length != 64)
            {
                _logger.LogDebug("HandleBotMessage: self-sent kind 14 p tag malformed — skipping");
                return;
            }
        }
        else
        {
            otherPartyPubKey = senderPubKey;
        }

        // Deduplicate by event ID
        if (!string.IsNullOrEmpty(nostrEvent.EventId) &&
            await _storageService.MessageExistsByNostrEventIdAsync(nostrEvent.EventId))
        {
            _logger.LogDebug("HandleBotMessage: skipping already-processed event {EventId}",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        // Find or create the bot chat.
        //  - Self-sent messages (NIP-59 self-wrap): the chat is keyed on the recipient
        //    extracted from the p tag above.
        //  - Incoming messages: DVMs sometimes respond with a different key than the
        //    one the user registered, so we fall back to relay-URL and recency
        //    heuristics before creating a new chat.
        Chat chat;
        if (isSelfSent)
        {
            chat = await GetOrCreateBotChatAsync(otherPartyPubKey);

            // For self-sent rescued messages: SendMessageAsync stored the local copy
            // with NostrEventId = the kind-1059 wrap id, but the rescan unwraps and
            // gives us the kind-14 rumor id — so the event-id dedup above misses the
            // collision. Fall back to (sender, timestamp, content) within a short
            // window to avoid duplicating every outgoing message on every rescan.
            var existing = await _storageService.GetMessagesForChatAsync(chat.Id, limit: 200, offset: 0);
            var dup = existing.FirstOrDefault(m =>
                string.Equals(m.SenderPublicKey, senderPubKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.Content, nostrEvent.Content, StringComparison.Ordinal) &&
                Math.Abs((m.Timestamp - nostrEvent.CreatedAt).TotalSeconds) < 60);
            if (dup != null)
            {
                _logger.LogDebug("HandleBotMessage: skipping self-echo — local copy already exists ({MsgId})",
                    dup.Id[..Math.Min(8, dup.Id.Length)]);
                return;
            }
        }
        else
        {
            chat = await FindChatForIncomingBotMessageAsync(senderPubKey, nostrEvent.RelayUrl, nostrEvent.CreatedAt);
        }

        // Content is already decrypted during gift-wrap unwrapping
        var content = nostrEvent.Content;

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ChatId = chat.Id,
            SenderPublicKey = senderPubKey,
            Type = MessageType.Text,
            Content = content,
            NostrEventId = nostrEvent.EventId,
            Timestamp = nostrEvent.CreatedAt,
            ReceivedAt = DateTime.UtcNow,
            Status = MessageStatus.Delivered,
            IsFromCurrentUser = isSelfSent
        };

        // Resolve the SENDER for display. For self-sent messages this is the
        // current user, which we already have.
        message.Sender = isSelfSent
            ? _currentUser
            : await _storageService.GetUserByPublicKeyAsync(senderPubKey);
        if (message.Sender == null && !isSelfSent)
        {
            _ = FetchUnknownSenderAsync(message);
        }

        await _storageService.SaveMessageAsync(message);
        _newMessages.OnNext(message);

        chat.LastActivityAt = DateTime.UtcNow;
        chat.LastMessage = message;
        // Only bump unread for incoming messages — our own outgoing/echoed messages
        // shouldn't show as unread on the device that pulled them from history.
        if (!isSelfSent)
            chat.UnreadCount++;
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);

        _logger.LogInformation("HandleBotMessage: saved {Direction} message {MsgId} in bot chat {ChatId}",
            isSelfSent ? "self-sent" : "incoming",
            message.Id[..Math.Min(8, message.Id.Length)], chat.Id[..Math.Min(8, chat.Id.Length)]);
    }

    private async Task HandleKeyPackageEventAsync(NostrEventReceived nostrEvent)
    {
        var keyPackageData = Convert.FromBase64String(nostrEvent.Content);
        var keyPackage = KeyPackage.Create(nostrEvent.PublicKey, keyPackageData);
        keyPackage.NostrEventId = nostrEvent.EventId;
        await _storageService.SaveKeyPackageAsync(keyPackage);
    }

    private async Task HandleWelcomeEventAsync(NostrEventReceived nostrEvent)
    {
        _logger.LogInformation("HandleWelcomeEventAsync: processing kind 444 event {EventId} from {Sender}",
            nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)],
            nostrEvent.PublicKey[..Math.Min(16, nostrEvent.PublicKey.Length)]);

        // Check if this welcome was previously accepted or declined
        if (await _storageService.IsWelcomeEventDismissedAsync(nostrEvent.EventId))
        {
            _logger.LogDebug("Skipping dismissed Welcome event {EventId}", nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        // Check for duplicate by NostrEventId
        var existingInvites = await _storageService.GetPendingInvitesAsync();
        if (existingInvites.Any(i => i.NostrEventId == nostrEvent.EventId))
        {
            _logger.LogDebug("Skipping duplicate Welcome event {EventId}", nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        var welcomeData = Convert.FromBase64String(nostrEvent.Content);

        // Check if we have key material to process this Welcome — skip silently if not
        try
        {
            var canProcess = await _mlsService.CanProcessWelcomeAsync(welcomeData);
            if (!canProcess)
            {
                _logger.LogWarning("HandleWelcome: no key material for welcome {EventId} — dismissing and incrementing skipped count",
                    nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
                await _storageService.DismissWelcomeEventAsync(nostrEvent.EventId);
                await _storageService.IncrementSkippedInviteCountAsync();
                _skippedInvites.OnNext(Unit.Default);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HandleWelcome: CanProcessWelcomeAsync threw for {EventId} — treating as processable",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
        }

        // Parse Welcome event tags via MIP-02 protocol library
        string? keyPackageEventId;
        List<string> relayUrls;
        try
        {
            var tagsArray = nostrEvent.Tags.Select(t => t.ToArray()).ToArray();
            var (_, parsedKpId, parsedRelays) = WelcomeEventParser.ParseWelcomeEvent(
                nostrEvent.Content, tagsArray);
            keyPackageEventId = parsedKpId;
            relayUrls = parsedRelays
                .Where(u => u.StartsWith("wss://") || u.StartsWith("ws://"))
                .ToList();
        }
        catch (FormatException ex)
        {
            _logger.LogWarning("HandleWelcome: rejecting malformed Welcome {EventId}: {Reason}",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)], ex.Message);
            return;
        }

        // Extract group ID from tags (h or g tag — not part of MIP-02 parser, added by some clients)
        var groupTag = nostrEvent.Tags.FirstOrDefault(t => t.Count > 1 && (t[0] == "h" || t[0] == "g"));
        var groupId = groupTag?[1];

        // ── Device-sync auto-accept ──
        // If this welcome is for a device-sync group (group name starts with "__device_sync__"),
        // auto-accept it immediately — no pending invite, no user interaction.
        // The sender is our own pubkey (another device), so the welcome proves device liveness.
        if (string.Equals(nostrEvent.PublicKey, _currentUser?.PublicKeyHex, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var groupInfo = await _mlsService.ProcessWelcomeAsync(welcomeData, nostrEvent.EventId);
                if (groupInfo.GroupName?.StartsWith(DeviceSyncGroupNamePrefix, StringComparison.Ordinal) == true)
                {
                    _logger.LogInformation("HandleWelcome: auto-accepting device-sync welcome {EventId}",
                        nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);

                    // Check for existing chat
                    var groupIdHex2 = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
                    var existingChats2 = await _storageService.GetAllChatsAsync();
                    var existingSync = existingChats2.FirstOrDefault(c =>
                        c.MlsGroupId != null &&
                        Convert.ToHexString(c.MlsGroupId).Equals(groupIdHex2, StringComparison.OrdinalIgnoreCase));

                    if (existingSync != null)
                    {
                        _logger.LogInformation("HandleWelcome: device-sync chat already exists {ChatId}", existingSync.Id);
                        await _storageService.DismissWelcomeEventAsync(nostrEvent.EventId);
                        return;
                    }

                    var nostrGroupId2 = _mlsService.GetNostrGroupId(groupInfo.GroupId);
                    var adminPubkeys2 = _mlsService.GetAdminPubkeys(groupInfo.GroupId);
                    var creatorPubkey2 = adminPubkeys2.Count > 0 ? adminPubkeys2[0] : nostrEvent.PublicKey;
                    if (adminPubkeys2.Count == 0 && !string.IsNullOrEmpty(creatorPubkey2))
                        adminPubkeys2 = new List<string> { creatorPubkey2.ToLowerInvariant() };

                    var syncChat = new Chat
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = "Private Notes",
                        Type = ChatType.DeviceSync,
                        MlsGroupId = groupInfo.GroupId,
                        NostrGroupId = nostrGroupId2,
                        MlsEpoch = groupInfo.Epoch,
                        ParticipantPublicKeys = groupInfo.MemberPublicKeys,
                        AdminPublicKeys = adminPubkeys2,
                        CreatorPublicKey = creatorPubkey2,
                        RelayUrls = relayUrls,
                        WelcomeNostrEventId = nostrEvent.EventId,
                        CreatedAt = DateTime.UtcNow,
                        LastActivityAt = DateTime.UtcNow,
                        IsPinned = true
                    };

                    // Connect to relay if needed
                    if (relayUrls.Count > 0)
                    {
                        var connectedUrls = _nostrService.ConnectedRelayUrls;
                        var hasOverlap = relayUrls.Any(r =>
                            connectedUrls.Any(c => string.Equals(c.TrimEnd('/'), r.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));
                        if (!hasOverlap)
                        {
                            try { await _nostrService.ConnectAsync(relayUrls[0]); }
                            catch (Exception ex2) { _logger.LogWarning(ex2, "DeviceSync: failed to connect to relay"); }
                        }
                    }

                    await _storageService.SaveChatAsync(syncChat);
                    await _storageService.SaveSettingAsync("device_sync_chat_id", syncChat.Id);
                    await _storageService.DismissWelcomeEventAsync(nostrEvent.EventId);
                    _chatUpdates.OnNext(syncChat);

                    // Mark consumed KeyPackage as used
                    if (!string.IsNullOrEmpty(keyPackageEventId))
                    {
                        var consumedKp = await _storageService.GetKeyPackageByNostrEventIdAsync(keyPackageEventId);
                        if (consumedKp != null)
                        {
                            await _storageService.MarkKeyPackageUsedAsync(consumedKp.Id);
                            _ = AutoPublishKeyPackageIfNeededAsync();
                        }
                    }

                    _pendingSelfUpdates[groupIdHex2] = DateTime.UtcNow;
                    _logger.LogInformation("HandleWelcome: device-sync group auto-accepted, chat {ChatId}", syncChat.Id);
                    return;
                }
                else
                {
                    // Not a device-sync group — we already processed the welcome (joined the group).
                    // Continue to create a regular pending invite so the user can see it.
                    // BUT: we already called ProcessWelcomeAsync which joined the MLS group,
                    // so we need to create the chat directly instead of a pending invite.
                    _logger.LogInformation("HandleWelcome: self-sent welcome for regular group '{Name}', creating chat directly",
                        groupInfo.GroupName);

                    var groupIdHex3 = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
                    var existingChats3 = await _storageService.GetAllChatsAsync();
                    var existingChat3 = existingChats3.FirstOrDefault(c =>
                        c.MlsGroupId != null &&
                        Convert.ToHexString(c.MlsGroupId).Equals(groupIdHex3, StringComparison.OrdinalIgnoreCase));

                    if (existingChat3 != null)
                    {
                        await _storageService.DismissWelcomeEventAsync(nostrEvent.EventId);
                        return;
                    }

                    var nostrGroupId3 = _mlsService.GetNostrGroupId(groupInfo.GroupId);
                    var adminPubkeys3 = _mlsService.GetAdminPubkeys(groupInfo.GroupId);
                    var creatorPubkey3 = adminPubkeys3.Count > 0 ? adminPubkeys3[0] : nostrEvent.PublicKey;
                    if (adminPubkeys3.Count == 0)
                        adminPubkeys3 = new List<string> { creatorPubkey3.ToLowerInvariant() };

                    var autoChat = new Chat
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = !string.IsNullOrWhiteSpace(groupInfo.GroupName)
                            ? groupInfo.GroupName : "Group Chat",
                        Type = ChatType.Group,
                        MlsGroupId = groupInfo.GroupId,
                        NostrGroupId = nostrGroupId3,
                        MlsEpoch = groupInfo.Epoch,
                        ParticipantPublicKeys = groupInfo.MemberPublicKeys,
                        AdminPublicKeys = adminPubkeys3,
                        CreatorPublicKey = creatorPubkey3,
                        RelayUrls = relayUrls,
                        WelcomeNostrEventId = nostrEvent.EventId,
                        CreatedAt = DateTime.UtcNow,
                        LastActivityAt = DateTime.UtcNow
                    };

                    if (relayUrls.Count > 0)
                    {
                        var connectedUrls = _nostrService.ConnectedRelayUrls;
                        var hasOverlap = relayUrls.Any(r =>
                            connectedUrls.Any(c => string.Equals(c.TrimEnd('/'), r.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));
                        if (!hasOverlap)
                        {
                            try { await _nostrService.ConnectAsync(relayUrls[0]); }
                            catch (Exception ex2) { _logger.LogWarning(ex2, "Auto-accept: failed to connect to relay"); }
                        }
                    }

                    await _storageService.SaveChatAsync(autoChat);
                    await _storageService.DismissWelcomeEventAsync(nostrEvent.EventId);
                    _chatUpdates.OnNext(autoChat);

                    if (!string.IsNullOrEmpty(keyPackageEventId))
                    {
                        var consumedKp = await _storageService.GetKeyPackageByNostrEventIdAsync(keyPackageEventId);
                        if (consumedKp != null)
                        {
                            await _storageService.MarkKeyPackageUsedAsync(consumedKp.Id);
                            _ = AutoPublishKeyPackageIfNeededAsync();
                        }
                    }

                    _pendingSelfUpdates[groupIdHex3] = DateTime.UtcNow;
                    _logger.LogInformation("HandleWelcome: self-sent regular group auto-accepted, chat {ChatId}", autoChat.Id);
                    return;
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("KeyPackage"))
            {
                _logger.LogWarning(ex, "HandleWelcome: KeyPackage mismatch for self-sent welcome, falling through to normal flow");
                // Fall through to normal pending-invite path
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HandleWelcome: failed to auto-accept self-sent welcome, falling through to normal flow");
                // Fall through to normal pending-invite path
            }
        }

        // Fetch and cache sender profile
        string? senderDisplayName = null;
        try
        {
            var metadata = await FetchAndCacheProfileAsync(nostrEvent.PublicKey);
            senderDisplayName = metadata?.DisplayName ?? metadata?.Name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch/cache sender metadata for welcome");
        }

        var invite = new PendingInvite
        {
            Id = Guid.NewGuid().ToString(),
            SenderPublicKey = nostrEvent.PublicKey,
            GroupId = groupId,
            WelcomeData = welcomeData,
            KeyPackageEventId = keyPackageEventId,
            NostrEventId = nostrEvent.EventId,
            ReceivedAt = DateTime.UtcNow,
            SenderDisplayName = senderDisplayName,
            RelayUrls = relayUrls
        };

        if (relayUrls.Count > 0)
            _logger.LogInformation("HandleWelcome: extracted {Count} relay(s) from welcome: {Relays}",
                relayUrls.Count, string.Join(", ", relayUrls));

        await _storageService.SavePendingInviteAsync(invite);
        _logger.LogInformation("Saved pending invite {InviteId} from {Sender}", invite.Id, nostrEvent.PublicKey[..Math.Min(16, nostrEvent.PublicKey.Length)]);
        _newInvites.OnNext(invite);
    }

    /// <summary>
    /// Detects the "pre-join commit" case: an inbound MLS commit at an epoch
    /// older than our local group state, typically the commit that added us to
    /// the group (we joined via the subsequent Welcome and are already at the
    /// post-commit epoch). This is expected MLS behaviour and should not surface
    /// as a decryption error.
    ///
    /// The match is intentionally narrow. Other Unprocessable failures
    /// — verify_id mismatches (MDK PR #287), unknown GroupContextExtensions
    /// proposals (WN PR #791), bad signatures, decryption failures —
    /// must NOT be swallowed; they indicate real protocol drift or bugs.
    ///
    /// Note: marmot-cs's <c>Mdk.ProcessMessageAsync</c> currently flattens
    /// inner exceptions into <c>UnprocessableResult.Reason = ex.Message</c>
    /// (see lib/marmot-cs/src/MarmotCs.Core/Mdk.cs:791-798), so we cannot
    /// discriminate on a typed <see cref="MarmotCs.Core.Errors.StaleEpochException"/>;
    /// we have to substring-match the reason. Until marmot-cs surfaces a
    /// typed stale-epoch result this is the best we can do without masking
    /// real failures.
    /// </summary>
    private static bool IsExpectedPreJoinCommitFailure(Exception ex)
    {
        // ManagedMlsService.DecryptMessageAsync wraps Unprocessable as:
        //   "Expected ApplicationMessageResult or CommitResult but got
        //    UnprocessableResult: <reason>"
        // So we only treat it as pre-join if BOTH markers are present AND the
        // reason mentions an epoch issue.
        var msg = ex.Message;
        if (msg is null) return false;
        if (!msg.Contains("UnprocessableResult", StringComparison.Ordinal)) return false;
        return msg.Contains("epoch", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleGroupMessageEventAsync(NostrEventReceived nostrEvent)
    {
        // Find the group tag (MIP-03 uses 'h', legacy uses 'g')
        var groupTag = nostrEvent.Tags.FirstOrDefault(t => t.Count > 1 && t[0] == "h")
                    ?? nostrEvent.Tags.FirstOrDefault(t => t.Count > 1 && t[0] == "g");
        if (groupTag == null)
        {
            _logger.LogWarning("HandleGroupMessage: no 'h' or 'g' tag in kind 445 event {EventId}",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        var groupIdHex = groupTag[1];
        var groupId = Convert.FromHexString(groupIdHex);
        _logger.LogInformation("HandleGroupMessage: kind 445 for group {GroupId} from {Sender}",
            groupIdHex[..Math.Min(16, groupIdHex.Length)],
            nostrEvent.PublicKey[..Math.Min(16, nostrEvent.PublicKey.Length)]);

        // Find chat with this group ID — searches ALL chats including archived
        var chat = await _storageService.GetChatByGroupIdAsync(groupIdHex);

        if (chat == null)
        {
            _logger.LogWarning("HandleGroupMessage: no chat found for group {GroupId}",
                groupIdHex[..Math.Min(16, groupIdHex.Length)]);
            return;
        }

        // Deduplicate: skip events already processed and saved to DB.
        // This also catches self-echoes (our own messages saved locally by SendMessageAsync
        // before the relay echoes them back). With MIP-03 ephemeral keys, we can't use
        // pubkey comparison for self-echo detection since the event pubkey is ephemeral.
        if (!string.IsNullOrEmpty(nostrEvent.EventId) &&
            await _storageService.MessageExistsByNostrEventIdAsync(nostrEvent.EventId))
        {
            _logger.LogDebug("HandleGroupMessage: skipping already-processed event {EventId}",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        // Decrypt message — must use the MLS group ID (not the NostrGroupId from the h-tag)
        _logger.LogDebug("HandleGroupMessage: raw content prefix ({Len} chars): {Prefix}",
            nostrEvent.Content.Length,
            nostrEvent.Content[..Math.Min(80, nostrEvent.Content.Length)]);
        var encryptedData = Convert.FromBase64String(nostrEvent.Content);
        _logger.LogInformation("HandleGroupMessage: decrypting {Len} bytes for chat {ChatName}, first4hex={First4}",
            encryptedData.Length, chat.Name,
            Convert.ToHexString(encryptedData[..Math.Min(4, encryptedData.Length)]).ToLowerInvariant());

        // Pass Nostr event metadata for MIP-03 tiebreaker resolution
        var eventCreatedAt = nostrEvent.CreatedAt != default
            ? (DateTimeOffset?)new DateTimeOffset(nostrEvent.CreatedAt, TimeSpan.Zero)
            : null;

        MlsDecryptedMessage decrypted;
        try
        {
            decrypted = await _mlsService.DecryptMessageAsync(
                chat.MlsGroupId!, encryptedData, nostrEvent.EventId, eventCreatedAt);
        }
        catch (MarmotCs.Core.Errors.RaceLostException raceLost)
        {
            // MIP-03 tiebreaker: our staged commit lost the race against this incoming commit.
            // The incoming commit was applied, our pending was cleared. Log and let the caller
            // retry their operation at the new epoch.
            _logger.LogWarning(
                "HandleGroupMessage: MIP-03 race lost — incoming commit {WinnerId} won for group {GroupId}, new epoch {NewEpoch}",
                raceLost.WinnerEventId, groupIdHex[..Math.Min(16, groupIdHex.Length)], raceLost.NewEpoch);
            return;
        }
        catch (Exception ex) when (IsExpectedPreJoinCommitFailure(ex))
        {
            // Pre-join commit: we joined via Welcome at epoch N+1 and cannot
            // decrypt the commit at epoch N that preceded our Welcome. This is
            // expected MLS behaviour. We log at Warning (not Error) and don't
            // surface to the UI via _decryptionErrors.
            //
            // NOTE: this filter is intentionally narrow. Broader catches (e.g.
            // any "Unprocessable" or any "stale*") mask real bugs such as
            // verify_id failures (MDK PR #287) and unhandled GroupContextExtensions
            // commits (WN PR #791) — both of which surface as Unprocessable from
            // the underlying MDK and must be visible.
            _logger.LogWarning(
                "HandleGroupMessage: skipping pre-join commit on event {EventId} in group {GroupId} (epoch={CurrentEpoch}, type={ExType}): {Error}",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)],
                groupIdHex[..Math.Min(16, groupIdHex.Length)],
                chat.MlsEpoch,
                ex.GetType().Name,
                ex.Message);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "HandleGroupMessage: MLS decrypt failed for event {EventId} in group {GroupId} (epoch={CurrentEpoch}, type={ExType}, ciphertextLen={Len}, first4hex={First4})",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)],
                groupIdHex[..Math.Min(16, groupIdHex.Length)],
                chat.MlsEpoch,
                ex.GetType().Name,
                encryptedData.Length,
                Convert.ToHexString(encryptedData[..Math.Min(4, encryptedData.Length)]).ToLowerInvariant());
            _decryptionErrors.OnNext(new MlsDecryptionError
            {
                ChatId = chat.Id,
                ChatName = chat.Name,
                EventId = nostrEvent.EventId,
                ErrorMessage = ex.Message,
                Timestamp = DateTime.UtcNow
            });

            // If the error mentions an epoch mismatch the device has fallen behind and can no
            // longer decrypt future messages — mark it so the UI can surface a resync banner.
            if (!chat.IsOutOfSync && ex.Message?.Contains("epoch", StringComparison.OrdinalIgnoreCase) == true)
            {
                chat.IsOutOfSync = true;
                await _storageService.SaveChatAsync(chat);
                _chatUpdates.OnNext(chat);
                _logger.LogWarning("HandleGroupMessage: chat {ChatId} marked out-of-sync (epoch error)", chat.Id);
            }

            return;
        }

        // Commit messages advance the MLS epoch but carry no user-visible content.
        // The DecryptMessageAsync call already updated the group state.
        if (decrypted.IsCommit)
        {
            _logger.LogInformation("HandleGroupMessage: processed commit for group {GroupId}, epoch advanced",
                groupIdHex[..Math.Min(16, groupIdHex.Length)]);
            return;
        }

        _logger.LogInformation("HandleGroupMessage: decrypted message from {Sender}, epoch={Epoch}, content length={Len}, hasImage={HasImage}, rumorKind={Kind}",
            decrypted.SenderPublicKey[..Math.Min(16, decrypted.SenderPublicKey.Length)], decrypted.Epoch, decrypted.Plaintext.Length,
            decrypted.ImageUrl != null, decrypted.RumorKind);

        // Handle reaction events (kind 7) — update the target message instead of creating a new one
        if (decrypted.RumorKind == 7 && !string.IsNullOrEmpty(decrypted.ReactionTargetEventId) && !string.IsNullOrEmpty(decrypted.ReactionEmoji))
        {
            _logger.LogInformation("HandleGroupMessage: processing reaction {Emoji} on event {TargetEvent} from {Sender}",
                decrypted.ReactionEmoji, decrypted.ReactionTargetEventId[..Math.Min(16, decrypted.ReactionTargetEventId.Length)],
                decrypted.SenderPublicKey[..Math.Min(16, decrypted.SenderPublicKey.Length)]);

            var targetMessage = await _storageService.GetMessageByNostrEventIdAsync(decrypted.ReactionTargetEventId);
            if (targetMessage != null)
            {
                if (!targetMessage.Reactions.ContainsKey(decrypted.ReactionEmoji))
                    targetMessage.Reactions[decrypted.ReactionEmoji] = new List<string>();

                if (!targetMessage.Reactions[decrypted.ReactionEmoji].Contains(decrypted.SenderPublicKey))
                {
                    targetMessage.Reactions[decrypted.ReactionEmoji].Add(decrypted.SenderPublicKey);
                    await _storageService.SaveMessageAsync(targetMessage);
                    _reactionUpdates.OnNext((targetMessage.Id, decrypted.ReactionEmoji, decrypted.SenderPublicKey));
                    _logger.LogInformation("HandleGroupMessage: added reaction {Emoji} to message {MessageId}", decrypted.ReactionEmoji, targetMessage.Id);
                }
            }
            else
            {
                _logger.LogWarning("HandleGroupMessage: reaction target event {EventId} not found in local DB", decrypted.ReactionTargetEventId);
            }

            // Mark this event as processed so relay echoes are skipped
            if (!string.IsNullOrEmpty(nostrEvent.EventId))
            {
                var reactionMarker = new Message
                {
                    Id = Guid.NewGuid().ToString(),
                    ChatId = chat.Id,
                    SenderPublicKey = decrypted.SenderPublicKey,
                    Type = MessageType.Text,
                    Content = string.Empty,
                    NostrEventId = nostrEvent.EventId,
                    Timestamp = nostrEvent.CreatedAt,
                    IsDeleted = true // Hidden from UI
                };
                await _storageService.SaveMessageAsync(reactionMarker);
            }
            return;
        }

        // DeviceSync resync request: fire handler in background so the message still
        // renders in Private Notes, but the re-invite logic runs asynchronously.
        if (chat.Type == ChatType.DeviceSync &&
            decrypted.Plaintext?.StartsWith("[ResyncRequest] ", StringComparison.Ordinal) == true)
        {
            _ = HandleResyncRequestAsync(decrypted.Plaintext);
        }

        // Determine message type and content based on imeta/image metadata
        var messageType = MessageType.Text;
        var content = decrypted.Plaintext;
        if (!string.IsNullOrEmpty(decrypted.ImageUrl))
        {
            if (decrypted.MediaType?.StartsWith("audio/") == true)
            {
                messageType = MessageType.Audio;
                if (string.IsNullOrEmpty(content))
                    content = "[Voice message]";
            }
            else if (decrypted.MediaType?.StartsWith("image/") == true || string.IsNullOrEmpty(decrypted.MediaType))
            {
                messageType = MessageType.Image;
                if (string.IsNullOrEmpty(content))
                    content = "[Image]";
            }
            else
            {
                messageType = MessageType.File;
                if (string.IsNullOrEmpty(content))
                    content = $"[File: {decrypted.FileName ?? "attachment"}]";
            }
        }

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ChatId = chat.Id,
            SenderPublicKey = decrypted.SenderPublicKey,
            Type = messageType,
            Content = content,
            ImageUrl = decrypted.ImageUrl,
            FileName = decrypted.FileName,
            FileSha256 = decrypted.FileSha256,
            EncryptionNonce = decrypted.EncryptionNonce,
            MediaType = decrypted.MediaType,
            EncryptionVersion = decrypted.EncryptionVersion,
            NostrEventId = nostrEvent.EventId,
            RumorEventId = decrypted.RumorEventId,
            MlsEpoch = decrypted.Epoch,
            Timestamp = nostrEvent.CreatedAt,
            ReceivedAt = DateTime.UtcNow,
            Status = MessageStatus.Delivered,
            IsFromCurrentUser = decrypted.SenderPublicKey == _currentUser?.PublicKeyHex
        };

        // Resolve reply-to reference if present
        if (!string.IsNullOrEmpty(decrypted.ReplyToRumorEventId))
        {
            var replyTarget = await _storageService.GetMessageByRumorEventIdAsync(decrypted.ReplyToRumorEventId)
                           ?? await _storageService.GetMessageByNostrEventIdAsync(decrypted.ReplyToRumorEventId);
            if (replyTarget != null)
            {
                message.ReplyToMessageId = replyTarget.Id;
                message.ReplyToMessage = replyTarget;
                _logger.LogInformation("HandleGroupMessage: resolved reply to message {MessageId}", replyTarget.Id);
            }
            else
            {
                _logger.LogDebug("HandleGroupMessage: reply target {EventId} not found in local DB",
                    decrypted.ReplyToRumorEventId[..Math.Min(16, decrypted.ReplyToRumorEventId.Length)]);
            }
        }

        message.Sender = await _storageService.GetUserByPublicKeyAsync(message.SenderPublicKey);

        // If sender is unknown, fetch their profile in the background so the message
        // is delivered immediately while the profile resolves asynchronously.
        if (message.Sender == null && !message.IsFromCurrentUser)
        {
            _ = FetchUnknownSenderAsync(message);
        }

        await _storageService.SaveMessageAsync(message);
        _newMessages.OnNext(message);

        // Update chat
        chat.LastActivityAt = DateTime.UtcNow;
        chat.LastMessage = message;
        if (!message.IsFromCurrentUser)
        {
            chat.UnreadCount++;
        }
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);
    }

    public async Task<IEnumerable<PendingInvite>> GetPendingInvitesAsync()
    {
        return await _storageService.GetPendingInvitesAsync();
    }

    public async Task<Chat> AcceptInviteAsync(string inviteId)
    {
        _logger.LogInformation("Accepting invite {InviteId}", inviteId);

        var invites = await _storageService.GetPendingInvitesAsync();
        var invite = invites.FirstOrDefault(i => i.Id == inviteId)
            ?? throw new ArgumentException("Invite not found", nameof(inviteId));

        // Process MLS welcome — pass both welcome data and the kind-444 wrapper event ID
        _logger.LogInformation("AcceptInvite: processing welcome with wrapperEventId={EventId}",
            invite.NostrEventId[..Math.Min(16, invite.NostrEventId.Length)]);

        MlsGroupInfo groupInfo;
        try
        {
            groupInfo = await _mlsService.ProcessWelcomeAsync(invite.WelcomeData, invite.NostrEventId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("KeyPackage"))
        {
            // The private key material for the targeted KeyPackage is lost — this invite
            // can never be accepted on this device. Auto-dismiss so it doesn't stay stuck.
            _logger.LogWarning(ex, "AcceptInvite: KeyPackage mismatch for invite {InviteId}, auto-dismissing", inviteId);
            await _storageService.DismissWelcomeEventAsync(invite.NostrEventId);
            await _storageService.DeletePendingInviteAsync(inviteId);
            throw new InvalidOperationException(
                "This invite targets a KeyPackage whose private key is no longer available. " +
                "The invite has been dismissed. Ask the sender to create a new invite.", ex);
        }

        // Check if we already have a chat for this MLS group (prevents duplicate chats)
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        var existingChats = await _storageService.GetAllChatsAsync();
        var existingChat = existingChats.FirstOrDefault(c =>
            c.MlsGroupId != null &&
            Convert.ToHexString(c.MlsGroupId).Equals(groupIdHex, StringComparison.OrdinalIgnoreCase));

        if (existingChat != null)
        {
            _logger.LogInformation("Chat already exists for group {GroupId}, returning existing chat {ChatId}",
                groupIdHex[..Math.Min(16, groupIdHex.Length)], existingChat.Id);
            await _storageService.DismissWelcomeEventAsync(invite.NostrEventId);
            await _storageService.DeletePendingInviteAsync(inviteId);

            // Welcome for a group we're already in = resync completed; clear the out-of-sync banner
            if (existingChat.IsOutOfSync || existingChat.IsResyncPending)
            {
                existingChat.IsOutOfSync = false;
                existingChat.IsResyncPending = false;
                await _storageService.SaveChatAsync(existingChat);
                _chatUpdates.OnNext(existingChat);
                _logger.LogInformation("AcceptInvite: resync complete for chat {ChatId}, out-of-sync flags cleared", existingChat.Id);
            }

            return existingChat;
        }

        // Create chat for the group — fall back to sender name if the MLS Welcome
        // didn't carry a group name (e.g., Rust-originated or older welcomes)
        var chatName = !string.IsNullOrWhiteSpace(groupInfo.GroupName)
            ? groupInfo.GroupName
            : !string.IsNullOrWhiteSpace(invite.SenderDisplayName)
                ? $"Chat with {invite.SenderDisplayName}"
                : "Group Chat";

        // Get the NostrGroupId (from 0xF2EE extension) for relay subscriptions and h-tag routing
        var nostrGroupId = _mlsService.GetNostrGroupId(groupInfo.GroupId);

        // Extract admin pubkeys from 0xF2EE extension; fall back to invite sender
        var adminPubkeys = _mlsService.GetAdminPubkeys(groupInfo.GroupId);
        var creatorPubkey = adminPubkeys.Count > 0 ? adminPubkeys[0] : invite.SenderPublicKey;
        if (adminPubkeys.Count == 0 && !string.IsNullOrEmpty(creatorPubkey))
            adminPubkeys = new List<string> { creatorPubkey.ToLowerInvariant() };

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = chatName,
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = groupInfo.MemberPublicKeys,
            AdminPublicKeys = adminPubkeys,
            CreatorPublicKey = creatorPubkey,
            RelayUrls = invite.RelayUrls,
            WelcomeNostrEventId = invite.NostrEventId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        // Ensure at least one of the group's relays is connected
        if (invite.RelayUrls.Count > 0)
        {
            var connectedUrls = _nostrService.ConnectedRelayUrls;
            var hasOverlap = invite.RelayUrls.Any(r =>
                connectedUrls.Any(c => string.Equals(c.TrimEnd('/'), r.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));

            if (!hasOverlap)
            {
                _logger.LogInformation("AcceptInvite: no overlap with connected relays, connecting to group relay {Relay}",
                    invite.RelayUrls[0]);
                try
                {
                    await _nostrService.ConnectAsync(invite.RelayUrls[0]);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AcceptInvite: failed to connect to group relay {Relay}", invite.RelayUrls[0]);
                }
            }
        }

        await _storageService.SaveChatAsync(chat);
        await _storageService.DismissWelcomeEventAsync(invite.NostrEventId);
        await _storageService.DeletePendingInviteAsync(inviteId);
        _chatUpdates.OnNext(chat);

        // MIP-02 §"Self-Update Timing": schedule self-update within 24h of joining
        _pendingSelfUpdates[groupIdHex] = DateTime.UtcNow;
        _logger.LogInformation("AcceptInvite: scheduled MIP-02 self-update for group {GroupId}", groupIdHex[..Math.Min(16, groupIdHex.Length)]);

        // Mark the consumed KeyPackage as used so future senders don't reuse it
        if (!string.IsNullOrEmpty(invite.KeyPackageEventId))
        {
            var consumedKp = await _storageService.GetKeyPackageByNostrEventIdAsync(invite.KeyPackageEventId);
            if (consumedKp != null)
            {
                await _storageService.MarkKeyPackageUsedAsync(consumedKp.Id);
                _logger.LogInformation("AcceptInvite: marked KeyPackage {KpId} as used (event {EventId})",
                    consumedKp.Id, invite.KeyPackageEventId[..Math.Min(16, invite.KeyPackageEventId.Length)]);

                // Auto-publish a new KeyPackage if this was the last unused one
                _ = AutoPublishKeyPackageIfNeededAsync();
            }
            else
            {
                _logger.LogDebug("AcceptInvite: KeyPackage with event ID {EventId} not found in local DB, skipping mark-as-used",
                    invite.KeyPackageEventId[..Math.Min(16, invite.KeyPackageEventId.Length)]);
            }
        }

        _logger.LogInformation("Accepted invite {InviteId}, created chat {ChatId} for group {GroupName}", inviteId, chat.Id, chat.Name);
        return chat;
    }

    public async Task AutoPublishKeyPackageIfNeededAsync()
    {
        try
        {
            if (_currentUser == null || string.IsNullOrEmpty(_currentUser.PublicKeyHex))
                return;

            var remaining = await _storageService.GetUnusedKeyPackagesAsync(_currentUser.PublicKeyHex);
            if (remaining.Any())
            {
                _logger.LogDebug("AutoPublishKP: {Count} unused KeyPackages remaining, no action needed", remaining.Count());
                return;
            }

            _logger.LogInformation("AutoPublishKP: no unused KeyPackages remaining, generating and publishing a new one");

            var keyPackage = await _mlsService.GenerateKeyPackageAsync();
            var eventId = await _nostrService.PublishKeyPackageAsync(
                keyPackage.Data, _currentUser.PrivateKeyHex, keyPackage.NostrTags);
            keyPackage.NostrEventId = eventId;
            keyPackage.RelayUrls = _nostrService.ConnectedRelayUrls.ToList();
            await _storageService.SaveKeyPackageAsync(keyPackage);

            _logger.LogInformation("AutoPublishKP: published new KeyPackage {EventId}", eventId[..Math.Min(16, eventId.Length)]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoPublishKP: failed to auto-publish new KeyPackage");
        }
    }

    // ====== MIP-02 Self-Update Scheduler ======

    private async void OnSelfUpdateTimerTick(object? state)
    {
        if (_currentUser == null || _pendingSelfUpdates.IsEmpty) return;

        foreach (var (groupIdHex, joinTime) in _pendingSelfUpdates.ToArray())
        {
            var elapsed = DateTime.UtcNow - joinTime;
            // MIP-02 §"Self-Update Timing": MUST within 24h. We trigger after 5 minutes
            // to give time for catch-up on outstanding commits (RECOMMENDED order),
            // and retry periodically if it fails.
            if (elapsed < TimeSpan.FromMinutes(5)) continue;

            try
            {
                var groupId = Convert.FromHexString(groupIdHex);
                await PerformSelfUpdateAsync(groupId);
                _pendingSelfUpdates.TryRemove(groupIdHex, out _);
                _logger.LogInformation("MIP-02 self-update completed for group {GroupId} ({Elapsed} after join)",
                    groupIdHex[..Math.Min(16, groupIdHex.Length)], elapsed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MIP-02 self-update failed for group {GroupId}, will retry",
                    groupIdHex[..Math.Min(16, groupIdHex.Length)]);

                // If past 24h, log an error (MUST violation) but keep retrying
                if (elapsed > TimeSpan.FromHours(24))
                {
                    _logger.LogError("MIP-02 VIOLATION: self-update for group {GroupId} not completed within 24h of join ({Elapsed})",
                        groupIdHex[..Math.Min(16, groupIdHex.Length)], elapsed);
                }
            }
        }
    }

    private async void OnKpRotationTimerTick(object? state)
    {
        if (_currentUser == null) return;
        try
        {
            await AutoPublishKeyPackageIfNeededAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KpRotation: proactive KeyPackage rotation check failed");
        }
    }

    // TODO: Migrate to staged commit path (StageSelfUpdateAsync + MergeStagedAsync)
    // for full MIP-03 compliance. Currently uses auto-merge UpdateKeysAsync which
    // advances local state before relay confirmation.
    private async Task PerformSelfUpdateAsync(byte[] groupId)
    {
        if (_currentUser == null) return;

        var commitData = await _mlsService.UpdateKeysAsync(groupId);
        var nostrGroupId = _mlsService.GetNostrGroupId(groupId);
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        var commitGroupId = nostrGroupId != null
            ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
            : groupIdHex;

        await _nostrService.PublishGroupMessageAsync(commitData, commitGroupId, _currentUser.PrivateKeyHex);
        _logger.LogInformation("PerformSelfUpdate: published self-update commit for group {GroupId}", groupIdHex[..Math.Min(16, groupIdHex.Length)]);
    }

    public async Task DeclineInviteAsync(string inviteId)
    {
        _logger.LogInformation("Declining invite {InviteId}", inviteId);

        var invites = await _storageService.GetPendingInvitesAsync();
        var invite = invites.FirstOrDefault(i => i.Id == inviteId);
        if (invite != null)
            await _storageService.DismissWelcomeEventAsync(invite.NostrEventId);

        await _storageService.DeletePendingInviteAsync(inviteId);
    }

    /// <summary>
    /// Restores NIP-17 (kind 14 DM) bot chats by fetching gift-wrap history from relays
    /// and routing each unwrapped event through the normal incoming-message path.
    /// HandleBotMessageEventAsync handles dedup and chat creation, so this is idempotent.
    /// Self-echoes (rumors authored by the current user) are skipped — their sender chat
    /// can't be derived from the rumor without parsing the inner p tag, and the user's
    /// own outgoing-only chats are rare in practice.
    /// </summary>
    public async Task RescanNip17ChatsAsync()
    {
        if (_currentUser == null || string.IsNullOrEmpty(_currentUser.PublicKeyHex))
        {
            _logger.LogWarning("Cannot rescan NIP-17 chats: no current user");
            return;
        }

        _logger.LogInformation("Rescanning NIP-17 DM history for {PubKey}...",
            _currentUser.PublicKeyHex[..Math.Min(16, _currentUser.PublicKeyHex.Length)]);

        var dmEvents = await _nostrService.FetchNip17DmHistoryAsync(_currentUser.PublicKeyHex, _currentUser.PrivateKeyHex);

        var processed = 0;
        foreach (var nostrEvent in dmEvents)
        {
            try
            {
                await HandleBotMessageEventAsync(nostrEvent);
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing NIP-17 DM event {EventId} during rescan", nostrEvent.EventId);
            }
        }

        _logger.LogInformation("NIP-17 rescan complete: {Total} kind 14 event(s) routed through bot handler",
            processed);
    }

    public async Task RescanInvitesAsync()
    {
        if (_currentUser == null || string.IsNullOrEmpty(_currentUser.PublicKeyHex))
        {
            _logger.LogWarning("Cannot rescan invites: no current user");
            return;
        }

        _logger.LogInformation("Rescanning for invites for {PubKey}...",
            _currentUser.PublicKeyHex[..Math.Min(16, _currentUser.PublicKeyHex.Length)]);

        // Directly fetch kind 444 events from each relay (opens fresh connections)
        var welcomeEvents = await _nostrService.FetchWelcomeEventsAsync(_currentUser.PublicKeyHex, _currentUser.PrivateKeyHex);

        var newCount = 0;
        foreach (var nostrEvent in welcomeEvents)
        {
            try
            {
                // Check if this welcome was previously accepted or declined
                if (await _storageService.IsWelcomeEventDismissedAsync(nostrEvent.EventId))
                {
                    _logger.LogDebug("Skipping dismissed Welcome event {EventId}", nostrEvent.EventId);
                    continue;
                }

                // Check for duplicate by NostrEventId
                var existingInvites = await _storageService.GetPendingInvitesAsync();
                if (existingInvites.Any(i => i.NostrEventId == nostrEvent.EventId))
                {
                    _logger.LogDebug("Skipping duplicate Welcome event {EventId}", nostrEvent.EventId);
                    continue;
                }

                // Also check if we already have a chat from this welcome (previously accepted)
                var chats = await _storageService.GetAllChatsAsync();
                var groupTag = nostrEvent.Tags.FirstOrDefault(t => t.Count > 1 && (t[0] == "h" || t[0] == "g"));
                var groupIdHex = groupTag?[1];
                if (!string.IsNullOrEmpty(groupIdHex) && chats.Any(c =>
                    c.MlsGroupId != null &&
                    Convert.ToHexString(c.MlsGroupId).Equals(groupIdHex, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug("Skipping Welcome event {EventId} — already have chat for group {GroupId}",
                        nostrEvent.EventId, groupIdHex);
                    continue;
                }

                await HandleWelcomeEventAsync(nostrEvent);
                newCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing fetched Welcome event {EventId}", nostrEvent.EventId);
            }
        }

        _logger.LogInformation("Rescan complete: {Total} Welcome event(s) found, {New} new invite(s) created",
            welcomeEvents.Count(), newCount);
    }

    public async Task ResetGroupAsync(string chatId)
    {
        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        if (chat.MlsGroupId == null)
            throw new InvalidOperationException("Cannot reset a non-MLS chat");

        var groupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
        _logger.LogInformation("ResetGroup: resetting MLS state for chat {ChatId}, group {GroupId}",
            chatId, groupIdHex[..Math.Min(16, groupIdHex.Length)]);

        // 1. Delete corrupted MLS state
        await _storageService.DeleteMlsStateAsync(groupIdHex);

        // 2. Un-dismiss the welcome so rescan can find it
        if (!string.IsNullOrEmpty(chat.WelcomeNostrEventId))
            await _storageService.UndismissWelcomeEventAsync(chat.WelcomeNostrEventId);

        // 3. Delete the chat and messages
        await _storageService.DeleteChatAsync(chatId);
        _chatUpdates.OnNext(chat);

        _logger.LogInformation("ResetGroup: chat {ChatId} deleted, welcome un-dismissed", chatId);
    }

    public async Task<KeyPackageAuditResult> AuditKeyPackagesAsync()
    {
        if (_currentUser == null || string.IsNullOrEmpty(_currentUser.PublicKeyHex))
            throw new InvalidOperationException("No current user. Initialize first.");

        _logger.LogInformation("AuditKeyPackages: fetching own KeyPackages from relays for {PubKey}",
            _currentUser.PublicKeyHex[..Math.Min(16, _currentUser.PublicKeyHex.Length)]);

        var result = new KeyPackageAuditResult();

        // Fetch all our KeyPackages from relays. Use a high per-relay limit (100) so the audit
        // surfaces stale slots from before the rotation fix and multi-device deployments — the
        // default cap of 5 is fine for invite flows but would silently truncate the audit count.
        var relayKeyPackages = (await _nostrService.FetchKeyPackagesAsync(_currentUser.PublicKeyHex, limit: 100)).ToList();
        result.TotalOnRelays = relayKeyPackages.Count;

        _logger.LogInformation("AuditKeyPackages: found {Count} KeyPackages on relays", relayKeyPackages.Count);

        var storedKpCount = _mlsService.GetStoredKeyPackageCount();
        _logger.LogInformation("AuditKeyPackages: {Count} KeyPackages with local private keys",
            storedKpCount >= 0 ? storedKpCount.ToString() : "unknown (native backend)");

        foreach (var relayKp in relayKeyPackages)
        {
            // Check if expired
            if (relayKp.ExpiresAt < DateTime.UtcNow)
            {
                result.Expired++;
                relayKp.Status = KeyPackageStatus.Expired;
                _logger.LogDebug("AuditKeyPackages: KeyPackage {EventId} is expired",
                    relayKp.NostrEventId?[..Math.Min(16, relayKp.NostrEventId?.Length ?? 0)] ?? "unknown");
            }
            // Check if we have the private key material locally
            else if (_mlsService.HasKeyMaterialForKeyPackage(relayKp.Data))
            {
                result.ActiveWithKeys++;
                relayKp.Status = KeyPackageStatus.Active;
                _logger.LogDebug("AuditKeyPackages: KeyPackage {EventId} has local keys (active)",
                    relayKp.NostrEventId?[..Math.Min(16, relayKp.NostrEventId?.Length ?? 0)] ?? "unknown");
            }
            else
            {
                result.Lost++;
                relayKp.Status = KeyPackageStatus.Lost;
                if (!string.IsNullOrEmpty(relayKp.NostrEventId))
                    result.LostEventIds.Add(relayKp.NostrEventId);
                _logger.LogWarning("AuditKeyPackages: KeyPackage {EventId} has NO local keys (LOST)",
                    relayKp.NostrEventId?[..Math.Min(16, relayKp.NostrEventId?.Length ?? 0)] ?? "unknown");
            }

            // Save/update in local DB
            await _storageService.SaveKeyPackageAsync(relayKp);
        }

        _logger.LogInformation(
            "AuditKeyPackages: complete — {Total} on relays, {Active} active, {Lost} lost, {Expired} expired",
            result.TotalOnRelays, result.ActiveWithKeys, result.Lost, result.Expired);

        return result;
    }

    public async Task<LoadOlderMessagesResult> LoadOlderMessagesAsync(string chatId, DateTimeOffset fetchBoundary)
    {
        _logger.LogInformation("LoadOlderMessages: starting for chat {ChatId}, boundary={Boundary}",
            chatId, fetchBoundary);

        var result = new LoadOlderMessagesResult();

        var chat = await _storageService.GetChatAsync(chatId);
        if (chat == null)
        {
            _logger.LogWarning("LoadOlderMessages: chat {ChatId} not found", chatId);
            result.HasMore = false;
            result.NewBoundary = fetchBoundary;
            return result;
        }

        if (chat.MlsGroupId == null)
        {
            _logger.LogWarning("LoadOlderMessages: chat {ChatId} has no MLS group, cannot load history", chatId);
            result.HasMore = false;
            result.NewBoundary = fetchBoundary;
            return result;
        }

        // Use NostrGroupId for the relay query h-tag filter, fall back to MlsGroupId
        var groupIdForQuery = chat.NostrGroupId ?? chat.MlsGroupId;
        var groupIdHex = Convert.ToHexString(groupIdForQuery).ToLowerInvariant();

        _logger.LogDebug("LoadOlderMessages: querying group {GroupId} (NostrGroupId={HasNostr})",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], chat.NostrGroupId != null);

        var chatCreatedAt = new DateTimeOffset(chat.CreatedAt, TimeSpan.Zero);
        var until = fetchBoundary;
        var retries = 0;
        const int maxRetries = 3;
        var fetchedEvents = new List<NostrEventReceived>();

        // 2-day sliding window with retry
        while (retries < maxRetries)
        {
            var since = until.AddDays(-2);

            // Clamp since to chat creation time
            if (since < chatCreatedAt)
            {
                since = chatCreatedAt;
            }

            _logger.LogDebug("LoadOlderMessages: window attempt {Attempt}, since={Since}, until={Until}",
                retries + 1, since, until);

            var events = await _nostrService.FetchGroupHistoryAsync(groupIdHex, since, until, 50);
            var eventList = events.ToList();

            if (eventList.Count > 0)
            {
                fetchedEvents.AddRange(eventList);
                result.NewBoundary = since;
                _logger.LogInformation("LoadOlderMessages: found {Count} events in window", eventList.Count);
                break;
            }

            // No results — move window back and retry
            retries++;
            until = since;

            if (since <= chatCreatedAt)
            {
                _logger.LogInformation("LoadOlderMessages: reached chat creation time, no more history");
                result.HasMore = false;
                result.NewBoundary = chatCreatedAt;
                return result;
            }

            _logger.LogDebug("LoadOlderMessages: no results, retrying (attempt {Attempt}/{Max})",
                retries + 1, maxRetries);
        }

        if (fetchedEvents.Count == 0)
        {
            _logger.LogInformation("LoadOlderMessages: no events found after {Retries} retries", maxRetries);
            result.HasMore = until > chatCreatedAt;
            result.NewBoundary = until;
            return result;
        }

        // Process fetched events: dedup, skip own, decrypt, build messages
        var processedCount = 0;
        var skipCount = 0;
        var decryptFailCount = 0;
        var unknownSenderKeys = new HashSet<string>();

        foreach (var nostrEvent in fetchedEvents)
        {
            // Dedup by NostrEventId — skip events already in the database
            if (!string.IsNullOrEmpty(nostrEvent.EventId) &&
                await _storageService.MessageExistsByNostrEventIdAsync(nostrEvent.EventId))
            {
                skipCount++;
                continue;
            }

            // Skip own messages (already saved locally by SendMessageAsync)
            if (nostrEvent.PublicKey == _currentUser?.PublicKeyHex)
            {
                skipCount++;
                continue;
            }

            // Decrypt
            var encryptedData = Convert.FromBase64String(nostrEvent.Content);

            MlsDecryptedMessage decrypted;
            try
            {
                // Use MlsGroupId for decrypt, NOT the NostrGroupId from the h-tag
                var olderCreatedAt = nostrEvent.CreatedAt != default
                    ? (DateTimeOffset?)new DateTimeOffset(nostrEvent.CreatedAt, TimeSpan.Zero)
                    : null;
                decrypted = await _mlsService.DecryptMessageAsync(
                    chat.MlsGroupId!, encryptedData, nostrEvent.EventId, olderCreatedAt);
            }
            catch (Exception ex)
            {
                decryptFailCount++;
                _logger.LogWarning(ex, "LoadOlderMessages: decrypt failed for event {EventId}, skipping",
                    nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
                continue;
            }

            // Determine message type based on imeta/image metadata
            var olderMsgType = MessageType.Text;
            var olderContent = decrypted.Plaintext;
            if (!string.IsNullOrEmpty(decrypted.ImageUrl))
            {
                if (decrypted.MediaType?.StartsWith("audio/") == true)
                {
                    olderMsgType = MessageType.Audio;
                    if (string.IsNullOrEmpty(olderContent))
                        olderContent = "[Voice message]";
                }
                else if (decrypted.MediaType?.StartsWith("image/") == true || string.IsNullOrEmpty(decrypted.MediaType))
                {
                    olderMsgType = MessageType.Image;
                    if (string.IsNullOrEmpty(olderContent))
                        olderContent = "[Image]";
                }
                else
                {
                    olderMsgType = MessageType.File;
                    if (string.IsNullOrEmpty(olderContent))
                        olderContent = $"[File: {decrypted.FileName ?? "attachment"}]";
                }
            }

            var message = new Message
            {
                Id = Guid.NewGuid().ToString(),
                ChatId = chat.Id,
                SenderPublicKey = decrypted.SenderPublicKey,
                Type = olderMsgType,
                Content = olderContent,
                ImageUrl = decrypted.ImageUrl,
                FileName = decrypted.FileName,
                FileSha256 = decrypted.FileSha256,
                EncryptionNonce = decrypted.EncryptionNonce,
                MediaType = decrypted.MediaType,
                EncryptionVersion = decrypted.EncryptionVersion,
                NostrEventId = nostrEvent.EventId,
                MlsEpoch = decrypted.Epoch,
                Timestamp = nostrEvent.CreatedAt,
                ReceivedAt = DateTime.UtcNow,
                Status = MessageStatus.Delivered,
                IsFromCurrentUser = decrypted.SenderPublicKey == _currentUser?.PublicKeyHex
            };

            message.Sender = await _storageService.GetUserByPublicKeyAsync(message.SenderPublicKey);

            // Queue unknown senders for batch profile fetch below
            if (message.Sender == null && !message.IsFromCurrentUser)
            {
                unknownSenderKeys.Add(message.SenderPublicKey);
            }

            await _storageService.SaveMessageAsync(message);
            result.Messages.Add(message);
            processedCount++;
        }

        // Batch-fetch profiles for unknown senders
        if (unknownSenderKeys.Count > 0)
        {
            _logger.LogInformation("LoadOlderMessages: fetching profiles for {Count} unknown senders", unknownSenderKeys.Count);
            foreach (var pubKey in unknownSenderKeys)
            {
                try
                {
                    var metadata = await FetchAndCacheProfileAsync(pubKey);
                    if (metadata != null)
                    {
                        // Backfill sender on already-processed messages
                        var user = await _storageService.GetUserByPublicKeyAsync(pubKey);
                        foreach (var msg in result.Messages.Where(m => m.SenderPublicKey == pubKey && m.Sender == null))
                        {
                            msg.Sender = user;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch profile for unknown sender {PubKey}", pubKey[..Math.Min(16, pubKey.Length)]);
                }
            }
        }

        _logger.LogInformation(
            "LoadOlderMessages: processed {Processed} messages, skipped {Skipped}, decrypt failures {DecryptFail}",
            processedCount, skipCount, decryptFailCount);

        // Sort by timestamp ascending (oldest first)
        result.Messages.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        // Check if we've reached the beginning
        if (result.NewBoundary <= chatCreatedAt)
        {
            result.HasMore = false;
        }

        return result;
    }

    public async Task<UserMetadata?> FetchAndCacheProfileAsync(string publicKeyHex)
    {
        try
        {
            var metadata = await _nostrService.FetchUserMetadataAsync(publicKeyHex);
            if (metadata != null && !string.IsNullOrEmpty(metadata.PublicKeyHex))
            {
                await SaveMetadataAsUserAsync(metadata);
            }
            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FetchAndCacheProfile failed for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);
            return null;
        }
    }

    public async Task<UserMetadata?> GetCachedOrFetchProfileAsync(string publicKeyHex)
    {
        // 1. Check DB — fast local SQLite lookup
        try
        {
            var dbUser = await _storageService.GetUserByPublicKeyAsync(publicKeyHex);
            if (dbUser != null && !string.IsNullOrEmpty(dbUser.DisplayName))
            {
                var metadata = UserToMetadata(dbUser);
                _ = BackgroundRefreshMetadataAsync(publicKeyHex);
                return metadata;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DB lookup failed for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);
        }

        // 2. Nothing in DB — do a synchronous relay fetch
        return await FetchAndCacheProfileAsync(publicKeyHex);
    }

    /// <summary>
    /// Fires a background relay fetch for the given pubkey, deduplicating concurrent requests.
    /// If newer metadata is returned, updates the DB and emits <see cref="MetadataUpdated"/>.
    /// </summary>
    private async Task BackgroundRefreshMetadataAsync(string publicKeyHex)
    {
        // Deduplicate: skip if a fetch for this pubkey is already in-flight
        if (!_inflightMetadataFetches.TryAdd(publicKeyHex, 0))
            return;

        try
        {
            var metadata = await _nostrService.FetchUserMetadataAsync(publicKeyHex);
            if (metadata == null || string.IsNullOrEmpty(metadata.PublicKeyHex))
                return;

            // Check if data actually changed compared to what's in the DB
            var dbUser = await _storageService.GetUserByPublicKeyAsync(publicKeyHex);
            var changed = dbUser == null
                || dbUser.DisplayName != (metadata.DisplayName ?? metadata.Name)
                || dbUser.AvatarUrl != metadata.Picture
                || dbUser.About != metadata.About
                || dbUser.Username != metadata.Username
                || dbUser.Nip05 != metadata.Nip05;

            await SaveMetadataAsUserAsync(metadata);

            if (changed)
            {
                _logger.LogInformation("Metadata updated from relay for {PubKey}: {Name}",
                    publicKeyHex[..Math.Min(16, publicKeyHex.Length)], metadata.GetDisplayName());
                _metadataUpdated.OnNext(metadata);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background metadata refresh failed for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);
        }
        finally
        {
            _inflightMetadataFetches.TryRemove(publicKeyHex, out _);
        }
    }

    /// <summary>
    /// Converts a <see cref="User"/> DB entity to a <see cref="UserMetadata"/> for cache/display use.
    /// </summary>
    private static UserMetadata UserToMetadata(User user)
    {
        return new UserMetadata
        {
            PublicKeyHex = user.PublicKeyHex,
            Npub = user.Npub,
            Name = user.DisplayName,
            DisplayName = user.DisplayName,
            Username = user.Username,
            About = user.About,
            Picture = user.AvatarUrl,
            Nip05 = user.Nip05,
            CreatedAt = user.CreatedAt,
        };
    }

    /// <summary>
    /// Background task: fetches profile for an unknown message sender and emits a message update
    /// so the UI can refresh the display name without blocking message delivery.
    /// </summary>
    private async Task FetchUnknownSenderAsync(Message message)
    {
        try
        {
            _logger.LogInformation("Fetching profile for unknown sender {PubKey}", message.SenderPublicKey[..Math.Min(16, message.SenderPublicKey.Length)]);
            var metadata = await FetchAndCacheProfileAsync(message.SenderPublicKey);
            if (metadata != null)
            {
                message.Sender = await _storageService.GetUserByPublicKeyAsync(message.SenderPublicKey);
                // Re-emit so the UI updates the display name
                _newMessages.OnNext(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background profile fetch failed for sender {PubKey}", message.SenderPublicKey[..Math.Min(16, message.SenderPublicKey.Length)]);
        }
    }

    /// <summary>
    /// Converts UserMetadata to a User and saves to the database (upsert).
    /// Preserves existing fields (private keys, signer details, IsCurrentUser) if the user already exists.
    /// </summary>
    private async Task SaveMetadataAsUserAsync(UserMetadata metadata)
    {
        try
        {
            var existing = await _storageService.GetUserByPublicKeyAsync(metadata.PublicKeyHex);
            if (existing != null)
            {
                // Update profile fields only, preserve everything else
                existing.DisplayName = metadata.DisplayName ?? metadata.Name ?? existing.DisplayName;
                existing.Username = metadata.Username ?? existing.Username;
                existing.AvatarUrl = metadata.Picture ?? existing.AvatarUrl;
                existing.About = metadata.About ?? existing.About;
                existing.Nip05 = metadata.Nip05 ?? existing.Nip05;
                existing.LastUpdatedAt = DateTime.UtcNow;

                // Download avatar if URL changed or no local copy
                if (!string.IsNullOrEmpty(existing.AvatarUrl) &&
                    (existing.LocalAvatarPath == null || !File.Exists(existing.LocalAvatarPath)))
                {
                    existing.LocalAvatarPath = await DownloadAvatarAsync(metadata.PublicKeyHex, existing.AvatarUrl);
                }

                await _storageService.SaveUserAsync(existing);
                _logger.LogDebug("Updated cached profile for {PubKey}", metadata.PublicKeyHex[..16]);
            }
            else
            {
                var npub = metadata.Npub ?? Bech32.Encode("npub", Convert.FromHexString(metadata.PublicKeyHex));
                var user = new User
                {
                    Id = Guid.NewGuid().ToString(),
                    PublicKeyHex = metadata.PublicKeyHex,
                    Npub = npub,
                    DisplayName = metadata.DisplayName ?? metadata.Name ?? string.Empty,
                    Username = metadata.Username,
                    AvatarUrl = metadata.Picture,
                    About = metadata.About,
                    Nip05 = metadata.Nip05,
                    CreatedAt = metadata.CreatedAt ?? DateTime.UtcNow,
                    LastUpdatedAt = DateTime.UtcNow,
                    IsCurrentUser = false
                };

                // Download avatar
                if (!string.IsNullOrEmpty(user.AvatarUrl))
                {
                    user.LocalAvatarPath = await DownloadAvatarAsync(metadata.PublicKeyHex, user.AvatarUrl);
                }

                await _storageService.SaveUserAsync(user);
                _logger.LogInformation("Cached new profile for {PubKey}: {Name}", metadata.PublicKeyHex[..16], user.DisplayName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache metadata for {PubKey}", metadata.PublicKeyHex[..16]);
        }
    }

    private async Task<string?> DownloadAvatarAsync(string publicKeyHex, string avatarUrl)
    {
        try
        {
            var avatarDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Scramble", "avatars");
            Directory.CreateDirectory(avatarDir);

            var extension = Path.GetExtension(new Uri(avatarUrl).AbsolutePath);
            if (string.IsNullOrEmpty(extension) || extension.Length > 5)
                extension = ".jpg";
            var localPath = Path.Combine(avatarDir, $"{publicKeyHex[..16]}{extension}");

            // Skip if already downloaded
            if (File.Exists(localPath))
                return localPath;

            // Validate URL to prevent SSRF (block private IPs and non-HTTPS)
            if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var parsedUri) ||
                (parsedUri.Scheme != "https" && parsedUri.Scheme != "http"))
            {
                _logger.LogWarning("Avatar URL rejected for {PubKey}: invalid scheme", publicKeyHex[..16]);
                return null;
            }
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(parsedUri.Host);
                foreach (var addr in addresses)
                {
                    if (System.Net.IPAddress.IsLoopback(addr))
                    {
                        _logger.LogWarning("Avatar URL rejected for {PubKey}: resolves to loopback", publicKeyHex[..16]);
                        return null;
                    }
                    var bytes = addr.GetAddressBytes();
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                         (bytes[0] == 192 && bytes[1] == 168) || bytes[0] == 127 ||
                         (bytes[0] == 169 && bytes[1] == 254) || bytes[0] == 0))
                    {
                        _logger.LogWarning("Avatar URL rejected for {PubKey}: resolves to private IP", publicKeyHex[..16]);
                        return null;
                    }
                }
            }
            catch (Exception)
            {
                _logger.LogWarning("Avatar URL rejected for {PubKey}: DNS resolution failed", publicKeyHex[..16]);
                return null;
            }

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await httpClient.GetAsync(avatarUrl);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Avatar download failed for {PubKey}: HTTP {Status}", publicKeyHex[..16], response.StatusCode);
                return null;
            }

            // Limit to 2MB
            var content = await response.Content.ReadAsByteArrayAsync();
            if (content.Length > 2 * 1024 * 1024)
            {
                _logger.LogWarning("Avatar too large for {PubKey}: {Size} bytes", publicKeyHex[..16], content.Length);
                return null;
            }

            await File.WriteAllBytesAsync(localPath, content);
            _logger.LogInformation("Downloaded avatar for {PubKey}: {Path} ({Size} bytes)", publicKeyHex[..16], localPath, content.Length);
            return localPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download avatar for {PubKey}", publicKeyHex[..16]);
            return null;
        }
    }

    public async Task AnnounceDeviceToSyncGroupAsync(string osName)
    {
        var syncChatId = await _storageService.GetSettingAsync("device_sync_chat_id");
        if (string.IsNullOrEmpty(syncChatId)) return;

        var slotId = _mlsService.GetLocalKeyPackageSlotId();
        var shortSlot = string.IsNullOrEmpty(slotId) ? "unknown" : slotId[..Math.Min(8, slotId.Length)];
        var content = $"[Device] {osName} · {shortSlot} online";

        try
        {
            await SendMessageAsync(syncChatId, content);
            _logger.LogInformation("AnnounceDevice: posted presence for {OS} device {ShortSlot}", osName, shortSlot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnnounceDevice: failed to post device announcement");
        }
    }

    public async Task RequestResyncAsync(string chatId)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("No current user");

        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new InvalidOperationException($"Chat {chatId} not found");
        if (chat.NostrGroupId == null)
            throw new InvalidOperationException("Chat has no NostrGroupId");

        var slotId = _mlsService.GetLocalKeyPackageSlotId();
        if (string.IsNullOrEmpty(slotId))
            throw new InvalidOperationException("No local KeyPackage slot ID — publish a KeyPackage first");

        _logger.LogInformation("RequestResync: posting resync request for chat {ChatId}", chatId);

        // No KP rotation needed — AutoPublishKeyPackageIfNeededAsync already runs after every
        // Welcome is processed, so a fresh KP is already on the relay for the other device to use.

        // Post structured request to DeviceSync group
        var syncChatId = await _storageService.GetSettingAsync("device_sync_chat_id");
        if (!string.IsNullOrEmpty(syncChatId))
        {
            var groupNostrIdHex = Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant();
            var payload = System.Text.Json.JsonSerializer.Serialize(
                new { slotId, groupNostrId = groupNostrIdHex });
            await SendMessageAsync(syncChatId, $"[ResyncRequest] {payload}");
            _logger.LogInformation("RequestResync: sent [ResyncRequest] to sync group for group {GroupId}",
                groupNostrIdHex[..Math.Min(16, groupNostrIdHex.Length)]);
        }

        chat.IsResyncPending = true;
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);
    }

    private async Task HandleResyncRequestAsync(string messageContent)
    {
        try
        {
            var jsonPart = messageContent["[ResyncRequest] ".Length..];
            using var doc = System.Text.Json.JsonDocument.Parse(jsonPart);
            var requestingSlotId = doc.RootElement.GetProperty("slotId").GetString();
            var groupNostrId = doc.RootElement.GetProperty("groupNostrId").GetString();

            if (string.IsNullOrEmpty(requestingSlotId) || string.IsNullOrEmpty(groupNostrId))
                return;

            // Skip if this is our own request
            var localSlotId = _mlsService.GetLocalKeyPackageSlotId();
            if (string.Equals(requestingSlotId, localSlotId, StringComparison.OrdinalIgnoreCase))
                return;

            _logger.LogInformation("HandleResyncRequest: received from slot {SlotId} for group {GroupId}",
                requestingSlotId[..Math.Min(8, requestingSlotId.Length)],
                groupNostrId[..Math.Min(16, groupNostrId.Length)]);

            // Find the affected group chat locally
            var allChats = await _storageService.GetAllChatsAsync();
            var affectedChat = allChats.FirstOrDefault(c =>
                c.NostrGroupId != null &&
                Convert.ToHexString(c.NostrGroupId).Equals(groupNostrId, StringComparison.OrdinalIgnoreCase));

            if (affectedChat == null)
            {
                _logger.LogWarning("HandleResyncRequest: no local chat found for group {GroupId}", groupNostrId);
                return;
            }

            // Re-invite our own identity — the MLS backend will send Welcomes to all our
            // active KeyPackages, including the freshly-rotated one from the requesting device.
            _logger.LogInformation("HandleResyncRequest: re-inviting own identity to chat {ChatId}", affectedChat.Id);
            await AddMemberAsync(affectedChat.Id, _currentUser!.PublicKeyHex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HandleResyncRequest: failed to process resync request");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _selfUpdateTimer?.Dispose();
        _kpRotationTimer?.Dispose();
        _eventSubscription?.Dispose();
        _newMessages.Dispose();
        _messageStatusUpdates.Dispose();
        _chatUpdates.Dispose();
        _newInvites.Dispose();
        _decryptionErrors.Dispose();
        _reactionUpdates.Dispose();
        _skippedInvites.Dispose();

        _disposed = true;
    }
}
