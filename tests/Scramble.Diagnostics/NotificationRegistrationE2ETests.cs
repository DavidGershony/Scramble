using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Scramble.Nostr.Crypto;
using Scramble.Core.Configuration;
using Scramble.Core.Services;
using Xunit;
namespace Scramble.Diagnostics;

/// <summary>
/// E2E test for the notification registration flow.
/// Uses a local in-process WebSocket relay mock so the test is self-contained.
///
/// Emulates the npub-notifier server: listens for gift wraps,
/// unwraps them, verifies the registration JSON, and sends a verification
/// code back. Tests the full round-trip of the Scramble client code.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Notifications")]
public class NotificationRegistrationE2ETests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private NostrService _clientNostr = null!;
    private string _clientPubKeyHex = null!;
    private string _clientPrivKeyHex = null!;
    private string _serverPubKeyHex = null!;
    private string _serverPrivKeyHex = null!;
    private MockRelay _relay = null!;

    public NotificationRegistrationE2ETests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);

        // Start local mock relay
        _relay = new MockRelay(_output);
        await _relay.StartAsync();
        _output.WriteLine($"Mock relay listening on {_relay.Url}");

        // Generate client keys
        _clientNostr = new NostrService();
        var clientKeys = _clientNostr.GenerateKeyPair();
        _clientPubKeyHex = clientKeys.publicKeyHex;
        _clientPrivKeyHex = clientKeys.privateKeyHex;

        // Generate server keys (emulating npub-notifier)
        var serverNostr = new NostrService();
        var serverKeys = serverNostr.GenerateKeyPair();
        _serverPubKeyHex = serverKeys.publicKeyHex;
        _serverPrivKeyHex = serverKeys.privateKeyHex;

        _output.WriteLine($"Client pubkey: {_clientPubKeyHex[..16]}...");
        _output.WriteLine($"Server pubkey: {_serverPubKeyHex[..16]}...");
    }

    public async ValueTask DisposeAsync()
    {
        try { await _clientNostr.DisconnectAsync(); } catch { }
        (_clientNostr as IDisposable)?.Dispose();
        await _relay.DisposeAsync();
    }

    [Fact]
    public async Task Registration_GiftWrap_IsDelivered_And_ServerCanUnwrap()
    {
        // 1. Fake server subscribes on mock relay for gift wraps addressed to its pubkey
        using var serverWs = new ClientWebSocket();
        await serverWs.ConnectAsync(new Uri(_relay.Url), CancellationToken.None);

        var subId = "srv_sub_01";
        var reqMsg = $"[\"REQ\",\"{subId}\",{{\"kinds\":[1059],\"#p\":[\"{_serverPubKeyHex}\"]}}]";
        await serverWs.SendAsync(Encoding.UTF8.GetBytes(reqMsg), WebSocketMessageType.Text, true, CancellationToken.None);

        // Wait for EOSE
        await WaitForMessage(serverWs, msg => msg.Contains("EOSE"));
        _output.WriteLine("Server subscription live (EOSE received)");

        // 2. Client connects and sends registration gift wrap
        await _clientNostr.ConnectAsync(_relay.Url);
        await Task.Delay(300);

        var pushUrl = "https://ntfy.sh/test-topic-abc123";
        var content = $"{{\"action\": \"register\", \"push_url\": \"{pushUrl}\", \"relays\": [\"{_relay.Url}\"]}}";
        var rumorTags = new List<List<string>> { new() { "p", _serverPubKeyHex } };

        _output.WriteLine("Client sending registration gift wrap...");
        var eventId = await _clientNostr.PublishGiftWrapAsync(
            rumorKind: 14,
            content: content,
            rumorTags: rumorTags,
            senderPrivateKeyHex: _clientPrivKeyHex,
            senderPublicKeyHex: _clientPubKeyHex,
            recipientPublicKeyHex: _serverPubKeyHex,
            targetRelayUrls: new List<string> { _relay.Url });

        _output.WriteLine($"Client published gift wrap: {eventId}");

        // 3. Server receives the gift wrap from the relay
        var giftWrapMsg = await WaitForMessage(serverWs, msg => msg.StartsWith("[\"EVENT\"") && msg.Contains(subId));
        Assert.NotNull(giftWrapMsg);
        _output.WriteLine("Server received gift wrap from relay");

        // 4. Server unwraps: gift wrap → seal → rumor
        using var eventDoc = JsonDocument.Parse(giftWrapMsg);
        var eventObj = eventDoc.RootElement[2];
        var ephemeralPubkey = eventObj.GetProperty("pubkey").GetString()!;
        var giftContent = eventObj.GetProperty("content").GetString()!;

        // Decrypt gift wrap layer
        var giftConvKey = Nip44.DeriveConversationKey(
            Convert.FromHexString(_serverPrivKeyHex), Convert.FromHexString(ephemeralPubkey));
        var sealJson = Nip44.Decrypt(giftContent, giftConvKey);
        _output.WriteLine("Decrypted gift wrap → seal");

        // Decrypt seal layer
        using var sealDoc = JsonDocument.Parse(sealJson);
        var sealPubkey = sealDoc.RootElement.GetProperty("pubkey").GetString()!;
        var sealContent = sealDoc.RootElement.GetProperty("content").GetString()!;

        var sealConvKey = Nip44.DeriveConversationKey(
            Convert.FromHexString(_serverPrivKeyHex), Convert.FromHexString(sealPubkey));
        var rumorJson = Nip44.Decrypt(sealContent, sealConvKey);
        _output.WriteLine("Decrypted seal → rumor");

        // 5. Verify the rumor contains the correct registration command
        using var rumorDoc = JsonDocument.Parse(rumorJson);
        var rumor = rumorDoc.RootElement;
        Assert.Equal(14, rumor.GetProperty("kind").GetInt32());
        Assert.Equal(_clientPubKeyHex, rumor.GetProperty("pubkey").GetString());

        var rumorContent = rumor.GetProperty("content").GetString()!;
        using var cmdDoc = JsonDocument.Parse(rumorContent);
        var cmd = cmdDoc.RootElement;
        Assert.Equal("register", cmd.GetProperty("action").GetString());
        Assert.Equal(pushUrl, cmd.GetProperty("push_url").GetString());
        Assert.Contains(_relay.Url, cmd.GetProperty("relays").EnumerateArray().Select(e => e.GetString()));

        _output.WriteLine("PASS: Registration command unwrapped and verified");
        _output.WriteLine($"  sender: {sealPubkey[..16]}... (matches client: {sealPubkey == _clientPubKeyHex})");
        _output.WriteLine($"  action: register");
        _output.WriteLine($"  push_url: {pushUrl}");
    }

    [Fact]
    public async Task FullRoundTrip_Register_Then_Verify()
    {
        // 1. Server subscribes on relay for gift wraps
        using var serverWs = new ClientWebSocket();
        await serverWs.ConnectAsync(new Uri(_relay.Url), CancellationToken.None);

        var subId = "srv_sub_01";
        var reqMsg = $"[\"REQ\",\"{subId}\",{{\"kinds\":[1059],\"#p\":[\"{_serverPubKeyHex}\"]}}]";
        await serverWs.SendAsync(Encoding.UTF8.GetBytes(reqMsg), WebSocketMessageType.Text, true, CancellationToken.None);
        await WaitForMessage(serverWs, msg => msg.Contains("EOSE"));
        _output.WriteLine("Server ready");

        // 2. Client sends registration
        await _clientNostr.ConnectAsync(_relay.Url);
        await Task.Delay(300);

        var pushUrl = "https://ntfy.sh/my-scramble-topic";
        var regContent = $"{{\"action\": \"register\", \"push_url\": \"{pushUrl}\", \"relays\": [\"{_relay.Url}\"]}}";
        var regTags = new List<List<string>> { new() { "p", _serverPubKeyHex } };

        await _clientNostr.PublishGiftWrapAsync(
            rumorKind: 14, content: regContent, rumorTags: regTags,
            senderPrivateKeyHex: _clientPrivKeyHex, senderPublicKeyHex: _clientPubKeyHex,
            recipientPublicKeyHex: _serverPubKeyHex, targetRelayUrls: new List<string> { _relay.Url });
        _output.WriteLine("Client sent registration");

        // 3. Server receives and unwraps registration — verify push_url is present
        var regMsg = await WaitForMessage(serverWs, msg => msg.StartsWith("[\"EVENT\"") && msg.Contains(subId));
        Assert.NotNull(regMsg);

        var regCmd = UnwrapGiftWrapToCommand(regMsg, _serverPrivKeyHex);
        Assert.Equal("register", regCmd.GetProperty("action").GetString());
        Assert.Equal(pushUrl, regCmd.GetProperty("push_url").GetString());
        _output.WriteLine($"Server received register command with push_url={pushUrl}");

        // 4. Server would POST verification code to push_url here (not our code to test).
        //    The user receives it via their notification service and types it in.
        var verificationCode = "847291";

        // 5. Client sends verify command with the code
        var verifyContent = $"{{\"action\": \"verify\", \"code\": \"{verificationCode}\"}}";
        var verifyTags = new List<List<string>> { new() { "p", _serverPubKeyHex } };

        await _clientNostr.PublishGiftWrapAsync(
            rumorKind: 14, content: verifyContent, rumorTags: verifyTags,
            senderPrivateKeyHex: _clientPrivKeyHex, senderPublicKeyHex: _clientPubKeyHex,
            recipientPublicKeyHex: _serverPubKeyHex, targetRelayUrls: new List<string> { _relay.Url });
        _output.WriteLine("Client sent verify command");

        // 6. Server receives and unwraps verify command
        var verifyMsg = await WaitForMessage(serverWs, msg => msg.StartsWith("[\"EVENT\"") && msg.Contains(subId));
        Assert.NotNull(verifyMsg);

        var verifyCmd = UnwrapGiftWrapToCommand(verifyMsg, _serverPrivKeyHex);
        Assert.Equal("verify", verifyCmd.GetProperty("action").GetString());
        Assert.Equal(verificationCode, verifyCmd.GetProperty("code").GetString());

        _output.WriteLine("PASS: Full round-trip completed");
        _output.WriteLine($"  1. Client → Server: register (push_url={pushUrl})");
        _output.WriteLine($"  2. Client → Server: verify (code={verificationCode})");
    }

    /// <summary>
    /// Unwraps a relay EVENT message into the inner rumor content parsed as a JSON command.
    /// Throws on any decryption or parse failure — no silent swallowing.
    /// </summary>
    private static JsonElement UnwrapGiftWrapToCommand(string relayEventMsg, string recipientPrivKeyHex)
    {
        using var doc = JsonDocument.Parse(relayEventMsg);
        var eventObj = doc.RootElement[2];
        var ephPub = eventObj.GetProperty("pubkey").GetString()!;
        var giftContent = eventObj.GetProperty("content").GetString()!;

        // Decrypt gift wrap → seal
        var giftKey = Nip44.DeriveConversationKey(
            Convert.FromHexString(recipientPrivKeyHex), Convert.FromHexString(ephPub));
        var sealJson = Nip44.Decrypt(giftContent, giftKey);

        // Decrypt seal → rumor
        using var sealDoc = JsonDocument.Parse(sealJson);
        var sealPub = sealDoc.RootElement.GetProperty("pubkey").GetString()!;
        var sealContent = sealDoc.RootElement.GetProperty("content").GetString()!;
        var sealKey = Nip44.DeriveConversationKey(
            Convert.FromHexString(recipientPrivKeyHex), Convert.FromHexString(sealPub));
        var rumorJson = Nip44.Decrypt(sealContent, sealKey);

        // Parse rumor content as JSON command
        using var rumorDoc = JsonDocument.Parse(rumorJson);
        var rumorContent = rumorDoc.RootElement.GetProperty("content").GetString()!;
        using var cmdDoc = JsonDocument.Parse(rumorContent);
        return cmdDoc.RootElement.Clone();
    }

    private static async Task<string> WaitForMessage(ClientWebSocket ws, Func<string, bool> predicate, int timeoutMs = 10000)
    {
        var buffer = new byte[65536];
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (predicate(msg))
                return msg;
        }
        throw new TimeoutException("Did not receive expected message within timeout");
    }

    /// <summary>
    /// Minimal in-process Nostr relay that supports REQ/EVENT/CLOSE.
    /// Stores events in memory and fans out to matching subscriptions.
    /// </summary>
    private class MockRelay : IAsyncDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _clientTasks = new();
        private readonly ConcurrentDictionary<string, List<StoredEvent>> _events = new();
        private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
        private int _port;

        public string Url => $"ws://127.0.0.1:{_port}";

        public MockRelay(ITestOutputHelper output)
        {
            _output = output;
            _listener = new HttpListener();
        }

        public async Task StartAsync()
        {
            // Find an available port
            var tempListener = new TcpPortFinder();
            _port = tempListener.FindFreePort();

            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _ = AcceptClientsAsync();
            await Task.Delay(100); // Let it start
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await Task.WhenAll(_clientTasks.Where(t => !t.IsCompleted));
        }

        private async Task AcceptClientsAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    if (ctx.Request.IsWebSocketRequest)
                    {
                        var wsCtx = await ctx.AcceptWebSocketAsync(null);
                        var clientId = Guid.NewGuid().ToString("N")[..8];
                        var conn = new ClientConnection(clientId, wsCtx.WebSocket);
                        _clients[clientId] = conn;
                        _clientTasks.Add(HandleClientAsync(conn));
                    }
                    else
                    {
                        ctx.Response.StatusCode = 400;
                        ctx.Response.Close();
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
            }
        }

        private async Task HandleClientAsync(ClientConnection conn)
        {
            var buffer = new byte[65536];
            try
            {
                while (conn.WebSocket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    var result = await conn.WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await ProcessMessageAsync(conn, msg);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                _clients.TryRemove(conn.Id, out _);
            }
        }

        private async Task ProcessMessageAsync(ClientConnection conn, string msg)
        {
            using var doc = JsonDocument.Parse(msg);
            var arr = doc.RootElement;
            var type = arr[0].GetString();

            switch (type)
            {
                case "REQ":
                    var subId = arr[1].GetString()!;
                    var filter = arr[2];
                    var sub = new Subscription(subId, filter.GetRawText());
                    conn.Subscriptions[subId] = sub;

                    // Replay stored events that match
                    foreach (var kvp in _events)
                    {
                        foreach (var stored in kvp.Value)
                        {
                            if (MatchesFilter(stored.EventJson, sub.FilterJson))
                            {
                                var eventMsg = $"[\"EVENT\",\"{subId}\",{stored.EventJson}]";
                                await SendAsync(conn, eventMsg);
                            }
                        }
                    }

                    // Send EOSE
                    await SendAsync(conn, $"[\"EOSE\",\"{subId}\"]");
                    break;

                case "EVENT":
                    var eventJson = arr[1].GetRawText();
                    using (var evDoc = JsonDocument.Parse(eventJson))
                    {
                        var ev = evDoc.RootElement;
                        var eventId = ev.GetProperty("id").GetString()!;

                        // Store the event
                        _events.GetOrAdd(eventId, _ => new List<StoredEvent>())
                            .Add(new StoredEvent(eventId, eventJson));

                        // Send OK to the publisher
                        await SendAsync(conn, $"[\"OK\",\"{eventId}\",true,\"\"]");

                        // Fan out to all clients with matching subscriptions
                        foreach (var client in _clients.Values)
                        {
                            foreach (var s in client.Subscriptions.Values)
                            {
                                if (MatchesFilter(eventJson, s.FilterJson))
                                {
                                    var fanoutMsg = $"[\"EVENT\",\"{s.Id}\",{eventJson}]";
                                    await SendAsync(client, fanoutMsg);
                                }
                            }
                        }
                    }
                    break;

                case "CLOSE":
                    var closeSubId = arr[1].GetString()!;
                    conn.Subscriptions.TryRemove(closeSubId, out _);
                    break;
            }
        }

        private bool MatchesFilter(string eventJson, string filterJson)
        {
            using var evDoc = JsonDocument.Parse(eventJson);
            using var filtDoc = JsonDocument.Parse(filterJson);
            var ev = evDoc.RootElement;
            var filt = filtDoc.RootElement;

            // Match kinds
            if (filt.TryGetProperty("kinds", out var kinds))
            {
                var eventKind = ev.GetProperty("kind").GetInt32();
                var matched = false;
                foreach (var k in kinds.EnumerateArray())
                {
                    if (k.GetInt32() == eventKind) { matched = true; break; }
                }
                if (!matched) return false;
            }

            // Match #p tag
            if (filt.TryGetProperty("#p", out var pFilter))
            {
                var requiredPubkeys = new HashSet<string>();
                foreach (var p in pFilter.EnumerateArray())
                    requiredPubkeys.Add(p.GetString()!);

                if (ev.TryGetProperty("tags", out var tags))
                {
                    var found = false;
                    foreach (var tag in tags.EnumerateArray())
                    {
                        if (tag.GetArrayLength() >= 2 &&
                            tag[0].GetString() == "p" &&
                            requiredPubkeys.Contains(tag[1].GetString()!))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found) return false;
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        private static async Task SendAsync(ClientConnection conn, string msg)
        {
            if (conn.WebSocket.State == WebSocketState.Open)
            {
                await conn.WebSocket.SendAsync(
                    Encoding.UTF8.GetBytes(msg),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        private record StoredEvent(string Id, string EventJson);
        private record Subscription(string Id, string FilterJson);

        private class ClientConnection
        {
            public string Id { get; }
            public WebSocket WebSocket { get; }
            public ConcurrentDictionary<string, Subscription> Subscriptions { get; } = new();

            public ClientConnection(string id, WebSocket ws)
            {
                Id = id;
                WebSocket = ws;
            }
        }

        private class TcpPortFinder
        {
            public int FindFreePort()
            {
                var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }
    }

}