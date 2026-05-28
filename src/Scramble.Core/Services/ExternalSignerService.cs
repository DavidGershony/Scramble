using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using MarmotCs.Protocol.Nip44;
using Scramble.Core.Crypto;
using Scramble.Core.Logging;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Aes = System.Security.Cryptography.Aes;
using RandomNumberGenerator = System.Security.Cryptography.RandomNumberGenerator;
using SHA256 = System.Security.Cryptography.SHA256;

namespace Scramble.Core.Services;

/// <summary>
/// NIP-46 (Nostr Connect) implementation for external signers.
/// Supports bunker URLs for signers like Amber, nsecBunker, etc.
/// </summary>
public class ExternalSignerService : IExternalSigner, IDisposable
{
    private readonly ILogger<ExternalSignerService> _logger;
    private readonly Subject<ExternalSignerStatus> _status = new();
    private readonly List<RelayConnection> _relayConnections = new();
    private CancellationTokenSource? _cts;
    private string? _remotePubKey;
    private string? _secret;
    private string? _localPrivateKeyHex;
    private string? _localPublicKeyHex;
    private readonly Dictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(bool accepted, string? reason)>> _pendingOkResponses = new();
    private string? _replayedSignEventResult;
    private long _subscriptionSince;
    private bool _disposed;

    /// <summary>
    /// Target NIP-13 proof-of-work difficulty for NIP-46 events.
    /// Relays like nos.lol require 28 bits, but mining that takes 10-30s per event.
    /// We mine a modest amount (21 bits, &lt;1s) and rely on relay fallback for stricter relays.
    /// Set to 0 to disable PoW entirely.
    /// </summary>
    private const int Nip46ProofOfWorkBits = 21;

    /// <summary>Maximum time to spend mining PoW for a single event.</summary>
    private static readonly TimeSpan ProofOfWorkTimeout = TimeSpan.FromSeconds(3);

    // Bounded set of event ids whose receipt has already been logged once (across relays).
    // Used to suppress N×relay-fanout duplicate "received event" INFO log lines that bloated
    // mobile log files (~94% of a 25 MB log was NIP-46 fanout). Capped to keep memory tiny;
    // when capacity is reached the oldest entries are evicted (FIFO via Queue).
    private readonly object _loggedEventsLock = new();
    private readonly HashSet<string> _loggedEventIds = new();
    private readonly Queue<string> _loggedEventOrder = new();
    private const int LoggedEventsCapacity = 1024;

    private class RelayConnection : IDisposable
    {
        public string Url { get; }
        public ClientWebSocket? WebSocket { get; set; }

        public RelayConnection(string url) => Url = url;

        public void Dispose() => WebSocket?.Dispose();
    }

    public ExternalSignerService()
    {
        _logger = LoggingConfiguration.CreateLogger<ExternalSignerService>();
        _logger.LogDebug("ExternalSignerService initialized");
    }

    public bool IsConnected { get; private set; }
    public string? PublicKeyHex { get; private set; }
    public string? Npub => PublicKeyHex != null ? Bech32.Encode("npub", Convert.FromHexString(PublicKeyHex)) : null;
    public IReadOnlyList<string> RelayUrls => _relayConnections.Select(c => c.Url).ToList();
    public string? RelayUrl => _relayConnections.FirstOrDefault()?.Url;
    public string? RemotePubKey => _remotePubKey;
    public string? Secret => _secret;
    public string? LocalPrivateKeyHex => _localPrivateKeyHex;
    public string? LocalPublicKeyHex => _localPublicKeyHex;
    public IObservable<ExternalSignerStatus> Status => _status.AsObservable();

    public async Task<bool> ConnectAsync(string bunkerUrl)
    {
        return await ConnectWithStringAsync(bunkerUrl);
    }

    public async Task<bool> ConnectWithStringAsync(string connectionString)
    {
        _logger.LogInformation("Attempting to connect with external signer using connection string");
        _logger.LogDebug("Connection string length: {Length}", connectionString.Length);

        try
        {
            _status.OnNext(new ExternalSignerStatus { State = ExternalSignerState.Connecting });

            // Parse the connection string
            if (!ParseConnectionString(connectionString, out var remotePubKey, out var relayUrls, out var secret))
            {
                _logger.LogError("Failed to parse connection string - invalid format. Input length: {Len}", connectionString.Trim().Length);
                _status.OnNext(new ExternalSignerStatus
                {
                    State = ExternalSignerState.Error,
                    Error = "Invalid connection string format. Expected bunker://... or nostrconnect://... with relay parameters."
                });
                return false;
            }

            _logger.LogDebug("Parsed connection string - Relays: {Relays}, RemotePubKey: {PubKey}", string.Join(", ", relayUrls!), remotePubKey?[..16] + "...");

            _remotePubKey = remotePubKey;
            _secret = secret;

            // Validate all relay URLs before connecting (SSRF / scheme check)
            foreach (var url in relayUrls!)
            {
                var validationError = await ValidateSignerRelayUrlAsync(url);
                if (validationError != null)
                {
                    _logger.LogWarning("Signer relay URL rejected: {Url} — {Error}", url, validationError);
                }
            }
            var validUrls = new List<string>();
            foreach (var url in relayUrls)
            {
                var err = await ValidateSignerRelayUrlAsync(url);
                if (err == null) validUrls.Add(url);
            }
            if (validUrls.Count == 0)
            {
                _logger.LogError("No valid signer relay URLs");
                _status.OnNext(new ExternalSignerStatus
                {
                    State = ExternalSignerState.Error,
                    Error = "No valid relay URLs"
                });
                return false;
            }

            // Generate ephemeral keypair for NIP-46 communication
            GenerateLocalKeyPair();
            _logger.LogDebug("Generated ephemeral keypair for NIP-46 communication");

            // Connect to all relays
            _logger.LogInformation("Connecting to {Count} signer relays: {Relays}", validUrls.Count, string.Join(", ", validUrls));
            _subscriptionSince = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5;
            _cts = new CancellationTokenSource();
            await ConnectToRelaysAsync(validUrls);

            // Send connect request
            _logger.LogInformation("Sending connect request to signer, waiting for approval...");
            _status.OnNext(new ExternalSignerStatus { State = ExternalSignerState.WaitingForApproval });
            var connectParams = _secret != null
                ? new[] { _localPublicKeyHex!, _secret }
                : new[] { _localPublicKeyHex! };
            var response = await SendRequestAsync("connect", connectParams);
            _logger.LogDebug("Received connect response: {Response}", response);

            if (response == "ack")
            {
                // Mark connected BEFORE awaiting get_public_key. The unmatched-response
                // branch in ProcessDecryptedNip46Message uses `!IsConnected` as the
                // signal to interpret a stray response as a nostrconnect connect-ack;
                // if IsConnected were still false during this await window, a duplicate
                // relay fanout of the connect ack would mis-route through
                // HandleIncomingConnect and overwrite PublicKeyHex with the bunker's
                // transport pubkey — exactly the bug that produced the wrong npub.
                IsConnected = true;

                // Get public key from signer
                _logger.LogInformation("Connection approved, fetching public key from signer");
                PublicKeyHex = await GetPublicKeyAsync();
                _logger.LogInformation("Successfully connected to external signer. Public key: {PubKey}", PublicKeyHex[..16] + "...");
                _status.OnNext(new ExternalSignerStatus
                {
                    State = ExternalSignerState.Connected,
                    IsConnected = true,
                    PublicKeyHex = PublicKeyHex
                });
                return true;
            }
            else
            {
                _logger.LogWarning("Connection rejected by signer. Response: {Response}", response);
                _status.OnNext(new ExternalSignerStatus
                {
                    State = ExternalSignerState.Error,
                    Error = "Connection rejected by signer"
                });
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to external signer");
            _status.OnNext(new ExternalSignerStatus
            {
                State = ExternalSignerState.Error,
                Error = ex.Message
            });
            return false;
        }
    }

    public async Task<bool> RestoreSessionAsync(IEnumerable<string> relayUrls, string remotePubKey, string localPrivateKeyHex, string localPublicKeyHex, string? secret = null)
    {
        var urls = relayUrls.Distinct().ToList();
        _logger.LogInformation("Restoring NIP-46 session: relays={Relays}, remotePubKey={PubKey}, localPubKey={LocalPub}",
            string.Join(", ", urls), remotePubKey[..Math.Min(16, remotePubKey.Length)], localPublicKeyHex[..Math.Min(16, localPublicKeyHex.Length)]);

        try
        {
            _status.OnNext(new ExternalSignerStatus { State = ExternalSignerState.Connecting });

            _remotePubKey = remotePubKey;
            _secret = secret;
            _localPrivateKeyHex = localPrivateKeyHex;
            _localPublicKeyHex = localPublicKeyHex;

            // Validate relay URLs (SSRF / scheme check)
            var validUrls = new List<string>();
            foreach (var url in urls)
            {
                var err = await ValidateSignerRelayUrlAsync(url);
                if (err != null)
                    _logger.LogWarning("Signer relay URL rejected during restore: {Url} — {Error}", url, err);
                else
                    validUrls.Add(url);
            }
            if (validUrls.Count == 0)
            {
                _logger.LogError("No valid signer relay URLs during restore");
                _status.OnNext(new ExternalSignerStatus
                {
                    State = ExternalSignerState.Error,
                    Error = "No valid relay URLs"
                });
                return false;
            }

            // Do NOT seed PublicKeyHex with remotePubKey — remotePubKey is the
            // NIP-46 transport key (the bunker key) and is NOT necessarily the
            // user's signing key. If get_public_key fails below we MUST fail the
            // restore rather than silently use the wrong key as the user's identity.
            PublicKeyHex = null;

            // Connect WebSockets to all relays
            _subscriptionSince = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5;
            _cts = new CancellationTokenSource();
            await ConnectToRelaysAsync(validUrls);

            IsConnected = true;

            // Fetch the actual signing pubkey — remotePubKey is the NIP-46 communication key,
            // which may differ from the key Amber uses to sign events.
            // Use a shorter timeout than sign_event: get_public_key is automatic (no user
            // interaction), so if the signer is online it responds near-instantly. A shorter
            // timeout allows the caller to retry sooner when the signer is momentarily offline.
            string? signingPubKey = null;
            try
            {
                signingPubKey = await SendRequestAsync("get_public_key", Array.Empty<string>(), TimeSpan.FromSeconds(20));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch signing pubkey from signer during restore");
            }

            if (string.IsNullOrEmpty(signingPubKey) || signingPubKey.Length != 64)
            {
                _logger.LogError("Signer did not return a valid signing pubkey during restore (got '{Value}'); aborting restore to avoid using transport key as user identity",
                    signingPubKey);
                IsConnected = false;
                _status.OnNext(new ExternalSignerStatus
                {
                    State = ExternalSignerState.Error,
                    Error = "Signer did not return a valid public key"
                });
                return false;
            }

            if (signingPubKey != remotePubKey)
            {
                _logger.LogInformation("NIP-46 signing pubkey differs from remote pubkey: signing={SigningKey}, remote={RemoteKey}",
                    signingPubKey[..Math.Min(16, signingPubKey.Length)] + "...",
                    remotePubKey[..Math.Min(16, remotePubKey.Length)] + "...");
            }
            PublicKeyHex = signingPubKey;

            _logger.LogInformation("NIP-46 session restored on {Count} relays. Ready to send requests.", _relayConnections.Count);
            _status.OnNext(new ExternalSignerStatus
            {
                State = ExternalSignerState.Connected,
                IsConnected = true,
                PublicKeyHex = PublicKeyHex
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore NIP-46 session");
            _status.OnNext(new ExternalSignerStatus
            {
                State = ExternalSignerState.Error,
                Error = ex.Message
            });
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        IsConnected = false;
        PublicKeyHex = null;

        _cts?.Cancel();

        foreach (var conn in _relayConnections)
        {
            if (conn.WebSocket?.State == WebSocketState.Open)
            {
                try
                {
                    await conn.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
                }
                catch { }
            }
            conn.Dispose();
        }
        _relayConnections.Clear();
        _cts?.Dispose();
        _cts = null;

        _status.OnNext(new ExternalSignerStatus { State = ExternalSignerState.Disconnected });
    }

    public async Task<string> GetPublicKeyAsync()
    {
        var response = await SendRequestAsync("get_public_key", Array.Empty<string>());
        return response;
    }

    public async Task<string?> ResolveSigningPubKeyAsync()
    {
        try
        {
            var pubKey = await GetPublicKeyAsync();
            if (!string.IsNullOrEmpty(pubKey) && pubKey.Length == 64)
            {
                if (!string.Equals(pubKey, PublicKeyHex, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "NIP-46 signing pubkey resolved — differs from transport pubkey (signing={Signing}, transport={Transport})",
                        pubKey[..Math.Min(16, pubKey.Length)] + "...",
                        PublicKeyHex?[..Math.Min(16, PublicKeyHex.Length)] + "...");
                }
                PublicKeyHex = pubKey;
                return pubKey;
            }
            _logger.LogWarning("get_public_key returned an invalid value: '{Value}'", pubKey);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve signing pubkey via get_public_key");
            return null;
        }
    }

    public async Task<string> SignEventAsync(UnsignedNostrEvent unsignedEvent)
    {
        var eventJson = JsonSerializer.Serialize(new
        {
            kind = unsignedEvent.Kind,
            content = unsignedEvent.Content,
            tags = unsignedEvent.Tags,
            created_at = new DateTimeOffset(unsignedEvent.CreatedAt).ToUnixTimeSeconds()
        });

        // sign_event requires user approval on the signer app — 60 s is enough to pull out your phone
        var response = await SendRequestAsync("sign_event", new[] { eventJson }, TimeSpan.FromSeconds(60));
        return response;
    }

    public async Task<string> Nip44EncryptAsync(string plaintext, string recipientPubKey)
    {
        var response = await SendRequestAsync("nip44_encrypt", new[] { recipientPubKey, plaintext });
        return response;
    }

    public async Task<string> Nip44DecryptAsync(string ciphertext, string senderPubKey)
    {
        var response = await SendRequestAsync("nip44_decrypt", new[] { senderPubKey, ciphertext });
        return response;
    }

    public string GenerateConnectionUri(IEnumerable<string> relayUrls)
    {
        GenerateLocalKeyPair();
        _secret = GenerateRandomSecret();
        var perms = "nip04_encrypt,nip04_decrypt,nip44_encrypt,nip44_decrypt,sign_event:443,sign_event:444,sign_event:445,sign_event:1059";
        // Relay URLs are not percent-encoded: Amber's bunker:// URIs use raw
        // wss://… in the relay query value, and Amber's nostrconnect parser
        // appears to fail validation when relays come back as wss%3A%2F%2F….
        // RFC 3986 allows ':' and '/' in query values un-encoded, so this is
        // still a valid URI. perms stays escaped (commas need encoding).
        var relayParams = string.Join("", relayUrls.Select(r => $"&relay={r}"));
        var uri = $"nostrconnect://{_localPublicKeyHex}?{relayParams.TrimStart('&')}&secret={_secret}&name=Scramble&perms={Uri.EscapeDataString(perms)}";
        _logger.LogInformation("Generated nostrconnect URI for {Count} relays, local pubkey {PubKey}", relayUrls.Count(), _localPublicKeyHex?[..16]);
        // Debug-only log of the full URI so we can diagnose connection issues
        // from the log file. Contains the secret — never persist beyond local logs.
        _logger.LogDebug("nostrconnect URI: {Uri}", uri);
        return uri;
    }

    public async Task<string> GenerateAndListenForConnectionAsync(IEnumerable<string> relayUrls)
    {
        var urls = relayUrls.Distinct().ToList();
        _logger.LogInformation("Generating nostrconnect URI and listening on {Count} relays: {Relays}", urls.Count, string.Join(", ", urls));

        // Validate all relay URLs (SSRF / scheme check)
        var validUrls = new List<string>();
        foreach (var url in urls)
        {
            var err = await ValidateSignerRelayUrlAsync(url);
            if (err != null)
                _logger.LogWarning("Signer relay URL rejected: {Url} — {Error}", url, err);
            else
                validUrls.Add(url);
        }
        if (validUrls.Count == 0)
            throw new ArgumentException("No valid relay URLs provided");

        // Reset connection state so Amber's connect ack is recognized (not cached as "replayed")
        IsConnected = false;

        var uri = GenerateConnectionUri(validUrls);

        _logger.LogInformation("Generated nostrconnect URI. Local pubkey: {PubKey}", _localPublicKeyHex);

        _status.OnNext(new ExternalSignerStatus { State = ExternalSignerState.Connecting });

        _subscriptionSince = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120; // 2 min window for slow connections
        _cts = new CancellationTokenSource();
        await ConnectToRelaysAsync(validUrls);
        _logger.LogInformation("Subscribed to kind 24133 events on {Count} relays. Waiting for signer...", _relayConnections.Count);

        _status.OnNext(new ExternalSignerStatus { State = ExternalSignerState.WaitingForApproval });

        return uri;
    }

    public async Task ReconnectAsync()
    {
        // No-op if already connected or no prior connection state
        if (IsConnected || _relayConnections.Count == 0 || _localPrivateKeyHex == null || _localPublicKeyHex == null)
        {
            _logger.LogDebug("ReconnectAsync: skipping (IsConnected={IsConnected}, relayCount={Count})",
                IsConnected, _relayConnections.Count);
            return;
        }

        _logger.LogInformation("Reconnecting to {Count} signer relays after app resume", _relayConnections.Count);

        try
        {
            // Clean up old connections
            _cts?.Cancel();
            foreach (var conn in _relayConnections)
            {
                try { conn.WebSocket?.Dispose(); } catch { }
                conn.WebSocket = null;
            }
            _cts?.Dispose();

            // Re-establish connections (keep the original _subscriptionSince so we don't miss events)
            _cts = new CancellationTokenSource();
            var reconnected = 0;
            foreach (var conn in _relayConnections)
            {
                try
                {
                    var validationError = await ValidateSignerRelayUrlAsync(conn.Url);
                    if (validationError != null)
                    {
                        _logger.LogWarning("Signer relay URL rejected on reconnect: {Url} — {Error}", conn.Url, validationError);
                        continue;
                    }

                    conn.WebSocket = new ClientWebSocket();
                    conn.WebSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    await conn.WebSocket.ConnectAsync(new Uri(conn.Url), _cts.Token);
                    _ = Task.Run(() => ListenForMessagesAsync(conn, _cts.Token));
                    await SubscribeToSignerAsync(conn);
                    reconnected++;
                    _logger.LogInformation("Reconnected to signer relay {Relay}", conn.Url);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to reconnect to signer relay {Relay}", conn.Url);
                }
            }

            if (reconnected > 0)
            {
                IsConnected = true;
                _status.OnNext(new ExternalSignerStatus
                {
                    State = ExternalSignerState.Connected,
                    IsConnected = true,
                    PublicKeyHex = PublicKeyHex
                });
                _logger.LogInformation("Reconnected to {Count} signer relays (since={Since})", reconnected, _subscriptionSince);
            }
            else
            {
                _logger.LogError("Failed to reconnect to any signer relay");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconnect to signer relays");
        }
    }

    private bool ParseConnectionString(string connectionString, out string? remotePubKey, out List<string>? relayUrls, out string? secret)
    {
        remotePubKey = null;
        relayUrls = null;
        secret = null;

        // Trim whitespace/newlines — users often paste bunker URLs with leading/trailing
        // whitespace from messaging apps, QR code scanners, or clipboard managers.
        connectionString = connectionString.Trim();

        if (string.IsNullOrEmpty(connectionString))
            return false;

        try
        {
            Uri uri;

            // Handle bunker:// URLs
            if (connectionString.StartsWith("bunker://", StringComparison.OrdinalIgnoreCase))
            {
                uri = new Uri(connectionString);
                remotePubKey = uri.Host;
            }
            // Handle nostrconnect:// URLs
            else if (connectionString.StartsWith("nostrconnect://", StringComparison.OrdinalIgnoreCase))
            {
                uri = new Uri(connectionString);
                remotePubKey = uri.Host;
            }
            else
            {
                _logger.LogWarning("Connection string has unrecognized scheme (expected bunker:// or nostrconnect://)");
                return false;
            }

            if (string.IsNullOrEmpty(remotePubKey) || remotePubKey.Length != 64 ||
                !remotePubKey.All(c => "0123456789abcdef".Contains(c)))
            {
                _logger.LogWarning("Connection string has invalid remote pubkey: '{PubKey}'",
                    remotePubKey?[..Math.Min(16, remotePubKey?.Length ?? 0)]);
                return false;
            }

            // Parse query parameters — collect all relay= values
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var relays = query.GetValues("relay");
            relayUrls = relays?.Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList() ?? new();
            secret = query["secret"];

            if (relayUrls.Count == 0)
            {
                _logger.LogWarning("Connection string has no relay= parameters");
                return false;
            }

            return true;
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning(ex, "Connection string is not a valid URI");
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task ConnectToRelaysAsync(List<string> urls)
    {
        // Clean up existing connections
        foreach (var conn in _relayConnections)
            conn.Dispose();
        _relayConnections.Clear();

        foreach (var url in urls)
        {
            var conn = new RelayConnection(url);
            try
            {
                conn.WebSocket = new ClientWebSocket();
                conn.WebSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token, connectTimeout.Token);
                await conn.WebSocket.ConnectAsync(new Uri(url), linked.Token);
                _relayConnections.Add(conn);
                _ = Task.Run(() => ListenForMessagesAsync(conn, _cts.Token));
                await SubscribeToSignerAsync(conn);
                _logger.LogInformation("Connected to signer relay {Relay}", url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to signer relay {Relay}", url);
                conn.Dispose();
            }
        }

        if (_relayConnections.Count == 0)
            throw new InvalidOperationException("Failed to connect to any signer relay");

        _logger.LogInformation("Connected to {Count}/{Total} signer relays", _relayConnections.Count, urls.Count);
    }

    /// <summary>
    /// Sends a NIP-46 event to a single signer relay, falling back to the next relay
    /// if the first one rejects the event (e.g. insufficient PoW). Waits briefly for
    /// each relay's OK response; if no OK arrives within 3 seconds the event is assumed
    /// accepted (some relays don't send OK messages). This prevents the "nos.lol PoW
    /// rejection" bug where the only relay tried rejects the event and the request times out.
    /// </summary>
    private async Task SendToOneRelayAsync(byte[] bytes, string eventId)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        foreach (var conn in _relayConnections)
        {
            if (conn.WebSocket?.State != WebSocketState.Open)
                continue;

            try
            {
                // Register OK tracker before sending so we don't miss a fast reply
                var okTcs = new TaskCompletionSource<(bool accepted, string? reason)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingOkResponses[eventId] = okTcs;

                await conn.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                _logger.LogDebug("NIP-46 sent to signer relay {Relay}", conn.Url);

                // Wait for relay OK/rejection — relays typically respond within milliseconds
                using var okTimeout = new CancellationTokenSource(3000);
                okTimeout.Token.Register(() => okTcs.TrySetResult((true, null)));
                var (accepted, reason) = await okTcs.Task;

                _pendingOkResponses.TryRemove(eventId, out _);

                if (accepted)
                    return;

                _logger.LogWarning("NIP-46 event {EventId} rejected by {Relay}: {Reason} — trying next relay",
                    eventId[..Math.Min(16, eventId.Length)], conn.Url, reason);
            }
            catch (Exception ex)
            {
                _pendingOkResponses.TryRemove(eventId, out _);
                _logger.LogWarning(ex, "Failed to send to signer relay {Relay}, trying next", conn.Url);
            }
        }
        _logger.LogError("NIP-46 failed to send: all relays rejected or unavailable");
    }

    /// <summary>
    /// Sends a message to all open signer relays. Only used for ack responses
    /// where delivery reliability matters more than avoiding duplicates.
    /// </summary>
    private async Task BroadcastToRelaysAsync(byte[] bytes)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        foreach (var conn in _relayConnections)
        {
            if (conn.WebSocket?.State == WebSocketState.Open)
            {
                try
                {
                    await conn.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send to signer relay {Relay}", conn.Url);
                }
            }
        }
    }

    private void GenerateLocalKeyPair()
    {
        var privateKeyBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(privateKeyBytes);

        _localPrivateKeyHex = Convert.ToHexString(privateKeyBytes).ToLowerInvariant();

        if (Context.Instance.TryCreateECPrivKey(privateKeyBytes, out var ecPrivKey) && ecPrivKey != null)
        {
            var pubKey = ecPrivKey.CreateXOnlyPubKey();
            var publicKeyBytes = new byte[32];
            pubKey.WriteToSpan(publicKeyBytes);
            _localPublicKeyHex = Convert.ToHexString(publicKeyBytes).ToLowerInvariant();
        }
    }

    private static string GenerateRandomSecret()
    {
        var bytes = new byte[16];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task SubscribeToSignerAsync(RelayConnection conn)
    {
        if (conn.WebSocket == null || _localPublicKeyHex == null) return;

        // Subscribe to kind 24133 events (NIP-46 responses) tagged to our local pubkey
        // Use the original subscription time so reconnects don't miss events sent while backgrounded
        var subscriptionId = Guid.NewGuid().ToString("N")[..8];
        var filter = new Dictionary<string, object>
        {
            ["kinds"] = new[] { 24133 },
            ["#p"] = new[] { _localPublicKeyHex },
            ["since"] = _subscriptionSince
        };

        var req = JsonSerializer.Serialize(new object[] { "REQ", subscriptionId, filter });
        _logger.LogDebug("NIP-46 sending REQ to {Relay}: {Req}", conn.Url, req);
        var bytes = Encoding.UTF8.GetBytes(req);
        await conn.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
    }

    private Task<string> SendRequestAsync(string method, string[] @params)
        => SendRequestAsync(method, @params, TimeSpan.FromSeconds(60));

    private async Task<string> SendRequestAsync(string method, string[] @params, TimeSpan timeout)
    {
        if (_localPrivateKeyHex == null || _remotePubKey == null)
        {
            throw new InvalidOperationException("Not connected — no signer session");
        }

        // Auto-reconnect if no WebSockets are open (e.g. app was backgrounded)
        var hasOpenSocket = _relayConnections.Any(c => c.WebSocket?.State == WebSocketState.Open);
        if (!hasOpenSocket)
        {
            _logger.LogWarning("No open WebSockets for {Method} — attempting reconnect", method);
            _replayedSignEventResult = null;
            IsConnected = false;
            await ReconnectAsync();
            hasOpenSocket = _relayConnections.Any(c => c.WebSocket?.State == WebSocketState.Open);
            if (!hasOpenSocket)
            {
                throw new InvalidOperationException(
                    $"Cannot send {method}: WebSocket reconnect failed — no relays connected");
            }

            // Wait briefly for the relay to replay stored responses from while we were offline.
            // Amber may have already sent the signed response while the socket was dead.
            _logger.LogInformation("Waiting for relay to replay stored responses...");
            await Task.Delay(3000);

            if (_replayedSignEventResult != null)
            {
                _logger.LogInformation("Found replayed {Method} response — using it instead of sending duplicate request", method);
                var result = _replayedSignEventResult;
                _replayedSignEventResult = null;
                return result;
            }

            _logger.LogInformation("No replayed response found — sending new {Method} request", method);
        }

        var requestId = Guid.NewGuid().ToString("N");
        var request = new Nip46Request
        {
            Id = requestId,
            Method = method,
            Params = @params
        };

        var requestJson = JsonSerializer.Serialize(request);

        // Encrypt the request using NIP-44 (AEAD — replaces deprecated NIP-04 AES-CBC)
        var encryptedContent = EncryptNip44(requestJson, _localPrivateKeyHex, _remotePubKey);

        // Create and sign the event (with optional NIP-13 proof-of-work)
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var baseTags = new[] { new[] { "p", _remotePubKey } };

        string eventId;
        string[][] tags;
        if (Nip46ProofOfWorkBits > 0)
        {
            (eventId, tags) = MineProofOfWork(24133, _localPublicKeyHex!, createdAt,
                baseTags, encryptedContent, Nip46ProofOfWorkBits, ProofOfWorkTimeout);
        }
        else
        {
            tags = baseTags;
            eventId = ComputeEventId(24133, _localPublicKeyHex!, createdAt, baseTags, encryptedContent);
        }

        var signature = SignEventId(eventId, _localPrivateKeyHex);

        // Serialize EVENT message with Utf8JsonWriter to ensure correct format
        using var eventMs = new MemoryStream();
        using (var ew = new Utf8JsonWriter(eventMs, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            ew.WriteStartArray();
            ew.WriteStringValue("EVENT");
            ew.WriteStartObject();
            ew.WriteString("id", eventId);
            ew.WriteString("pubkey", _localPublicKeyHex);
            ew.WriteNumber("created_at", createdAt);
            ew.WriteNumber("kind", 24133);
            ew.WritePropertyName("tags");
            ew.WriteStartArray();
            foreach (var tag in tags)
            {
                ew.WriteStartArray();
                foreach (var v in tag)
                    ew.WriteStringValue(v);
                ew.WriteEndArray();
            }
            ew.WriteEndArray();
            ew.WriteString("content", encryptedContent);
            ew.WriteString("sig", signature);
            ew.WriteEndObject();
            ew.WriteEndArray();
        }
        var eventMessage = Encoding.UTF8.GetString(eventMs.ToArray());
        var bytes = Encoding.UTF8.GetBytes(eventMessage);

        _logger.LogDebug("NIP-46 sending {Method} request {Id}: {Event}", method, requestId[..8],
            eventMessage.Length > 300 ? eventMessage[..300] + "..." : eventMessage);

        var tcs = new TaskCompletionSource<string>();
        _pendingRequests[requestId] = tcs;

        // Send to one relay with fallback — if first relay rejects, try the next
        await SendToOneRelayAsync(bytes, eventId);

        // Wait for response with timeout (sign_event needs longer for user approval)
        using var timeoutCts = new CancellationTokenSource(timeout);
        timeoutCts.Token.Register(() =>
        {
            _pendingRequests.Remove(requestId);
            tcs.TrySetException(new TimeoutException($"Signer request timed out after {timeout.TotalSeconds}s ({method})"));
        });

        return await tcs.Task;
    }

    /// <summary>
    /// Processes a decrypted NIP-46 JSON message (request or response) from a signer.
    /// Extracted for testability — called by ProcessMessage after NIP-04/NIP-44 decryption.
    /// </summary>
    internal void ProcessDecryptedNip46Message(string decryptedJson, string senderPubKey)
    {
        using var nip46Doc = JsonDocument.Parse(decryptedJson);
        if (nip46Doc.RootElement.TryGetProperty("method", out var methodProp))
        {
            // Incoming request from signer (nostrconnect:// flow)
            var method = methodProp.GetString();
            var reqId = nip46Doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            _logger.LogInformation("NIP-46 incoming request: method={Method}, id={Id}", method, reqId);

            if (method == "connect")
            {
                HandleIncomingConnect(senderPubKey, nip46Doc.RootElement, reqId);
            }
        }
        else
        {
            // Response to a pending request (bunker:// flow)
            var response = JsonSerializer.Deserialize<Nip46Response>(decryptedJson);

            if (response?.Id != null && _pendingRequests.TryGetValue(response.Id, out var tcs))
            {
                _pendingRequests.Remove(response.Id);

                if (response.Error != null)
                {
                    _logger.LogWarning("NIP-46 signer returned error: {Error}", response.Error);
                    tcs.TrySetException(new Exception(response.Error));
                }
                else
                {
                    var idPreview = response.Id.Length >= 8 ? response.Id[..8] : response.Id;
                    _logger.LogInformation("NIP-46 signer returned result for request {Id}", idPreview);
                    tcs.TrySetResult(response.Result ?? "");
                }
            }
            else if (!IsConnected && response?.Result != null && _pendingRequests.Count == 0)
            {
                // Unmatched response while not connected AND nothing pending — this is
                // Amber's connect ack for the nostrconnect:// flow (Amber sends a
                // response with the secret as result). For the bunker:// flow there is
                // ALWAYS a pending request at this point (the connect or get_public_key
                // request we just sent), so the pending-count guard prevents the bunker
                // reply from being mis-routed here and clobbering PublicKeyHex with the
                // bunker's transport pubkey.
                _logger.LogInformation("NIP-46 treating unmatched response as connect ack from {Sender}", senderPubKey[..16]);
                HandleIncomingConnect(senderPubKey, nip46Doc.RootElement, response?.Id);
            }
            else if (response?.Result != null && response.Error == null)
            {
                // Unmatched response while already connected — likely a sign_event response replayed after reconnect.
                var idStr = response.Id != null && response.Id.Length >= 8 ? response.Id[..8] : response.Id ?? "?";
                _logger.LogInformation("NIP-46 caching unmatched response {Id} (likely replayed after reconnect)", idStr);
                _replayedSignEventResult = response.Result;
            }
            else
            {
                _logger.LogWarning("NIP-46 received response for unknown request: {Id}", response?.Id);
            }
        }
    }

    private void HandleIncomingConnect(string senderPubKey, JsonElement root, string? reqId)
    {
        _remotePubKey = senderPubKey;

        // Do NOT default PublicKeyHex to senderPubKey. The sender pubkey is the
        // NIP-46 *transport* key (the bunker key), not the user's signing pubkey.
        // For Amber and other signers these can be different, and using the
        // transport key as the user identity caused a wrong-npub bug where the
        // chat list was filtered against the bunker pubkey and showed empty.
        // Only set PublicKeyHex from an explicit signing-key payload below; if
        // none is present, leave the previous value (caller will resolve it via
        // a follow-up get_public_key).
        string? signingPubKey = null;

        // Try to extract the actual signer pubkey from params[0] if available
        if (root.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Array && paramsProp.GetArrayLength() > 0)
        {
            var p0 = paramsProp[0].GetString();
            if (!string.IsNullOrEmpty(p0) && p0.Length == 64)
                signingPubKey = p0;
        }
        // Or from result field (some signers put pubkey there)
        else if (root.TryGetProperty("result", out var resultProp))
        {
            var resultVal = resultProp.GetString();
            if (!string.IsNullOrEmpty(resultVal) && resultVal.Length == 64)
                signingPubKey = resultVal;
        }

        if (signingPubKey != null)
            PublicKeyHex = signingPubKey;

        IsConnected = true;

        if (reqId != null)
            _ = SendNip46ResponseAsync(reqId, "ack", senderPubKey);

        var pubKeyForLog = PublicKeyHex != null && PublicKeyHex.Length >= 16 ? PublicKeyHex[..16] : (PublicKeyHex ?? "(unresolved — pending get_public_key)");
        _logger.LogInformation("NIP-46 signer connected via nostrconnect. PubKey: {PubKey}", pubKeyForLog);
        _status.OnNext(new ExternalSignerStatus
        {
            State = ExternalSignerState.Connected,
            IsConnected = true,
            PublicKeyHex = PublicKeyHex
        });
    }

    private async Task SendNip46ResponseAsync(string requestId, string result, string recipientPubKey)
    {
        if (_localPrivateKeyHex == null || _localPublicKeyHex == null) return;

        try
        {
            var responseJson = JsonSerializer.Serialize(new { id = requestId, result });
            var encryptedContent = EncryptNip44(responseJson, _localPrivateKeyHex, recipientPubKey);

            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var baseTags = new[] { new[] { "p", recipientPubKey } };

            string eventId;
            string[][] tags;
            if (Nip46ProofOfWorkBits > 0)
            {
                (eventId, tags) = MineProofOfWork(24133, _localPublicKeyHex, createdAt,
                    baseTags, encryptedContent, Nip46ProofOfWorkBits, ProofOfWorkTimeout);
            }
            else
            {
                tags = baseTags;
                eventId = ComputeEventId(24133, _localPublicKeyHex, createdAt, baseTags, encryptedContent);
            }

            var signature = SignEventId(eventId, _localPrivateKeyHex);

            using var ms = new MemoryStream();
            using (var ew = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                ew.WriteStartArray();
                ew.WriteStringValue("EVENT");
                ew.WriteStartObject();
                ew.WriteString("id", eventId);
                ew.WriteString("pubkey", _localPublicKeyHex);
                ew.WriteNumber("created_at", createdAt);
                ew.WriteNumber("kind", 24133);
                ew.WritePropertyName("tags");
                ew.WriteStartArray();
                foreach (var tag in tags)
                {
                    ew.WriteStartArray();
                    foreach (var v in tag) ew.WriteStringValue(v);
                    ew.WriteEndArray();
                }
                ew.WriteEndArray();
                ew.WriteString("content", encryptedContent);
                ew.WriteString("sig", signature);
                ew.WriteEndObject();
                ew.WriteEndArray();
            }

            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ms.ToArray()));
            await BroadcastToRelaysAsync(bytes);
            _logger.LogDebug("NIP-46 sent ack response for request {Id}", requestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NIP-46 failed to send response for request {Id}", requestId);
        }
    }

    private async Task ListenForMessagesAsync(RelayConnection conn, CancellationToken ct)
    {
        _logger.LogInformation("NIP-46 WebSocket listener started for {Relay}. State: {State}", conn.Url, conn.WebSocket?.State);
        var buffer = new byte[8192];
        var messageBuffer = new List<byte>();

        while (!ct.IsCancellationRequested && conn.WebSocket?.State == WebSocketState.Open)
        {
            try
            {
                var result = await conn.WebSocket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("NIP-46 WebSocket closed by relay {Relay}", conn.Url);
                    break;
                }

                messageBuffer.AddRange(buffer.Take(result.Count));

                if (result.EndOfMessage)
                {
                    var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    messageBuffer.Clear();
                    // Debug-level: every NIP-46 relay frame echoes encrypted base64 content.
                    // At INF on a 5-relay setup this produced ~94% of a 25 MB daily log.
                    _logger.LogDebug("NIP-46 raw relay data from {Relay} ({Len} bytes): {Data}",
                        conn.Url, message.Length, message.Length > 300 ? message[..300] + "..." : message);
                    ProcessMessage(message, conn);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("NIP-46 WebSocket listener cancelled for {Relay}", conn.Url);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NIP-46 WebSocket listener error for {Relay}. State: {State}", conn.Url, conn.WebSocket?.State);
            }
        }

        _logger.LogWarning("NIP-46 WebSocket listener exited for {Relay}. State: {State}, Cancelled: {Cancelled}",
            conn.Url, conn.WebSocket?.State, ct.IsCancellationRequested);
    }

    /// <summary>
    /// Returns true the first time a given NIP-46 event id is observed and false on subsequent
    /// per-relay duplicates. Bounded FIFO cache; the same event id observed after eviction will
    /// log again, which is acceptable for diagnostic noise reduction.
    /// </summary>
    private bool ShouldLogReceivedEvent(string eventId)
    {
        lock (_loggedEventsLock)
        {
            if (!_loggedEventIds.Add(eventId))
                return false;

            _loggedEventOrder.Enqueue(eventId);
            if (_loggedEventOrder.Count > LoggedEventsCapacity)
            {
                var evicted = _loggedEventOrder.Dequeue();
                _loggedEventIds.Remove(evicted);
            }
            return true;
        }
    }

    private void ProcessMessage(string message, RelayConnection conn)
    {
        try
        {
            _logger.LogDebug("NIP-46 relay message from {Relay}: {Message}", conn.Url, message.Length > 500 ? message[..500] + "..." : message);

            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2)
                return;

            var messageType = root[0].GetString();

            if (messageType == "OK" && root.GetArrayLength() >= 4)
            {
                var eventId = root[1].GetString();
                var accepted = root[2].GetBoolean();
                var reason = root.GetArrayLength() > 3 ? root[3].GetString() : "";

                // Resolve pending OK tracker (used by SendToOneRelayAsync for relay fallback)
                if (eventId != null && _pendingOkResponses.TryRemove(eventId, out var okTcs))
                    okTcs.TrySetResult((accepted, reason));

                if (accepted)
                    _logger.LogInformation("NIP-46 event {EventId} accepted by {Relay}", eventId?[..16], conn.Url);
                else
                    _logger.LogError("NIP-46 event {EventId} REJECTED by {Relay}: {Reason}", eventId?[..16], conn.Url, reason);
            }
            else if (messageType == "NOTICE")
            {
                var notice = root[1].GetString();
                _logger.LogWarning("NIP-46 relay NOTICE from {Relay}: {Notice}", conn.Url, notice);
            }
            else if (messageType == "EOSE")
            {
                _logger.LogDebug("NIP-46 end of stored events from {Relay}", conn.Url);
            }
            else if (messageType == "AUTH" && root.GetArrayLength() >= 2)
            {
                var challenge = root[1].GetString();
                _logger.LogInformation("NIP-42 AUTH challenge from {Relay}: {Challenge}", conn.Url, challenge);
                if (!string.IsNullOrEmpty(challenge))
                {
                    _ = HandleAuthChallengeAsync(challenge, conn);
                }
            }
            else if (messageType == "EVENT" && root.GetArrayLength() >= 3)
            {
                var eventObj = root[2];
                var kind = eventObj.GetProperty("kind").GetInt32();
                var senderPubKey = eventObj.GetProperty("pubkey").GetString();
                var eventId = eventObj.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

                // Suppress per-relay fanout: a single event delivered on N NIP-46 relays
                // used to log N INFO lines. Now we log once per unique event id.
                if (eventId == null || ShouldLogReceivedEvent(eventId))
                {
                    _logger.LogInformation("NIP-46 received event kind={Kind} from {Sender}", kind, senderPubKey?[..16]);
                }
                else
                {
                    _logger.LogDebug("NIP-46 duplicate event {EventId} from {Relay} (already processed)", eventId[..16], conn.Url);
                }

                if (kind == 24133 && _localPrivateKeyHex != null)
                {
                    var content = eventObj.GetProperty("content").GetString();

                    if (content != null && senderPubKey != null)
                    {
                        string decrypted;
                        if (content.Contains("?iv="))
                        {
                            // NIP-04 format
                            decrypted = DecryptNip04(content, _localPrivateKeyHex, senderPubKey);
                            _logger.LogDebug("NIP-46 decrypted (NIP-04): {Length} bytes", decrypted.Length);
                        }
                        else
                        {
                            // NIP-44 format
                            decrypted = DecryptNip44(content, _localPrivateKeyHex, senderPubKey);
                            _logger.LogDebug("NIP-46 decrypted (NIP-44): {Length} bytes", decrypted.Length);
                        }

                        ProcessDecryptedNip46Message(decrypted, senderPubKey);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NIP-46 error processing relay message");
        }
    }

    private async Task HandleAuthChallengeAsync(string challenge, RelayConnection conn)
    {
        try
        {
            if (conn.WebSocket == null || conn.WebSocket.State != WebSocketState.Open ||
                _localPrivateKeyHex == null || _localPublicKeyHex == null)
            {
                _logger.LogWarning("NIP-42: cannot respond to AUTH — no connection or keys");
                return;
            }

            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var tags = new[] { new[] { "relay", conn.Url }, new[] { "challenge", challenge } };
            var eventId = ComputeEventId(22242, _localPublicKeyHex, createdAt, tags, "");
            var signature = SignEventId(eventId, _localPrivateKeyHex);

            using var ms = new MemoryStream();
            using (var ew = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                ew.WriteStartArray();
                ew.WriteStringValue("AUTH");
                ew.WriteStartObject();
                ew.WriteString("id", eventId);
                ew.WriteString("pubkey", _localPublicKeyHex);
                ew.WriteNumber("created_at", createdAt);
                ew.WriteNumber("kind", 22242);
                ew.WritePropertyName("tags");
                ew.WriteStartArray();
                foreach (var tag in tags)
                {
                    ew.WriteStartArray();
                    foreach (var v in tag) ew.WriteStringValue(v);
                    ew.WriteEndArray();
                }
                ew.WriteEndArray();
                ew.WriteString("content", "");
                ew.WriteString("sig", signature);
                ew.WriteEndObject();
                ew.WriteEndArray();
            }

            var bytes = ms.ToArray();
            await conn.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
            _logger.LogInformation("NIP-42: sent AUTH response to {Relay}", conn.Url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NIP-42: failed to handle AUTH challenge");
        }
    }

    private static string ComputeEventId(int kind, string pubkey, long createdAt, string[][] tags, string content)
    {
        // Use Utf8JsonWriter to avoid the object[] boxing bug with string[][] tags
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(0);
            writer.WriteStringValue(pubkey);
            writer.WriteNumberValue(createdAt);
            writer.WriteNumberValue(kind);

            // Tags: array of arrays of strings
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

        var hash = SHA256.HashData(ms.ToArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// NIP-13 proof-of-work miner. Appends a ["nonce","N","target"] tag and iterates
    /// until the event ID (SHA-256 hash) has at least <paramref name="targetBits"/>
    /// leading zero bits, or <paramref name="timeout"/> is exceeded.
    /// Uses parallel mining across available CPU cores for faster results.
    /// </summary>
    private static (string eventId, string[][] tags) MineProofOfWork(
        int kind, string pubkey, long createdAt,
        string[][] baseTags, string content,
        int targetBits, TimeSpan timeout)
    {
        var targetStr = targetBits.ToString();

        // Build the tags array template with a nonce slot at the end
        var allTags = new string[baseTags.Length + 1][];
        Array.Copy(baseTags, allTags, baseTags.Length);

        // Single-threaded fast path for very low difficulty (≤16 bits)
        if (targetBits <= 16)
        {
            for (long i = 0; i < 200_000; i++)
            {
                allTags[baseTags.Length] = new[] { "nonce", i.ToString(), targetStr };
                var id = ComputeEventId(kind, pubkey, createdAt, allTags, content);
                if (CountLeadingZeroBits(id) >= targetBits)
                    return (id, allTags);
            }
            // Shouldn't reach here for ≤16 bits, but fall through to return best effort
            allTags[baseTags.Length] = new[] { "nonce", "0", targetStr };
            return (ComputeEventId(kind, pubkey, createdAt, allTags, content), allTags);
        }

        // Parallel mining for higher difficulty
        var threadCount = Math.Max(1, Environment.ProcessorCount);
        var cts = new CancellationTokenSource(timeout);
        string? bestId = null;
        string[][]? bestTags = null;
        var found = new ManualResetEventSlim(false);

        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, threadIdx =>
        {
            // Each thread gets its own tags array copy to avoid contention
            var threadTags = new string[baseTags.Length + 1][];
            Array.Copy(baseTags, threadTags, baseTags.Length);

            for (long i = threadIdx; !cts.IsCancellationRequested && !found.IsSet; i += threadCount)
            {
                threadTags[baseTags.Length] = new[] { "nonce", i.ToString(), targetStr };
                var id = ComputeEventId(kind, pubkey, createdAt, threadTags, content);
                if (CountLeadingZeroBits(id) >= targetBits)
                {
                    // Clone the winning tags before signaling (other threads may modify theirs)
                    var winningTags = new string[threadTags.Length][];
                    Array.Copy(threadTags, winningTags, threadTags.Length);
                    winningTags[baseTags.Length] = new[] { "nonce", i.ToString(), targetStr };
                    Interlocked.CompareExchange(ref bestId, id, null);
                    Interlocked.CompareExchange(ref bestTags, winningTags, null);
                    found.Set();
                    return;
                }
            }
        });

        if (bestId != null && bestTags != null)
            return (bestId, bestTags);

        // Timeout — return with whatever nonce we ended on (won't meet target but
        // the relay fallback will route around relays that reject it)
        allTags[baseTags.Length] = new[] { "nonce", "0", targetStr };
        return (ComputeEventId(kind, pubkey, createdAt, allTags, content), allTags);
    }

    /// <summary>
    /// Count leading zero bits in a lowercase hex event ID string.
    /// Each hex char contributes 0-4 leading zero bits.
    /// </summary>
    internal static int CountLeadingZeroBits(string hexId)
    {
        int bits = 0;
        foreach (var c in hexId)
        {
            var nibble = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1
            };
            if (nibble < 0) break;
            if (nibble == 0) { bits += 4; continue; }
            // Count leading zeros in a 4-bit nibble
            if (nibble < 2) bits += 3;
            else if (nibble < 4) bits += 2;
            else if (nibble < 8) bits += 1;
            break;
        }
        return bits;
    }

    private static string SignEventId(string eventId, string privateKeyHex)
    {
        var privateKeyBytes = Convert.FromHexString(privateKeyHex);
        var messageBytes = Convert.FromHexString(eventId);

        if (!Context.Instance.TryCreateECPrivKey(privateKeyBytes, out var ecPrivKey) || ecPrivKey == null)
        {
            throw new ArgumentException("Invalid private key");
        }

        // Sign with Schnorr signature
        if (!ecPrivKey.TrySignBIP340(messageBytes, null, out var signature))
        {
            throw new InvalidOperationException("Failed to sign event");
        }
        var sigBytes = new byte[64];
        signature.WriteToSpan(sigBytes);
        return Convert.ToHexString(sigBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the NIP-04 ECDH shared secret between a private key and a public key.
    /// Returns the 32-byte x-coordinate of the ECDH shared point.
    /// </summary>
    private static byte[] ComputeNip04SharedSecret(string privateKeyHex, string pubKeyHex)
    {
        var privateKeyBytes = Convert.FromHexString(privateKeyHex);
        var pubKeyBytes = Convert.FromHexString(pubKeyHex);

        if (!Context.Instance.TryCreateECPrivKey(privateKeyBytes, out var ecPrivKey) || ecPrivKey == null)
            throw new ArgumentException("Invalid private key for NIP-04");

        // Convert x-only pubkey (32 bytes) to compressed pubkey (33 bytes, 0x02 prefix = even y)
        var compressedPubKey = new byte[33];
        compressedPubKey[0] = 0x02;
        Array.Copy(pubKeyBytes, 0, compressedPubKey, 1, 32);

        if (!ECPubKey.TryCreate(compressedPubKey, Context.Instance, out _, out var ecPubKey) || ecPubKey == null)
            throw new ArgumentException("Invalid public key for NIP-04");

        // ECDH: shared point = privkey * pubkey
        var sharedPubKey = ecPubKey.GetSharedPubkey(ecPrivKey);

        // Extract x-coordinate (32 bytes) — this is the NIP-04 shared secret
        var sharedCompressed = new byte[33];
        sharedPubKey.WriteToSpan(compressed: true, sharedCompressed, out _);
        var sharedSecret = new byte[32];
        Array.Copy(sharedCompressed, 1, sharedSecret, 0, 32);

        return sharedSecret;
    }

    /// <summary>
    /// NIP-04 encryption: AES-256-CBC with ECDH shared secret.
    /// Format: base64(ciphertext)?iv=base64(iv)
    /// </summary>
    private static string EncryptNip04(string plaintext, string privateKeyHex, string pubKeyHex)
    {
        var sharedSecret = ComputeNip04SharedSecret(privateKeyHex, pubKeyHex);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var iv = new byte[16];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(iv);

        using var aes = Aes.Create();
        aes.Key = sharedSecret;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        return Convert.ToBase64String(encrypted) + "?iv=" + Convert.ToBase64String(iv);
    }

    /// <summary>
    /// NIP-04 decryption: AES-256-CBC with ECDH shared secret.
    /// </summary>
    private static string DecryptNip04(string ciphertext, string privateKeyHex, string pubKeyHex)
    {
        var parts = ciphertext.Split("?iv=");
        if (parts.Length != 2)
        {
            throw new FormatException("Invalid NIP-04 ciphertext format");
        }

        var encryptedData = Convert.FromBase64String(parts[0]);
        var iv = Convert.FromBase64String(parts[1]);

        var sharedSecret = ComputeNip04SharedSecret(privateKeyHex, pubKeyHex);

        using var aes = Aes.Create();
        aes.Key = sharedSecret;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);

        return Encoding.UTF8.GetString(decrypted);
    }

    private static string EncryptNip44(string plaintext, string privateKeyHex, string pubKeyHex)
    {
        var conversationKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(privateKeyHex), Convert.FromHexString(pubKeyHex));
        return Nip44Encryption.Encrypt(plaintext, conversationKey);
    }

    private static string DecryptNip44(string base64Payload, string privateKeyHex, string pubKeyHex)
    {
        var conversationKey = Nip44Encryption.DeriveConversationKey(
            Convert.FromHexString(privateKeyHex), Convert.FromHexString(pubKeyHex));
        return Nip44Encryption.Decrypt(base64Payload, conversationKey);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _cts?.Cancel();
        foreach (var conn in _relayConnections)
            conn.Dispose();
        _relayConnections.Clear();
        _cts?.Dispose();
        _status.Dispose();

        _disposed = true;
    }

    private class Nip46Request
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("method")]
        public string Method { get; set; } = "";

        [JsonPropertyName("params")]
        public string[] Params { get; set; } = Array.Empty<string>();
    }

    private class Nip46Response
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    /// <summary>
    /// Validates a signer relay URL for safe connection.
    /// Enforces wss:// scheme and blocks private/reserved IP addresses (SSRF prevention).
    /// </summary>
    internal async Task<string?> ValidateSignerRelayUrlAsync(string relayUrl)
    {
        if (!Uri.TryCreate(relayUrl, UriKind.Absolute, out var uri))
            return "Invalid URL format.";

        if (uri.Scheme != "wss" && uri.Scheme != "ws")
            return $"Relay URL must use ws:// or wss:// scheme, got: {uri.Scheme}://";

        if (uri.Scheme == "ws" && !Configuration.ProfileConfiguration.AllowLocalRelays)
            return "Signer relay URL must use wss:// (TLS required).";

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

    private static bool IsPrivateOrReservedIp(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 127) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            if (bytes[0] == 0) return true;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
            if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any))
                return true;
        }

        return false;
    }
}
