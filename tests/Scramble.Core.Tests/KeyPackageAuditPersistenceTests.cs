using Microsoft.Data.Sqlite;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Core.Tests.TestHelpers;
using Xunit;
namespace Scramble.Core.Tests;

/// <summary>
/// Reproduces a bug where KeyPackage private key material is lost after publish:
///
///   User publishes a KeyPackage from mobile → audit shows the KP on relays
///   but reports "0 local keys" (LOST) — even for the KP just published.
///
/// Root cause investigation: the audit uses HasKeyMaterialForKeyPackage()
/// which compares relay-fetched KP bytes against in-memory _storedKeyPackages.
/// If the bytes don't match (e.g. after base64 round-trip) or the in-memory
/// list was cleared (e.g. by a redundant ImportServiceStateAsync call in
/// MainViewModel), the KP appears as "lost".
/// </summary>
public class KeyPackageAuditPersistenceTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private string _dbPath = null!;
    private string _privKeyHex = null!;
    private string _pubKeyHex = null!;

    public KeyPackageAuditPersistenceTests(ITestOutputHelper output) => _output = output;

    public ValueTask InitializeAsync()
    {
        var nostr = new NostrService();
        (_privKeyHex, _pubKeyHex, _, _) = nostr.GenerateKeyPair();
        _dbPath = Path.Combine(Path.GetTempPath(), $"scramble_kp_audit_{Guid.NewGuid()}.db");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (_dbPath != null) try { File.Delete(_dbPath); } catch { }
        return ValueTask.CompletedTask;
    }

    private async Task<(StorageService storage, ManagedMlsService mls)> CreateAndInitialize(string? dbPath = null)
    {
        var path = dbPath ?? _dbPath;
        var storage = new StorageService(path, new MockSecureStorage());
        await storage.InitializeAsync();
        var mls = new ManagedMlsService(storage);
        await mls.InitializeAsync(_privKeyHex, _pubKeyHex);
        return (storage, mls);
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 1: Basic in-memory — generate KP, immediately check
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateKeyPackage_HasKeyMaterial_ReturnsTrue()
    {
        var (storage, mls) = await CreateAndInitialize();

        var kp = await mls.GenerateKeyPackageAsync();

        _output.WriteLine($"Generated KP: {kp.Data.Length} bytes");
        _output.WriteLine($"Stored KP count: {mls.GetStoredKeyPackageCount()}");

        Assert.True(mls.GetStoredKeyPackageCount() > 0,
            "After GenerateKeyPackageAsync, stored KP count should be > 0");
        Assert.True(mls.HasKeyMaterialForKeyPackage(kp.Data),
            "After GenerateKeyPackageAsync, HasKeyMaterialForKeyPackage should return true " +
            "for the KP data that was just generated");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 2: Base64 round-trip (simulates Nostr relay publish/fetch)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateKeyPackage_Base64RoundTrip_HasKeyMaterial()
    {
        var (storage, mls) = await CreateAndInitialize();

        var kp = await mls.GenerateKeyPackageAsync();
        var originalBytes = kp.Data;

        // Simulate Nostr relay: publish encodes as base64, fetch decodes
        var base64 = Convert.ToBase64String(originalBytes);
        var roundTrippedBytes = Convert.FromBase64String(base64);

        _output.WriteLine($"Original bytes length: {originalBytes.Length}");
        _output.WriteLine($"Round-tripped bytes length: {roundTrippedBytes.Length}");
        _output.WriteLine($"Bytes equal: {originalBytes.AsSpan().SequenceEqual(roundTrippedBytes)}");

        Assert.True(mls.HasKeyMaterialForKeyPackage(roundTrippedBytes),
            "After base64 round-trip (simulating Nostr relay), " +
            "HasKeyMaterialForKeyPackage should still return true");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 3: Persistence — generate, then restart with same DB
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateKeyPackage_RestartService_HasKeyMaterial()
    {
        byte[] kpData;

        // Session 1: generate a KeyPackage
        {
            var (storage1, mls1) = await CreateAndInitialize();
            var kp = await mls1.GenerateKeyPackageAsync();
            kpData = kp.Data;
            _output.WriteLine($"Session 1: Generated KP ({kpData.Length} bytes), " +
                $"stored count = {mls1.GetStoredKeyPackageCount()}");
            Assert.True(mls1.HasKeyMaterialForKeyPackage(kpData));
        }

        // Session 2: create new service with same DB (simulates app restart)
        {
            var (storage2, mls2) = await CreateAndInitialize();
            _output.WriteLine($"Session 2: Stored KP count after restart = {mls2.GetStoredKeyPackageCount()}");

            Assert.True(mls2.GetStoredKeyPackageCount() > 0,
                "After restart, stored KP count should be > 0 (restored from persistence)");
            Assert.True(mls2.HasKeyMaterialForKeyPackage(kpData),
                "After restart, HasKeyMaterialForKeyPackage should return true " +
                "for a KP generated in the previous session");
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 4: Multiple KPs — all should be recognized
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateMultipleKeyPackages_AllHaveKeyMaterial()
    {
        var (storage, mls) = await CreateAndInitialize();

        var kp1 = await mls.GenerateKeyPackageAsync();
        var kp2 = await mls.GenerateKeyPackageAsync();
        var kp3 = await mls.GenerateKeyPackageAsync();

        _output.WriteLine($"Stored KP count: {mls.GetStoredKeyPackageCount()}");

        Assert.Equal(3, mls.GetStoredKeyPackageCount());
        Assert.True(mls.HasKeyMaterialForKeyPackage(kp1.Data), "KP 1 should have local key material");
        Assert.True(mls.HasKeyMaterialForKeyPackage(kp2.Data), "KP 2 should have local key material");
        Assert.True(mls.HasKeyMaterialForKeyPackage(kp3.Data), "KP 3 should have local key material");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 5: Double ImportServiceStateAsync (reproduces MainViewModel behavior)
    //
    //  MainViewModel.InitializeAfterLoginAsync calls:
    //    1. _mlsService.InitializeAsync(...)    — which internally restores state
    //    2. _mlsService.ImportServiceStateAsync(...) — redundant second import
    //
    //  ImportServiceStateAsync starts with _storedKeyPackages.Clear(),
    //  so if KPs were generated BETWEEN the two calls, they would be lost.
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DoubleImport_DoesNotLoseKeyPackages()
    {
        var (storage, mls) = await CreateAndInitialize();

        // Generate a KeyPackage
        var kp = await mls.GenerateKeyPackageAsync();
        _output.WriteLine($"Before double import: stored count = {mls.GetStoredKeyPackageCount()}");
        Assert.True(mls.HasKeyMaterialForKeyPackage(kp.Data));

        // Simulate MainViewModel's redundant ImportServiceStateAsync call
        // This reads persisted state (which should include the KP) and re-imports
        var serviceState = await storage.GetMlsStateAsync("__service__");
        Assert.NotNull(serviceState);
        _output.WriteLine($"Persisted state: {serviceState!.Length} bytes");

        await mls.ImportServiceStateAsync(serviceState);

        _output.WriteLine($"After double import: stored count = {mls.GetStoredKeyPackageCount()}");
        Assert.True(mls.GetStoredKeyPackageCount() > 0,
            "After redundant ImportServiceStateAsync, stored KP count should still be > 0");
        Assert.True(mls.HasKeyMaterialForKeyPackage(kp.Data),
            "After redundant ImportServiceStateAsync, KP should still be recognized");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 6: Full mobile flow simulation
    //
    //  Reproduces the exact mobile startup + publish + audit flow:
    //  1. InitializeAsync with placeholder private key ("0000...")
    //  2. Second ImportServiceStateAsync (MainViewModel behavior)
    //  3. InitializeAsync again (from SettingsViewModel — should be no-op)
    //  4. GenerateKeyPackageAsync (publish)
    //  5. HasKeyMaterialForKeyPackage (audit)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task MobileFlow_PublishThenAudit_HasKeyMaterial()
    {
        var storage = new StorageService(_dbPath, new MockSecureStorage());
        await storage.InitializeAsync();
        var mls = new ManagedMlsService(storage);

        // Step 1: InitializeAsync with placeholder private key (Amber flow)
        var placeholderPrivKey = new string('0', 64);
        await mls.InitializeAsync(placeholderPrivKey, _pubKeyHex);
        _output.WriteLine($"After init: stored count = {mls.GetStoredKeyPackageCount()}");

        // Step 2: MainViewModel's redundant ImportServiceStateAsync
        var serviceState = await storage.GetMlsStateAsync("__service__");
        if (serviceState != null)
        {
            await mls.ImportServiceStateAsync(serviceState);
            _output.WriteLine($"After double import: stored count = {mls.GetStoredKeyPackageCount()}");
        }

        // Step 3: SettingsViewModel calls InitializeAsync again (should be no-op due to guard)
        await mls.InitializeAsync(placeholderPrivKey, _pubKeyHex);

        // Step 4: User publishes KeyPackage
        var kp = await mls.GenerateKeyPackageAsync();
        _output.WriteLine($"After publish: stored count = {mls.GetStoredKeyPackageCount()}");

        // Step 5: User audits — simulate base64 round-trip through relay
        var relayKpData = Convert.FromBase64String(Convert.ToBase64String(kp.Data));

        Assert.True(mls.GetStoredKeyPackageCount() > 0,
            "Mobile flow: after publish, stored KP count should be > 0");
        Assert.True(mls.HasKeyMaterialForKeyPackage(relayKpData),
            "Mobile flow: after publish, audit should recognize the just-published KP");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 7: Full mobile flow with app restart
    //
    //  Same as Test 6 but adds an app restart between publish and audit.
    //  This tests whether SaveServiceStateAsync correctly persists
    //  the KP private keys to SQLite.
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task MobileFlow_PublishRestartAudit_HasKeyMaterial()
    {
        byte[] kpData;

        // Session 1: Initialize and publish
        {
            var storage1 = new StorageService(_dbPath, new MockSecureStorage());
            await storage1.InitializeAsync();
            var mls1 = new ManagedMlsService(storage1);
            await mls1.InitializeAsync(new string('0', 64), _pubKeyHex);
            var kp = await mls1.GenerateKeyPackageAsync();
            kpData = kp.Data;
            _output.WriteLine($"Session 1: published KP ({kpData.Length} bytes), " +
                $"stored count = {mls1.GetStoredKeyPackageCount()}");
            Assert.True(mls1.HasKeyMaterialForKeyPackage(kpData));
        }

        // Session 2: Restart, double import, then audit
        {
            var storage2 = new StorageService(_dbPath, new MockSecureStorage());
            await storage2.InitializeAsync();
            var mls2 = new ManagedMlsService(storage2);

            // InitializeAsync restores state internally
            await mls2.InitializeAsync(new string('0', 64), _pubKeyHex);
            _output.WriteLine($"Session 2 after init: stored count = {mls2.GetStoredKeyPackageCount()}");

            // MainViewModel's redundant ImportServiceStateAsync
            var state = await storage2.GetMlsStateAsync("__service__");
            if (state != null)
            {
                await mls2.ImportServiceStateAsync(state);
                _output.WriteLine($"Session 2 after double import: stored count = {mls2.GetStoredKeyPackageCount()}");
            }

            // Audit: check the KP from session 1 (base64 round-tripped)
            var relayKpData = Convert.FromBase64String(Convert.ToBase64String(kpData));

            Assert.True(mls2.GetStoredKeyPackageCount() > 0,
                "After restart + double import, stored KP count should be > 0");
            Assert.True(mls2.HasKeyMaterialForKeyPackage(relayKpData),
                "After restart + double import, KP from previous session should be recognized");
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 8: Export/Import state preserves KeyPackage material
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportImportState_PreservesKeyPackageMaterial()
    {
        var (storage, mls) = await CreateAndInitialize();
        var kp = await mls.GenerateKeyPackageAsync();

        // Export the service state
        var stateBytes = await mls.ExportServiceStateAsync();
        Assert.NotNull(stateBytes);
        _output.WriteLine($"Exported state: {stateBytes!.Length} bytes");

        // Create a fresh service and import the state
        var dbPath2 = Path.Combine(Path.GetTempPath(), $"scramble_kp_audit2_{Guid.NewGuid()}.db");
        try
        {
            var storage2 = new StorageService(dbPath2, new MockSecureStorage());
            await storage2.InitializeAsync();
            var mls2 = new ManagedMlsService(storage2);
            await mls2.InitializeAsync(_privKeyHex, _pubKeyHex);

            // Import the exported state (this should restore the KP)
            await mls2.ImportServiceStateAsync(stateBytes);
            _output.WriteLine($"After import: stored count = {mls2.GetStoredKeyPackageCount()}");

            Assert.True(mls2.GetStoredKeyPackageCount() > 0,
                "After ImportServiceStateAsync, stored KP count should be > 0");
            Assert.True(mls2.HasKeyMaterialForKeyPackage(kp.Data),
                "After ImportServiceStateAsync, KP should be recognized");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath2); } catch { }
        }
    }
}
