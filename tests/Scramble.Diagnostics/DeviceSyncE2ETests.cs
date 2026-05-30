using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
using Xunit;

namespace Scramble.Diagnostics;

/// <summary>
/// End-to-end device sync (Private Notes) test: 2 Scramble instances with the SAME
/// nsec (simulating 2 devices for the same user), connected to a real relay.
///
/// Verifies that:
///   1. Both devices can create/join a shared DeviceSync group
///   2. Messages sent on Device A appear on Device B and vice versa
///
/// Relay: wss://test.thedude.cloud (override via SCRAMBLE_TEST_RELAY env var)
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "DeviceSync")]
public class DeviceSyncE2ETests : IAsyncLifetime
{
    private static string RelayUrl => TestRelayConfig.RelayUrl;

    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();
    private readonly List<NostrService> _nostrServices = new();
    private readonly List<MessageService> _messageServices = new();

    public DeviceSyncE2ETests(ITestOutputHelper output)
    {
        _output = output;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var ms in _messageServices)
            ms.Dispose();

        foreach (var ns in _nostrServices)
        {
            try { await ns.DisconnectAsync(); }
            catch { }
            (ns as IDisposable)?.Dispose();
        }

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        foreach (var path in _dbPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    private record Device(
        string Name,
        string PubKeyHex, string PrivKeyHex,
        NostrService NostrService,
        StorageService Storage,
        IMlsService MlsService,
        MessageService MessageService);

    /// <summary>
    /// Create a device instance for the given identity. Each device gets its own
    /// StorageService (separate DB), MlsService (separate MLS state/slot), and
    /// NostrService (separate relay connection), but they share the same Nostr keypair.
    /// </summary>
    private async Task<Device> CreateDevice(
        string name, string pubKeyHex, string privKeyHex)
    {
        var nostrService = new NostrService();
        _nostrServices.Add(nostrService);

        var dbPath = Path.Combine(Path.GetTempPath(), $"oc_sync_{name}_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();
        await storage.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = pubKeyHex,
            PrivateKeyHex = privKeyHex,
            Npub = $"npub_{name}",
            Nsec = $"nsec_{name}",
            DisplayName = name,
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        IMlsService mlsService = new ManagedMlsService(storage);

        var messageService = new MessageService(storage, nostrService, mlsService);
        _messageServices.Add(messageService);
        await messageService.InitializeAsync();

        // Connect to relay
        await nostrService.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        _output.WriteLine($"[{name}] Created device: pubkey={pubKeyHex[..16]}... connected to {RelayUrl}");
        _output.WriteLine($"  Relay status: {string.Join(", ", nostrService.ConnectedRelayUrls)}");
        _output.WriteLine($"  MLS slot ID: {mlsService.GetLocalKeyPackageSlotId() ?? "(none yet)"}");

        return new Device(name, pubKeyHex, privKeyHex,
            nostrService, storage, mlsService, messageService);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test: Two devices with the same nsec sync Private Notes via relay
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeviceSync_TwoDevices_SameNsec_MessagesSyncViaRelay()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  DEVICE SYNC E2E: 2 devices, same nsec, Private Notes");
        _output.WriteLine($"  Relay: {RelayUrl}");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        // Generate a shared identity
        var tmpNostr = new NostrService();
        var keys = tmpNostr.GenerateKeyPair();
        (tmpNostr as IDisposable)?.Dispose();
        _output.WriteLine($"\nShared identity: {keys.publicKeyHex[..16]}...");

        // ── Step 1: Create both devices ──
        _output.WriteLine("\n[Step 1] Creating Device A and Device B with the same identity");
        var deviceA = await CreateDevice("DeviceA", keys.publicKeyHex, keys.privateKeyHex);
        var deviceB = await CreateDevice("DeviceB", keys.publicKeyHex, keys.privateKeyHex);

        // ── Step 2: Both publish KeyPackages ──
        _output.WriteLine("\n[Step 2] Publishing KeyPackages for both devices");
        var kpA = await deviceA.MlsService.GenerateKeyPackageAsync();
        await deviceA.NostrService.PublishKeyPackageAsync(kpA.Data, keys.privateKeyHex, kpA.NostrTags);
        var slotA = deviceA.MlsService.GetLocalKeyPackageSlotId();
        _output.WriteLine($"  Device A KeyPackage published (slot={slotA?[..Math.Min(16, slotA?.Length ?? 0)]})");

        var kpB = await deviceB.MlsService.GenerateKeyPackageAsync();
        await deviceB.NostrService.PublishKeyPackageAsync(kpB.Data, keys.privateKeyHex, kpB.NostrTags);
        var slotB = deviceB.MlsService.GetLocalKeyPackageSlotId();
        _output.WriteLine($"  Device B KeyPackage published (slot={slotB?[..Math.Min(16, slotB?.Length ?? 0)]})");

        Assert.NotEqual(slotA, slotB); // Different devices must have different slots
        await Task.Delay(2000); // Let relay process

        // ── Step 3: Device A creates DeviceSync group ──
        _output.WriteLine("\n[Step 3] Device A creates DeviceSync (Private Notes) group");
        var syncChatA = await deviceA.MessageService.GetOrCreateDeviceSyncGroupAsync();
        Assert.NotNull(syncChatA);
        Assert.Equal(ChatType.DeviceSync, syncChatA.Type);
        var groupIdHex = Convert.ToHexString(syncChatA.MlsGroupId!).ToLowerInvariant();
        _output.WriteLine($"  Sync group created: chatId={syncChatA.Id}, mlsGroupId={groupIdHex[..16]}...");

        // ── Step 4: Device A discovers Device B's KeyPackage ──
        _output.WriteLine("\n[Step 4] Device A fetches KeyPackages and discovers Device B");
        var allKps = (await deviceA.NostrService.FetchKeyPackagesAsync(keys.publicKeyHex)).ToList();
        _output.WriteLine($"  Found {allKps.Count} KeyPackage(s) on relay");
        foreach (var kp in allKps)
            _output.WriteLine($"    slot={kp.SlotId?[..Math.Min(16, kp.SlotId?.Length ?? 0)]} cipher={kp.IsCipherSuiteSupported}");

        var peerKpForA = allKps.FirstOrDefault(kp =>
            kp.IsCipherSuiteSupported &&
            !string.IsNullOrEmpty(kp.SlotId) &&
            kp.SlotId != slotA);
        Assert.NotNull(peerKpForA); // Device B's KP must be discoverable
        _output.WriteLine($"  Peer device found: slot={peerKpForA.SlotId?[..Math.Min(16, peerKpForA.SlotId?.Length ?? 0)]}");

        // ── Step 5: Device B subscribes to Welcomes ──
        _output.WriteLine("\n[Step 5] Device B subscribes to Welcomes (kind 1059)");
        await deviceB.NostrService.SubscribeToWelcomesAsync(keys.publicKeyHex, keys.privateKeyHex);
        await Task.Delay(1000);

        // ── Step 6: Device A invites Device B to the sync group ──
        _output.WriteLine("\n[Step 6] Device A invites Device B to the sync group");
        await deviceA.MessageService.InvitePeerToSyncGroupAsync(peerKpForA, syncChatA.Id);
        _output.WriteLine("  Welcome sent to Device B");
        await Task.Delay(3000); // Give time for gift wrap delivery + MLS processing

        // ── Step 7: Verify Device B has the sync group ──
        _output.WriteLine("\n[Step 7] Verifying Device B has the shared sync group");
        var syncChatIdB = await deviceB.Storage.GetSettingAsync("device_sync_chat_id");
        Assert.False(string.IsNullOrEmpty(syncChatIdB), "Device B should have device_sync_chat_id setting after Welcome");
        var syncChatB = await deviceB.Storage.GetChatAsync(syncChatIdB!);
        Assert.NotNull(syncChatB);
        Assert.Equal(ChatType.DeviceSync, syncChatB!.Type);
        Assert.Equal("Private Notes", syncChatB.Name);
        _output.WriteLine($"  Device B sync group: chatId={syncChatB.Id}, mlsGroupId={Convert.ToHexString(syncChatB.MlsGroupId!).ToLowerInvariant()[..16]}...");

        // Both must be in the SAME MLS group
        var groupIdHexB = Convert.ToHexString(syncChatB.MlsGroupId!).ToLowerInvariant();
        Assert.Equal(groupIdHex, groupIdHexB);
        _output.WriteLine("  ✓ Both devices are in the SAME MLS group!");

        // ── Step 8: Subscribe both devices to group messages ──
        _output.WriteLine("\n[Step 8] Subscribing both devices to group messages");
        var nostrGroupIdA = syncChatA.NostrGroupId != null
            ? Convert.ToHexString(syncChatA.NostrGroupId).ToLowerInvariant()
            : groupIdHex;
        await deviceA.NostrService.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdA });
        await deviceB.NostrService.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdA });
        await Task.Delay(1000);

        // ── Step 9: Device A sends a note ──
        _output.WriteLine("\n[Step 9] Device A sends a Private Note");
        var noteTextA = $"Hello from Device A! ({Guid.NewGuid():N})";
        var sentMsgA = await deviceA.MessageService.SendMessageAsync(syncChatA.Id, noteTextA);
        Assert.NotNull(sentMsgA);
        _output.WriteLine($"  Sent: \"{noteTextA}\"");
        _output.WriteLine($"  NostrEventId: {sentMsgA.NostrEventId?[..Math.Min(16, sentMsgA.NostrEventId?.Length ?? 0)]}");

        // Wait for Device B to receive it via relay
        _output.WriteLine("  Waiting for Device B to receive...");
        Message? receivedByB = null;
        for (int i = 0; i < 30; i++) // 15 seconds max
        {
            await Task.Delay(500);
            var messagesB = await deviceB.Storage.GetMessagesForChatAsync(syncChatB.Id);
            receivedByB = messagesB.FirstOrDefault(m =>
                m.Content == noteTextA && m.Type == MessageType.Text);
            if (receivedByB != null) break;
        }

        Assert.NotNull(receivedByB);
        _output.WriteLine($"  ✓ Device B received: \"{receivedByB!.Content}\"");

        // ── Step 10: Device B sends a note back ──
        _output.WriteLine("\n[Step 10] Device B sends a Private Note");
        var noteTextB = $"Hello from Device B! ({Guid.NewGuid():N})";
        var sentMsgB = await deviceB.MessageService.SendMessageAsync(syncChatB.Id, noteTextB);
        Assert.NotNull(sentMsgB);
        _output.WriteLine($"  Sent: \"{noteTextB}\"");

        // Wait for Device A to receive it
        _output.WriteLine("  Waiting for Device A to receive...");
        Message? receivedByA = null;
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            var messagesA = await deviceA.Storage.GetMessagesForChatAsync(syncChatA.Id);
            receivedByA = messagesA.FirstOrDefault(m =>
                m.Content == noteTextB && m.Type == MessageType.Text);
            if (receivedByA != null) break;
        }

        Assert.NotNull(receivedByA);
        _output.WriteLine($"  ✓ Device A received: \"{receivedByA!.Content}\"");

        // ── Done ──
        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  ✓ DEVICE SYNC TEST PASSED");
        _output.WriteLine("  Both devices successfully exchanged Private Notes via relay");
        _output.WriteLine("═══════════════════════════════════════════════════════════");
    }
}
