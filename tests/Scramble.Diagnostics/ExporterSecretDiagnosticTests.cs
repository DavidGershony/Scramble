using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
using Xunit;
namespace Scramble.Diagnostics;

/// <summary>
/// Diagnostic tests to dump MLS key schedule values for cross-implementation comparison.
/// Used to find the exact point of exporter secret divergence between marmot-cs and ts-mls.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "WebInterop")]
public class ExporterSecretDiagnosticTests : IAsyncLifetime
{
    private static string RelayUrl => TestRelayConfig.RelayUrl;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();
    private readonly List<NostrService> _nostrServices = new();
    private readonly List<MessageService> _messageServices = new();

    public ExporterSecretDiagnosticTests(ITestOutputHelper output) => _output = output;
    public ValueTask InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ms in _messageServices) ms.Dispose();
        foreach (var ns in _nostrServices)
        {
            try { await ns.DisconnectAsync(); } catch { }
            (ns as IDisposable)?.Dispose();
        }
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var p in _dbPaths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }

    private async Task<(NostrService nostr, StorageService storage, ManagedMlsService mls, MessageService msg, string pubHex, string privHex, string npub, string nsec)>
        CreateUser(string name)
    {
        var nostr = new NostrService();
        _nostrServices.Add(nostr);
        var keys = nostr.GenerateKeyPair();
        var dbPath = Path.Combine(Path.GetTempPath(), $"scramble_diag_{name}_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();
        await storage.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = keys.publicKeyHex,
            PrivateKeyHex = keys.privateKeyHex,
            Npub = keys.npub, Nsec = keys.nsec,
            DisplayName = name, IsCurrentUser = true, CreatedAt = DateTime.UtcNow
        });
        var mls = new ManagedMlsService(storage);
        var msg = new MessageService(storage, nostr, mls);
        _messageServices.Add(msg);
        await msg.InitializeAsync();
        return (nostr, storage, mls, msg, keys.publicKeyHex, keys.privateKeyHex, keys.npub, keys.nsec);
    }

    /// <summary>
    /// Creates a group between two Scramble users, dumps ALL key schedule
    /// values at each epoch so they can be compared with ts-mls.
    /// Also verifies that both sides derive the same exporter secret.
    /// </summary>
    [Fact]
    public async Task DumpKeyScheduleValues_ForCrossImplComparison()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  DIAGNOSTIC: Key Schedule Values for Cross-Impl Comparison");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var alice = await CreateUser("DiagAlice");
        var bob = await CreateUser("DiagBob");

        _output.WriteLine($"\nAlice pubkey: {alice.pubHex}");
        _output.WriteLine($"Alice nsec:   {alice.nsec}");
        _output.WriteLine($"Bob pubkey:   {bob.pubHex}");
        _output.WriteLine($"Bob nsec:     {bob.nsec}");

        // Alice creates a group
        var groupInfo = await alice.mls.CreateGroupAsync("Diagnostic Group", new[] { "wss://relay.test" });
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        var nostrGroupId = alice.mls.GetNostrGroupId(groupInfo.GroupId);
        var nostrGroupIdHex = nostrGroupId != null ? Convert.ToHexString(nostrGroupId).ToLowerInvariant() : "none";

        _output.WriteLine($"\n--- Group Created (Epoch 0) ---");
        _output.WriteLine($"MLS GroupId:    {groupIdHex}");
        _output.WriteLine($"Nostr GroupId:  {nostrGroupIdHex}");
        _output.WriteLine($"Epoch:          {groupInfo.Epoch}");

        // Dump Alice's exporter secret at epoch 0
        var aliceExporter0 = alice.mls.GetExporterSecret(groupInfo.GroupId);
        _output.WriteLine($"\nAlice exporter secret (epoch 0): {Convert.ToHexString(aliceExporter0).ToLowerInvariant()}");

        // Bob generates KeyPackage
        var kpBob = await bob.mls.GenerateKeyPackageAsync();
        _output.WriteLine($"\nBob KeyPackage: {kpBob.Data.Length} bytes");
        _output.WriteLine($"Bob KP hex (first 64): {Convert.ToHexString(kpBob.Data[..Math.Min(32, kpBob.Data.Length)]).ToLowerInvariant()}");

        // Create a fake event JSON for the KeyPackage (needed by AddMemberAsync)
        var kpContent = Convert.ToBase64String(kpBob.Data);
        var fakeEventJson = $"{{\"id\":\"fake\",\"pubkey\":\"{bob.pubHex}\",\"created_at\":0,\"kind\":30443,\"tags\":[],\"content\":\"{kpContent}\",\"sig\":\"fake\"}}";
        var kp = new KeyPackage
        {
            Id = Guid.NewGuid().ToString(),
            Data = kpBob.Data,
            NostrEventId = "fake_kp_event",
            OwnerPublicKey = bob.pubHex,
            EventJson = fakeEventJson,
            CreatedAt = DateTime.UtcNow
        };

        // Alice adds Bob
        _output.WriteLine($"\n--- Alice adds Bob (Epoch 0 → 1) ---");
        var welcome = await alice.mls.AddMemberAsync(groupInfo.GroupId, kp);
        _output.WriteLine($"Welcome data: {welcome.WelcomeData.Length} bytes");
        _output.WriteLine($"Welcome hex (first 64): {Convert.ToHexString(welcome.WelcomeData[..Math.Min(32, welcome.WelcomeData.Length)]).ToLowerInvariant()}");
        _output.WriteLine($"Commit data: {welcome.CommitData?.Length ?? 0} bytes");

        // Dump Alice's exporter secret at epoch 1
        var aliceExporter1 = alice.mls.GetExporterSecret(groupInfo.GroupId);
        _output.WriteLine($"\nAlice exporter secret (epoch 1): {Convert.ToHexString(aliceExporter1).ToLowerInvariant()}");

        // Bob processes the Welcome
        _output.WriteLine($"\n--- Bob processes Welcome ---");
        var bobGroupInfo = await bob.mls.ProcessWelcomeAsync(welcome.WelcomeData, "0000000000000000000000000000000000000000000000000000000000000000");
        var bobGroupIdHex = Convert.ToHexString(bobGroupInfo.GroupId).ToLowerInvariant();
        _output.WriteLine($"Bob MLS GroupId: {bobGroupIdHex}");
        _output.WriteLine($"Bob Epoch:       {bobGroupInfo.Epoch}");

        // Dump Bob's exporter secret
        var bobExporter = bob.mls.GetExporterSecret(bobGroupInfo.GroupId);
        _output.WriteLine($"Bob exporter secret (epoch 1):   {Convert.ToHexString(bobExporter).ToLowerInvariant()}");

        // Compare
        var match = aliceExporter1.AsSpan().SequenceEqual(bobExporter.AsSpan());
        _output.WriteLine($"\n╔══════════════════════════════════════════════╗");
        _output.WriteLine($"║ Exporter secrets match: {(match ? "YES ✅" : "NO ❌")}              ║");
        _output.WriteLine($"╚══════════════════════════════════════════════╝");

        if (!match)
        {
            _output.WriteLine("\nDIVERGENCE DETECTED within marmot-cs itself!");
            _output.WriteLine("This is a bug in dotnet-mls Welcome processing.");
        }
        else
        {
            _output.WriteLine("\nmarmot-cs internally consistent. Cross-impl divergence");
            _output.WriteLine("must be in how ts-mls processes the same Welcome bytes.");
        }

        // Verify message exchange works
        _output.WriteLine($"\n--- Message exchange test ---");
        var ct = await alice.mls.EncryptMessageAsync(groupInfo.GroupId, "diagnostic test");
        using var doc = JsonDocument.Parse(ct);
        var content = doc.RootElement.GetProperty("content").GetString()!;
        var ctBytes = Convert.FromBase64String(content);
        _output.WriteLine($"Ciphertext: {ctBytes.Length} bytes");
        _output.WriteLine($"Nonce (12b): {Convert.ToHexString(ctBytes[..12]).ToLowerInvariant()}");

        var decrypted = await bob.mls.DecryptMessageAsync(bobGroupInfo.GroupId, ctBytes);
        _output.WriteLine($"Decrypted: \"{decrypted.Plaintext}\"");
        Assert.Equal("diagnostic test", decrypted.Plaintext);

        _output.WriteLine($"\n═══════════════════════════════════════════════════════════");
        _output.WriteLine($"  VALUES TO COMPARE WITH ts-mls / web app");
        _output.WriteLine($"═══════════════════════════════════════════════════════════");
        _output.WriteLine($"Group ID:                 {groupIdHex}");
        _output.WriteLine($"Nostr Group ID (h-tag):   {nostrGroupIdHex}");
        _output.WriteLine($"Epoch:                    1");
        _output.WriteLine($"Exporter secret (32b):    {Convert.ToHexString(aliceExporter1).ToLowerInvariant()}");
        _output.WriteLine($"Welcome (base64):         {Convert.ToBase64String(welcome.WelcomeData)}");
    }

    /// <summary>
    /// Publishes a KeyPackage to relay2.angor.io and waits for a Welcome from the web app.
    /// When a Welcome arrives, processes it and dumps all key schedule values.
    /// User should import the nsec into the web app, create a group, and invite this user.
    /// </summary>
    [Fact]
    public async Task PublishKPAndWaitForWebAppWelcome()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  INTERACTIVE: Publish KP, wait for web app Welcome");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var user = await CreateUser("WebInteropUser");
        await user.nostr.ConnectAsync(RelayUrl);
        await Task.Delay(2000);

        // Publish KeyPackage
        var kp = await user.mls.GenerateKeyPackageAsync();
        var eventId = await user.nostr.PublishKeyPackageAsync(kp.Data, user.privHex, kp.NostrTags);

        _output.WriteLine($"\n╔══════════════════════════════════════════════════════════╗");
        _output.WriteLine($"║  User published to {RelayUrl}");
        _output.WriteLine($"║  npub: {user.npub}");
        _output.WriteLine($"║  nsec: {user.nsec}");
        _output.WriteLine($"║  pubkey: {user.pubHex}");
        _output.WriteLine($"║  KeyPackage event: {eventId}");
        _output.WriteLine($"║");
        _output.WriteLine($"║  Now go to the web app:");
        _output.WriteLine($"║  1. Create/login with a different user");
        _output.WriteLine($"║  2. Set relay to {RelayUrl}");
        _output.WriteLine($"║  3. Create a group");
        _output.WriteLine($"║  4. Invite npub above");
        _output.WriteLine($"║  5. This test will process the Welcome and dump values");
        _output.WriteLine($"╚══════════════════════════════════════════════════════════╝");

        // Subscribe to welcomes
        await user.nostr.SubscribeToWelcomesAsync(user.pubHex, user.privHex);

        // Wait for a welcome (up to 5 minutes for manual interaction)
        var welcomeTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = user.msg.NewInvites
            .Take(1)
            .Subscribe(invite => welcomeTcs.TrySetResult(invite));

        // Also try rescan periodically
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(10000);
                if (welcomeTcs.Task.IsCompleted) break;
                _output.WriteLine($"  Rescanning... ({i + 1}/30)");
                try { await user.msg.RescanInvitesAsync(); }
                catch (Exception ex) { _output.WriteLine($"  Rescan error: {ex.Message}"); }
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        cts.Token.Register(() => welcomeTcs.TrySetCanceled());

        PendingInvite invite;
        try
        {
            invite = await welcomeTcs.Task;
        }
        catch (TaskCanceledException)
        {
            _output.WriteLine("\nTimeout waiting for Welcome. Run the test again after sending the invite from the web app.");
            return;
        }

        _output.WriteLine($"\n--- Welcome received! ---");
        _output.WriteLine($"Sender: {invite.SenderPublicKey}");
        _output.WriteLine($"Welcome data: {invite.WelcomeData.Length} bytes");
        _output.WriteLine($"Event ID: {invite.NostrEventId}");

        // Process the Welcome
        _output.WriteLine($"\n--- Processing Welcome ---");
        try
        {
            var chat = await user.msg.AcceptInviteAsync(invite.Id);
            _output.WriteLine($"Chat created: {chat.Name}");
            _output.WriteLine($"MLS GroupId: {Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant()}");

            var nostrGid = chat.NostrGroupId != null
                ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
                : "none";
            _output.WriteLine($"Nostr GroupId: {nostrGid}");
            _output.WriteLine($"Epoch: {chat.MlsEpoch}");

            // Dump exporter secret
            var exporterSecret = user.mls.GetExporterSecret(chat.MlsGroupId!);
            _output.WriteLine($"\n╔══════════════════════════════════════════════════════════╗");
            _output.WriteLine($"║  Exporter secret: {Convert.ToHexString(exporterSecret).ToLowerInvariant()}");
            _output.WriteLine($"║  Group ID:        {Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant()}");
            _output.WriteLine($"║  Nostr Group ID:  {nostrGid}");
            _output.WriteLine($"║  Epoch:           {chat.MlsEpoch}");
            _output.WriteLine($"╚══════════════════════════════════════════════════════════╝");

            _output.WriteLine("\nCompare this exporter secret with the web app's value.");
            _output.WriteLine("In the web app, open browser console and check the group's");
            _output.WriteLine("clientState.keySchedule.exporterSecret value.");

            // Try to send a message
            _output.WriteLine($"\n--- Sending test message ---");
            var msgBytes = await user.mls.EncryptMessageAsync(chat.MlsGroupId!, "Hello from Scramble diagnostic!");
            var msgEventId = await user.nostr.PublishRawEventJsonAsync(msgBytes);
            _output.WriteLine($"Published kind 445 message: {msgEventId}");

            // Wait for relay to acknowledge and store the event
            _output.WriteLine("Waiting 5s for relay to store event...");
            await Task.Delay(5000);

            _output.WriteLine("Check if the web app can decrypt this message.");
            _output.WriteLine("Keeping connection alive for 30s so relay processes everything...");
            await Task.Delay(30000);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            _output.WriteLine($"Stack: {ex.StackTrace}");

            // Still dump the welcome bytes for analysis
            _output.WriteLine($"\nWelcome hex (first 64): {Convert.ToHexString(invite.WelcomeData[..Math.Min(32, invite.WelcomeData.Length)]).ToLowerInvariant()}");
            if (invite.WelcomeData.Length >= 4)
            {
                var ver = (invite.WelcomeData[0] << 8) | invite.WelcomeData[1];
                var wf = (invite.WelcomeData[2] << 8) | invite.WelcomeData[3];
                _output.WriteLine($"MLSMessage version: 0x{ver:X4}, wireFormat: 0x{wf:X4}");
            }
        }
    }
}
