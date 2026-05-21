using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using Scramble.Core;
using Scramble.Core.Configuration;
using Scramble.Core.Crypto;
using Scramble.Core.Logging;
using Scramble.Core.Models;
using MarmotCs.Protocol.Mip00;
using MarmotCs.Protocol.Mip02;
using MarmotCs.Protocol.Nip44;

namespace Scramble.Core.Services;

public class NostrService : INostrService, IDisposable
{
    private readonly ILogger<NostrService> _logger;
    private readonly Subject<NostrConnectionStatus> _connectionStatus = new();
    private readonly Subject<NostrEventReceived> _events = new();
    private readonly Subject<MarmotWelcomeEvent> _welcomeMessages = new();
    private readonly Subject<MarmotGroupMessageEvent> _groupMessages = new();
    private readonly ConcurrentDictionary<string, IDisposable> _subscriptions = new();
    private readonly ConcurrentDictionary<string, byte> _connectedRelays = new();
    private readonly ConcurrentDictionary<string, NostrRelayConnection> _relayConnections = new();
    private readonly ConcurrentDictionary<string, IDisposable> _relayMessageSubscriptions = new();
    private readonly ConcurrentDictionary<string, byte> _subscribedGroupIds = new();
    private readonly ConcurrentDictionary<string, byte> _recentlyProcessedEventIds = new();
    private readonly ILogger<NostrRelayConnection> _connectionLogger = LoggingConfiguration.CreateLogger<NostrRelayConnection>();
    private readonly NostrConnectionProvider _connectionProvider = new();
    private readonly ConcurrentDictionary<string, PublishOkTracker> _pendingOkTrackers = new();

    /// <summary>
    /// Tracks OK responses from multiple relays for a single published event.
    /// Succeeds on first accept, fails only when all relays have rejected.
    /// </summary>
    private class PublishOkTracker
    {
        private readonly TaskCompletionSource<(bool accepted, string? reason)> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expectedRelays;
        private int _responseCount;
        private readonly List<string> _rejectionReasons = new();

        public PublishOkTracker(int expectedRelays)
        {
            _expectedRelays = expectedRelays;
        }

        public Task<(bool accepted, string? reason)> Task => _tcs.Task;

        public void OnAccepted()
        {
            // First accept wins — resolve immediately
            _tcs.TrySetResult((true, null));
        }

        public void OnRejected(string reason)
        {
            int count;
            lock (_rejectionReasons)
            {
                _rejectionReasons.Add(reason);
                count = Interlocked.Increment(ref _responseCount);
            }

            // Only fail if ALL relays have responded and none accepted
            if (count >= _expectedRelays)
            {
                string allReasons;
                lock (_rejectionReasons)
                {
                    allReasons = string.Join("; ", _rejectionReasons.Distinct());
                }
                _tcs.TrySetResult((false, allReasons));
            }
        }

        public void OnTimeout()
        {
            // If we haven't resolved yet (no accepts, not all rejected), fail with timeout
            string? reasons;
            lock (_rejectionReasons)
            {
                reasons = _rejectionReasons.Count > 0
                    ? $"timeout (partial rejections: {string.Join("; ", _rejectionReasons.Distinct())})"
                    : "timeout";
            }
            _tcs.TrySetResult((false, reasons));
        }
    }

    // Relays connected only for bot chat DM delivery/reception — excluded from group message broadcasts
    private readonly ConcurrentDictionary<string, byte> _botOnlyRelays = new();

    // Relays connected for outbox model (contacts' preferred relays) — excluded from group/KP broadcasts
    private readonly ConcurrentDictionary<string, byte> _outboxRelays = new();

    // Optional storage service for contact relay list caching
    private IStorageService? _storageService;
    private static readonly TimeSpan RelayListCacheTtl = TimeSpan.FromMinutes(30);

    // Per-relay rate limiting: tracks event count in the current sliding window
    private readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _relayEventRates = new();
    private const int MaxEventsPerMinute = 500;
    private static readonly TimeSpan RateWindowDuration = TimeSpan.FromMinutes(1);

    private bool IsBotOnlyRelay(string relayUrl)
        => _botOnlyRelays.ContainsKey(relayUrl.TrimEnd('/'));
    private const int RateLimitBackoffSeconds = 60;
    private const int MaxInitialConnectBackoffSeconds = 60;
    private DateTimeOffset? _groupMessagesSince;
    private DateTimeOffset? _welcomeMessagesSince;
    private string? _subscribedUserPubKey;
    private string? _subscribedUserPrivKey;

    public IReadOnlyList<string> ConnectedRelayUrls => _connectedRelays.Keys.ToList();
    public IReadOnlyList<string> ConfiguredRelayUrls => _relayConnections.Keys.ToList();
    private IExternalSigner? _externalSigner;
    private bool _disposed;

    public NostrService()
    {
        _logger = LoggingConfiguration.CreateLogger<NostrService>();
        _logger.LogDebug("NostrService initialized");
    }

    public IObservable<NostrConnectionStatus> ConnectionStatus => _connectionStatus.AsObservable();
    public IObservable<NostrEventReceived> Events => _events.AsObservable();
    public IObservable<MarmotWelcomeEvent> WelcomeMessages => _welcomeMessages.AsObservable();
    public IObservable<MarmotGroupMessageEvent> GroupMessages => _groupMessages.AsObservable();

    public void SetExternalSigner(IExternalSigner? signer)
    {
        _externalSigner = signer;
        _logger.LogInformation("External signer {Status}", signer?.IsConnected == true ? "set and connected" : "cleared");
    }

    public bool HasExternalSigner => _externalSigner != null;

    public void SetAuthCredentials(string? privateKeyHex)
    {
        _subscribedUserPrivKey = privateKeyHex;
        _logger.LogInformation("NIP-42 auth credentials {Status}", !string.IsNullOrEmpty(privateKeyHex) ? "set" : "cleared");
    }

    public void SetStorageService(IStorageService storageService)
    {
        _storageService = storageService;
        _logger.LogInformation("Storage service set for relay list caching");
    }

    private bool IsOutboxRelay(string relayUrl)
        => _outboxRelays.ContainsKey(relayUrl.TrimEnd('/'));

    /// <inheritdoc />
    public async Task<List<RelayPreference>> GetOrFetchRelayListAsync(string publicKeyHex)
    {
        if (_storageService != null)
        {
            try
            {
                var cached = await _storageService.GetContactRelayPreferencesAsync(publicKeyHex);
                if (cached.HasValue && DateTimeOffset.UtcNow - cached.Value.FetchedAt < RelayListCacheTtl)
                {
                    _logger.LogDebug("Using cached NIP-65 relay list for {PubKey} (fetched {Ago} ago)",
                        publicKeyHex[..8], DateTimeOffset.UtcNow - cached.Value.FetchedAt);
                    return cached.Value.Relays;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read cached relay preferences for {PubKey}", publicKeyHex[..8]);
            }
        }

        var relays = await FetchRelayListAsync(publicKeyHex);

        if (_storageService != null && relays.Count > 0)
        {
            try
            {
                await _storageService.SaveContactRelayPreferencesAsync(publicKeyHex, relays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cache relay preferences for {PubKey}", publicKeyHex[..8]);
            }
        }

        return relays;
    }

    /// <inheritdoc />
    public async Task<List<string>> GetOrFetchDmRelayListAsync(string publicKeyHex)
    {
        return await GetOrFetchRelayTagListAsync(10050, publicKeyHex);
    }

    /// <inheritdoc />
    public async Task<List<string>> GetOrFetchKeyPackageRelayListAsync(string publicKeyHex)
    {
        return await GetOrFetchRelayTagListAsync(10051, publicKeyHex);
    }

    /// <summary>
    /// Shared cache-first fetch for kind 10050 / 10051 relay lists.
    /// </summary>
    private async Task<List<string>> GetOrFetchRelayTagListAsync(int kind, string publicKeyHex)
    {
        if (_storageService != null)
        {
            try
            {
                var cached = await _storageService.GetContactRelayListAsync(publicKeyHex, kind);
                if (cached.HasValue && DateTimeOffset.UtcNow - cached.Value.FetchedAt < RelayListCacheTtl)
                {
                    _logger.LogDebug("Using cached kind {Kind} relay list for {PubKey} (fetched {Ago} ago)",
                        kind, publicKeyHex[..8], DateTimeOffset.UtcNow - cached.Value.FetchedAt);
                    return cached.Value.Urls;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read cached kind {Kind} relay list for {PubKey}", kind, publicKeyHex[..8]);
            }
        }

        var urls = kind == 10050
            ? await FetchDmRelayListAsync(publicKeyHex)
            : await FetchKeyPackageRelayListAsync(publicKeyHex);

        if (_storageService != null && urls.Count > 0)
        {
            try
            {
                await _storageService.SaveContactRelayListAsync(publicKeyHex, kind, urls);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cache kind {Kind} relay list for {PubKey}", kind, publicKeyHex[..8]);
            }
        }

        return urls;
    }

    public async Task ConnectAsync(string relayUrl)
    {
        await ConnectAsync(new[] { relayUrl });
    }

    public async Task ConnectAsync(IEnumerable<string> relayUrls)
    {
        var relayList = relayUrls.ToList();
        _logger.LogInformation("Connecting to {Count} relays: {Relays}", relayList.Count, string.Join(", ", relayList));

        // Promote any bot-only relays to normal if they appear in the user's relay list
        foreach (var url in relayList)
            _botOnlyRelays.TryRemove(url.TrimEnd('/'), out _);

        var connectionTasks = relayList.Select(ConnectToRelayAsync);
        await Task.WhenAll(connectionTasks);
    }

    public async Task ConnectBotRelaysAsync(IEnumerable<string> relayUrls)
    {
        var newRelays = relayUrls
            .Where(url => !_relayConnections.ContainsKey(url) &&
                          !_relayConnections.Keys.Any(k => string.Equals(k.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (newRelays.Count == 0) return;

        _logger.LogInformation("Connecting to {Count} bot-only relays: {Relays}", newRelays.Count, string.Join(", ", newRelays));

        foreach (var url in newRelays)
            _botOnlyRelays.TryAdd(url.TrimEnd('/'), 0);

        var connectionTasks = newRelays.Select(ConnectToRelayAsync);
        await Task.WhenAll(connectionTasks);
    }

    public async Task ConnectOutboxRelaysAsync(IEnumerable<string> relayUrls)
    {
        var newRelays = relayUrls
            .Where(url => !_relayConnections.ContainsKey(url) &&
                          !_relayConnections.Keys.Any(k => string.Equals(k.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (newRelays.Count == 0) return;

        _logger.LogInformation("Connecting to {Count} outbox relays: {Relays}", newRelays.Count, string.Join(", ", newRelays));

        foreach (var url in newRelays)
            _outboxRelays.TryAdd(url.TrimEnd('/'), 0);

        var connectionTasks = newRelays.Select(ConnectToRelayAsync);
        await Task.WhenAll(connectionTasks);
    }

    private async Task ConnectToRelayAsync(string relayUrl, int retryAttempt = 0)
    {
        // Skip if a previous retry already succeeded for this relay
        if (_relayConnections.ContainsKey(relayUrl) && _connectedRelays.ContainsKey(relayUrl))
        {
            _logger.LogDebug("Relay {RelayUrl} is already connected, skipping retry", relayUrl);
            return;
        }

        try
        {
            _logger.LogDebug("Attempting connection to relay: {RelayUrl} (attempt {Attempt})", relayUrl, retryAttempt + 1);

            var validationError = await ValidateRelayUrlAsync(relayUrl);
            if (validationError != null)
            {
                var isPrivateIpError = validationError.Contains("private", StringComparison.OrdinalIgnoreCase) ||
                                       validationError.Contains("reserved", StringComparison.OrdinalIgnoreCase);

                if (isPrivateIpError && ProfileConfiguration.AllowLocalRelays)
                {
                    _logger.LogWarning("Relay URL {RelayUrl} resolves to private/reserved IP — allowed by --allow-local-relays", relayUrl);
                }
                else
                {
                    _logger.LogWarning("Rejected relay URL {RelayUrl}: {Reason}", relayUrl, validationError);
                    _connectionStatus.OnNext(new NostrConnectionStatus
                    {
                        RelayUrl = relayUrl,
                        IsConnected = false,
                        Error = validationError
                    });

                    // Retry DNS resolution failures — network may not be ready yet
                    if (validationError.Contains("Cannot resolve hostname"))
                    {
                        ScheduleConnectionRetry(relayUrl, retryAttempt);
                    }
                    return;
                }
            }

            var connection = new NostrRelayConnection(relayUrl, _connectionLogger);
            await connection.ConnectAsync();

            _relayConnections[relayUrl] = connection;
            _connectionProvider.RegisterConnection(relayUrl, connection);
            _connectedRelays.TryAdd(relayUrl, 0);

            // Wire message stream → ProcessRelayMessageAsync
            var msgSub = connection.Messages.Subscribe(async message =>
            {
                try
                {
                    await ProcessRelayMessageAsync(relayUrl, message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing message from {RelayUrl}", relayUrl);
                }
            });

            // Forward connection status
            var statusSub = connection.ConnectionStatus.Subscribe(status =>
            {
                _connectionStatus.OnNext(status);

                if (status.IsConnected)
                {
                    _connectedRelays.TryAdd(relayUrl, 0);
                }
                else
                {
                    _connectedRelays.TryRemove(relayUrl, out _);
                }
            });

            // Store combined subscription for cleanup
            var combinedSub = new System.Reactive.Disposables.CompositeDisposable(msgSub, statusSub);
            _relayMessageSubscriptions[relayUrl] = combinedSub;

            // Emit connected status now that the subscription is wired
            // (the initial status from ConnectAsync fires before the subscription is set up)
            _connectionStatus.OnNext(new NostrConnectionStatus
            {
                RelayUrl = relayUrl,
                IsConnected = true
            });

            _logger.LogInformation("Successfully connected to relay: {RelayUrl}", relayUrl);

            // Register active subscriptions on this new connection
            await RegisterActiveSubscriptionsAsync(connection);
        }
        catch (WebSocketException ex) when (ex.Message.Contains("429"))
        {
            _logger.LogWarning("Rate-limited by relay {RelayUrl} (HTTP 429), backing off {Seconds}s",
                relayUrl, RateLimitBackoffSeconds);
            _connectionStatus.OnNext(new NostrConnectionStatus
            {
                RelayUrl = relayUrl,
                IsConnected = false,
                Error = "Rate-limited (429)"
            });
            ScheduleConnectionRetry(relayUrl, retryAttempt, RateLimitBackoffSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to relay: {RelayUrl}", relayUrl);
            _connectionStatus.OnNext(new NostrConnectionStatus
            {
                RelayUrl = relayUrl,
                IsConnected = false,
                Error = ex.Message
            });
            ScheduleConnectionRetry(relayUrl, retryAttempt);
        }
    }

    /// <summary>
    /// Schedules a fire-and-forget retry of <see cref="ConnectToRelayAsync"/> with exponential backoff.
    /// Used when a connection attempt fails (DNS, timeout, WebSocket error) so the app
    /// automatically recovers without requiring the user to manually reconnect.
    /// Retries indefinitely with backoff capped at <see cref="MaxInitialConnectBackoffSeconds"/>,
    /// matching the post-connection auto-reconnect behaviour in <see cref="NostrRelayConnection"/>.
    /// </summary>
    private void ScheduleConnectionRetry(string relayUrl, int currentAttempt, int? forcedBackoffSeconds = null)
    {
        var backoff = forcedBackoffSeconds
            ?? Math.Min((int)Math.Pow(2, currentAttempt + 1), MaxInitialConnectBackoffSeconds);
        _logger.LogInformation("Scheduling retry for {RelayUrl} in {Seconds}s (attempt {Attempt})",
            relayUrl, backoff, currentAttempt + 1);
        _ = RetryConnectionAfterDelayAsync(relayUrl, backoff, currentAttempt);
    }

    private async Task RetryConnectionAfterDelayAsync(string relayUrl, int backoffSeconds, int currentAttempt)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(backoffSeconds));
            await ConnectToRelayAsync(relayUrl, currentAttempt + 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relay connection retry failed for {RelayUrl} (attempt {Attempt})",
                relayUrl, currentAttempt + 1);
        }
    }

    /// <summary>
    /// Registers all active subscriptions (welcomes + group messages) on a newly connected relay.
    /// The connection auto-replays these on reconnect, so this is only needed for the initial registration.
    /// </summary>
    private async Task RegisterActiveSubscriptionsAsync(NostrRelayConnection connection)
    {
        try
        {
            // Register Welcome subscription (kind 1059 gift wrap)
            if (!string.IsNullOrEmpty(_subscribedUserPubKey) && connection.IsConnected)
            {
                var subId = $"welcome_{Guid.NewGuid():N}"[..16];
                var filter = new Dictionary<string, object>
                {
                    { "kinds", new[] { 1059 } },
                    { "#p", new[] { _subscribedUserPubKey } }
                };

                // NIP-59 gift wraps use randomized created_at timestamps for privacy.
                // A 'since' filter would miss new events whose random timestamp is before
                // the disconnect time. Rely on in-memory + DB deduplication instead.

                var filterJson = JsonSerializer.Serialize(filter);
                await connection.RegisterSubscriptionAsync(subId, filterJson);

                // Track in provider for H2 filter validation
                var parsedFilter = ParsedFilter.FromJson(filterJson);
                _connectionProvider.TrackSubscription(subId, parsedFilter, new[] { connection.RelayUrl });

                _logger.LogInformation("Registered Welcome subscription on {RelayUrl} (no since — NIP-59 randomized timestamps)",
                    connection.RelayUrl);
            }

            // Register Group subscription (kind 445)
            if (_subscribedGroupIds.Count > 0 && connection.IsConnected)
            {
                var subId = $"group_{Guid.NewGuid():N}"[..16];
                var filter = new Dictionary<string, object>
                {
                    { "kinds", new[] { 445 } },
                    { "#h", _subscribedGroupIds.Keys.ToArray() }
                };

                if (_groupMessagesSince.HasValue)
                {
                    filter["since"] = _groupMessagesSince.Value.ToUnixTimeSeconds();
                }

                var filterJson = JsonSerializer.Serialize(filter);
                await connection.RegisterSubscriptionAsync(subId, filterJson);

                // Track in provider for H2 filter validation
                var parsedFilter = ParsedFilter.FromJson(filterJson);
                _connectionProvider.TrackSubscription(subId, parsedFilter, new[] { connection.RelayUrl });

                _logger.LogInformation("Registered Group subscription ({Count} groups) on {RelayUrl}",
                    _subscribedGroupIds.Count, connection.RelayUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register subscriptions on {RelayUrl}", connection.RelayUrl);
        }
    }

    /// <summary>
    /// Queries a relay for events matching the given filter. Reuses an existing persistent
    /// connection if available; otherwise opens a temporary connection for the query.
    /// Returns raw event JSON strings (inner event objects).
    /// </summary>
    private async Task<List<string>> QueryRelayAsync(string relayUrl, string filterJson, TimeSpan? timeout = null)
    {
        var queryTimeout = timeout ?? TimeSpan.FromSeconds(10);

        // Prefer persistent connection if the relay is already connected
        if (_relayConnections.TryGetValue(relayUrl, out var connection) && connection.IsConnected)
        {
            _logger.LogDebug("QueryRelay: reusing persistent connection to {Relay}", relayUrl);
            return await connection.QueryAsync(filterJson, queryTimeout);
        }

        // Fall back to a temporary connection
        _logger.LogDebug("QueryRelay: opening temporary connection to {Relay}", relayUrl);
        await using var temp = NostrRelayConnection.CreateTemporary(relayUrl, _connectionLogger);
        await temp.ConnectAsync();
        return await temp.QueryAsync(filterJson, queryTimeout);
    }


    private async Task ProcessRelayMessageAsync(string relayUrl, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 1)
                return;

            var messageType = root[0].GetString();

            if (messageType == "EVENT" && root.GetArrayLength() >= 3)
            {
                // Per-relay rate limiting — drop events if relay is flooding
                if (IsRelayRateLimited(relayUrl))
                    return;

                var subId = root[1].GetString();
                var eventData = root[2];
                var nostrEvent = ParseNostrEvent(eventData, relayUrl);
                if (nostrEvent != null)
                {
                    // H2: Validate event against subscription filter — drop events that don't
                    // match the filter for their subscription ID (defends against malicious relays
                    // pushing arbitrary events)
                    if (!string.IsNullOrEmpty(subId) && !_connectionProvider.ValidateEvent(subId, nostrEvent))
                    {
                        return;
                    }

                    // Deduplicate events across relays — skip if already processed
                    if (!string.IsNullOrEmpty(nostrEvent.EventId) &&
                        !_recentlyProcessedEventIds.TryAdd(nostrEvent.EventId, 0))
                    {
                        _logger.LogTrace("Skipping duplicate event {EventId} from {RelayUrl}",
                            nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)], relayUrl);
                        return;
                    }

                    // Prevent unbounded growth — clear when cache gets large
                    if (_recentlyProcessedEventIds.Count > 10_000)
                    {
                        _logger.LogInformation("Clearing event dedup cache ({Count} entries)", _recentlyProcessedEventIds.Count);
                        _recentlyProcessedEventIds.Clear();
                    }

                    _logger.LogDebug("Received event kind {Kind} from {RelayUrl}", nostrEvent.Kind, relayUrl);
                    _events.OnNext(nostrEvent);

                    // Route Marmot-specific events
                    // Allow unwrapping when either local private key OR external signer is available
                    if (nostrEvent.Kind == 1059 &&
                        (!string.IsNullOrEmpty(_subscribedUserPrivKey) || _externalSigner != null))
                    {
                        // NIP-59 Gift Wrap — unwrap to find the inner rumor
                        var rumor = await UnwrapGiftWrapAsync(nostrEvent);
                        if (rumor != null)
                        {
                            if (rumor.Kind == 444)
                            {
                                _logger.LogInformation("Unwrapped gift wrap → kind 444 Welcome from {Sender}",
                                    rumor.PublicKey[..Math.Min(16, rumor.PublicKey.Length)]);
                                _events.OnNext(rumor); // Fire the unwrapped rumor as an event
                                ProcessWelcomeEvent(rumor);
                            }
                            else if (rumor.Kind == 14)
                            {
                                _logger.LogInformation("Unwrapped gift wrap → kind 14 DM from {Sender}",
                                    rumor.PublicKey[..Math.Min(16, rumor.PublicKey.Length)]);
                                _events.OnNext(rumor); // Route to MessageService via Events observable
                            }
                        }
                    }
                    else if (nostrEvent.Kind == 444)
                    {
                        // Legacy: raw kind 444 (not gift-wrapped) for backward compatibility
                        ProcessWelcomeEvent(nostrEvent);
                    }
                    else if (nostrEvent.Kind == 445)
                    {
                        ProcessGroupMessageEvent(nostrEvent);
                    }
                }
            }
            else if (messageType == "OK" && root.GetArrayLength() >= 3)
            {
                var eventId = root[1].GetString();
                var accepted = root[2].GetBoolean();
                var reason = root.GetArrayLength() >= 4 ? root[3].GetString() : null;
                if (accepted)
                {
                    _logger.LogDebug("Event {EventId} accepted by {RelayUrl}", eventId, relayUrl);
                    if (eventId != null && _pendingOkTrackers.TryGetValue(eventId, out var tracker))
                    {
                        tracker.OnAccepted();
                        _pendingOkTrackers.TryRemove(eventId, out _);
                    }
                }
                else
                {
                    _logger.LogWarning("Event {EventId} rejected by {RelayUrl}: {Reason}",
                        eventId, relayUrl, reason ?? "no reason given");
                    if (eventId != null && _pendingOkTrackers.TryGetValue(eventId, out var tracker))
                        tracker.OnRejected(reason ?? "rejected by relay");
                }
            }
            else if (messageType == "AUTH" && root.GetArrayLength() >= 2)
            {
                var challenge = root[1].GetString();
                _logger.LogInformation("NIP-42 AUTH challenge from {RelayUrl}: {Challenge}", relayUrl, challenge);
                if (!string.IsNullOrEmpty(challenge))
                {
                    await HandleAuthChallengeAsync(relayUrl, challenge);
                }
            }
            else if (messageType == "NOTICE")
            {
                var notice = root.GetArrayLength() >= 2 ? root[1].GetString() : "Unknown notice";
                _logger.LogWarning("Notice from {RelayUrl}: {Notice}", relayUrl, notice);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse message from {RelayUrl}", relayUrl);
        }
    }

    private async Task HandleAuthChallengeAsync(string relayUrl, string challenge)
    {
        try
        {
            if (!_relayConnections.TryGetValue(relayUrl, out var connection) || !connection.IsConnected)
            {
                _logger.LogWarning("NIP-42: cannot respond to AUTH — no open connection to {RelayUrl}", relayUrl);
                return;
            }

            string authEventMessage;

            if (!string.IsNullOrEmpty(_subscribedUserPrivKey))
            {
                // Sign locally
                var privateKeyBytes = Convert.FromHexString(_subscribedUserPrivKey);
                var publicKeyBytes = DerivePublicKey(privateKeyBytes);
                var publicKeyHex = Convert.ToHexString(publicKeyBytes).ToLowerInvariant();

                var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var tags = new List<List<string>>
                {
                    new() { "relay", relayUrl },
                    new() { "challenge", challenge }
                };

                var serializedForId = SerializeForEventId(publicKeyHex, createdAt, 22242, tags, "");
                var eventIdBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serializedForId));
                var eventId = Convert.ToHexString(eventIdBytes).ToLowerInvariant();
                var signatureHex = Convert.ToHexString(SignSchnorr(eventIdBytes, privateKeyBytes)).ToLowerInvariant();

                authEventMessage = SerializeEventMessage(eventId, publicKeyHex, createdAt, 22242, tags, "", signatureHex);
                // Wrap in ["AUTH", {...}] format
                // The SerializeEventMessage returns ["EVENT", {...}], we need ["AUTH", {...}]
                authEventMessage = authEventMessage.Replace("[\"EVENT\",", "[\"AUTH\",");
            }
            else if (_externalSigner != null)
            {
                // Sign via external signer (auto-reconnects if WebSocket dropped)
                _logger.LogInformation("NIP-42: using external signer for AUTH on {RelayUrl}", relayUrl);
                var unsignedEvent = new UnsignedNostrEvent
                {
                    Kind = 22242,
                    Content = "",
                    Tags = new List<List<string>>
                    {
                        new() { "relay", relayUrl },
                        new() { "challenge", challenge }
                    },
                    CreatedAt = DateTime.UtcNow
                };

                var signedEventJson = await _externalSigner.SignEventAsync(unsignedEvent);
                authEventMessage = $"[\"AUTH\",{signedEventJson}]";
            }
            else
            {
                _logger.LogWarning("NIP-42: cannot respond to AUTH — no private key or external signer available for {RelayUrl}", relayUrl);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(authEventMessage);
            await connection.SendAsync(bytes);
            _logger.LogInformation("NIP-42: sent AUTH response to {RelayUrl}", relayUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NIP-42: failed to handle AUTH challenge from {RelayUrl}", relayUrl);
        }
    }

    private void ProcessWelcomeEvent(NostrEventReceived nostrEvent)
    {
        try
        {
            // Extract recipient from p-tag
            var recipientPubKey = nostrEvent.Tags
                .FirstOrDefault(t => t.Count >= 2 && t[0] == "p")?[1];

            // Extract KeyPackage event ID from e-tag (MIP-02 requirement)
            var keyPackageEventId = nostrEvent.Tags
                .FirstOrDefault(t => t.Count >= 2 && t[0] == "e")?[1];

            // Extract group ID from h-tag if present (MIP-03 uses 'h' tag)
            var groupId = nostrEvent.Tags
                .FirstOrDefault(t => t.Count >= 2 && t[0] == "h")?[1];

            // Fallback to 'g' tag for backwards compatibility
            if (string.IsNullOrEmpty(groupId))
            {
                groupId = nostrEvent.Tags
                    .FirstOrDefault(t => t.Count >= 2 && t[0] == "g")?[1];
            }

            // Extract relay hints from relays tag
            var relaysTag = nostrEvent.Tags
                .FirstOrDefault(t => t.Count >= 2 && t[0] == "relays");
            var relayHints = relaysTag?.Skip(1).ToList() ?? new List<string>();

            var welcomeEvent = new MarmotWelcomeEvent
            {
                EventId = nostrEvent.EventId,
                SenderPublicKey = nostrEvent.PublicKey,
                RecipientPublicKey = recipientPubKey ?? "",
                WelcomeData = nostrEvent.Content,
                GroupId = groupId,
                KeyPackageEventId = keyPackageEventId,
                RelayHints = relayHints,
                CreatedAt = nostrEvent.CreatedAt,
                RelayUrl = nostrEvent.RelayUrl
            };

            _logger.LogInformation("Received Welcome message from {Sender} for {Recipient} (KeyPackage: {KpId})",
                welcomeEvent.SenderPublicKey[..Math.Min(16, welcomeEvent.SenderPublicKey.Length)] + "...",
                welcomeEvent.RecipientPublicKey[..Math.Min(16, welcomeEvent.RecipientPublicKey.Length)] + "...",
                keyPackageEventId?[..Math.Min(16, keyPackageEventId.Length)] ?? "none");

            _welcomeMessages.OnNext(welcomeEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Welcome event {EventId}", nostrEvent.EventId);
        }
    }

    private void ProcessGroupMessageEvent(NostrEventReceived nostrEvent)
    {
        try
        {
            // Extract group ID from h-tag per MIP-03 spec (no g-tag fallback)
            var groupId = nostrEvent.Tags
                .FirstOrDefault(t => t.Count >= 2 && t[0] == "h")?[1] ?? "";

            if (string.IsNullOrEmpty(groupId))
            {
                _logger.LogWarning("Ignoring kind 445 event {EventId}: missing required 'h' tag (MIP-03)",
                    nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
                return;
            }

            // Extract epoch from epoch tag if present
            ulong? epoch = null;
            var epochTag = nostrEvent.Tags.FirstOrDefault(t => t.Count >= 2 && t[0] == "epoch");
            if (epochTag != null && ulong.TryParse(epochTag[1], out var parsedEpoch))
            {
                epoch = parsedEpoch;
            }

            var groupMessageEvent = new MarmotGroupMessageEvent
            {
                EventId = nostrEvent.EventId,
                SenderPublicKey = nostrEvent.PublicKey,
                GroupId = groupId,
                EncryptedData = nostrEvent.Content,
                Epoch = epoch,
                CreatedAt = nostrEvent.CreatedAt,
                RelayUrl = nostrEvent.RelayUrl
            };

            _logger.LogDebug("Received Group message for group {GroupId} from {Sender}",
                groupId[..Math.Min(16, groupId.Length)] + "...",
                groupMessageEvent.SenderPublicKey[..Math.Min(16, groupMessageEvent.SenderPublicKey.Length)] + "...");

            _groupMessages.OnNext(groupMessageEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Group message event {EventId}", nostrEvent.EventId);
        }
    }

    private NostrEventReceived? ParseNostrEvent(JsonElement eventData, string relayUrl)
    {
        try
        {
            var kind = eventData.GetProperty("kind").GetInt32();
            var id = eventData.GetProperty("id").GetString() ?? "";
            var pubkey = eventData.GetProperty("pubkey").GetString() ?? "";
            var content = eventData.GetProperty("content").GetString() ?? "";
            var createdAt = eventData.GetProperty("created_at").GetInt64();
            var sig = eventData.TryGetProperty("sig", out var sigElement) ? sigElement.GetString() ?? "" : "";

            var tags = new List<List<string>>();
            if (eventData.TryGetProperty("tags", out var tagsElement))
            {
                if (tagsElement.GetArrayLength() > 1000)
                {
                    _logger.LogWarning("Rejected event with excessive tags ({Count}) from {RelayUrl}",
                        tagsElement.GetArrayLength(), relayUrl);
                    return null;
                }
                foreach (var tag in tagsElement.EnumerateArray())
                {
                    if (tag.ValueKind != JsonValueKind.Array || tag.GetArrayLength() == 0)
                        continue; // Skip malformed tags
                    var tagList = new List<string>();
                    foreach (var item in tag.EnumerateArray())
                    {
                        var val = item.GetString() ?? "";
                        if (val.Length > 100_000)
                        {
                            _logger.LogWarning("Rejected event with oversized tag value ({Len} chars) from {RelayUrl}",
                                val.Length, relayUrl);
                            return null;
                        }
                        tagList.Add(val);
                    }
                    tags.Add(tagList);
                }
            }

            // NIP-01: Verify event ID and signature before accepting
            if (!VerifyEventSignature(id, pubkey, createdAt, kind, tags, content, sig))
            {
                _logger.LogWarning(
                    "Rejected event with invalid signature from {RelayUrl}: id={EventId}, kind={Kind}, pubkey={PubKey}",
                    relayUrl, id[..Math.Min(16, id.Length)], kind, pubkey[..Math.Min(16, pubkey.Length)]);
                return null;
            }

            // Replay attack prevention: reject events with stale or future timestamps.
            // Exempt gift wraps (kind 1059 — randomized timestamps by NIP-59 design)
            // and replaceable events (kind 0, 3, 10002 — historical by nature).
            bool isGiftWrap = kind == 1059;
            bool isReplaceable = kind == 0 || kind == 3 || (kind >= 10000 && kind < 20000);
            if (!IsTimestampAcceptable(createdAt, isGiftWrap, isReplaceable))
            {
                _logger.LogDebug(
                    "Rejected event with stale/future timestamp from {RelayUrl}: id={EventId}, kind={Kind}, created_at={CreatedAt}",
                    relayUrl, id[..Math.Min(16, id.Length)], kind, createdAt);
                return null;
            }

            return new NostrEventReceived
            {
                Kind = kind,
                EventId = id,
                PublicKey = pubkey,
                Content = content,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime,
                Tags = tags,
                RelayUrl = relayUrl,
                Signature = sig
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Nostr event from {RelayUrl}", relayUrl);
            return null;
        }
    }

    /// <summary>
    /// Verifies a Nostr event's ID (SHA-256) and BIP-340 Schnorr signature per NIP-01.
    /// Returns true if valid, false if forged or malformed.
    /// </summary>
    internal static bool VerifyEventSignature(
        string eventId, string pubkey, long createdAt, int kind,
        List<List<string>> tags, string content, string sig)
    {
        // Validate field formats
        if (eventId.Length != 64 || pubkey.Length != 64 || sig.Length != 128)
            return false;

        byte[] eventIdBytes, pubkeyBytes, sigBytes;
        try
        {
            eventIdBytes = Convert.FromHexString(eventId);
            pubkeyBytes = Convert.FromHexString(pubkey);
            sigBytes = Convert.FromHexString(sig);
        }
        catch (FormatException)
        {
            return false;
        }

        // Step 1: Recompute event ID = SHA-256([0, pubkey, created_at, kind, tags, content])
        var serialized = SerializeForEventId(pubkey, createdAt, kind, tags, content);
        var computedIdBytes = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(serialized));

        if (!CryptographicOperations.FixedTimeEquals(computedIdBytes, eventIdBytes))
            return false;

        // Step 2: Verify BIP-340 Schnorr signature over the event ID
        try
        {
            var xOnlyPub = ECXOnlyPubKey.Create(pubkeyBytes);
            if (!SecpSchnorrSignature.TryCreate(sigBytes, out var schnorrSig) || schnorrSig is null)
                return false;
            return xOnlyPub.SigVerifyBIP340(schnorrSig, eventIdBytes);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether an event timestamp is within acceptable bounds.
    /// Prevents replay attacks from malicious relays sending stale events.
    /// </summary>
    /// <param name="createdAt">Unix timestamp from the event.</param>
    /// <param name="isGiftWrap">True for kind 1059 events (randomized timestamps per NIP-59).</param>
    /// <param name="isReplaceableEvent">True for kind 0, 3, 10000-19999 (historical by nature).</param>
    internal static bool IsTimestampAcceptable(long createdAt, bool isGiftWrap = false, bool isReplaceableEvent = false)
    {
        // Gift wraps use randomized timestamps (±2.5 days per NIP-59) — skip validation
        if (isGiftWrap) return true;

        // Replaceable events (metadata, contacts, relay lists) are historical — skip validation
        if (isReplaceableEvent) return true;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const long maxPastSeconds = 15 * 60;   // 15 minutes
        const long maxFutureSeconds = 5 * 60;  // 5 minutes

        if (createdAt < now - maxPastSeconds) return false;  // Too old
        if (createdAt > now + maxFutureSeconds) return false; // Too far in the future

        return true;
    }

    /// <summary>
    /// Checks if a relay is sending events too fast. Returns true if the relay should be throttled.
    /// Uses a simple sliding window: resets the counter when the window expires.
    /// </summary>
    private bool IsRelayRateLimited(string relayUrl)
    {
        var now = DateTime.UtcNow;
        var rate = _relayEventRates.AddOrUpdate(
            relayUrl,
            _ => (1, now),
            (_, existing) =>
            {
                if (now - existing.WindowStart > RateWindowDuration)
                    return (1, now); // New window
                return (existing.Count + 1, existing.WindowStart);
            });

        if (rate.Count > MaxEventsPerMinute)
        {
            if (rate.Count == MaxEventsPerMinute + 1) // Log once per window
            {
                _logger.LogWarning("Rate limiting relay {RelayUrl}: {Count} events in {Window}s, dropping events",
                    relayUrl, rate.Count, RateWindowDuration.TotalSeconds);
            }
            return true;
        }
        return false;
    }

    public async Task DisconnectAsync()
    {
        _logger.LogInformation("Disconnecting from all relays");

        // Save timestamp so reconnect subscriptions use 'since' filter
        _welcomeMessagesSince = DateTimeOffset.UtcNow;

        // Dispose Rx subscriptions
        foreach (var (_, sub) in _relayMessageSubscriptions)
        {
            sub.Dispose();
        }
        _relayMessageSubscriptions.Clear();

        // Dispose all relay connections (handles cancel, close, cleanup)
        foreach (var (relayUrl, connection) in _relayConnections)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing connection to {RelayUrl}", relayUrl);
            }

            // Emit disconnected status explicitly — the Rx forwarding subscription
            // was already disposed above, so the connection's own status event is lost
            _connectionStatus.OnNext(new NostrConnectionStatus
            {
                RelayUrl = relayUrl,
                IsConnected = false
            });
        }
        _relayConnections.Clear();
        _connectedRelays.Clear();
        // Preserve _recentlyProcessedEventIds across reconnects to avoid reprocessing
        // events that arrive again from relays. The 10K cap in ProcessRelayMessageAsync
        // prevents unbounded growth.
    }

    public async Task ReconnectRelayAsync(string relayUrl)
    {
        _logger.LogInformation("Reconnecting to relay: {RelayUrl}", relayUrl);
        await DisconnectSingleRelayAsync(relayUrl);
        await ConnectToRelayAsync(relayUrl);
    }

    public async Task DisconnectRelayAsync(string relayUrl)
    {
        _logger.LogInformation("Disconnecting from relay: {RelayUrl}", relayUrl);
        await DisconnectSingleRelayAsync(relayUrl);
        _connectionStatus.OnNext(new NostrConnectionStatus
        {
            RelayUrl = relayUrl,
            IsConnected = false,
            Removed = true
        });
    }

    private async Task DisconnectSingleRelayAsync(string relayUrl)
    {
        // Track disconnect time so reconnect subscriptions use 'since' filter
        _welcomeMessagesSince = DateTimeOffset.UtcNow;

        if (_relayMessageSubscriptions.TryRemove(relayUrl, out var sub))
        {
            sub.Dispose();
        }
        _connectionProvider.UnregisterConnection(relayUrl);
        if (_relayConnections.TryRemove(relayUrl, out var connection))
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing connection to {RelayUrl}", relayUrl);
            }
        }
        _connectedRelays.TryRemove(relayUrl, out _);
    }

    public (string privateKeyHex, string publicKeyHex, string nsec, string npub) GenerateKeyPair()
    {
        _logger.LogInformation("Generating new Nostr keypair");

        try
        {
            // Generate a random 32-byte private key
            var privateKeyBytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(privateKeyBytes);
            _logger.LogDebug("Generated random 32-byte private key");

            var privateKeyHex = Convert.ToHexString(privateKeyBytes).ToLowerInvariant();

            // Derive public key using secp256k1
            var publicKeyBytes = DerivePublicKey(privateKeyBytes);
            var publicKeyHex = Convert.ToHexString(publicKeyBytes).ToLowerInvariant();
            _logger.LogDebug("Derived public key using secp256k1");

            // Generate bech32 encoded versions (NIP-19)
            var nsec = Bech32.Encode("nsec", privateKeyBytes);
            var npub = Bech32.Encode("npub", publicKeyBytes);
            _logger.LogDebug("Encoded keys to bech32 format - nsec length: {NsecLen}, npub length: {NpubLen}", nsec.Length, npub.Length);

            _logger.LogInformation("Successfully generated keypair. Public key (npub): {Npub}...", npub[..12]);
            return (privateKeyHex, publicKeyHex, nsec, npub);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate keypair");
            throw;
        }
    }

    public (string privateKeyHex, string publicKeyHex, string nsec, string npub) ImportPrivateKey(string privateKey)
    {
        var keyFormat = privateKey.StartsWith("nsec") ? "nsec (bech32)" : "hex";
        _logger.LogInformation("Importing private key in {Format} format, length: {Length}", keyFormat, privateKey.Length);

        try
        {
            byte[] privateKeyBytes;

            if (privateKey.StartsWith("nsec"))
            {
                _logger.LogDebug("Decoding bech32 nsec key");
                privateKeyBytes = Bech32.Decode(privateKey, out var hrp);
                _logger.LogDebug("Decoded bech32 key with HRP: {Hrp}, resulting bytes: {ByteCount}", hrp, privateKeyBytes.Length);
            }
            else
            {
                _logger.LogDebug("Parsing hex private key");
                privateKeyBytes = Convert.FromHexString(privateKey);
                _logger.LogDebug("Parsed hex key, resulting bytes: {ByteCount}", privateKeyBytes.Length);
            }

            if (privateKeyBytes.Length != 32)
            {
                _logger.LogError("Invalid private key length: {Length} bytes (expected 32)", privateKeyBytes.Length);
                throw new ArgumentException($"Private key must be 32 bytes, got {privateKeyBytes.Length}");
            }

            var privateKeyHex = Convert.ToHexString(privateKeyBytes).ToLowerInvariant();

            _logger.LogDebug("Deriving public key from private key");
            var publicKeyBytes = DerivePublicKey(privateKeyBytes);
            var publicKeyHex = Convert.ToHexString(publicKeyBytes).ToLowerInvariant();

            var nsec = Bech32.Encode("nsec", privateKeyBytes);
            var npub = Bech32.Encode("npub", publicKeyBytes);

            _logger.LogInformation("Successfully imported private key. Public key (npub): {Npub}...", npub[..12]);
            return (privateKeyHex, publicKeyHex, nsec, npub);
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Failed to parse private key - invalid format");
            throw new ArgumentException($"Invalid private key format: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            _logger.LogError(ex, "Unexpected error importing private key");
            throw;
        }
    }

    public async Task SubscribeAsync(string subscriptionId, NostrFilter filter)
    {
        // TODO: Integrate with Blockcore nostr-client
        // Build REQ message and send to connected relays
        await Task.CompletedTask;
    }

    public async Task UnsubscribeAsync(string subscriptionId)
    {
        if (_subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            subscription.Dispose();
            _subscriptions.TryRemove(subscriptionId, out _);
        }
        await Task.CompletedTask;
    }

    public async Task SubscribeToWelcomesAsync(string publicKeyHex, string? privateKeyHex = null)
    {
        _logger.LogInformation("Subscribing to Welcome messages (kind 1059 gift wrap) for {PubKey}",
            publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");

        _subscribedUserPubKey = publicKeyHex;
        _subscribedUserPrivKey = privateKeyHex;

        var filter = new Dictionary<string, object>
        {
            { "kinds", new[] { 1059 } },
            { "#p", new[] { publicKeyHex } }
        };

        var filterJson = JsonSerializer.Serialize(filter);
        _logger.LogDebug("Sending Welcome subscription filter: {Filter}", filterJson);

        var parsedFilter = ParsedFilter.FromJson(filterJson);

        // Register on all connected relays (each connection tracks for auto-replay on reconnect)
        foreach (var (relayUrl, connection) in _relayConnections)
        {
            if (connection.IsConnected)
            {
                var subId = $"welcome_{Guid.NewGuid():N}"[..16];
                await connection.RegisterSubscriptionAsync(subId, filterJson);
                _connectionProvider.TrackSubscription(subId, parsedFilter, new[] { relayUrl });
                _logger.LogDebug("Registered Welcome subscription on {RelayUrl}", relayUrl);
            }
        }
    }

    public async Task SubscribeToGroupMessagesAsync(IEnumerable<string> groupIds, DateTimeOffset? since = null)
    {
        // Filter out invalid group IDs (max 64 hex chars = 32 bytes, typical for MLS group IDs)
        var groupList = groupIds
            .Where(id =>
            {
                if (id.Length <= 64) return true;
                _logger.LogWarning("Skipping oversized group ID ({Length} chars): {Preview}...",
                    id.Length, id[..Math.Min(32, id.Length)]);
                return false;
            })
            .ToList();

        if (groupList.Count == 0)
        {
            _logger.LogDebug("No groups to subscribe to");
            return;
        }

        _logger.LogInformation("Subscribing to Group messages for {Count} groups", groupList.Count);

        foreach (var groupId in groupList)
        {
            _subscribedGroupIds.TryAdd(groupId, 0);
        }

        if (since.HasValue)
        {
            _groupMessagesSince = since;
        }

        var filter = new Dictionary<string, object>
        {
            { "kinds", new[] { 445 } },
            { "#h", groupList.ToArray() }
        };

        if (since.HasValue)
        {
            filter["since"] = since.Value.ToUnixTimeSeconds();
        }

        var filterJson = JsonSerializer.Serialize(filter);
        _logger.LogDebug("Sending Group subscription filter: {Filter}", filterJson);

        var parsedFilter = ParsedFilter.FromJson(filterJson);

        // Register on all connected relays (skip bot-only and outbox relays — group subs don't belong there)
        foreach (var (relayUrl, connection) in _relayConnections)
        {
            if (IsBotOnlyRelay(relayUrl) || IsOutboxRelay(relayUrl)) continue;
            if (connection.IsConnected)
            {
                var subId = $"group_{Guid.NewGuid():N}"[..16];
                await connection.RegisterSubscriptionAsync(subId, filterJson);
                _connectionProvider.TrackSubscription(subId, parsedFilter, new[] { relayUrl });
                _logger.LogDebug("Registered Group subscription on {RelayUrl}", relayUrl);
            }
        }
    }

    public async Task<string> PublishKeyPackageAsync(byte[] keyPackageData, string? privateKeyHex, List<List<string>>? mdkTags = null)
    {
        if (mdkTags == null || mdkTags.Count == 0)
        {
            _logger.LogWarning("No MDK tags provided for KeyPackage — MDK should always provide tags");
        }

        // Use MDK-provided tags directly — marmot-cs now produces the correct MIP-00 format.
        // Only filter out the NIP-70 "-" tag (protected event marker) which relays reject without NIP-42 auth,
        // and populate empty relay tags with connected relay URLs.
        var relayUrls = _connectedRelays.Count > 0
            ? _connectedRelays.Keys.ToList()
            : NostrConstants.DefaultRelays.ToList();

        var tags = (mdkTags ?? new List<List<string>>())
            .Where(t => !(t.Count == 1 && t[0] == "-"))
            .Select(t =>
            {
                // Populate empty relay tags with connected relay URLs
                if (t.Count >= 1 && t[0] == "relays" && t.Count <= 1)
                {
                    var withRelays = new List<string>(t);
                    withRelays.AddRange(relayUrls);
                    return withRelays;
                }
                return t;
            })
            .ToList();

        _logger.LogInformation("Publishing KeyPackage with {Count} tags", tags.Count);
        foreach (var tag in tags)
        {
            _logger.LogDebug("KP Tag: [{Tags}]", string.Join(", ", tag));
        }

        var eventId = await PublishEventAsync(30443, Convert.ToBase64String(keyPackageData), tags, privateKeyHex);

        if (!LastPublishOkResult.accepted)
        {
            throw new PublishUnconfirmedException(eventId, 30443, LastPublishOkResult.reason);
        }

        return eventId;
    }

    public async Task<string> PublishWelcomeAsync(byte[] welcomeData, string recipientPublicKey, string? privateKeyHex, string keyPackageEventId)
    {
        // Create kind 444 Welcome rumor (unsigned) wrapped in NIP-59 Gift Wrap per MIP-02
        var relayUrls = _connectedRelays.Count > 0
            ? _connectedRelays.Keys.ToList()
            : NostrConstants.DefaultRelays.ToList();

        // Discover recipient's preferred DM relays (kind 10050), fall back to NIP-65 read relays
        try
        {
            var dmRelays = await GetOrFetchDmRelayListAsync(recipientPublicKey);
            if (dmRelays.Count > 0)
            {
                _logger.LogInformation("Using {Count} kind 10050 DM relays for Welcome delivery", dmRelays.Count);
                foreach (var dmRelay in dmRelays.Where(r => !_connectedRelays.ContainsKey(r)))
                {
                    try { await ConnectAsync(dmRelay); }
                    catch { /* best-effort */ }
                }
                relayUrls = relayUrls.Union(dmRelays).Distinct().ToList();
            }
            else
            {
                // Fall back to NIP-65 read relays
                var recipientRelays = await GetOrFetchRelayListAsync(recipientPublicKey);
                var readRelays = recipientRelays
                    .Where(r => r.Usage == RelayUsage.Read || r.Usage == RelayUsage.Both)
                    .Select(r => r.Url)
                    .ToList();
                if (readRelays.Count > 0)
                {
                    _logger.LogInformation("Adding {Count} NIP-65 read relays for Welcome delivery", readRelays.Count);
                    foreach (var readRelay in readRelays.Where(r => !_connectedRelays.ContainsKey(r)))
                    {
                        try { await ConnectAsync(readRelay); }
                        catch { /* best-effort */ }
                    }
                    relayUrls = relayUrls.Union(readRelays).Distinct().ToList();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover recipient relays for Welcome delivery");
        }

        // Build the unsigned kind 444 rumor tags via MIP-02 protocol library
        var (_, mip02Tags) = WelcomeEventBuilder.BuildWelcomeEvent(
            welcomeData, keyPackageEventId, relayUrls.ToArray());

        // Convert string[][] to List<List<string>> and add p-tag for recipient routing
        var rumorTags = mip02Tags.Select(t => t.ToList()).ToList();
        rumorTags.Insert(0, new List<string> { "p", recipientPublicKey });

        // Determine sender pubkey
        string? senderPrivateKeyHex = null;
        string senderPublicKeyHex;
        if (!string.IsNullOrEmpty(privateKeyHex))
        {
            senderPrivateKeyHex = privateKeyHex;
            senderPublicKeyHex = Convert.ToHexString(DerivePublicKey(Convert.FromHexString(privateKeyHex))).ToLowerInvariant();
        }
        else if (_externalSigner != null)
        {
            senderPublicKeyHex = _externalSigner.PublicKeyHex
                ?? throw new InvalidOperationException("External signer connected but public key not available.");
            _logger.LogInformation("Using external signer for Welcome gift wrap creation");
        }
        else
        {
            throw new InvalidOperationException("Cannot publish Welcome: no private key and no external signer connected.");
        }

        // Create gift-wrapped event
        var giftWrapMessage = await CreateGiftWrapAsync(
            kind: 444,
            content: Convert.ToBase64String(welcomeData),
            rumorTags: rumorTags,
            senderPrivateKeyHex: senderPrivateKeyHex,
            senderPublicKeyHex: senderPublicKeyHex,
            recipientPublicKeyHex: recipientPublicKey);

        _logger.LogInformation("Publishing NIP-59 gift-wrapped Welcome (kind 1059) for {Recipient}",
            recipientPublicKey[..Math.Min(16, recipientPublicKey.Length)]);

        // Publish the kind 1059 gift wrap to all relays (skip bot-only — MLS welcomes don't belong there)
        var eventBytes = Encoding.UTF8.GetBytes(giftWrapMessage.EventMessage);
        var targetRelays = new List<(string url, NostrRelayConnection conn)>();
        foreach (var (relayUrl, connection) in _relayConnections)
        {
            if (IsBotOnlyRelay(relayUrl)) continue;
            if (connection.IsConnected)
                targetRelays.Add((relayUrl, connection));
        }

        if (targetRelays.Count > 0)
        {
            var tracker = new PublishOkTracker(targetRelays.Count);
            _pendingOkTrackers[giftWrapMessage.EventId] = tracker;

            foreach (var (relayUrl, connection) in targetRelays)
            {
                try
                {
                    await connection.SendAsync(eventBytes);
                    _logger.LogDebug("Sent gift-wrapped Welcome to {RelayUrl}", relayUrl);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send gift-wrapped Welcome to {RelayUrl}", relayUrl);
                }
            }

            // Wait for first accept or all rejections (5s timeout)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            cts.Token.Register(() =>
            {
                tracker.OnTimeout();
                _pendingOkTrackers.TryRemove(giftWrapMessage.EventId, out _);
            });

            var okResult = await tracker.Task;
            _pendingOkTrackers.TryRemove(giftWrapMessage.EventId, out _);
            LastPublishOkResult = okResult;

            if (!okResult.accepted)
            {
                throw new PublishUnconfirmedException(giftWrapMessage.EventId, 1059, okResult.reason);
            }
        }

        return giftWrapMessage.EventId;
    }

    /// <summary>
    /// Publish a NIP-59 gift-wrapped event (rumor → seal → gift wrap) to all connected relays.
    /// Used for NIP-17 DMs (kind 14 rumor) and other gift-wrapped protocols.
    /// </summary>
    public async Task<string> PublishGiftWrapAsync(
        int rumorKind, string content, List<List<string>> rumorTags,
        string? senderPrivateKeyHex, string senderPublicKeyHex,
        string recipientPublicKeyHex, List<string>? targetRelayUrls = null)
    {
        var giftWrapMessage = await CreateGiftWrapAsync(
            kind: rumorKind,
            content: content,
            rumorTags: rumorTags,
            senderPrivateKeyHex: senderPrivateKeyHex,
            senderPublicKeyHex: senderPublicKeyHex,
            recipientPublicKeyHex: recipientPublicKeyHex);

        // Auto-discover recipient's DM relays when no target relays are specified
        if (targetRelayUrls == null || targetRelayUrls.Count == 0)
        {
            try
            {
                var dmRelays = await GetOrFetchDmRelayListAsync(recipientPublicKeyHex);
                if (dmRelays.Count > 0)
                {
                    _logger.LogInformation("Auto-discovered {Count} DM relays for gift wrap recipient {Recipient}",
                        dmRelays.Count, recipientPublicKeyHex[..Math.Min(8, recipientPublicKeyHex.Length)]);
                    targetRelayUrls = dmRelays;
                }
                else
                {
                    // Fall back to NIP-65 read relays
                    var recipientRelays = await GetOrFetchRelayListAsync(recipientPublicKeyHex);
                    var readRelays = recipientRelays
                        .Where(r => r.Usage == RelayUsage.Read || r.Usage == RelayUsage.Both)
                        .Select(r => r.Url)
                        .ToList();
                    if (readRelays.Count > 0)
                    {
                        _logger.LogInformation("Using {Count} NIP-65 read relays for gift wrap recipient {Recipient}",
                            readRelays.Count, recipientPublicKeyHex[..Math.Min(8, recipientPublicKeyHex.Length)]);
                        targetRelayUrls = readRelays;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-discover relays for gift wrap recipient, broadcasting to all connected relays");
            }
        }

        _logger.LogInformation("Publishing NIP-59 gift-wrapped kind {Kind} (kind 1059) for {Recipient} to {Target}",
            rumorKind, recipientPublicKeyHex[..Math.Min(16, recipientPublicKeyHex.Length)],
            targetRelayUrls != null ? string.Join(", ", targetRelayUrls) : "all relays");

        var eventBytes = Encoding.UTF8.GetBytes(giftWrapMessage.EventMessage);

        // Track which target relays we've sent to so we know which ones need ad-hoc connections
        var sentToRelays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (relayUrl, connection) in _relayConnections)
        {
            // If target relays specified, only send to those
            if (targetRelayUrls != null && !targetRelayUrls.Any(t =>
                string.Equals(t.TrimEnd('/'), relayUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                continue;

            if (connection.IsConnected)
            {
                try
                {
                    await connection.SendAsync(eventBytes);
                    sentToRelays.Add(relayUrl.TrimEnd('/'));
                    _logger.LogDebug("Sent gift-wrapped kind {Kind} to {RelayUrl}", rumorKind, relayUrl);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send gift-wrapped kind {Kind} to {RelayUrl}", rumorKind, relayUrl);
                }
            }
        }

        // For target relays that aren't in _relayConnections, open a temporary connection and send
        string? lastError = null;
        if (targetRelayUrls != null)
        {
            foreach (var targetUrl in targetRelayUrls)
            {
                if (sentToRelays.Contains(targetUrl.TrimEnd('/')))
                    continue;

                try
                {
                    _logger.LogInformation("Opening temporary connection to {RelayUrl} for gift wrap delivery", targetUrl);
                    using var tempWs = new ClientWebSocket();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await tempWs.ConnectAsync(new Uri(targetUrl), cts.Token);

                    await tempWs.SendAsync(new ArraySegment<byte>(eventBytes),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                    _logger.LogInformation("Sent gift-wrapped kind {Kind} to temporary relay {RelayUrl}", rumorKind, targetUrl);

                    // Read relay OK response
                    var buffer = new byte[4096];
                    var result = await tempWs.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    var response = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _logger.LogDebug("Relay response from {RelayUrl}: {Response}", targetUrl, response);

                    // Parse ["OK", "<event_id>", true/false, "<message>"]
                    using var doc = System.Text.Json.JsonDocument.Parse(response);
                    var arr = doc.RootElement;
                    if (arr.GetArrayLength() >= 3 && arr[0].GetString() == "OK")
                    {
                        var accepted = arr[2].GetBoolean();
                        var msg = arr.GetArrayLength() >= 4 ? arr[3].GetString() : "";
                        if (!accepted)
                        {
                            lastError = $"Relay {targetUrl} rejected event: {msg}";
                            _logger.LogWarning("Relay {RelayUrl} rejected gift wrap: {Message}", targetUrl, msg);
                            await tempWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                            continue;
                        }
                    }

                    sentToRelays.Add(targetUrl.TrimEnd('/'));
                    await tempWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    lastError = $"Relay {targetUrl}: {ex.Message}";
                    _logger.LogWarning(ex, "Failed to send gift wrap to relay {RelayUrl}", targetUrl);
                }
            }
        }

        if (sentToRelays.Count == 0)
        {
            throw new InvalidOperationException(lastError ?? "Failed to deliver event to any relay");
        }

        return giftWrapMessage.EventId;
    }

    /// <inheritdoc />
    public async Task PublishDeletionAsync(string eventId, string? reason = null)
    {
        _logger.LogInformation("Publishing NIP-09 deletion for event {EventId}", eventId);

        var tags = new List<List<string>> { new() { "e", eventId } };
        var content = reason ?? "";

        await PublishEventAsync(5, content, tags, _subscribedUserPrivKey);
    }

    /// <summary>
    /// Creates a NIP-59 Gift Wrap: Rumor → Seal (kind 13) → Gift Wrap (kind 1059).
    /// When senderPrivateKeyHex is null, delegates NIP-44 encrypt and seal signing to the external signer.
    /// The outer gift wrap always uses an ephemeral key (per NIP-59 spec).
    /// </summary>
    private async Task<(string EventMessage, string EventId)> CreateGiftWrapAsync(
        int kind, string content, List<List<string>> rumorTags,
        string? senderPrivateKeyHex, string senderPublicKeyHex,
        string recipientPublicKeyHex)
    {
        // 1. Create unsigned rumor
        var rumorCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rumorJson = SerializeRumor(senderPublicKeyHex, rumorCreatedAt, kind, rumorTags, content);

        // 2. Create Seal (kind 13): encrypt rumor with NIP-44, sign with sender key
        string sealContent;
        string sealJson;
        var sealCreatedAt = RandomizeTimestamp(rumorCreatedAt);
        var sealTags = new List<List<string>>();

        if (!string.IsNullOrEmpty(senderPrivateKeyHex))
        {
            // Local key path: encrypt and sign locally
            var sealConvKey = Nip44Encryption.DeriveConversationKey(
                Convert.FromHexString(senderPrivateKeyHex), Convert.FromHexString(recipientPublicKeyHex));
            sealContent = Nip44Encryption.Encrypt(rumorJson, sealConvKey);

            var sealSerializedForId = SerializeForEventId(senderPublicKeyHex, sealCreatedAt, 13, sealTags, sealContent);
            var sealIdBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sealSerializedForId));
            var sealId = Convert.ToHexString(sealIdBytes).ToLowerInvariant();
            var sealSig = Convert.ToHexString(SignSchnorr(sealIdBytes, Convert.FromHexString(senderPrivateKeyHex))).ToLowerInvariant();
            sealJson = SerializeEventJson(sealId, senderPublicKeyHex, sealCreatedAt, 13, sealTags, sealContent, sealSig);

            _logger.LogDebug("Created seal locally: id={SealId}", sealId[..16]);
        }
        else if (_externalSigner != null)
        {
            // External signer path: delegate NIP-44 encrypt and event signing
            _logger.LogDebug("Creating seal via external signer");
            sealContent = await _externalSigner.Nip44EncryptAsync(rumorJson, recipientPublicKeyHex);

            var unsignedSeal = new UnsignedNostrEvent
            {
                Kind = 13,
                Content = sealContent,
                Tags = sealTags,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(sealCreatedAt).UtcDateTime
            };
            sealJson = await _externalSigner.SignEventAsync(unsignedSeal);

            _logger.LogDebug("Created seal via external signer");
        }
        else
        {
            throw new InvalidOperationException(
                "Cannot create gift wrap: no private key and no external signer connected.");
        }

        // 3. Create Gift Wrap (kind 1059): encrypt seal with NIP-44 using ephemeral key (always local per NIP-59)
        var (ephemeralPrivHex, ephemeralPubHex, _, _) = GenerateKeyPair();
        var giftConvKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(ephemeralPrivHex), Convert.FromHexString(recipientPublicKeyHex));
        var giftContent = Nip44Encryption.Encrypt(sealJson, giftConvKey);
        var giftCreatedAt = RandomizeTimestamp(rumorCreatedAt);
        var giftTags = new List<List<string>>
        {
            new() { "p", recipientPublicKeyHex }
        };

        var giftSerializedForId = SerializeForEventId(ephemeralPubHex, giftCreatedAt, 1059, giftTags, giftContent);
        var giftIdBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(giftSerializedForId));
        var giftId = Convert.ToHexString(giftIdBytes).ToLowerInvariant();
        var giftSig = Convert.ToHexString(SignSchnorr(giftIdBytes, Convert.FromHexString(ephemeralPrivHex))).ToLowerInvariant();

        var eventMessage = SerializeEventMessage(giftId, ephemeralPubHex, giftCreatedAt, 1059, giftTags, giftContent, giftSig);

        _logger.LogDebug("Created gift wrap: rumor kind={Kind}, wrap={GiftId}", kind, giftId[..16]);

        return (eventMessage, giftId);
    }

    /// <summary>
    /// Unwraps a NIP-59 Gift Wrap event into the original rumor.
    /// Uses local private key when available, otherwise delegates NIP-44 decrypt to the external signer.
    /// Returns the parsed rumor as a NostrEventReceived, or null if unwrapping fails.
    /// </summary>
    private async Task<NostrEventReceived?> UnwrapGiftWrapAsync(NostrEventReceived giftWrapEvent)
    {
        try
        {
            string sealJson;
            string rumorJson;

            if (!string.IsNullOrEmpty(_subscribedUserPrivKey))
            {
                // Local key path: decrypt both layers locally
                var unwrapConvKey = Nip44Encryption.DeriveConversationKey(
                    Convert.FromHexString(_subscribedUserPrivKey), Convert.FromHexString(giftWrapEvent.PublicKey));
                sealJson = Nip44Encryption.Decrypt(giftWrapEvent.Content, unwrapConvKey);

                using var sealDoc = JsonDocument.Parse(sealJson);
                var seal = sealDoc.RootElement;
                var sealPubkey = seal.GetProperty("pubkey").GetString() ?? "";
                var sealContent = seal.GetProperty("content").GetString() ?? "";

                var sealConvKey2 = Nip44Encryption.DeriveConversationKey(
                    Convert.FromHexString(_subscribedUserPrivKey), Convert.FromHexString(sealPubkey));
                rumorJson = Nip44Encryption.Decrypt(sealContent, sealConvKey2);
            }
            else if (_externalSigner != null)
            {
                // External signer path: delegate NIP-44 decrypt
                _logger.LogDebug("Unwrapping gift wrap via external signer");

                // 1. Decrypt gift wrap content → Seal JSON (counterparty is the ephemeral GW pubkey)
                sealJson = await _externalSigner.Nip44DecryptAsync(giftWrapEvent.Content, giftWrapEvent.PublicKey);

                using var sealDoc = JsonDocument.Parse(sealJson);
                var seal = sealDoc.RootElement;
                var sealPubkey = seal.GetProperty("pubkey").GetString() ?? "";
                var sealContent = seal.GetProperty("content").GetString() ?? "";

                // 2. Decrypt seal content → Rumor JSON (counterparty is the seal sender)
                rumorJson = await _externalSigner.Nip44DecryptAsync(sealContent, sealPubkey);
            }
            else
            {
                _logger.LogWarning("Cannot unwrap gift wrap: no private key and no external signer");
                return null;
            }

            // 3. Parse rumor into NostrEventReceived
            using var rumorDoc = JsonDocument.Parse(rumorJson);
            var rumor = rumorDoc.RootElement;

            var rumorKind = rumor.GetProperty("kind").GetInt32();
            var rumorContent = rumor.GetProperty("content").GetString() ?? "";
            var rumorPubkey = rumor.GetProperty("pubkey").GetString() ?? "";
            var rumorCreatedAt = rumor.GetProperty("created_at").GetInt64();

            var tags = new List<List<string>>();
            if (rumor.TryGetProperty("tags", out var tagsElement))
            {
                foreach (var tag in tagsElement.EnumerateArray())
                {
                    var tagList = new List<string>();
                    foreach (var item in tag.EnumerateArray())
                        tagList.Add(item.GetString() ?? "");
                    tags.Add(tagList);
                }
            }

            // Use the rumor's own id if present, otherwise fall back to the gift wrap event ID
            var rumorId = rumor.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            return new NostrEventReceived
            {
                Kind = rumorKind,
                EventId = !string.IsNullOrEmpty(rumorId) ? rumorId : giftWrapEvent.EventId,
                PublicKey = rumorPubkey,
                Content = rumorContent,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(rumorCreatedAt).UtcDateTime,
                Tags = tags,
                RelayUrl = giftWrapEvent.RelayUrl
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to unwrap gift wrap event {EventId}: {ExceptionType}",
                giftWrapEvent.EventId[..Math.Min(16, giftWrapEvent.EventId.Length)], ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Serializes an unsigned rumor event to JSON (no sig field).
    /// </summary>
    private string SerializeRumor(string pubkey, long createdAt, int kind, List<List<string>> tags, string content)
    {
        // Compute the event id (SHA-256 of NIP-01 canonical serialization).
        // Rumors are unsigned (no sig) but MUST include id per NIP-59 — the web client
        // uses rumor.id as a storage key and route parameter.
        var serialized = SerializeForEventId(pubkey, createdAt, kind, tags, content);
        var idBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        var idHex = Convert.ToHexString(idBytes).ToLowerInvariant();

        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteString("id", idHex);
        w.WriteString("pubkey", pubkey);
        w.WriteNumber("created_at", createdAt);
        w.WriteNumber("kind", kind);
        w.WritePropertyName("tags");
        w.WriteStartArray();
        foreach (var tag in tags)
        {
            w.WriteStartArray();
            foreach (var item in tag)
                w.WriteStringValue(item);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteString("content", content);
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Serializes a signed event to JSON (with id, pubkey, sig, etc).
    /// </summary>
    private string SerializeEventJson(string id, string pubkey, long createdAt, int kind,
        List<List<string>> tags, string content, string sig)
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("pubkey", pubkey);
        w.WriteNumber("created_at", createdAt);
        w.WriteNumber("kind", kind);
        w.WritePropertyName("tags");
        w.WriteStartArray();
        foreach (var tag in tags)
        {
            w.WriteStartArray();
            foreach (var item in tag)
                w.WriteStringValue(item);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteString("content", content);
        w.WriteString("sig", sig);
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Randomizes timestamp for NIP-59 unlinkability.
    /// Only offsets into the past (0 to -2 days) to avoid relay rejection
    /// for future timestamps ("created_at too late").
    /// </summary>
    /// <summary>
    /// Randomizes a timestamp for NIP-59 unlinkability. Uses two independent uniform draws
    /// summed together (triangular distribution) over a 5-day window to make correlation harder
    /// than a single uniform draw.
    /// </summary>
    private static long RandomizeTimestamp(long baseTimestamp)
    {
        var halfWindow = (int)(2.5 * 24 * 60 * 60); // 2.5 days in seconds
        var offset1 = RandomNumberGenerator.GetInt32(-halfWindow, 0);
        var offset2 = RandomNumberGenerator.GetInt32(-halfWindow, 0);
        return baseTimestamp + offset1 + offset2;
    }

    public async Task<string> PublishCommitAsync(byte[] commitData, string groupId, string? privateKeyHex)
    {
        _logger.LogInformation("PublishCommitAsync: {Len} bytes for group {GroupId}, hasPrivKey={HasKey}, first4={First4}",
            commitData.Length, groupId[..Math.Min(16, groupId.Length)],
            !string.IsNullOrEmpty(privateKeyHex),
            Convert.ToHexString(commitData[..Math.Min(4, commitData.Length)]).ToLowerInvariant());

        // Create kind 445 event for commit/evolution messages (MIP-03)
        var tags = new List<List<string>>
        {
            new() { "h", groupId },
            new() { "encoding", "base64" }
        };

        var eventId = await PublishEventAsync(445, Convert.ToBase64String(commitData), tags, privateKeyHex);

        if (!LastPublishOkResult.accepted)
        {
            throw new PublishUnconfirmedException(eventId, 445, LastPublishOkResult.reason);
        }

        _logger.LogInformation("PublishCommitAsync: published event {EventId}", eventId);
        return eventId;
    }

    public async Task<string> PublishGroupMessageAsync(byte[] encryptedData, string groupId, string? privateKeyHex)
    {
        // Create kind 445 event (MIP-03) — h + encoding tags per spec
        var tags = new List<List<string>>
        {
            new() { "h", groupId },
            new() { "encoding", "base64" }
        };
        var eventId = await PublishEventAsync(445, Convert.ToBase64String(encryptedData), tags, privateKeyHex);

        if (!LastPublishOkResult.accepted)
        {
            throw new PublishUnconfirmedException(eventId, 445, LastPublishOkResult.reason);
        }

        return eventId;
    }

    public async Task<string> PublishRawEventJsonAsync(byte[] eventJsonBytes)
    {
        var eventJsonStr = Encoding.UTF8.GetString(eventJsonBytes);

        // Parse the event ID and kind from the JSON
        using var doc = JsonDocument.Parse(eventJsonStr);
        var eventId = doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Pre-built event JSON missing 'id' field");
        var kind = doc.RootElement.TryGetProperty("kind", out var kindProp) ? kindProp.GetInt32() : -1;

        _logger.LogInformation("Publishing pre-built event {EventId} kind {Kind} to {Count} relays",
            eventId[..Math.Min(16, eventId.Length)], kind, _relayConnections.Count);

        // Wrap in Nostr EVENT message: ["EVENT", {event}]
        var eventMessage = $"[\"EVENT\",{eventJsonStr}]";
        var messageBytes = Encoding.UTF8.GetBytes(eventMessage);

        // Count target relays and register tracker BEFORE sending
        var targetRelays = new List<(string url, NostrRelayConnection conn)>();
        foreach (var (relayUrl, connection) in _relayConnections)
        {
            if (IsBotOnlyRelay(relayUrl) || IsOutboxRelay(relayUrl)) continue;
            if (connection.IsConnected)
                targetRelays.Add((relayUrl, connection));
        }

        if (targetRelays.Count == 0)
        {
            _logger.LogWarning("Pre-built event {EventId}: no connected relays to publish to", eventId);
            throw new InvalidOperationException("No connected relays available. Check your relay configuration.");
        }

        var tracker = new PublishOkTracker(targetRelays.Count);
        _pendingOkTrackers[eventId] = tracker;

        var sentCount = 0;
        foreach (var (relayUrl, connection) in targetRelays)
        {
            try
            {
                await connection.SendAsync(messageBytes);
                sentCount++;
                _logger.LogDebug("Sent pre-built event to relay {Relay}", relayUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send pre-built event to relay {Relay}", relayUrl);
            }
        }

        // Wait for first accept or all rejections (5s timeout)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        cts.Token.Register(() =>
        {
            tracker.OnTimeout();
            _pendingOkTrackers.TryRemove(eventId, out _);
        });

        var okResult = await tracker.Task;
        _pendingOkTrackers.TryRemove(eventId, out _);
        LastPublishOkResult = okResult;
        if (okResult.accepted)
        {
            _logger.LogInformation("Pre-built event {EventId} kind {Kind} confirmed by relay", eventId, kind);
        }
        else
        {
            _logger.LogWarning("Pre-built event {EventId} kind {Kind} NOT confirmed: {Reason}. Sent to {Count} relays",
                eventId, kind, okResult.reason, sentCount);
        }

        return eventId;
    }

    public async Task<(bool accepted, string? reason)> WaitForRelayOkAsync(string eventId, int timeoutMs = 5000)
    {
        var tracker = new PublishOkTracker(1);
        _pendingOkTrackers[eventId] = tracker;

        using var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() =>
        {
            tracker.OnTimeout();
            _pendingOkTrackers.TryRemove(eventId, out _);
        });

        var (accepted, reason) = await tracker.Task;
        _pendingOkTrackers.TryRemove(eventId, out _);
        if (accepted)
            _logger.LogDebug("Relay OK confirmed for event {EventId}", eventId[..Math.Min(16, eventId.Length)]);
        else if (reason?.StartsWith("timeout") == true)
            _logger.LogWarning("Relay OK timeout for event {EventId} after {Timeout}ms", eventId[..Math.Min(16, eventId.Length)], timeoutMs);
        else
            _logger.LogWarning("Relay rejected event {EventId}: {Reason}", eventId[..Math.Min(16, eventId.Length)], reason);

        return (accepted, reason);
    }

    public Task<IEnumerable<KeyPackage>> FetchKeyPackagesAsync(string publicKeyHex)
        => FetchKeyPackagesAsync(publicKeyHex, limit: 5);

    public async Task<IEnumerable<KeyPackage>> FetchKeyPackagesAsync(string publicKeyHex, int limit)
    {
        _logger.LogInformation("Fetching KeyPackages for {PubKey} (limit={Limit} per relay)",
            publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...", limit);

        var keyPackages = new List<KeyPackage>();

        // Prefer target user's kind 10051 KeyPackage relays, fall back to NIP-65 write relays
        var relaysToTry = new List<string>();
        try
        {
            var kpRelays = await GetOrFetchKeyPackageRelayListAsync(publicKeyHex);
            if (kpRelays.Count > 0)
            {
                _logger.LogInformation("Using {Count} kind 10051 relays for KeyPackage fetch", kpRelays.Count);
                relaysToTry.AddRange(kpRelays);
            }
            else
            {
                // Fall back to NIP-65 write relays
                var userRelays = await GetOrFetchRelayListAsync(publicKeyHex);
                var writeRelays = userRelays
                    .Where(r => r.Usage == RelayUsage.Write || r.Usage == RelayUsage.Both)
                    .Select(r => r.Url)
                    .ToList();
                if (writeRelays.Count > 0)
                {
                    _logger.LogInformation("Using {Count} NIP-65 write relays for KeyPackage fetch", writeRelays.Count);
                    relaysToTry.AddRange(writeRelays);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover relays for {PubKey}, using defaults", publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);
        }

        // Also try connected/default relays (dedup later)
        if (_connectedRelays.Count > 0)
            relaysToTry.AddRange(_connectedRelays.Keys);
        else
            relaysToTry.AddRange(NostrConstants.DefaultRelays);

        // Deduplicate relay URLs
        relaysToTry = relaysToTry.Distinct().ToList();

        var seenEventIds = new HashSet<string>();

        foreach (var relayUrl in relaysToTry)
        {
            try
            {
                var packages = await FetchKeyPackagesFromRelayAsync(relayUrl, publicKeyHex, limit);
                foreach (var pkg in packages)
                {
                    // Deduplicate by NostrEventId across relays
                    var eventId = pkg.NostrEventId ?? pkg.Id;
                    if (seenEventIds.Add(eventId))
                    {
                        keyPackages.Add(pkg);
                    }
                }

                _logger.LogInformation("Found {Count} KeyPackages from {Relay} ({Total} total unique)",
                    packages.Count, relayUrl, keyPackages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch KeyPackages from relay {Relay}", relayUrl);
            }
        }

        return keyPackages;
    }

    private async Task<List<KeyPackage>> FetchKeyPackagesFromRelayAsync(string relayUrl, string publicKeyHex, int limit)
    {
        var keyPackages = new List<KeyPackage>();
        var filterJson = JsonSerializer.Serialize(new { kinds = new[] { 30443 }, authors = new[] { publicKeyHex }, limit });
        var rawEvents = await QueryRelayAsync(relayUrl, filterJson);

        foreach (var eventJson in rawEvents)
        {
            try
            {
                using var doc = JsonDocument.Parse(eventJson);
                var eventData = doc.RootElement;
                var kind = eventData.GetProperty("kind").GetInt32();

                if (kind == 30443)
                {
                    var content = eventData.GetProperty("content").GetString();
                    var eventId = eventData.GetProperty("id").GetString();
                    var createdAt = eventData.GetProperty("created_at").GetInt64();

                    if (!string.IsNullOrEmpty(content))
                    {
                        try
                        {
                            // Parse and validate via MIP-00 protocol library
                            var tags = eventData.GetProperty("tags");
                            var tagsArray = tags.EnumerateArray()
                                .Select(t => t.EnumerateArray().Select(v => v.GetString() ?? "").ToArray())
                                .ToArray();

                            var (keyPackageData, keyPackageRefHex, parsedRelays) =
                                KeyPackageEventParser.ParseKeyPackageEvent(content, tagsArray);

                            // Basic sanity check: MLS KeyPackages are typically > 100 bytes
                            if (keyPackageData.Length < 64)
                            {
                                _logger.LogWarning(
                                    "KeyPackage event {EventId} content too short ({Length} bytes), likely invalid",
                                    eventId, keyPackageData.Length);
                                continue;
                            }

                            // Extract ciphersuite ID from tags (not part of MIP-00 parser output)
                            ushort ciphersuiteId = 0x0001;
                            var csTag = tagsArray.FirstOrDefault(t => t.Length >= 2 && t[0] == "mls_ciphersuite");
                            if (csTag != null && csTag[1].StartsWith("0x") &&
                                ushort.TryParse(csTag[1][2..], System.Globalization.NumberStyles.HexNumber, null, out var parsed))
                                ciphersuiteId = parsed;

                            // Extract d-tag (slot ID) for multi-device differentiation
                            var dTagValue = tagsArray
                                .FirstOrDefault(t => t.Length >= 2 && t[0] == "d")?[1];

                            var keyPackage = new KeyPackage
                            {
                                Id = eventId ?? Guid.NewGuid().ToString(),
                                OwnerPublicKey = publicKeyHex,
                                Data = keyPackageData,
                                EventJson = eventData.GetRawText(),
                                NostrEventId = eventId,
                                CiphersuiteId = ciphersuiteId,
                                SlotId = dTagValue,
                                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime,
                                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime.AddDays(30),
                                RelayUrls = parsedRelays.Length > 0
                                    ? parsedRelays.ToList()
                                    : new List<string> { relayUrl }
                            };
                            keyPackages.Add(keyPackage);
                            _logger.LogDebug("Found valid KeyPackage {EventId} from {Relay} (ciphersuite=0x{Cs:x4}, kpRef={KpRef}, {Len} bytes)",
                                eventId, relayUrl, ciphersuiteId, keyPackageRefHex[..Math.Min(16, keyPackageRefHex.Length)], keyPackageData.Length);
                        }
                        catch (FormatException)
                        {
                            _logger.LogWarning("Invalid base64 content in KeyPackage event {EventId}", eventId);
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse KeyPackage event from {Relay}", relayUrl);
            }
        }

        return keyPackages;
    }

    public async Task<IEnumerable<NostrEventReceived>> FetchWelcomeEventsAsync(string publicKeyHex, string? privateKeyHex = null)
    {
        _logger.LogInformation("Fetching Welcome events for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");

        // Temporarily set the private key for gift wrap decryption if not already set
        var previousPrivKey = _subscribedUserPrivKey;
        if (!string.IsNullOrEmpty(privateKeyHex) && string.IsNullOrEmpty(_subscribedUserPrivKey))
            _subscribedUserPrivKey = privateKeyHex;

        var allEvents = new List<NostrEventReceived>();

        var relaysToTry = _connectedRelays.Count > 0
            ? _connectedRelays.Keys.ToList()
            : NostrConstants.DefaultRelays.ToList();

        foreach (var relayUrl in relaysToTry)
        {
            try
            {
                var events = await FetchGiftWrapsFromRelayAsync(relayUrl, publicKeyHex, wantedInnerKind: 444, limit: 50);
                _logger.LogInformation("Found {Count} Welcome event(s) from {Relay}", events.Count, relayUrl);
                allEvents.AddRange(events);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Welcome events from relay {Relay}", relayUrl);
            }
        }

        // Restore previous state
        _subscribedUserPrivKey = previousPrivKey;

        // Deduplicate by EventId
        var unique = allEvents.GroupBy(e => e.EventId).Select(g => g.First()).ToList();
        _logger.LogInformation("Total unique Welcome events found: {Count}", unique.Count);
        return unique;
    }

    /// <summary>
    /// Generic per-relay fetch for kind 1059 gift wraps addressed to <paramref name="publicKeyHex"/>.
    /// Unwraps each and emits the inner rumor only if its kind matches <paramref name="wantedInnerKind"/>.
    /// Used by both the Welcome fetch (inner kind 444) and the NIP-17 DM history fetch (inner kind 14).
    /// </summary>
    private async Task<List<NostrEventReceived>> FetchGiftWrapsFromRelayAsync(
        string relayUrl, string publicKeyHex, int wantedInnerKind, int limit)
    {
        var events = new List<NostrEventReceived>();

        var filter = new Dictionary<string, object>
        {
            { "kinds", new[] { 1059 } },
            { "#p", new[] { publicKeyHex } },
            { "limit", limit }
        };
        var filterJson = JsonSerializer.Serialize(filter);
        var rawEvents = await QueryRelayAsync(relayUrl, filterJson, TimeSpan.FromSeconds(15));

        foreach (var eventJson in rawEvents)
        {
            try
            {
                using var doc = JsonDocument.Parse(eventJson);
                var eventData = doc.RootElement;
                var nostrEvent = ParseNostrEvent(eventData, relayUrl);
                if (nostrEvent != null && nostrEvent.Kind == 1059 &&
                    (!string.IsNullOrEmpty(_subscribedUserPrivKey) || _externalSigner != null))
                {
                    var rumor = await UnwrapGiftWrapAsync(nostrEvent);
                    if (rumor != null && rumor.Kind == wantedInnerKind)
                    {
                        _logger.LogDebug("Unwrapped gift-wrap (inner kind {Kind}) {EventId} from {Sender}",
                            wantedInnerKind,
                            rumor.EventId[..Math.Min(16, rumor.EventId.Length)],
                            rumor.PublicKey[..Math.Min(16, rumor.PublicKey.Length)]);
                        events.Add(rumor);
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse gift-wrap event from {Relay}", relayUrl);
            }
        }

        return events;
    }

    public async Task<IEnumerable<NostrEventReceived>> FetchNip17DmHistoryAsync(string publicKeyHex, string? privateKeyHex = null)
    {
        _logger.LogInformation("Fetching NIP-17 DM history for {PubKey}",
            publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");

        var previousPrivKey = _subscribedUserPrivKey;
        if (!string.IsNullOrEmpty(privateKeyHex) && string.IsNullOrEmpty(_subscribedUserPrivKey))
            _subscribedUserPrivKey = privateKeyHex;

        var allEvents = new List<NostrEventReceived>();
        var relaysToTry = _connectedRelays.Count > 0
            ? _connectedRelays.Keys.ToList()
            : NostrConstants.DefaultRelays.ToList();

        foreach (var relayUrl in relaysToTry)
        {
            try
            {
                var events = await FetchGiftWrapsFromRelayAsync(relayUrl, publicKeyHex, wantedInnerKind: 14, limit: 200);
                _logger.LogInformation("Found {Count} NIP-17 DM event(s) from {Relay}", events.Count, relayUrl);
                allEvents.AddRange(events);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch NIP-17 DMs from relay {Relay}", relayUrl);
            }
        }

        _subscribedUserPrivKey = previousPrivKey;

        var unique = allEvents.GroupBy(e => e.EventId).Select(g => g.First()).ToList();
        _logger.LogInformation("Total unique NIP-17 DM events found: {Count}", unique.Count);
        return unique;
    }

    public async Task<IEnumerable<NostrEventReceived>> FetchGroupHistoryAsync(
        string nostrGroupIdHex, DateTimeOffset since, DateTimeOffset until, int limit = 50)
    {
        _logger.LogInformation(
            "Fetching group history for {GroupId}, since={Since}, until={Until}, limit={Limit}",
            nostrGroupIdHex[..Math.Min(16, nostrGroupIdHex.Length)],
            since, until, limit);

        var allEvents = new List<NostrEventReceived>();

        var relaysToTry = _connectedRelays.Count > 0
            ? _connectedRelays.Keys.ToList()
            : NostrConstants.DefaultRelays.ToList();

        foreach (var relayUrl in relaysToTry)
        {
            try
            {
                var events = await FetchGroupHistoryFromRelayAsync(relayUrl, nostrGroupIdHex, since, until, limit);
                _logger.LogInformation("Found {Count} group history event(s) from {Relay}",
                    events.Count, relayUrl);
                allEvents.AddRange(events);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch group history from relay {Relay}", relayUrl);
            }
        }

        // Deduplicate by EventId
        var unique = allEvents.GroupBy(e => e.EventId).Select(g => g.First()).ToList();
        _logger.LogInformation("Total unique group history events found: {Count}", unique.Count);
        return unique;
    }

    private async Task<List<NostrEventReceived>> FetchGroupHistoryFromRelayAsync(
        string relayUrl, string nostrGroupIdHex, DateTimeOffset since, DateTimeOffset until, int limit)
    {
        var events = new List<NostrEventReceived>();

        var filter = new Dictionary<string, object>
        {
            { "kinds", new[] { 445 } },
            { "#h", new[] { nostrGroupIdHex } },
            { "since", since.ToUnixTimeSeconds() },
            { "until", until.ToUnixTimeSeconds() },
            { "limit", limit }
        };
        var filterJson = JsonSerializer.Serialize(filter);
        var rawEvents = await QueryRelayAsync(relayUrl, filterJson, TimeSpan.FromSeconds(15));

        foreach (var eventJson in rawEvents)
        {
            try
            {
                using var doc = JsonDocument.Parse(eventJson);
                var eventData = doc.RootElement;
                var nostrEvent = ParseNostrEvent(eventData, relayUrl);
                if (nostrEvent != null && nostrEvent.Kind == 445)
                {
                    events.Add(nostrEvent);
                    _logger.LogDebug("Found group history event {EventId} from {Sender}",
                        nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)],
                        nostrEvent.PublicKey[..Math.Min(16, nostrEvent.PublicKey.Length)]);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse group history event from {Relay}", relayUrl);
            }
        }

        return events;
    }

    public async Task<UserMetadata?> FetchUserMetadataAsync(string publicKeyHex)
    {
        _logger.LogInformation("Fetching metadata for public key: {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");

        try
        {
            var npub = Bech32.Encode("npub", Convert.FromHexString(publicKeyHex));

            // Discover target user's relays via NIP-65, then fall back to connected/default relays
            var relaysToTry = new List<string>();
            try
            {
                var userRelays = await GetOrFetchRelayListAsync(publicKeyHex);
                var userRelayUrls = userRelays
                    .Where(r => r.Usage == RelayUsage.Read || r.Usage == RelayUsage.Both)
                    .Select(r => r.Url)
                    .ToList();
                if (userRelayUrls.Count > 0)
                {
                    _logger.LogInformation("Using {Count} NIP-65 relays for metadata fetch", userRelayUrls.Count);
                    relaysToTry.AddRange(userRelayUrls);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to discover NIP-65 relays for metadata fetch, using defaults");
            }

            // Also try connected/default relays
            if (_connectedRelays.Count > 0)
                relaysToTry.AddRange(_connectedRelays.Keys);
            else
                relaysToTry.AddRange(NostrConstants.DefaultRelays);

            relaysToTry = relaysToTry.Distinct().ToList();

            foreach (var relayUrl in relaysToTry)
            {
                try
                {
                    var metadata = await FetchMetadataFromRelayAsync(relayUrl, publicKeyHex, npub);
                    if (metadata != null)
                    {
                        _logger.LogInformation("Fetched metadata for {Npub} from {Relay}: {DisplayName}",
                            npub, relayUrl, metadata.GetDisplayName());
                        return metadata;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch metadata from relay {Relay}", relayUrl);
                }
            }

            // If no metadata found, return basic info
            _logger.LogInformation("No metadata found for {Npub}..., returning basic info", npub[..Math.Min(12, npub.Length)]);
            return new UserMetadata
            {
                PublicKeyHex = publicKeyHex,
                Npub = npub,
                CreatedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch metadata for {PubKey}", publicKeyHex);
            return null;
        }
    }

    private async Task<UserMetadata?> FetchMetadataFromRelayAsync(string relayUrl, string publicKeyHex, string npub)
    {
        var filterJson = JsonSerializer.Serialize(new { kinds = new[] { 0 }, authors = new[] { publicKeyHex }, limit = 1 });
        var events = await QueryRelayAsync(relayUrl, filterJson);

        foreach (var eventJson in events)
        {
            try
            {
                using var doc = JsonDocument.Parse(eventJson);
                var eventData = doc.RootElement;
                var kind = eventData.GetProperty("kind").GetInt32();

                if (kind == 0)
                {
                    var content = eventData.GetProperty("content").GetString();
                    var createdAt = eventData.GetProperty("created_at").GetInt64();

                    if (!string.IsNullOrEmpty(content))
                    {
                        var metadata = ParseMetadataContent(content, publicKeyHex, npub);
                        if (metadata != null)
                        {
                            metadata.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime;
                            return metadata;
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse metadata event from {Relay}", relayUrl);
            }
        }

        return null;
    }

    private UserMetadata? ParseMetadataContent(string content, string publicKeyHex, string npub)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            var metadata = new UserMetadata
            {
                PublicKeyHex = publicKeyHex,
                Npub = npub
            };

            if (root.TryGetProperty("name", out var name))
                metadata.Name = name.GetString();

            if (root.TryGetProperty("display_name", out var displayName))
                metadata.DisplayName = displayName.GetString();

            if (root.TryGetProperty("displayName", out var displayName2))
                metadata.DisplayName ??= displayName2.GetString();

            if (root.TryGetProperty("username", out var username))
                metadata.Username = username.GetString();

            if (root.TryGetProperty("about", out var about))
                metadata.About = about.GetString();

            if (root.TryGetProperty("picture", out var picture))
                metadata.Picture = picture.GetString();

            if (root.TryGetProperty("banner", out var banner))
                metadata.Banner = banner.GetString();

            if (root.TryGetProperty("website", out var website))
                metadata.Website = website.GetString();

            if (root.TryGetProperty("nip05", out var nip05))
                metadata.Nip05 = nip05.GetString();

            if (root.TryGetProperty("lud16", out var lud16))
                metadata.Lud16 = lud16.GetString();

            if (root.TryGetProperty("lud06", out var lud06))
                metadata.Lud06 = lud06.GetString();

            _logger.LogDebug("Parsed metadata - Name: {Name}, DisplayName: {DisplayName}, Nip05: {Nip05}",
                metadata.Name, metadata.DisplayName, metadata.Nip05);

            return metadata;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse metadata content: {Content}", content[..Math.Min(100, content.Length)]);
            return null;
        }
    }

    public async Task<List<RelayPreference>> FetchRelayListAsync(string publicKeyHex)
    {
        _logger.LogInformation("Fetching NIP-65 relay list for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");

        // Query discovery relays AND already-connected relays in parallel
        var allRelays = NostrConstants.DiscoveryRelays
            .Union(_connectedRelays.Keys)
            .Distinct()
            .ToList();

        var tasks = allRelays.Select(async relay =>
        {
            try
            {
                return await FetchRelayListFromRelayAsync(relay, publicKeyHex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch relay list from {Relay}", relay);
                return (new List<RelayPreference>(), 0L);
            }
        });

        var results = await Task.WhenAll(tasks);

        // Pick the result with the most recent created_at
        var relays = new List<RelayPreference>();
        long latestCreatedAt = 0;
        foreach (var (found, createdAt) in results)
        {
            if (found.Count > 0 && createdAt > latestCreatedAt)
            {
                relays = found;
                latestCreatedAt = createdAt;
            }
        }

        _logger.LogInformation("NIP-65 relay list: {Count} relays found", relays.Count);
        return relays;
    }

    private async Task<(List<RelayPreference> Relays, long CreatedAt)> FetchRelayListFromRelayAsync(string relayUrl, string publicKeyHex)
    {
        var filterJson = JsonSerializer.Serialize(new { kinds = new[] { 10002 }, authors = new[] { publicKeyHex }, limit = 1 });
        var events = await QueryRelayAsync(relayUrl, filterJson);

        var relays = new List<RelayPreference>();
        long createdAt = 0;

        foreach (var eventJson in events)
        {
            try
            {
                using var doc = JsonDocument.Parse(eventJson);
                var eventData = doc.RootElement;
                var kind = eventData.GetProperty("kind").GetInt32();

                if (kind == 10002)
                {
                    createdAt = eventData.GetProperty("created_at").GetInt64();
                    var tags = eventData.GetProperty("tags");

                    foreach (var tag in tags.EnumerateArray())
                    {
                        if (tag.GetArrayLength() < 2) continue;
                        if (tag[0].GetString() != "r") continue;

                        var url = tag[1].GetString();
                        if (string.IsNullOrEmpty(url)) continue;

                        var usage = RelayUsage.Both;
                        if (tag.GetArrayLength() >= 3)
                        {
                            var marker = tag[2].GetString();
                            usage = marker switch
                            {
                                "read" => RelayUsage.Read,
                                "write" => RelayUsage.Write,
                                _ => RelayUsage.Both
                            };
                        }

                        relays.Add(new RelayPreference { Url = url, Usage = usage });
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse relay list event from {Relay}", relayUrl);
            }
        }

        return (relays, createdAt);
    }

    public async Task<List<Follow>> FetchFollowingListAsync(string publicKeyHex)
    {
        _logger.LogInformation("Fetching NIP-02 following list for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");

        // Use user's NIP-65 write relays if available, plus connected relays
        var relaysToTry = new List<string>();
        try
        {
            var userRelays = await GetOrFetchRelayListAsync(publicKeyHex);
            var writeRelays = userRelays
                .Where(r => r.Usage == RelayUsage.Write || r.Usage == RelayUsage.Both)
                .Select(r => r.Url);
            relaysToTry.AddRange(writeRelays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NIP-65 relay discovery failed for follow fetch");
        }

        if (_connectedRelays.Count > 0)
            relaysToTry.AddRange(_connectedRelays.Keys);
        else
            relaysToTry.AddRange(NostrConstants.DefaultRelays);

        relaysToTry = relaysToTry.Distinct().ToList();

        var tasks = relaysToTry.Select(async relay =>
        {
            try { return await FetchFollowingFromRelayAsync(relay, publicKeyHex); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch follow list from {Relay}", relay);
                return (new List<Follow>(), 0L);
            }
        });

        var results = await Task.WhenAll(tasks);

        // Pick the most recent kind 3 event
        var follows = new List<Follow>();
        long latest = 0;
        foreach (var (found, createdAt) in results)
        {
            if (createdAt > latest)
            {
                follows = found;
                latest = createdAt;
            }
        }

        _logger.LogInformation("Following list: {Count} contacts", follows.Count);
        return follows;
    }

    private async Task<(List<Follow> Follows, long CreatedAt)> FetchFollowingFromRelayAsync(string relayUrl, string publicKeyHex)
    {
        var filterJson = JsonSerializer.Serialize(new { kinds = new[] { 3 }, authors = new[] { publicKeyHex }, limit = 1 });
        var rawEvents = await QueryRelayAsync(relayUrl, filterJson);

        var follows = new List<Follow>();
        long createdAt = 0;

        foreach (var eventJson in rawEvents)
        {
            try
            {
                using var doc = JsonDocument.Parse(eventJson);
                var eventData = doc.RootElement;
                if (eventData.GetProperty("kind").GetInt32() != 3) continue;

                createdAt = eventData.GetProperty("created_at").GetInt64();
                follows.Clear();
                foreach (var tag in eventData.GetProperty("tags").EnumerateArray())
                {
                    if (tag.GetArrayLength() < 2) continue;
                    if (tag[0].GetString() != "p") continue;
                    var pub = tag[1].GetString();
                    if (string.IsNullOrEmpty(pub) || pub.Length != 64) continue;

                    string? relayHint = tag.GetArrayLength() >= 3 ? tag[2].GetString() : null;
                    string? petname = tag.GetArrayLength() >= 4 ? tag[3].GetString() : null;
                    follows.Add(new Follow
                    {
                        PublicKeyHex = pub.ToLowerInvariant(),
                        RelayHint = string.IsNullOrWhiteSpace(relayHint) ? null : relayHint,
                        Petname = string.IsNullOrWhiteSpace(petname) ? null : petname,
                        AddedAt = DateTime.UtcNow
                    });
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse follow event from {Relay}", relayUrl);
            }
        }

        return (follows, createdAt);
    }

    public async Task<string> PublishMetadataAsync(string name, string? displayName, string? about, string? picture, string? privateKeyHex)
    {
        _logger.LogInformation("Publishing metadata (kind 0) for {Name}", name);

        // NIP-01 kind 0: content is a JSON string with metadata fields
        var metadata = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(name)) metadata["name"] = name;
        if (!string.IsNullOrEmpty(displayName)) metadata["display_name"] = displayName;
        if (!string.IsNullOrEmpty(about)) metadata["about"] = about;
        if (!string.IsNullOrEmpty(picture)) metadata["picture"] = picture;

        var content = JsonSerializer.Serialize(metadata);

        // Kind 0 is replaceable — no tags needed
        var eventId = await PublishEventAsync(0, content, new List<List<string>>(), privateKeyHex);
        _logger.LogInformation("Published metadata, event ID: {EventId}", eventId);
        return eventId;
    }

    public async Task<string> PublishRelayListAsync(List<RelayPreference> relays, string? privateKeyHex)
    {
        _logger.LogInformation("Publishing NIP-65 relay list with {Count} relays", relays.Count);

        var tags = new List<List<string>>();
        foreach (var relay in relays)
        {
            var tag = relay.Usage switch
            {
                RelayUsage.Read => new List<string> { "r", relay.Url, "read" },
                RelayUsage.Write => new List<string> { "r", relay.Url, "write" },
                _ => new List<string> { "r", relay.Url }
            };
            tags.Add(tag);
        }

        // Kind 10002 is replaceable — content is empty per NIP-65
        var eventId = await PublishEventAsync(10002, "", tags, privateKeyHex);
        _logger.LogInformation("Published NIP-65 relay list, event ID: {EventId}", eventId);
        return eventId;
    }

    public async Task<string> PublishDmRelayListAsync(List<string> relayUrls, string? privateKeyHex)
    {
        _logger.LogInformation("Publishing DM relay list (kind 10050) with {Count} relays", relayUrls.Count);

        // NIP-17: kind 10050 uses ["relay", url] tags
        var tags = relayUrls
            .Select(url => new List<string> { "relay", url })
            .ToList();

        var eventId = await PublishEventAsync(10050, "", tags, privateKeyHex);
        _logger.LogInformation("Published DM relay list, event ID: {EventId}", eventId);
        return eventId;
    }

    public async Task<string> PublishKeyPackageRelayListAsync(List<string> relayUrls, string? privateKeyHex)
    {
        _logger.LogInformation("Publishing KeyPackage relay list (kind 10051) with {Count} relays", relayUrls.Count);

        // Kind 10051 uses ["relay", url] tags (same format as 10050)
        var tags = relayUrls
            .Select(url => new List<string> { "relay", url })
            .ToList();

        var eventId = await PublishEventAsync(10051, "", tags, privateKeyHex);
        _logger.LogInformation("Published KeyPackage relay list, event ID: {EventId}", eventId);
        return eventId;
    }

    public async Task<List<string>> FetchDmRelayListAsync(string publicKeyHex)
    {
        _logger.LogInformation("Fetching DM relay list (kind 10050) for {PubKey}",
            publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");

        return await FetchRelayTagListAsync(10050, publicKeyHex);
    }

    public async Task<List<string>> FetchKeyPackageRelayListAsync(string publicKeyHex)
    {
        _logger.LogInformation("Fetching KeyPackage relay list (kind 10051) for {PubKey}",
            publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");

        return await FetchRelayTagListAsync(10051, publicKeyHex);
    }

    /// <summary>
    /// Generic fetcher for relay-tag list events (kinds 10050, 10051).
    /// Queries discovery relays and connected relays in parallel, picks the most recent event,
    /// and extracts ["relay", url] tags.
    /// </summary>
    private async Task<List<string>> FetchRelayTagListAsync(int kind, string publicKeyHex)
    {
        var allRelays = NostrConstants.DiscoveryRelays
            .Union(_connectedRelays.Keys)
            .Distinct()
            .ToList();

        var tasks = allRelays.Select(async relay =>
        {
            try
            {
                return await FetchRelayTagListFromRelayAsync(relay, kind, publicKeyHex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch kind {Kind} relay list from {Relay}", kind, relay);
                return (new List<string>(), 0L);
            }
        });

        var results = await Task.WhenAll(tasks);

        var relayUrls = new List<string>();
        long latestCreatedAt = 0;
        foreach (var (found, createdAt) in results)
        {
            if (found.Count > 0 && createdAt > latestCreatedAt)
            {
                relayUrls = found;
                latestCreatedAt = createdAt;
            }
        }

        _logger.LogInformation("Kind {Kind} relay list: {Count} relays found", kind, relayUrls.Count);
        return relayUrls;
    }

    private async Task<(List<string> Relays, long CreatedAt)> FetchRelayTagListFromRelayAsync(
        string relayUrl, int kind, string publicKeyHex)
    {
        var filterJson = JsonSerializer.Serialize(new { kinds = new[] { kind }, authors = new[] { publicKeyHex }, limit = 1 });
        var events = await QueryRelayAsync(relayUrl, filterJson);

        var relays = new List<string>();
        long createdAt = 0;

        foreach (var eventJson in events)
        {
            try
            {
                using var doc = JsonDocument.Parse(eventJson);
                var eventData = doc.RootElement;
                if (eventData.GetProperty("kind").GetInt32() != kind) continue;

                createdAt = eventData.GetProperty("created_at").GetInt64();
                var tags = eventData.GetProperty("tags");

                foreach (var tag in tags.EnumerateArray())
                {
                    if (tag.GetArrayLength() < 2) continue;
                    if (tag[0].GetString() != "relay") continue;

                    var url = tag[1].GetString();
                    if (!string.IsNullOrEmpty(url))
                        relays.Add(url);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse kind {Kind} relay list event from {Relay}", kind, relayUrl);
            }
        }

        return (relays, createdAt);
    }

    public string? NpubToHex(string npub)
    {
        if (string.IsNullOrEmpty(npub))
            return null;

        try
        {
            if (npub.StartsWith("npub"))
            {
                var bytes = Bech32.Decode(npub, out var hrp);
                if (hrp == "npub" && bytes.Length == 32)
                {
                    return Convert.ToHexString(bytes).ToLowerInvariant();
                }
            }
            else if (npub.Length == 64)
            {
                // Already hex
                return npub.ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert npub to hex: {Npub}...", npub[..Math.Min(12, npub.Length)]);
        }

        return null;
    }

    public async Task<string> Nip44EncryptAsync(string plaintext, string recipientPubKey)
    {
        if (_externalSigner != null)
        {
            _logger.LogDebug("Delegating NIP-44 encrypt to external signer for recipient {Recipient}",
                recipientPubKey[..Math.Min(16, recipientPubKey.Length)]);
            return await _externalSigner.Nip44EncryptAsync(plaintext, recipientPubKey);
        }

        if (string.IsNullOrEmpty(_subscribedUserPrivKey))
        {
            throw new InvalidOperationException(
                "Cannot NIP-44 encrypt: no private key and no external signer connected.");
        }

        _logger.LogDebug("NIP-44 encrypting locally for recipient {Recipient}",
            recipientPubKey[..Math.Min(16, recipientPubKey.Length)]);
        var convKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(_subscribedUserPrivKey), Convert.FromHexString(recipientPubKey));
        return Nip44Encryption.Encrypt(plaintext, convKey);
    }

    public async Task<string> Nip44DecryptAsync(string ciphertext, string senderPubKey)
    {
        if (_externalSigner != null)
        {
            _logger.LogDebug("Delegating NIP-44 decrypt to external signer for sender {Sender}",
                senderPubKey[..Math.Min(16, senderPubKey.Length)]);
            return await _externalSigner.Nip44DecryptAsync(ciphertext, senderPubKey);
        }

        if (string.IsNullOrEmpty(_subscribedUserPrivKey))
        {
            throw new InvalidOperationException(
                "Cannot NIP-44 decrypt: no private key and no external signer connected.");
        }

        _logger.LogDebug("NIP-44 decrypting locally for sender {Sender}",
            senderPubKey[..Math.Min(16, senderPubKey.Length)]);
        var convKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(_subscribedUserPrivKey), Convert.FromHexString(senderPubKey));
        return Nip44Encryption.Decrypt(ciphertext, convKey);
    }

    /// <summary>
    /// Last publish OK result. Set by PublishEventAsync and PublishRawEventJsonAsync after each publish.
    /// Callers that need to verify relay acceptance can check this after awaiting the publish method.
    /// </summary>
    internal (bool accepted, string? reason) LastPublishOkResult { get; private set; }

    private async Task<string> PublishEventAsync(int kind, string content, List<List<string>> tags, string? privateKeyHex)
    {
        _logger.LogInformation("Publishing event kind {Kind} to {Count} relays", kind, _relayConnections.Count);

        string eventMessage;
        string eventId;

        if (!string.IsNullOrEmpty(privateKeyHex))
        {
            // Sign locally with private key
            var privateKeyBytes = Convert.FromHexString(privateKeyHex);
            var publicKeyBytes = DerivePublicKey(privateKeyBytes);
            var publicKeyHex = Convert.ToHexString(publicKeyBytes).ToLowerInvariant();

            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var serializedForId = SerializeForEventId(publicKeyHex, createdAt, kind, tags, content);
            _logger.LogDebug("Event serialization for ID: {Serialized}", serializedForId);

            var eventIdBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serializedForId));
            eventId = Convert.ToHexString(eventIdBytes).ToLowerInvariant();

            var signature = SignSchnorr(eventIdBytes, privateKeyBytes);
            var signatureHex = Convert.ToHexString(signature).ToLowerInvariant();

            eventMessage = SerializeEventMessage(eventId, publicKeyHex, createdAt, kind, tags, content, signatureHex);
        }
        else if (_externalSigner != null)
        {
            // Sign via external signer (NIP-46)
            // If signer is disconnected, attempt a reconnect before signing
            if (!_externalSigner.IsConnected)
            {
                _logger.LogWarning("External signer is set but disconnected — attempting reconnect before signing kind {Kind}", kind);
                try
                {
                    await _externalSigner.ReconnectAsync();
                    if (_externalSigner.IsConnected)
                    {
                        _logger.LogInformation("External signer reconnected successfully");
                    }
                    else
                    {
                        _logger.LogWarning("External signer reconnect completed but still not connected");
                    }
                }
                catch (Exception reconnectEx)
                {
                    _logger.LogWarning(reconnectEx, "External signer reconnect failed — will attempt sign anyway");
                }
            }

            _logger.LogInformation("Using external signer to sign kind {Kind} event", kind);

            var unsignedEvent = new UnsignedNostrEvent
            {
                Kind = kind,
                Content = content,
                Tags = tags,
                CreatedAt = DateTime.UtcNow
            };

            var signedEventJson = await _externalSigner.SignEventAsync(unsignedEvent);

            // Validate the signed event JSON before sending to relays.
            // The signer must return a full event object with id, pubkey, sig as strings.
            // After a WebSocket reconnect, ExternalSignerService may replay a stale NIP-46
            // response (e.g. nip44_decrypt result) that isn't valid event JSON — sending
            // that verbatim to relays causes "event sig was not a string" rejections.
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(signedEventJson);
                root = doc.RootElement.Clone(); // Clone so it survives doc disposal
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "External signer returned invalid JSON for kind {Kind}. Response length: {Len}, preview: {Preview}",
                    kind, signedEventJson?.Length ?? 0,
                    signedEventJson != null && signedEventJson.Length > 100 ? signedEventJson[..100] + "..." : signedEventJson ?? "(null)");
                throw new InvalidOperationException(
                    $"Signer returned invalid JSON for kind {kind} event. The signer may have returned " +
                    "a stale response after reconnecting. Please try again.", jsonEx);
            }

            eventId = root.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Signer returned event without id");

            // Validate sig field exists and is a non-empty string
            if (!root.TryGetProperty("sig", out var sigProp) ||
                sigProp.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(sigProp.GetString()))
            {
                _logger.LogError("Signer returned event with invalid sig for kind {Kind}. sig type: {SigType}, event preview: {Preview}",
                    kind,
                    root.TryGetProperty("sig", out var s) ? s.ValueKind.ToString() : "missing",
                    signedEventJson.Length > 200 ? signedEventJson[..200] + "..." : signedEventJson);
                throw new InvalidOperationException(
                    $"Signer returned event with invalid signature for kind {kind}. " +
                    "The signature may have been corrupted or the signer returned a stale response. Please try again.");
            }

            // Wrap in ["EVENT", {...}] relay message format
            eventMessage = $"[\"EVENT\",{signedEventJson}]";
        }
        else
        {
            throw new InvalidOperationException(
                "Cannot publish event: no private key and no external signer connected. " +
                "Please log in with a private key or connect an external signer like Amber.");
        }

        var eventBytes = Encoding.UTF8.GetBytes(eventMessage);
        _logger.LogDebug("Publishing event {EventId} to relays", eventId);

        // Count target relays and register tracker BEFORE sending
        var targetRelays = new List<(string url, NostrRelayConnection conn)>();
        foreach (var (relayUrl, connection) in _relayConnections)
        {
            if (IsBotOnlyRelay(relayUrl) || IsOutboxRelay(relayUrl)) continue;
            if (connection.IsConnected)
                targetRelays.Add((relayUrl, connection));
        }

        if (targetRelays.Count == 0)
        {
            _logger.LogWarning("Event {EventId} kind {Kind}: no connected relays to publish to", eventId, kind);
            throw new InvalidOperationException("No connected relays available. Check your relay configuration.");
        }

        var tracker = new PublishOkTracker(targetRelays.Count);
        _pendingOkTrackers[eventId] = tracker;

        var publishTasks = targetRelays.Select(r => SendToRelayConnectionAsync(r.url, r.conn, eventBytes));
        await Task.WhenAll(publishTasks);

        // Wait for first accept or all rejections (5s timeout)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        cts.Token.Register(() =>
        {
            tracker.OnTimeout();
            _pendingOkTrackers.TryRemove(eventId, out _);
        });

        var okResult = await tracker.Task;
        _pendingOkTrackers.TryRemove(eventId, out _);
        LastPublishOkResult = okResult;
        if (okResult.accepted)
        {
            _logger.LogInformation("Event {EventId} kind {Kind} confirmed by relay", eventId, kind);
        }
        else
        {
            _logger.LogWarning("Event {EventId} kind {Kind} NOT confirmed: {Reason}. Sent to {Count} relays",
                eventId, kind, okResult.reason, targetRelays.Count);
        }

        return eventId;
    }

    private async Task SendToRelayConnectionAsync(string relayUrl, NostrRelayConnection connection, byte[] data)
    {
        try
        {
            await connection.SendAsync(data);
            _logger.LogDebug("Sent event to relay {RelayUrl}", relayUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send event to relay {RelayUrl}", relayUrl);
        }
    }

    /// <summary>
    /// Serialize the pre-event array for NIP-01 event ID computation.
    /// Uses Utf8JsonWriter for deterministic output instead of JsonSerializer.Serialize(object[])
    /// which incorrectly serializes boxed List&lt;List&lt;string&gt;&gt; values.
    /// </summary>
    internal static string SerializeForEventId(string pubkey, long createdAt, int kind, List<List<string>> tags, string content)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(0);
            writer.WriteStringValue(pubkey);
            writer.WriteNumberValue(createdAt);
            writer.WriteNumberValue(kind);
            writer.WriteStartArray();
            foreach (var tag in tags)
            {
                writer.WriteStartArray();
                foreach (var value in tag)
                {
                    writer.WriteStringValue(value);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStringValue(content);
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Serialize the full EVENT message for sending to relays.
    /// </summary>
    private static string SerializeEventMessage(string eventId, string pubkey, long createdAt, int kind, List<List<string>> tags, string content, string sig)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("EVENT");
            writer.WriteStartObject();
            writer.WriteString("id", eventId);
            writer.WriteString("pubkey", pubkey);
            writer.WriteNumber("created_at", createdAt);
            writer.WriteNumber("kind", kind);
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in tags)
            {
                writer.WriteStartArray();
                foreach (var value in tag)
                {
                    writer.WriteStringValue(value);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteString("content", content);
            writer.WriteString("sig", sig);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static byte[] SignSchnorr(byte[] message, byte[] privateKey)
    {
        if (!Context.Instance.TryCreateECPrivKey(privateKey, out var ecPrivKey) || ecPrivKey is null)
        {
            throw new ArgumentException("Invalid private key for signing");
        }

        // Use BIP-340 Schnorr signature
        var sig = ecPrivKey.SignBIP340(message);
        var sigBytes = new byte[64];
        sig.WriteToSpan(sigBytes);
        return sigBytes;
    }

    internal static byte[] DerivePublicKey(byte[] privateKey)
    {
        // Use secp256k1 to derive the public key (x-only for Nostr)
        if (!Context.Instance.TryCreateECPrivKey(privateKey, out var ecPrivKey) || ecPrivKey is null)
        {
            throw new ArgumentException("Invalid private key");
        }

        var pubKey = ecPrivKey.CreateXOnlyPubKey();
        var publicKeyBytes = new byte[32];
        pubKey.WriteToSpan(publicKeyBytes);
        return publicKeyBytes;
    }

    /// <summary>
    /// Validates a relay URL for safe connection. Returns null if valid, or an error message if rejected.
    /// Checks: valid URI, ws/wss scheme only, no private/reserved IP addresses.
    /// </summary>
    internal static async Task<string?> ValidateRelayUrlAsync(string relayUrl)
    {
        if (!Uri.TryCreate(relayUrl, UriKind.Absolute, out var uri))
            return "Invalid URL format.";

        if (uri.Scheme == "ws" && !ProfileConfiguration.AllowLocalRelays)
            return "Relay URL must use wss:// (TLS required). Use --allow-local-relays for unencrypted ws:// connections.";

        if (uri.Scheme != "wss" && uri.Scheme != "ws")
            return $"Relay URL must use ws:// or wss:// scheme, got: {uri.Scheme}://";

        // Resolve DNS and check for private/reserved IPs
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host);
            foreach (var addr in addresses)
            {
                if (IsPrivateOrReservedIp(addr))
                    return $"Relay URL resolves to private/reserved IP address ({addr}).";
            }
        }
        catch (SocketException)
        {
            return $"Cannot resolve hostname: {uri.Host}";
        }

        return null;
    }

    /// <summary>
    /// Checks if an IP address is in a private or reserved range.
    /// Blocks: 10.x, 172.16-31.x, 192.168.x, 127.x, 169.254.x, 0.x, ::1, fc00::/7, fe80::/10
    /// </summary>
    internal static bool IsPrivateOrReservedIp(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 10) return true;                                    // 10.0.0.0/8
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true; // 172.16.0.0/12
            if (bytes[0] == 192 && bytes[1] == 168) return true;                // 192.168.0.0/16
            if (bytes[0] == 127) return true;                                    // 127.0.0.0/8
            if (bytes[0] == 169 && bytes[1] == 254) return true;                // 169.254.0.0/16
            if (bytes[0] == 0) return true;                                      // 0.0.0.0/8
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;                         // fc00::/7
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;     // fe80::/10
            if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any))
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();

        // Dispose Rx subscriptions and relay connections
        foreach (var (_, sub) in _relayMessageSubscriptions)
        {
            sub.Dispose();
        }
        _relayMessageSubscriptions.Clear();

        foreach (var (_, connection) in _relayConnections)
        {
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _relayConnections.Clear();

        _connectionProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _connectionStatus.Dispose();
        _events.Dispose();
        _welcomeMessages.Dispose();
        _groupMessages.Dispose();

        // Zero private key material from memory
        _subscribedUserPrivKey = null;

        _disposed = true;
    }
}
