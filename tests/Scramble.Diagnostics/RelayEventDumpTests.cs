using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Scramble.Diagnostics.TestHelpers;
using Xunit;
namespace Scramble.Diagnostics;

[Trait("Category", "Integration")]
public class RelayEventDumpTests
{
    // TODO: DIAGNOSTIC ONLY — remove after cross-impl investigation is complete
    private readonly ITestOutputHelper _output;
    public RelayEventDumpTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task DumpGroupEventsFromRelay()
    {
        var relayUrl = TestRelayConfig.RelayUrl;
        var nostrGroupId = "5a616302fd11f9c2bc51a62a81d934adb16d3d75c4a0af91872b463439afccda";
        var ourEventId = "4acec5809422780d2aac58dae5488eebea4a46df4d8cbfbc3e0010a72487b7d4";
        var failedEventId = "a06e6ac92dcc618091594e8cdc2681d69de667fe53c1c1aaaad29b453a6e2370";

        _output.WriteLine($"Fetching kind 445 events for group h-tag: {nostrGroupId}");
        _output.WriteLine($"Our message event: {ourEventId}");
        _output.WriteLine($"Web app failed event: {failedEventId}");

        // Fetch by h-tag
        var events1 = await FetchEvents(relayUrl, $"{{\"kinds\":[445],\"#h\":[\"{nostrGroupId}\"],\"limit\":20}}");
        _output.WriteLine($"\n--- Kind 445 events with h-tag (group messages + commits) ---");
        _output.WriteLine($"Found: {events1.Count}");
        foreach (var ev in events1) AnalyzeEvent(ev);

        // Also fetch the specific failed event by ID
        _output.WriteLine($"\n--- Fetching failed event by ID ---");
        var events2 = await FetchEvents(relayUrl, $"{{\"ids\":[\"{failedEventId}\"],\"limit\":1}}");
        _output.WriteLine($"Found: {events2.Count}");
        foreach (var ev in events2) AnalyzeEvent(ev);

        // Also fetch our event by ID
        _output.WriteLine($"\n--- Fetching our event by ID ---");
        var events3 = await FetchEvents(relayUrl, $"{{\"ids\":[\"{ourEventId}\"],\"limit\":1}}");
        _output.WriteLine($"Found: {events3.Count}");
        foreach (var ev in events3) AnalyzeEvent(ev);
    }

    private void AnalyzeEvent(string eventJson)
    {
        using var doc = JsonDocument.Parse(eventJson);
        var ev = doc.RootElement;
        var id = ev.GetProperty("id").GetString()!;
        var pubkey = ev.GetProperty("pubkey").GetString()!;
        var kind = ev.GetProperty("kind").GetInt32();
        var content = ev.GetProperty("content").GetString() ?? "";
        var createdAt = ev.GetProperty("created_at").GetInt64();

        _output.WriteLine($"\n  Event: {id}");
        _output.WriteLine($"  kind: {kind}");
        _output.WriteLine($"  pubkey: {pubkey}");
        _output.WriteLine($"  created_at: {createdAt} ({DateTimeOffset.FromUnixTimeSeconds(createdAt):u})");
        _output.WriteLine($"  content length: {content.Length} chars");

        // Decode content
        try
        {
            var data = Convert.FromBase64String(content);
            _output.WriteLine($"  decoded: {data.Length} bytes");
            _output.WriteLine($"  first 16 hex: {Convert.ToHexString(data[..Math.Min(16, data.Length)])}");

            if (data.Length >= 28)
            {
                _output.WriteLine($"  nonce (12b): {Convert.ToHexString(data[..12])}");
                _output.WriteLine($"  payload: {data.Length - 12} bytes (ciphertext + 16-byte tag)");
            }

            // Check if it looks like an MLS PrivateMessage (0x0001 0x0002)
            if (data.Length >= 4)
            {
                var v = (data[0] << 8) | data[1];
                var wf = (data[2] << 8) | data[3];
                if (v == 0x0001 && wf == 0x0002)
                    _output.WriteLine($"  → MLS PrivateMessage (raw, NOT MIP-03 encrypted)");
                else if (v == 0x0001 && wf == 0x0003)
                    _output.WriteLine($"  → MLS Welcome message");
                else
                    _output.WriteLine($"  → MIP-03 encrypted (nonce + ChaCha20-Poly1305 ciphertext)");
            }
        }
        catch
        {
            _output.WriteLine($"  content is NOT valid base64");
            if (content.Length < 200) _output.WriteLine($"  raw: {content}");
        }

        // Tags
        foreach (var tag in ev.GetProperty("tags").EnumerateArray())
        {
            var parts = new List<string>();
            foreach (var item in tag.EnumerateArray()) parts.Add(item.GetString() ?? "");
            _output.WriteLine($"  tag: [{string.Join(", ", parts)}]");
        }
    }

    private async Task<List<string>> FetchEvents(string relayUrl, string filterJson)
    {
        var events = new List<string>();
        var subId = $"dump_{Guid.NewGuid():N}"[..12];
        using var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(relayUrl), new CancellationTokenSource(10000).Token);
            var req = $"[\"REQ\",\"{subId}\",{filterJson}]";
            _output.WriteLine($"  REQ: {req}");
            await ws.SendAsync(Encoding.UTF8.GetBytes(req), WebSocketMessageType.Text, true, CancellationToken.None);

            var buf = new byte[65536];
            var sb = new StringBuilder();
            var cts = new CancellationTokenSource(10000);
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;
                var msg = sb.ToString(); sb.Clear();
                if (msg.StartsWith("[\"EOSE\"")) break;
                if (msg.StartsWith("[\"EVENT\""))
                {
                    using var d = JsonDocument.Parse(msg);
                    if (d.RootElement.GetArrayLength() >= 3)
                        events.Add(d.RootElement[2].GetRawText());
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  Error: {ex.Message}");
        }
        return events;
    }
}
