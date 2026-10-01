using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Scramble.Diagnostics.RelayHarness;

/// <summary>
/// In-process Nostr relay shim used to inject network faults into integration tests.
/// Speaks a minimal subset of the Nostr wire protocol: EVENT, REQ, CLOSE from the
/// client; EVENT, OK, EOSE back to the client. Filter matching covers kinds,
/// "#tag" filters, authors and ids — enough for kind 445 / 444 / 30443 traffic.
///
/// Faults are configured via <see cref="Faults"/> and read at the moment an OK
/// would be sent, so a test can flip a knob mid-run.
/// </summary>
public sealed class FaultyRelay : IAsyncDisposable
{
    private const int MaxMessageSize = 16 * 1024 * 1024;

    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<JsonDocument> _events = new();
    private readonly List<Connection> _connections = new();
    private readonly object _lock = new();
    private Task? _acceptLoop;

    public RelayFaults Faults { get; } = new();

    public string WsUrl => $"ws://127.0.0.1:{_port}";

    public int ConnectionCount
    {
        get { lock (_lock) return _connections.Count; }
    }

    public int StoredEventCount
    {
        get { lock (_lock) return _events.Count; }
    }

    /// <summary>
    /// Snapshot of stored events as raw JSON strings (safe to inspect after the
    /// relay is disposed, since the underlying JsonDocuments are owned here).
    /// </summary>
    public IReadOnlyList<string> StoredEventsJson
    {
        get { lock (_lock) return _events.Select(d => d.RootElement.GetRawText()).ToList(); }
    }

    public FaultyRelay()
    {
        _port = GetFreeLoopbackPort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public Task StartAsync()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                try { ctx.Response.Close(); } catch { }
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(ctx));
        }
    }

    private async Task HandleConnectionAsync(HttpListenerContext ctx)
    {
        HttpListenerWebSocketContext wsCtx;
        try { wsCtx = await ctx.AcceptWebSocketAsync(null); }
        catch { try { ctx.Response.Close(); } catch { } return; }

        var conn = new Connection(wsCtx.WebSocket);
        lock (_lock) _connections.Add(conn);

        try
        {
            var buffer = new byte[64 * 1024];
            while (conn.WebSocket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var msg = await ReceiveFullMessageAsync(conn.WebSocket, buffer, _cts.Token);
                if (msg == null || msg.Value.Type == WebSocketMessageType.Close) break;
                if (msg.Value.Type != WebSocketMessageType.Text) continue;

                await DispatchAsync(conn, msg.Value.Text);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception) { /* per-connection isolation */ }
        finally
        {
            lock (_lock) _connections.Remove(conn);
            try { conn.WebSocket.Dispose(); } catch { }
            conn.Dispose();
        }
    }

    private async Task DispatchAsync(Connection conn, string text)
    {
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return; }

        try
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return;
            var type = root[0].GetString();

            if (type == "EVENT" && root.GetArrayLength() >= 2)
                await HandleEventAsync(conn, root[1]);
            else if (type == "REQ" && root.GetArrayLength() >= 3)
                await HandleReqAsync(conn, root[1].GetString() ?? "", root[2]);
            else if (type == "CLOSE" && root.GetArrayLength() >= 2)
                HandleClose(conn, root[1].GetString() ?? "");
        }
        finally { doc.Dispose(); }
    }

    private async Task HandleEventAsync(Connection conn, JsonElement eventObj)
    {
        var eventId = eventObj.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var rawJson = eventObj.GetRawText();

        if (Faults.BlackholeEvent)
        {
            // Total drop: no OK, no store, no broadcast. As if the EVENT
            // never reached the relay at all (TCP went into the void).
            return;
        }

        if (Faults.RejectAllEvents)
        {
            await SendAsync(conn, BuildOkMessage(eventId, false, "rejected by FaultyRelay"));
            return;
        }

        // Store the event and snapshot the matching subscribers under the lock.
        var stored = JsonDocument.Parse(rawJson);
        List<(Connection conn, string subId)> matching;
        lock (_lock)
        {
            _events.Add(stored);
            matching = new List<(Connection, string)>();
            foreach (var c in _connections)
            {
                foreach (var (subId, filterDoc) in c.Subscriptions)
                {
                    if (MatchesFilter(stored.RootElement, filterDoc.RootElement))
                        matching.Add((c, subId));
                }
            }
        }

        // Broadcast EVENT to all matching subscribers (including self — self-echo
        // is the whole point of this shim).
        foreach (var (c, subId) in matching)
        {
            try { await SendAsync(c, BuildEventMessage(subId, rawJson)); }
            catch { /* connection may have died mid-broadcast */ }
        }

        // OK delivery — apply fault knobs.
        if (Faults.DisconnectAfterEvent)
        {
            try { await conn.WebSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "fault", _cts.Token); }
            catch { }
            return;
        }

        if (Faults.DropOk) return;

        if (Faults.DelayOkMs > 0)
        {
            try { await Task.Delay(Faults.DelayOkMs, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }

        await SendAsync(conn, BuildOkMessage(eventId, true, ""));
    }

    private async Task HandleReqAsync(Connection conn, string subId, JsonElement filter)
    {
        var filterDoc = JsonDocument.Parse(filter.GetRawText());

        // Replace any existing subscription with the same id.
        if (conn.Subscriptions.TryRemove(subId, out var prior)) prior.Dispose();
        conn.Subscriptions[subId] = filterDoc;

        // Send historical EVENTs that match.
        List<string> historical;
        lock (_lock)
        {
            historical = _events
                .Where(e => MatchesFilter(e.RootElement, filterDoc.RootElement))
                .Select(e => e.RootElement.GetRawText())
                .ToList();
        }

        foreach (var raw in historical)
            await SendAsync(conn, BuildEventMessage(subId, raw));

        // The subscription stays open and unterminated: the querying side cannot tell
        // "still sending" from "never going to answer" and has to wait out its own
        // timeout. See RelayFaults.DropEose.
        if (Faults.DropEose) return;

        await SendAsync(conn, BuildEoseMessage(subId));
    }

    private static void HandleClose(Connection conn, string subId)
    {
        if (conn.Subscriptions.TryRemove(subId, out var doc)) doc.Dispose();
    }

    private static bool MatchesFilter(JsonElement evt, JsonElement filter)
    {
        if (filter.TryGetProperty("kinds", out var kinds))
        {
            var evKind = evt.GetProperty("kind").GetInt32();
            var match = false;
            foreach (var k in kinds.EnumerateArray())
                if (k.GetInt32() == evKind) { match = true; break; }
            if (!match) return false;
        }

        if (filter.TryGetProperty("authors", out var authors))
        {
            var evAuthor = evt.TryGetProperty("pubkey", out var p) ? p.GetString() ?? "" : "";
            var match = false;
            foreach (var a in authors.EnumerateArray())
                if (a.GetString() == evAuthor) { match = true; break; }
            if (!match) return false;
        }

        if (filter.TryGetProperty("ids", out var ids))
        {
            var evId = evt.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            var match = false;
            foreach (var x in ids.EnumerateArray())
                if (x.GetString() == evId) { match = true; break; }
            if (!match) return false;
        }

        // "#<tag>" filters: e.g. "#h": ["abc..."] matches an event tag ["h", "abc..."].
        foreach (var prop in filter.EnumerateObject())
        {
            if (!prop.Name.StartsWith('#') || prop.Name.Length < 2) continue;
            var tagKey = prop.Name.Substring(1);
            var allowed = new HashSet<string>();
            foreach (var v in prop.Value.EnumerateArray())
            {
                var s = v.GetString();
                if (s != null) allowed.Add(s);
            }

            var matched = false;
            if (evt.TryGetProperty("tags", out var tags))
            {
                foreach (var t in tags.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() < 2) continue;
                    if (t[0].GetString() == tagKey)
                    {
                        var v = t[1].GetString();
                        if (v != null && allowed.Contains(v)) { matched = true; break; }
                    }
                }
            }
            if (!matched) return false;
        }

        return true;
    }

    private static string BuildOkMessage(string eventId, bool accepted, string message)
        => "[\"OK\"," + JsonSerializer.Serialize(eventId) + "," + (accepted ? "true" : "false") + "," + JsonSerializer.Serialize(message) + "]";

    private static string BuildEventMessage(string subId, string rawEventJson)
        => "[\"EVENT\"," + JsonSerializer.Serialize(subId) + "," + rawEventJson + "]";

    private static string BuildEoseMessage(string subId)
        => "[\"EOSE\"," + JsonSerializer.Serialize(subId) + "]";

    private static async Task SendAsync(Connection conn, string text)
    {
        await conn.WriteLock.WaitAsync();
        try
        {
            if (conn.WebSocket.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            await conn.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (WebSocketException) { /* peer gone */ }
        catch (ObjectDisposedException) { /* socket closed */ }
        finally { conn.WriteLock.Release(); }
    }

    private static async Task<(WebSocketMessageType Type, string Text)?> ReceiveFullMessageAsync(
        WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            try { result = await ws.ReceiveAsync(buffer, ct); }
            catch { return null; }
            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, "");
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > MaxMessageSize) return null;
        } while (!result.EndOfMessage);
        return (result.MessageType, Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static int GetFreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        if (_acceptLoop != null)
        {
            try { await _acceptLoop; }
            catch { }
        }
        lock (_lock)
        {
            foreach (var c in _connections)
            {
                try { c.WebSocket.Dispose(); } catch { }
                c.Dispose();
            }
            _connections.Clear();
            foreach (var e in _events) e.Dispose();
            _events.Clear();
        }
        _cts.Dispose();
    }

    private sealed class Connection : IDisposable
    {
        public WebSocket WebSocket { get; }
        public ConcurrentDictionary<string, JsonDocument> Subscriptions { get; } = new();
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        public Connection(WebSocket ws) { WebSocket = ws; }

        public void Dispose()
        {
            foreach (var doc in Subscriptions.Values)
            {
                try { doc.Dispose(); } catch { }
            }
            Subscriptions.Clear();
            try { WriteLock.Dispose(); } catch { }
        }
    }
}

/// <summary>
/// Network-fault knobs, mutable so a single test can flip them between operations.
/// </summary>
public sealed class RelayFaults
{
    /// <summary>
    /// When true, the relay still stores the EVENT and broadcasts it to subscribers
    /// but never sends an OK response — the publisher's retry policy will time out.
    /// This is the precise condition that triggers WN issue #813.
    /// </summary>
    public bool DropOk { get; set; }

    /// <summary>
    /// Delay the OK response by this many milliseconds. Used to push OK past the
    /// publisher's retry-window threshold while still eventually delivering it.
    /// </summary>
    public int DelayOkMs { get; set; }

    /// <summary>
    /// Close the WebSocket immediately after accepting (and storing/broadcasting)
    /// an EVENT. Simulates a relay that crashes mid-handshake.
    /// </summary>
    public bool DisconnectAfterEvent { get; set; }

    /// <summary>
    /// Reject every incoming EVENT with OK=false. The event is NOT stored and NOT
    /// broadcast. Useful for the "all relays rejected" path.
    /// </summary>
    public bool RejectAllEvents { get; set; }

    /// <summary>
    /// Accept the REQ and send the matching stored events, but never send EOSE, so the
    /// subscription never completes. Models a relay that is reachable and willing but
    /// does not finish a query -- the condition that made relay-list discovery cost a
    /// full QueryRelayAsync timeout per lookup against two public discovery relays.
    /// </summary>
    public bool DropEose { get; set; }

    /// <summary>
    /// Total drop: the relay accepts the EVENT on the wire but does nothing with
    /// it — no store, no broadcast, no OK. Models the case where the relay
    /// silently loses the event (transient outage, queue overflow, crash before
    /// persist). Strongest demonstrator of silent state divergence between the
    /// publisher and the rest of the group.
    /// </summary>
    public bool BlackholeEvent { get; set; }
}
