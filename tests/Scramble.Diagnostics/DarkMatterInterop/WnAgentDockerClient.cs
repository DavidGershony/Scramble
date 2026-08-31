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
