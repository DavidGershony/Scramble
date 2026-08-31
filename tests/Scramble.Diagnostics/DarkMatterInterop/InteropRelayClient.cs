using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// Just enough of a Nostr relay client to fetch events for one filter.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <c>Scramble.Core</c>'s relay client. These tests exist to
/// check our own stack against the reference implementation, and routing them
/// through the production client would mean a bug there could mask — or
/// manufacture — an interop failure. A hundred lines of <c>ClientWebSocket</c>
/// keeps the thing under test and the thing measuring it apart.
/// </para>
/// <para>
/// It reads until EOSE and then stops. Stored events are all these tests want;
/// nothing here waits for live ones.
/// </para>
/// </remarks>
public sealed class InteropRelayClient(string relayUrl)
{
    /// <summary>The docker-compose relay, on the host's published port.</summary>
    public const string DefaultRelayUrl = "ws://127.0.0.1:7777";

    /// <summary>
    /// Fetches every stored event matching a filter.
    /// </summary>
    /// <param name="filter">A Nostr filter object, serialized as-is.</param>
    /// <param name="timeout">How long to wait for EOSE.</param>
    /// <returns>The raw event envelopes, exactly as the relay sent them.</returns>
    /// <remarks>
    /// Raw strings rather than parsed objects: the codec under test parses and
    /// verifies the envelope itself, and re-serializing a parsed event here
    /// would hand it bytes we produced instead of bytes upstream did.
    /// </remarks>
    public async Task<IReadOnlyList<string>> FetchAsync(
        object filter, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(relayUrl), cts.Token);

        string subscription = Guid.NewGuid().ToString("N")[..8];
        string request = JsonSerializer.Serialize(new object[] { "REQ", subscription, filter });

        await socket.SendAsync(
            Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, cts.Token);

        var events = new List<string>();
        var buffer = new byte[64 * 1024];

        while (!cts.Token.IsCancellationRequested)
        {
            var message = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return events;

                message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            using var document = JsonDocument.Parse(message.ToString());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                continue;

            switch (root[0].GetString())
            {
                case "EVENT" when root.GetArrayLength() >= 3:
                    events.Add(root[2].GetRawText());
                    break;

                case "EOSE":
                    return events;

                case "CLOSED":
                    throw new InvalidOperationException(
                        $"The relay closed the subscription: {message}");
            }
        }

        return events;
    }

    /// <summary>Fetches the KeyPackage publications of one author.</summary>
    public Task<IReadOnlyList<string>> FetchKeyPackagesAsync(
        string authorHex, TimeSpan timeout, CancellationToken ct = default) =>
        FetchAsync(
            new Dictionary<string, object>
            {
                ["kinds"] = new[] { 30443 },
                ["authors"] = new[] { authorHex },
            },
            timeout,
            ct);
}
