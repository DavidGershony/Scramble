using System.Text;
using System.Text.Json;
using DotnetMls.Codec;
using Microsoft.Data.Sqlite;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Core.Tests.TestHelpers;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// Tests for MIP-00 §"Deletion Timing" — 24 h last-resort init_key retention.
///
/// Production changes covered:
///   - StoredKeyPackageMaterial.ConsumedAt — timestamp when a last-resort KP was consumed by Welcome
///   - ExportServiceStateAsync writes v5 (includes ConsumedAt per KP)
///   - ImportServiceStateAsync reads v5 (restores ConsumedAt; migrates v4 → v5 with ConsumedAt = null)
///   - PruneExpiredConsumedKeyPackages — removes KPs with ConsumedAt > 24 h after ImportServiceStateAsync
///   - ProcessWelcomeAsync — sets ConsumedAt instead of zeroizing for last-resort KPs
/// </summary>
public class Mip00RetentionTests : IAsyncLifetime
{
    // v4 = old format without ConsumedAt; v5 = current format with ConsumedAt
    private const byte ServiceStateVersion4 = 4;
    private const byte ServiceStateVersion5 = 5;

    private readonly ITestOutputHelper _output;
    private string _dbPath = null!;
    private string _privKeyHex = null!;
    private string _pubKeyHex = null!;

    public Mip00RetentionTests(ITestOutputHelper output) => _output = output;

    public ValueTask InitializeAsync()
    {
        var nostr = new NostrService();
        (_privKeyHex, _pubKeyHex, _, _) = nostr.GenerateKeyPair();
        _dbPath = Path.Combine(Path.GetTempPath(), $"scramble_mip00_{Guid.NewGuid()}.db");
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
    //  Helper: build a minimal kind-30443 event JSON (same as E2E tests)
    // ──────────────────────────────────────────────────────────────

    private static string CreateFakeKeyPackageEventJson(string ownerPubKey, byte[] kpData, List<List<string>>? tags = null)
    {
        var contentBase64 = Convert.ToBase64String(kpData);
        var tagsArray = tags?.Select(t => t.ToArray()).ToArray()
            ?? new[]
            {
                new[] { "encoding", "base64" },
                new[] { "mls_protocol_version", "1.0" },
                new[] { "mls_ciphersuite", "0x0001" }
            };
        var eventObj = new
        {
            id = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            pubkey = ownerPubKey,
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            kind = 30443,
            tags = tagsArray,
            content = contentBase64,
            sig = new string('a', 128)
        };
        return JsonSerializer.Serialize(eventObj);
    }

    // ──────────────────────────────────────────────────────────────
    //  Helper: encode/decode big-endian int64 (mirrors production)
    // ──────────────────────────────────────────────────────────────

    private static byte[] EncodeBigEndianInt64(long value) =>
        new byte[]
        {
            (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32),
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        };

    private static long DecodeBigEndianInt64(byte[] bytes) =>
        (long)bytes[0] << 56 | (long)bytes[1] << 48 | (long)bytes[2] << 40 | (long)bytes[3] << 32
        | (long)bytes[4] << 24 | (long)bytes[5] << 16 | (long)bytes[6] << 8 | (long)bytes[7];

    // ──────────────────────────────────────────────────────────────
    //  Test 1: Fresh KP export/import roundtrip — ConsumedAt is null,
    //  KP survives import (not pruned)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportImport_V5_FreshKP_NotPruned()
    {
        var (storage, mls) = await CreateAndInitialize();

        var kp = await mls.GenerateKeyPackageAsync();
        var countBefore = mls.GetStoredKeyPackageCount();
        Assert.True(countBefore > 0, "Should have at least 1 stored KP after generate");

        // Export v5 state
        var stateBytes = await mls.ExportServiceStateAsync();
        Assert.NotNull(stateBytes);

        // Parse v5 header to verify version byte
        Assert.Equal(ServiceStateVersion5, stateBytes![0]);

        _output.WriteLine($"Exported v5 state: {stateBytes.Length} bytes, {countBefore} KPs");

        // Import into fresh instance
        var dbPath2 = Path.Combine(Path.GetTempPath(), $"scramble_mip00_2_{Guid.NewGuid()}.db");
        try
        {
            var (storage2, mls2) = await CreateAndInitialize(dbPath2);
            await mls2.ImportServiceStateAsync(stateBytes);

            // Fresh KPs have ConsumedAt = null → PruneExpiredConsumedKeyPackages is a no-op
            var countAfter = mls2.GetStoredKeyPackageCount();
            _output.WriteLine($"After import: {countAfter} KPs");
            Assert.Equal(countBefore, countAfter);
            Assert.True(mls2.HasKeyMaterialForKeyPackage(kp.Data),
                "KP material should survive v5 export/import roundtrip");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath2); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 2: ProcessWelcome on a last-resort KP sets ConsumedAt
    //  instead of zeroizing — the KP is retained (count unchanged)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessWelcome_LastResortKP_RetainsKeyMaterial()
    {
        // User A: group creator
        var nostrA = new NostrService();
        var (privA, pubA, _, _) = nostrA.GenerateKeyPair();
        var dbPathA = Path.Combine(Path.GetTempPath(), $"scramble_mip00_A_{Guid.NewGuid()}.db");
        var storageA = new StorageService(dbPathA, new MockSecureStorage());
        await storageA.InitializeAsync();
        var mlsA = new ManagedMlsService(storageA);
        await mlsA.InitializeAsync(privA, pubA);

        // User B: joiner (will receive Welcome)
        var dbPathB = Path.Combine(Path.GetTempPath(), $"scramble_mip00_B_{Guid.NewGuid()}.db");
        var storageB = new StorageService(dbPathB, new MockSecureStorage());
        await storageB.InitializeAsync();
        var mlsB = new ManagedMlsService(storageB);
        await mlsB.InitializeAsync(_privKeyHex, _pubKeyHex);

        try
        {
            // B generates KP (last-resort by default in this implementation)
            var kpB = await mlsB.GenerateKeyPackageAsync();
            var kpCountBefore = mlsB.GetStoredKeyPackageCount();
            _output.WriteLine($"Before Welcome: B has {kpCountBefore} stored KPs");

            // A creates group
            var groupInfo = await mlsA.CreateGroupAsync("Retention Test", new[] { "wss://relay.test" });

            // A adds B using B's KP
            kpB.EventJson = CreateFakeKeyPackageEventJson(_pubKeyHex, kpB.Data, kpB.NostrTags);
            kpB.NostrEventId = "fake_kp_" + Guid.NewGuid().ToString("N");
            var welcome = await mlsA.AddMemberAsync(groupInfo.GroupId, kpB);

            // B processes Welcome → should mark KP consumed, NOT remove it
            var joinedGroup = await mlsB.ProcessWelcomeAsync(welcome.WelcomeData, "fake_welcome_event");

            var kpCountAfter = mlsB.GetStoredKeyPackageCount();
            _output.WriteLine($"After Welcome: B has {kpCountAfter} stored KPs");

            // Key assertion: last-resort KP is retained (ConsumedAt set, not zeroized)
            Assert.Equal(kpCountBefore, kpCountAfter);
            Assert.NotNull(joinedGroup);
            Assert.Equal(groupInfo.GroupName, joinedGroup.GroupName);

            // Export and verify the ConsumedAt was serialized (v5 format)
            var exported = await mlsB.ExportServiceStateAsync();
            Assert.NotNull(exported);
            Assert.Equal(ServiceStateVersion5, exported![0]);

            // Parse v5: skip identity + signingPriv + signingPub + slotId, read KP count
            var reader = new TlsReader(exported);
            reader.ReadUint8(); // version
            reader.ReadOpaqueV(); // identity
            reader.ReadOpaqueV(); // signingPriv
            reader.ReadOpaqueV(); // signingPub
            reader.ReadOpaqueV(); // slotId
            var kpCount = reader.ReadUint16();
            Assert.True(kpCount >= 1, $"Expected at least 1 KP in v5 export, got {kpCount}");

            // Read first KP's ConsumedAt
            reader.ReadOpaqueV(); // kpBytes
            reader.ReadOpaqueV(); // initPriv
            reader.ReadOpaqueV(); // hpkePriv
            var consumedAtBytes = reader.ReadOpaqueV();

            _output.WriteLine($"ConsumedAt bytes length: {consumedAtBytes.Length}");
            Assert.Equal(8, consumedAtBytes.Length); // Should be 8-byte BE int64
            var consumedAtUnix = DecodeBigEndianInt64(consumedAtBytes);
            var consumedAt = DateTimeOffset.FromUnixTimeSeconds(consumedAtUnix);
            _output.WriteLine($"ConsumedAt: {consumedAt:O}");

            // Should be very recent (within last 30 seconds)
            var elapsed = DateTimeOffset.UtcNow - consumedAt;
            Assert.True(elapsed.TotalSeconds < 30,
                $"ConsumedAt should be very recent, but was {elapsed.TotalSeconds:F1}s ago");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { File.Delete(dbPathA); } catch { }
            try { File.Delete(dbPathB); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 3: Import v5 state with ConsumedAt > 24h ago → KP is pruned
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportV5_ConsumedAtOlderThan24h_PrunesKP()
    {
        var (storage, mls) = await CreateAndInitialize();

        // Generate a KP so we have valid state to work with
        var kp = await mls.GenerateKeyPackageAsync();
        var stateBytes = await mls.ExportServiceStateAsync();
        Assert.NotNull(stateBytes);
        Assert.Equal(1, mls.GetStoredKeyPackageCount());

        // Now craft a modified v5 blob where ConsumedAt is 25 hours ago
        var oldConsumedAt = DateTimeOffset.UtcNow.AddHours(-25);
        var modifiedState = ReplaceConsumedAtInV5State(stateBytes!, oldConsumedAt);

        _output.WriteLine($"Original state: {stateBytes!.Length} bytes");
        _output.WriteLine($"Modified state: {modifiedState.Length} bytes");
        _output.WriteLine($"ConsumedAt set to: {oldConsumedAt:O}");

        // Import into a fresh instance — PruneExpiredConsumedKeyPackages should remove the KP
        var dbPath2 = Path.Combine(Path.GetTempPath(), $"scramble_mip00_prune_{Guid.NewGuid()}.db");
        try
        {
            var (storage2, mls2) = await CreateAndInitialize(dbPath2);
            await mls2.ImportServiceStateAsync(modifiedState);

            var count = mls2.GetStoredKeyPackageCount();
            _output.WriteLine($"After import with expired ConsumedAt: {count} KPs");
            Assert.Equal(0, count); // The expired KP should have been pruned
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath2); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 4: Import v5 state with ConsumedAt < 24h ago → KP is retained
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportV5_ConsumedAtWithin24h_RetainsKP()
    {
        var (storage, mls) = await CreateAndInitialize();

        var kp = await mls.GenerateKeyPackageAsync();
        var stateBytes = await mls.ExportServiceStateAsync();
        Assert.NotNull(stateBytes);

        // Craft a v5 blob where ConsumedAt is 23 hours ago (within the 24h window)
        var recentConsumedAt = DateTimeOffset.UtcNow.AddHours(-23);
        var modifiedState = ReplaceConsumedAtInV5State(stateBytes!, recentConsumedAt);

        var dbPath2 = Path.Combine(Path.GetTempPath(), $"scramble_mip00_retain_{Guid.NewGuid()}.db");
        try
        {
            var (storage2, mls2) = await CreateAndInitialize(dbPath2);
            await mls2.ImportServiceStateAsync(modifiedState);

            var count = mls2.GetStoredKeyPackageCount();
            _output.WriteLine($"After import with recent ConsumedAt (23h): {count} KPs");
            Assert.Equal(1, count); // Within 24h window, should NOT be pruned
            Assert.True(mls2.HasKeyMaterialForKeyPackage(kp.Data),
                "KP within 24h grace window should be retained");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath2); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 5: Import v4 state → migrated to v5 with ConsumedAt = null
    //  (KPs not pruned, re-exported as v5)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportV4_MigratesToV5_ConsumedAtNull_NotPruned()
    {
        var (storage, mls) = await CreateAndInitialize();

        // Generate a KP to get valid key material
        var kp = await mls.GenerateKeyPackageAsync();
        var v5State = await mls.ExportServiceStateAsync();
        Assert.NotNull(v5State);

        // Downgrade the v5 state to v4 format (remove ConsumedAt from each KP)
        var v4State = ConvertV5ToV4(v5State!);
        Assert.Equal(ServiceStateVersion4, v4State[0]);
        _output.WriteLine($"v5 state: {v5State!.Length} bytes → v4 state: {v4State.Length} bytes");

        // Import the v4 state — should migrate to v5 internally
        var dbPath2 = Path.Combine(Path.GetTempPath(), $"scramble_mip00_v4mig_{Guid.NewGuid()}.db");
        try
        {
            var (storage2, mls2) = await CreateAndInitialize(dbPath2);
            await mls2.ImportServiceStateAsync(v4State);

            // KPs should be preserved (ConsumedAt = null → not pruned)
            var count = mls2.GetStoredKeyPackageCount();
            _output.WriteLine($"After v4 import: {count} KPs");
            Assert.Equal(1, count);
            Assert.True(mls2.HasKeyMaterialForKeyPackage(kp.Data),
                "KP should survive v4 → v5 migration");

            // Re-export should produce v5
            var reExported = await mls2.ExportServiceStateAsync();
            Assert.NotNull(reExported);
            Assert.Equal(ServiceStateVersion5, reExported![0]);

            // Verify ConsumedAt is empty (null) in the re-exported v5 blob
            var reader = new TlsReader(reExported);
            reader.ReadUint8(); // version
            reader.ReadOpaqueV(); // identity
            reader.ReadOpaqueV(); // signingPriv
            reader.ReadOpaqueV(); // signingPub
            reader.ReadOpaqueV(); // slotId
            var kpCount = reader.ReadUint16();
            Assert.Equal(1, (int)kpCount);

            reader.ReadOpaqueV(); // kpBytes
            reader.ReadOpaqueV(); // initPriv
            reader.ReadOpaqueV(); // hpkePriv
            var consumedAtBytes = reader.ReadOpaqueV();

            Assert.Empty(consumedAtBytes); // null ConsumedAt → empty bytes in v5
            _output.WriteLine("v4→v5 migration: ConsumedAt correctly set to null (empty bytes)");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath2); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Helper: replace ConsumedAt in a v5 state blob
    //
    //  Re-serializes the entire v5 blob but with the specified
    //  ConsumedAt for every KP entry.
    // ──────────────────────────────────────────────────────────────

    private static byte[] ReplaceConsumedAtInV5State(byte[] original, DateTimeOffset consumedAt)
    {
        var reader = new TlsReader(original);
        var version = reader.ReadUint8();
        if (version != ServiceStateVersion5)
            throw new ArgumentException($"Expected v5 state, got v{version}");

        var identity = reader.ReadOpaqueV();
        var signingPriv = reader.ReadOpaqueV();
        var signingPub = reader.ReadOpaqueV();
        var slotId = reader.ReadOpaqueV();
        var count = reader.ReadUint16();

        // Read all KPs
        var kps = new List<(byte[] kpBytes, byte[] initPriv, byte[] hpkePriv)>();
        for (int i = 0; i < count; i++)
        {
            var kpBytes = reader.ReadOpaqueV();
            var initPriv = reader.ReadOpaqueV();
            var hpkePriv = reader.ReadOpaqueV();
            reader.ReadOpaqueV(); // skip original consumedAt
            kps.Add((kpBytes, initPriv, hpkePriv));
        }

        // Re-serialize with modified ConsumedAt
        var consumedAtBytes = EncodeBigEndianInt64(consumedAt.ToUnixTimeSeconds());

        return TlsCodec.Serialize(writer =>
        {
            writer.WriteUint8(ServiceStateVersion5);
            writer.WriteOpaqueV(identity);
            writer.WriteOpaqueV(signingPriv);
            writer.WriteOpaqueV(signingPub);
            writer.WriteOpaqueV(slotId);
            writer.WriteUint16((ushort)kps.Count);
            foreach (var (kpBytes, initPriv, hpkePriv) in kps)
            {
                writer.WriteOpaqueV(kpBytes);
                writer.WriteOpaqueV(initPriv);
                writer.WriteOpaqueV(hpkePriv);
                writer.WriteOpaqueV(consumedAtBytes);
            }
        });
    }

    // ──────────────────────────────────────────────────────────────
    //  Helper: downgrade a v5 state blob to v4 (strip ConsumedAt)
    // ──────────────────────────────────────────────────────────────

    private static byte[] ConvertV5ToV4(byte[] v5State)
    {
        var reader = new TlsReader(v5State);
        var version = reader.ReadUint8();
        if (version != ServiceStateVersion5)
            throw new ArgumentException($"Expected v5 state, got v{version}");

        var identity = reader.ReadOpaqueV();
        var signingPriv = reader.ReadOpaqueV();
        var signingPub = reader.ReadOpaqueV();
        var slotId = reader.ReadOpaqueV();
        var count = reader.ReadUint16();

        var kps = new List<(byte[] kpBytes, byte[] initPriv, byte[] hpkePriv)>();
        for (int i = 0; i < count; i++)
        {
            var kpBytes = reader.ReadOpaqueV();
            var initPriv = reader.ReadOpaqueV();
            var hpkePriv = reader.ReadOpaqueV();
            reader.ReadOpaqueV(); // skip ConsumedAt (v5 only)
            kps.Add((kpBytes, initPriv, hpkePriv));
        }

        // Write v4 format (same as v5 but without ConsumedAt per KP)
        return TlsCodec.Serialize(writer =>
        {
            writer.WriteUint8(ServiceStateVersion4);
            writer.WriteOpaqueV(identity);
            writer.WriteOpaqueV(signingPriv);
            writer.WriteOpaqueV(signingPub);
            writer.WriteOpaqueV(slotId);
            writer.WriteUint16((ushort)kps.Count);
            foreach (var (kpBytes, initPriv, hpkePriv) in kps)
            {
                writer.WriteOpaqueV(kpBytes);
                writer.WriteOpaqueV(initPriv);
                writer.WriteOpaqueV(hpkePriv);
                // No ConsumedAt in v4
            }
        });
    }
}
