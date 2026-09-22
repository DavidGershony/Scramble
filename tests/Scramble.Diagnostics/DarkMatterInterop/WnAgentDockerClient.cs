using System.Diagnostics;
using System.Text.Json;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// Drives the `wn-agent` interop peer through `docker exec`.
/// </summary>
/// <remarks>
/// <para>
/// The agent's control plane is a Unix socket inside its container, and the
/// runtime image carries no socat, nc or python to reach it from outside. Its
/// CLI subcommands connect to that socket themselves, so driving the binary is
/// the whole client — which is also why this is a handful of process calls
/// rather than a socket protocol implementation.
/// </para>
/// <para>
/// The container runs with host networking. That is required rather than
/// convenient: the agent accepts a plaintext <c>ws://</c> relay only for a
/// literal loopback host and rejects private ranges outright, so it cannot
/// reach the relay at its bridge address or by service name.
/// </para>
/// </remarks>
public sealed class WnAgentDockerClient(Action<string> log)
{
    public const string ContainerName = "wn-agent-interop";

    private const string Home = "/data/marmot-agent";
    private const string Socket = "/run/marmot-agent/wn-agent.sock";

    /// <summary>
    /// Whether the agent container is up and its control socket exists.
    /// </summary>
    /// <remarks>
    /// Checks the socket rather than the container status. "Running" is not the
    /// same as "ready" here — the process creates its socket after start, and
    /// every command below needs it.
    /// </remarks>
    public async Task<bool> IsReadyAsync()
    {
        try
        {
            string status = await RunDockerAsync(
                "inspect", "-f", "{{.State.Running}}", ContainerName);

            if (!status.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                return false;

            await RunDockerAsync("exec", ContainerName, "test", "-S", Socket);
            return true;
        }
        catch (Exception ex)
        {
            log($"wn-agent not ready: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates or reuses the agent's account and publishes its KeyPackage.
    /// </summary>
    /// <remarks>
    /// Idempotent, and reports which it did: on a second call
    /// <c>created</c> is false but the KeyPackage is republished or repaired.
    /// That is what makes this safe to call at the start of every test rather
    /// than once per container lifetime.
    /// </remarks>
    public async Task<AgentBootstrap> BootstrapAsync()
    {
        string json = await RunDockerAsync(
            "exec", ContainerName,
            "wn-agent", "bootstrap",
            "--home", Home,
            "--socket", Socket,
            "--no-quic",
            "--json");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var bootstrap = new AgentBootstrap(
            root.GetProperty("account_id_hex").GetString()!,
            root.GetProperty("key_package_published").GetBoolean(),
            root.TryGetProperty("key_package_bytes", out var size) ? size.GetInt32() : 0);

        log($"wn-agent account {bootstrap.AccountIdHex}, " +
            $"key package published={bootstrap.KeyPackagePublished} ({bootstrap.KeyPackageBytes} bytes)");

        return bootstrap;
    }

    /// <summary>
    /// Sends one control request over the agent's Unix socket.
    /// </summary>
    /// <param name="type">The request's <c>type</c> discriminator, snake_case.</param>
    /// <param name="fields">Additional request fields.</param>
    /// <returns>The response object.</returns>
    /// <remarks>
    /// <para>
    /// Driven with <c>socat</c> inside the container, because the control plane
    /// is a Unix socket there and the CLI exposes only <c>bootstrap</c>. Without
    /// this there is no way to ask the agent anything — including the question
    /// these tests exist to ask, which is whether it joined.
    /// </para>
    /// <para>
    /// The protocol version is <b>not</b> optional: the agent refuses a request
    /// whose <c>marmot_agent_control</c> does not match, which is a clearer
    /// failure than a silently ignored field.
    /// </para>
    /// </remarks>
    public async Task<JsonElement> ControlAsync(
        string type, IReadOnlyDictionary<string, string>? fields = null)
    {
        var request = new Dictionary<string, object>
        {
            ["marmot_agent_control"] = ProtocolVersion,
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["type"] = type,
        };

        foreach (var (key, value) in fields ?? new Dictionary<string, string>())
            request[key] = value;

        string json = JsonSerializer.Serialize(request);

        // Single-quoted for the container's shell, so the JSON's own double
        // quotes survive; the payload never contains a single quote because
        // every value here is hex or a snake_case identifier.
        // The newline is an escape for the container's printf rather than a real
        // one, so the command stays a single line while the agent still gets the
        // line-terminated request it reads.
        string command =
            $"printf '%s\\n' '{json}' | socat -t 10 - UNIX-CONNECT:{Socket}";

        string response = await RunDockerAsync("exec", ContainerName, "sh", "-c", command);

        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidOperationException($"The agent returned nothing for '{type}'.");

        return JsonDocument.Parse(response).RootElement.Clone();
    }

    /// <summary>
    /// Whether the agent has joined a group, by asking it directly.
    /// </summary>
    /// <remarks>
    /// The agent answers <c>type: "error"</c> for a group it does not know, so
    /// absence and presence are distinguishable without guessing from a timeout.
    /// </remarks>
    public async Task<bool> HasGroupAsync(string accountIdHex, string groupIdHex)
    {
        JsonElement response = await ControlAsync(
            "group_info",
            new Dictionary<string, string>
            {
                ["account_id_hex"] = accountIdHex,
                ["group_id_hex"] = groupIdHex,
            });

        return response.TryGetProperty("type", out var type) && type.GetString() != "error";
    }

    /// <summary>
    /// Holds an inbound subscription open for as long as it is not disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The agent fetches nothing while nobody is subscribed.</b>
    /// <c>subscribe_inbound</c> is a streaming request: the connector answers
    /// with an ack and then holds the connection, and the relay subscription
    /// lives exactly as long as it does. The relay log makes this visible — the
    /// agent keeps a connection open and the relay records <c>sent: 0 events</c>
    /// against it, because there is no filter to match anything.
    /// </para>
    /// <para>
    /// So a test that publishes a Welcome and then subscribes has already
    /// missed it, and one that subscribes over a short-lived <c>socat</c> has
    /// unsubscribed before the event arrives. Start this first, keep it, and
    /// publish into it.
    /// </para>
    /// </remarks>
    public InboundSubscription SubscribeInbound(string accountIdHex)
    {
        string json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["marmot_agent_control"] = ProtocolVersion,
            ["id"] = "inbound-" + Guid.NewGuid().ToString("N")[..8],
            ["type"] = "subscribe_inbound",
            ["account_id_hex"] = accountIdHex,
        });

        // The sleep is what holds the subscription open, and it is not optional.
        // With a bare `printf | socat`, stdin reaches EOF the moment printf
        // finishes; socat half-closes, the agent drops the subscription, and the
        // stream is gone before the first event can arrive. Keeping the pipe's
        // writer alive is what keeps the connection alive.
        string command =
            $"{{ printf '%s\\n' '{json}'; sleep {(int)SubscriptionLifetime.TotalSeconds}; }} " +
            $"| socat - UNIX-CONNECT:{Socket}";

        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string arg in new[] { "exec", ContainerName, "sh", "-c", command })
            psi.ArgumentList.Add(arg);

        var subscription = new InboundSubscription(psi, log);
        log($"inbound subscription open for {accountIdHex}");
        return subscription;
    }

    /// <summary>The control protocol this client speaks.</summary>
    private const string ProtocolVersion = "marmot.agent-control.v2";

    /// <summary>
    /// How long a held subscription lives before its own sleep ends it.
    /// </summary>
    /// <remarks>
    /// A backstop, not a schedule: the subscription is normally disposed by the
    /// test. It exists so a crashed test run cannot leave a connection holding
    /// one of the agent's control slots indefinitely — which starves every other
    /// request, including <c>bootstrap</c>, and looks like the agent hanging.
    /// </remarks>
    private static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The mdk revision the running agent was built from.</summary>
    public async Task<string> VersionAsync() =>
        (await RunDockerAsync("exec", ContainerName, "wn-agent", "--version")).Trim();

    private static async Task<string> RunDockerAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        // ArgumentList rather than a joined string: under Git Bash a Unix-style
        // path in a docker argument is rewritten to a Windows one
        // (/run/... becomes C:/Program Files/Git/run/...), and the failure that
        // produces names no path.
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start docker.");

        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', args)} failed (exit {process.ExitCode}):\n{stderr}\n{stdout}");
        }

        return stdout;
    }
}

/// <param name="AccountIdHex">The agent's Nostr account key, x-only, lowercase hex.</param>
/// <param name="KeyPackagePublished">Whether a KeyPackage reached the relay.</param>
/// <param name="KeyPackageBytes">Size of the published KeyPackage, for the log.</param>
public sealed record AgentBootstrap(
    string AccountIdHex, bool KeyPackagePublished, int KeyPackageBytes);

/// <summary>
/// A held <c>subscribe_inbound</c> stream, and everything it has said.
/// </summary>
/// <remarks>
/// The stream is read rather than merely held, because it is the only channel
/// that reports inbound activity while it is open: the agent serves control
/// requests from a small pool, and a held subscription starves the rest, so a
/// concurrent <c>group_info</c> poll simply gets nothing back. Ask the stream
/// while subscribed; ask the socket after releasing.
/// </remarks>
public sealed class InboundSubscription : IDisposable
{
    private readonly Process _process;
    private readonly Action<string> _log;
    private readonly List<string> _lines = [];

    internal InboundSubscription(ProcessStartInfo psi, Action<string> log)
    {
        _log = log;
        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the inbound subscription.");

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;

            lock (_lines)
                _lines.Add(e.Data);
        };

        _process.BeginOutputReadLine();
    }

    /// <summary>Whether the stream is still running.</summary>
    public bool IsAlive => !_process.HasExited;

    /// <summary>Everything the stream has emitted so far.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
                return [.. _lines];
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // Losing the subscription at teardown costs nothing; the agent drops
            // it when the socket closes either way.
            _log($"inbound subscription teardown: {ex.Message}");
        }

        _process.Dispose();
    }
}
