using System.Diagnostics;
using System.Text.Json;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// Drives the Marmot reference CLI (`wn` / `wnd`) in its container.
/// </summary>
/// <remarks>
/// <para>
/// This is the peer that can actually complete an invite, and it is worth
/// saying why it took three tries to find. <c>whitenoise-rs</c> gave us a full
/// client CLI and an automated interop suite, but upstream archived it — the
/// repository says "this repository is obsolete" and pins <c>mdk-core 0.8.0</c>,
/// so it can only ever speak the legacy protocol. <c>wn-agent</c> is the Dark
/// Matter peer but is an <i>agent connector</i>: two commands and a control
/// socket, and it never subscribes for inbound, so an invite to it is never
/// read. <c>mdk</c>'s own <c>crates/cli</c> is the missing piece — a complete
/// Dark Matter client with groups, invites, messages and sync.
/// </para>
/// <para>
/// Two flags make it usable headless where the old image needed a patched
/// binary: <c>--secret-store file</c> keeps secrets out of the OS keychain, and
/// the daemon's relay flags replace patching hardcoded relay defaults into the
/// source.
/// </para>
/// </remarks>
public sealed class MdkCliDockerClient(Action<string> log)
{
    public const string ContainerName = "mdk-cli-interop";

    private const string Home = "/data/wn";
    private const string Socket = "/data/wn/wnd.sock";
    private const string LogsDir = "/logs";

    /// <summary>
    /// The account every command after <see cref="CreateIdentityAsync"/> runs as.
    /// </summary>
    /// <remarks>
    /// Not optional once a container has been used twice. The CLI refuses to
    /// guess between identities — "multiple accounts exist; pass --account" —
    /// and a test peer accumulates them, so every command carries the selector
    /// rather than relying on there being exactly one.
    /// </remarks>
    public string? SelectedAccount { get; set; }

    /// <summary>Whether the container is up and the binaries run.</summary>
    public async Task<bool> IsReadyAsync()
    {
        try
        {
            string status = await RunAsync(
                "inspect", "-f", "{{.State.Running}}", ContainerName);

            if (!status.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                return false;

            await ExecAsync("wn", "--version");
            return true;
        }
        catch (Exception ex)
        {
            log($"mdk-cli not ready: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Starts the daemon, pointed at one relay for both discovery and accounts.
    /// </summary>
    /// <remarks>
    /// Idempotent: a daemon already running is left alone rather than treated as
    /// an error, so a test can call this without knowing the container's history.
    /// </remarks>
    public async Task StartDaemonAsync(string relayUrl)
    {
        try
        {
            await CliAsync("daemon", "start",
                "--data-dir", Home,
                "--discovery-relays", relayUrl,
                "--default-account-relays", relayUrl,
                "--logs-dir", LogsDir);
        }
        catch (Exception ex) when (ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            log("daemon already running");
        }
    }

    /// <summary>Creates a local signing identity and returns its hex pubkey.</summary>
    public async Task<string> CreateIdentityAsync()
    {
        JsonElement created = await CliJsonAsync("create-identity");
        string pubkey = FindPubkey(created)
            ?? throw new InvalidOperationException(
                $"create-identity returned no pubkey: {created}");

        SelectedAccount = pubkey;
        log($"mdk-cli identity {pubkey}");
        return pubkey;
    }

    /// <summary>Publishes the account's KeyPackage so it can be invited.</summary>
    public Task PublishKeyPackageAsync() => CliAsync("keys", "publish");

    /// <summary>Processes relay events for the selected account.</summary>
    public Task SyncAsync() => CliAsync("sync");

    /// <summary>Raw JSON of the account's pending group invites.</summary>
    public Task<JsonElement> InvitesAsync() => CliJsonAsync("groups", "invites");

    /// <summary>Accepts a pending invite.</summary>
    public Task AcceptInviteAsync(string groupIdHex) =>
        CliAsync("groups", "accept", groupIdHex);

    /// <summary>
    /// Whether the peer can resolve an account well enough to invite it.
    /// </summary>
    /// <remarks>
    /// The peer needs relay lists <b>and</b> a fetchable KeyPackage. Asking it
    /// directly turns "we are not discoverable" into a named failure instead of
    /// a group that silently never arrives.
    /// </remarks>
    public async Task<bool> CanInviteAsync(string pubkeyHex)
    {
        try
        {
            JsonElement response = await CliJsonAsync("keys", "check", pubkeyHex);

            // The peer answers with an explicit `available` flag. Read it rather
            // than scanning the text: an earlier version searched for the word
            // "missing" and matched the empty `missing: []` array that a
            // perfectly resolvable account also carries.
            if (response.TryGetProperty("result", out var result)
                && result.TryGetProperty("available", out var available)
                && available.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                if (!available.GetBoolean())
                    log($"keys check says not available: {result}");

                return available.GetBoolean();
            }

            log($"keys check gave no available flag: {response}");
            return false;
        }
        catch (Exception ex)
        {
            log($"keys check: {ex.Message}");
            return false;
        }
    }

    /// <summary>Creates a group with the given members.</summary>
    public Task CreateGroupAsync(string name, params string[] memberPubkeys) =>
        CliAsync(["groups", "create", name, .. memberPubkeys]);

    /// <summary>Raw JSON of the account's groups.</summary>
    public Task<JsonElement> GroupsAsync() => CliJsonAsync("groups", "list");

    /// <summary>Raw JSON of one group's members.</summary>
    public Task<JsonElement> MembersAsync(string groupIdHex) =>
        CliJsonAsync("groups", "members", groupIdHex);

    /// <summary>Sends a message to a group.</summary>
    public Task SendMessageAsync(string groupIdHex, string text) =>
        CliAsync("messages", "send", "--group", groupIdHex, text);

    /// <summary>Raw JSON of the messages the peer has in a group.</summary>
    public Task<JsonElement> MessagesAsync(string groupIdHex) =>
        CliJsonAsync("messages", "list", "--group", groupIdHex);

    /// <summary>
    /// Whether the peer holds a message with exactly this content.
    /// </summary>
    /// <remarks>
    /// Matched on the message body rather than on an id, because the ids the
    /// two sides use for a message are not required to agree and asserting on
    /// them would test our reading of the CLI's JSON rather than interop.
    /// </remarks>
    public async Task<bool> HasMessageAsync(string groupIdHex, string content)
    {
        JsonElement messages = await MessagesAsync(groupIdHex);
        return Strings(messages).Any(value => value == content);
    }

    /// <summary>The CLI version, for the test log.</summary>
    public async Task<string> VersionAsync() => (await ExecAsync("wn", "--version")).Trim();

    /// <summary>
    /// Whether any group id under <paramref name="json"/> matches.
    /// </summary>
    /// <remarks>
    /// Searches the whole document rather than a fixed path: the CLI's JSON
    /// shape is not part of any contract we control, and a test that hardcodes
    /// it breaks on an upstream field rename for no protocol reason.
    /// </remarks>
    public static bool ContainsGroupId(JsonElement json, string groupIdHex)
    {
        foreach (string value in Strings(json))
        {
            if (value.Equals(groupIdHex, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Every string value anywhere in a JSON document.</summary>
    public static IEnumerable<string> Strings(JsonElement json)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.String:
                if (json.GetString() is { } s)
                    yield return s;
                break;

            case JsonValueKind.Array:
                foreach (var item in json.EnumerateArray())
                    foreach (string nested in Strings(item))
                        yield return nested;
                break;

            case JsonValueKind.Object:
                foreach (var property in json.EnumerateObject())
                    foreach (string nested in Strings(property.Value))
                        yield return nested;
                break;
        }
    }

    private static string? FindPubkey(JsonElement json)
    {
        // A 64-character lowercase hex string is an x-only pubkey; nothing else
        // the CLI emits looks like one, so this survives a field rename.
        foreach (string value in Strings(json))
        {
            if (value.Length == 64 && value.All(c => char.IsAsciiHexDigitLower(c)))
                return value;
        }

        return null;
    }

    private async Task<JsonElement> CliJsonAsync(params string[] args)
    {
        string output = await CliAsync(["--json", .. args]);
        string json = output.Trim();

        if (json.Length == 0)
            throw new InvalidOperationException($"wn {string.Join(' ', args)} produced no output.");

        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"wn {string.Join(' ', args)} did not produce JSON: {ex.Message}\n{json}");
        }
    }

    private Task<string> CliAsync(params string[] args)
    {
        string[] selector = SelectedAccount is null ? [] : ["--account", SelectedAccount];

        return ExecAsync([
            "wn",
            "--home", Home,
            "--socket", Socket,
            // File-backed secrets: there is no OS keychain in a container, and
            // this is what removes the need for the patched test binary the old
            // whitenoise image carried.
            "--secret-store", "file",
            .. selector,
            .. args,
        ]);
    }

    private Task<string> ExecAsync(params string[] args) =>
        RunAsync(["exec", ContainerName, .. args]);

    private static async Task<string> RunAsync(params string[] args)
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
        // path in a docker argument is rewritten to a Windows one, and the
        // failure that produces names no path.
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
