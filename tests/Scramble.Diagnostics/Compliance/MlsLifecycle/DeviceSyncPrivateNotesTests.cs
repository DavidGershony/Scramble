using Scramble.Core.Models;
using Xunit;

namespace Scramble.Diagnostics.Compliance.MlsLifecycle;

/// <summary>
/// Hermetic device-sync lifecycle test — two devices, same nsec, exchanging
/// Private Notes over an in-process relay. This is the compliance-suite
/// promotion of the diagnostic in <c>DeviceSyncE2ETests.cs</c> (commit
/// <c>bb2466da</c>), which finally proved device sync had been silently
/// broken for ~7 weeks.
///
/// Historical context (ANALYSIS.md STEP 6):
/// Device sync was broken from 2026-04-08 through 2026-05-30. Both the
/// original bug (`3f18c51`) and the diagnostic that proved it (`bb2466da`)
/// landed the same day. This test locks in the fix as a merge gate so it
/// cannot regress silently again.
///
/// The existing E2E variant depends on an external relay and is skipped in
/// most CI configurations. This copy uses <see cref="Scramble.Diagnostics.RelayHarness.FaultyRelay"/>
/// so it runs unconditionally in the integration workflow (S1).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "DeviceSync")]
[Trait("MlsLifecycle", "DeviceSyncPrivateNotes")]
public class DeviceSyncPrivateNotesTests : MlsLifecycleTestBase
{
    public DeviceSyncPrivateNotesTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task TwoDevices_SameNsec_ExchangePrivateNotes_Bidirectional()
    {
        // Generate a shared identity out-of-band, then hand each device a
        // party that overrides its identity with those keys.
        var keygen = new Scramble.Core.Services.NostrService();
        var shared = keygen.GenerateKeyPair();
        (keygen as IDisposable)?.Dispose();

        var deviceA = await CreatePartyWithIdentityAsync("DeviceA", shared.publicKeyHex, shared.privateKeyHex);
        var deviceB = await CreatePartyWithIdentityAsync("DeviceB", shared.publicKeyHex, shared.privateKeyHex);

        // Both devices publish KeyPackages. Multi-device is what makes the
        // slot IDs diverge — we sanity check that below.
        var kpA = await deviceA.MlsService.GenerateKeyPackageAsync();
        await deviceA.NostrService.PublishKeyPackageAsync(kpA.Data, shared.privateKeyHex, kpA.NostrTags);
        var slotA = deviceA.MlsService.GetLocalKeyPackageSlotId();

        var kpB = await deviceB.MlsService.GenerateKeyPackageAsync();
        await deviceB.NostrService.PublishKeyPackageAsync(kpB.Data, shared.privateKeyHex, kpB.NostrTags);
        var slotB = deviceB.MlsService.GetLocalKeyPackageSlotId();

        Assert.NotEqual(slotA, slotB); // else this is a single-device test
        await Task.Delay(1500);

        // Device A creates the DeviceSync (Private Notes) group.
        var syncChatA = await deviceA.MessageService.GetOrCreateDeviceSyncGroupAsync();
        Assert.Equal(ChatType.DeviceSync, syncChatA.Type);
        var syncGroupIdHex = Convert.ToHexString(syncChatA.MlsGroupId!).ToLowerInvariant();

        // Device A fetches KPs, finds device B's, and invites it.
        var allKps = (await deviceA.NostrService.FetchKeyPackagesAsync(shared.publicKeyHex)).ToList();
        var peerKp = allKps.FirstOrDefault(kp =>
            kp.IsCipherSuiteSupported &&
            !string.IsNullOrEmpty(kp.SlotId) &&
            kp.SlotId != slotA);
        Assert.NotNull(peerKp);

        await deviceB.NostrService.SubscribeToWelcomesAsync(shared.publicKeyHex, shared.privateKeyHex);
        await Task.Delay(1000);

        await deviceA.MessageService.InvitePeerToSyncGroupAsync(peerKp!, syncChatA.Id);
        await Task.Delay(3000);

        // Device B must now have the same DeviceSync group under the same MLS id.
        var syncChatIdOnB = await deviceB.Storage.GetSettingAsync("device_sync_chat_id");
        Assert.False(string.IsNullOrEmpty(syncChatIdOnB));
        var syncChatB = await deviceB.Storage.GetChatAsync(syncChatIdOnB!);
        Assert.NotNull(syncChatB);
        Assert.Equal(ChatType.DeviceSync, syncChatB!.Type);
        Assert.Equal(syncGroupIdHex,
            Convert.ToHexString(syncChatB.MlsGroupId!).ToLowerInvariant());

        // Subscribe both devices to their group's kind-445 traffic.
        var nostrGroupId = syncChatA.NostrGroupId != null
            ? Convert.ToHexString(syncChatA.NostrGroupId).ToLowerInvariant()
            : syncGroupIdHex;
        await deviceA.NostrService.SubscribeToGroupMessagesAsync(new[] { nostrGroupId });
        await deviceB.NostrService.SubscribeToGroupMessagesAsync(new[] { nostrGroupId });
        await Task.Delay(1000);

        // A → B
        var noteA = $"noteA-{Guid.NewGuid():N}";
        await deviceA.MessageService.SendMessageAsync(syncChatA.Id, noteA);
        await WaitForMessageAsync(deviceB, syncChatB.Id, noteA, timeout: TimeSpan.FromSeconds(15));

        // B → A
        var noteB = $"noteB-{Guid.NewGuid():N}";
        await deviceB.MessageService.SendMessageAsync(syncChatB.Id, noteB);
        await WaitForMessageAsync(deviceA, syncChatA.Id, noteB, timeout: TimeSpan.FromSeconds(15));

        Output.WriteLine("[device-sync] bidirectional Private Notes exchange OK");
    }

    /// <summary>
    /// Variant of <see cref="MlsLifecycleTestBase.CreatePartyAsync"/> that
    /// overrides the identity with an externally-generated keypair, so both
    /// devices in a device-sync test can share the same nsec.
    /// </summary>
    private async Task<Party> CreatePartyWithIdentityAsync(
        string name, string publicKeyHex, string privateKeyHex)
    {
        var nostr = new Scramble.Core.Services.NostrService();
        var dbPath = Path.Combine(Path.GetTempPath(),
            $"scramble_mls_{name}_{Guid.NewGuid():N}.db");
        var storage = new Scramble.Core.Services.StorageService(
            dbPath, new Scramble.Diagnostics.TestHelpers.MockSecureStorage());
        await storage.InitializeAsync();
        await storage.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = publicKeyHex,
            PrivateKeyHex = privateKeyHex,
            Npub = $"npub_{name}",
            Nsec = $"nsec_{name}",
            DisplayName = name,
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        Scramble.Core.Services.IMlsService mls =
            new Scramble.Core.Services.ManagedMlsService(storage);
        var messages = new Scramble.Core.Services.MessageService(storage, nostr, mls);
        await messages.InitializeAsync();

        await nostr.ConnectAsync(Relay.WsUrl);
        await Task.Delay(300);

        // Register with the base for cleanup by calling the tracked-collection
        // mutators via reflection would be brittle — instead track locally with
        // parallel cleanup here.
        _trackedForCleanup.Add((nostr, messages, dbPath));

        return new Party(name, publicKeyHex, privateKeyHex, nostr, storage, mls, messages);
    }

    private readonly List<(Scramble.Core.Services.NostrService, Scramble.Core.Services.MessageService, string)> _trackedForCleanup = new();

    public override async ValueTask DisposeAsync()
    {
        foreach (var (nostr, msg, dbPath) in _trackedForCleanup)
        {
            try { msg.Dispose(); } catch { }
            try { await nostr.DisconnectAsync(); } catch { }
            try { (nostr as IDisposable)?.Dispose(); } catch { }
        }
        await base.DisposeAsync();
        // Delete our extra DB paths after base disposal (which does the pool clear).
        foreach (var (_, _, dbPath) in _trackedForCleanup)
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}
