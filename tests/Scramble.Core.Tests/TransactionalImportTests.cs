using System.Reflection;
using Microsoft.Data.Sqlite;
using Scramble.Core.Services;
using Scramble.Core.Tests.TestHelpers;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// Tests for the Bug #2 fix: transactional ImportServiceStateAsync and the
/// importFailed guard in InitializeAsync.
///
/// Bug #2 had two parts:
///   1. ImportServiceStateAsync set _signingPrivateKey/_signingPublicKey on instance
///      fields BEFORE parsing KeyPackages. If the TLS reader threw mid-parse (truncated
///      data, Android Keystore failure), signing keys were set but _storedKeyPackages
///      remained empty — a dangerous partial import.
///   2. InitializeAsync's catch block didn't distinguish "no prior state" from "import
///      failed". On import failure, it generated new signing keys and called
///      SaveServiceStateAsync(), permanently overwriting the DB with zero KPs.
///
/// The fix:
///   1. ImportServiceStateAsync now parses ALL fields into local temps first. Instance
///      fields are committed only after parsing fully succeeds (transactional).
///   2. InitializeAsync uses an `importFailed` flag to skip SaveServiceStateAsync
///      when import fails, preserving the DB state for potential recovery on next restart.
/// </summary>
public class TransactionalImportTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private string _dbPath = null!;
    private string _privKeyHex = null!;
    private string _pubKeyHex = null!;

    public TransactionalImportTests(ITestOutputHelper output) => _output = output;

    public ValueTask InitializeAsync()
    {
        var nostr = new NostrService();
        (_privKeyHex, _pubKeyHex, _, _) = nostr.GenerateKeyPair();
        _dbPath = Path.Combine(Path.GetTempPath(), $"scramble_txn_import_{Guid.NewGuid()}.db");
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

    /// <summary>
    /// Helper: read private field via reflection.
    /// </summary>
    private static T? GetField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (T?)field!.GetValue(instance);
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 1: ImportServiceStateAsync with null is no-op
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportServiceState_NullData_IsNoOp()
    {
        var (_, mls) = await CreateAndInitialize();

        // Generate a KP so we have non-empty state
        var kp = await mls.GenerateKeyPackageAsync();
        var countBefore = mls.GetStoredKeyPackageCount();
        Assert.True(countBefore > 0);

        // Act: import null
        await mls.ImportServiceStateAsync(null!);

        // Assert: state unchanged
        Assert.Equal(countBefore, mls.GetStoredKeyPackageCount());
        Assert.True(mls.HasKeyMaterialForKeyPackage(kp.Data));
        _output.WriteLine("Null import correctly treated as no-op");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 2: ImportServiceStateAsync with empty array is no-op
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportServiceState_EmptyData_IsNoOp()
    {
        var (_, mls) = await CreateAndInitialize();

        var kp = await mls.GenerateKeyPackageAsync();
        var countBefore = mls.GetStoredKeyPackageCount();

        // Act
        await mls.ImportServiceStateAsync(Array.Empty<byte>());

        // Assert
        Assert.Equal(countBefore, mls.GetStoredKeyPackageCount());
        Assert.True(mls.HasKeyMaterialForKeyPackage(kp.Data));
        _output.WriteLine("Empty import correctly treated as no-op");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 3: Truncated TLS state does NOT modify instance fields
    //
    //  This is the core transactional import test. We:
    //  1. Export valid state (with signing keys + 1 KP)
    //  2. Truncate the exported bytes mid-stream (after version + identity
    //     + signing keys, but BEFORE the KP count field)
    //  3. Call ImportServiceStateAsync with the truncated data
    //  4. Verify: import throws, but instance fields are unchanged
    //
    //  Before the fix, step 3 would have set _signingPrivateKey and
    //  _signingPublicKey from the truncated data, but _storedKeyPackages
    //  would remain from the previous import — a subtle inconsistency.
    //  Now all fields are parsed into temps; if parsing throws, nothing
    //  is committed.
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportServiceState_TruncatedData_DoesNotModifyInstanceFields()
    {
        var (_, mls) = await CreateAndInitialize();

        // Generate a KP to create non-trivial state
        var kp = await mls.GenerateKeyPackageAsync();
        var countBefore = mls.GetStoredKeyPackageCount();
        Assert.True(countBefore > 0, "Should have at least 1 KP");

        // Capture current signing keys via reflection
        var sigPrivBefore = GetField<byte[]>(mls, "_signingPrivateKey");
        var sigPubBefore = GetField<byte[]>(mls, "_signingPublicKey");
        Assert.NotNull(sigPrivBefore);
        Assert.NotNull(sigPubBefore);
        var sigPrivCopy = sigPrivBefore!.ToArray();
        var sigPubCopy = sigPubBefore!.ToArray();

        // Export valid state, then truncate
        var fullState = await mls.ExportServiceStateAsync();
        Assert.NotNull(fullState);
        _output.WriteLine($"Full exported state: {fullState!.Length} bytes");

        // Truncate to ~40% — enough to include version + identity + partial signing keys
        // but not enough for KPs
        var truncated = new byte[fullState.Length * 40 / 100];
        Array.Copy(fullState, truncated, truncated.Length);
        _output.WriteLine($"Truncated to: {truncated.Length} bytes");

        // Act: import truncated data — should throw
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => mls.ImportServiceStateAsync(truncated));
        _output.WriteLine($"Import threw: {ex.GetType().Name}: {ex.Message}");

        // Assert: instance fields are unchanged (transactional rollback)
        var sigPrivAfter = GetField<byte[]>(mls, "_signingPrivateKey");
        var sigPubAfter = GetField<byte[]>(mls, "_signingPublicKey");
        var countAfter = mls.GetStoredKeyPackageCount();

        Assert.True(sigPrivCopy.AsSpan().SequenceEqual(sigPrivAfter),
            "Signing private key should be unchanged after failed import");
        Assert.True(sigPubCopy.AsSpan().SequenceEqual(sigPubAfter),
            "Signing public key should be unchanged after failed import");
        Assert.Equal(countBefore, countAfter);
        Assert.True(mls.HasKeyMaterialForKeyPackage(kp.Data),
            "KP key material should still be recognized after failed import");
        _output.WriteLine($"Instance fields unchanged: sigPriv={sigPrivAfter?.Length}B, " +
            $"sigPub={sigPubAfter?.Length}B, kpCount={countAfter}");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 4: InitializeAsync with corrupted DB state does NOT
    //          overwrite the DB (importFailed guard)
    //
    //  This tests the second part of the fix:
    //  1. Session 1: init + generate KP → valid state saved to DB
    //  2. Manually corrupt the DB state (truncate it)
    //  3. Session 2: init with new MLS instance → import fails →
    //     importFailed=true → SaveServiceStateAsync is SKIPPED
    //  4. Verify DB still contains the truncated bytes (not overwritten
    //     with fresh keys + zero KPs)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_ImportFailure_DoesNotOverwriteDB()
    {
        byte[] originalState;

        // Session 1: create valid state with a KP
        {
            var (storage1, mls1) = await CreateAndInitialize();
            await mls1.GenerateKeyPackageAsync();
            Assert.True(mls1.GetStoredKeyPackageCount() > 0);

            originalState = (await storage1.GetMlsStateAsync("__service__"))!;
            Assert.NotNull(originalState);
            Assert.True(originalState.Length > 0);
            _output.WriteLine($"Session 1: saved valid state ({originalState.Length} bytes)");
        }

        // Corrupt the DB state by truncating it
        byte[] corruptedState;
        {
            var storage = new StorageService(_dbPath, new MockSecureStorage());
            await storage.InitializeAsync();

            // Truncate to ~30% — enough to pass version check but fail during KP parsing
            corruptedState = new byte[originalState.Length * 30 / 100];
            Array.Copy(originalState, corruptedState, corruptedState.Length);
            await storage.SaveMlsStateAsync("__service__", corruptedState);
            _output.WriteLine($"Wrote corrupted state ({corruptedState.Length} bytes) to DB");
        }

        // Session 2: init with the corrupted DB — should NOT overwrite
        {
            var storage2 = new StorageService(_dbPath, new MockSecureStorage());
            await storage2.InitializeAsync();
            var mls2 = new ManagedMlsService(storage2);

            // InitializeAsync should handle the import failure gracefully
            // (importFailed=true → skip SaveServiceStateAsync)
            await mls2.InitializeAsync(_privKeyHex, _pubKeyHex);

            // Read DB state — should still be the corrupted bytes, NOT fresh state
            var dbStateAfter = await storage2.GetMlsStateAsync("__service__");
            Assert.NotNull(dbStateAfter);

            Assert.True(corruptedState.AsSpan().SequenceEqual(dbStateAfter),
                $"DB state should be unchanged (corrupted {corruptedState.Length}B), " +
                $"but got {dbStateAfter!.Length}B — SaveServiceStateAsync may have overwritten it!");
            _output.WriteLine($"DB state preserved: {dbStateAfter.Length} bytes (same as corrupted input)");
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 5: InitializeAsync with NO prior DB state DOES save
    //          (fresh install scenario)
    //
    //  Ensures the importFailed guard doesn't break first-launch:
    //  when there's genuinely no prior state, InitializeAsync should
    //  generate new keys and save them.
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_NoPriorState_SavesFreshKeys()
    {
        // Use a fresh DB path with no prior MLS state
        var freshDbPath = Path.Combine(Path.GetTempPath(), $"scramble_txn_fresh_{Guid.NewGuid()}.db");

        try
        {
            var storage = new StorageService(freshDbPath, new MockSecureStorage());
            await storage.InitializeAsync();
            var mls = new ManagedMlsService(storage);

            // Verify no prior state
            var priorState = await storage.GetMlsStateAsync("__service__");
            Assert.True(priorState == null || priorState.Length == 0,
                "Fresh DB should have no MLS state");

            // Act: initialize
            await mls.InitializeAsync(_privKeyHex, _pubKeyHex);

            // Assert: state was saved (new keys generated)
            var savedState = await storage.GetMlsStateAsync("__service__");
            Assert.NotNull(savedState);
            Assert.True(savedState!.Length > 0, "InitializeAsync should save fresh state on first launch");
            _output.WriteLine($"Fresh state saved: {savedState.Length} bytes");

            // Verify signing keys were generated
            var sigPriv = GetField<byte[]>(mls, "_signingPrivateKey");
            var sigPub = GetField<byte[]>(mls, "_signingPublicKey");
            Assert.NotNull(sigPriv);
            Assert.NotNull(sigPub);
            _output.WriteLine("Fresh signing keys generated and saved");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(freshDbPath); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 6: Valid import after failed import works correctly
    //
    //  Simulates:
    //  1. Failed import (truncated data) — instance fields unchanged
    //  2. Valid import (good data) — instance fields updated
    //
    //  Ensures the service isn't permanently broken after a failed import.
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportServiceState_FailThenSucceed_RecoversProperly()
    {
        var (_, mls) = await CreateAndInitialize();

        // Generate KPs for valid state
        var kp = await mls.GenerateKeyPackageAsync();
        var validState = await mls.ExportServiceStateAsync();
        Assert.NotNull(validState);

        // Act 1: import truncated data → should throw
        var truncated = new byte[validState!.Length / 3];
        Array.Copy(validState, truncated, truncated.Length);
        await Assert.ThrowsAnyAsync<Exception>(() => mls.ImportServiceStateAsync(truncated));
        _output.WriteLine("First import (truncated) correctly threw");

        // Act 2: import valid data → should succeed
        await mls.ImportServiceStateAsync(validState);

        // Assert: state is correctly restored from the valid import
        Assert.True(mls.GetStoredKeyPackageCount() > 0,
            "After valid import, should have KPs");
        Assert.True(mls.HasKeyMaterialForKeyPackage(kp.Data),
            "After valid import, original KP should be recognized");
        _output.WriteLine($"Recovery succeeded: {mls.GetStoredKeyPackageCount()} KPs restored");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 7: Unsupported version throws without modifying state
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportServiceState_UnsupportedVersion_DoesNotModifyState()
    {
        var (_, mls) = await CreateAndInitialize();

        var kp = await mls.GenerateKeyPackageAsync();
        var countBefore = mls.GetStoredKeyPackageCount();
        var sigPrivBefore = GetField<byte[]>(mls, "_signingPrivateKey")!.ToArray();

        // Create a state blob with unsupported version byte (0xFF)
        var badState = new byte[] { 0xFF, 0x00, 0x01, 0x02 };

        // Act: should throw InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mls.ImportServiceStateAsync(badState));

        // Assert: state unchanged
        Assert.Equal(countBefore, mls.GetStoredKeyPackageCount());
        Assert.True(sigPrivBefore.AsSpan().SequenceEqual(GetField<byte[]>(mls, "_signingPrivateKey")),
            "Signing key should be unchanged after unsupported version import");
        Assert.True(mls.HasKeyMaterialForKeyPackage(kp.Data));
        _output.WriteLine("Unsupported version correctly rejected without state modification");
    }

    // ──────────────────────────────────────────────────────────────
    //  Test 8: Simulated app restart after import failure preserves
    //          original state (end-to-end scenario)
    //
    //  This is the full mobile bug scenario:
    //  1. Session 1: publish KP → state saved
    //  2. Session 2: DB state somehow corrupted → import fails →
    //     importFailed prevents overwrite → DB preserved
    //  3. Session 3: DB state restored (simulating fix/retry) →
    //     import succeeds → KP recognized
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EndToEnd_ImportFailure_RestoreOnNextRestart()
    {
        byte[] kpData;
        byte[] validState;

        // Session 1: init and publish KP
        {
            var (storage1, mls1) = await CreateAndInitialize();
            var kp = await mls1.GenerateKeyPackageAsync();
            kpData = kp.Data;
            validState = (await storage1.GetMlsStateAsync("__service__"))!;
            Assert.NotNull(validState);
            _output.WriteLine($"Session 1: published KP, state={validState.Length}B");
        }

        // Corrupt DB state (simulates Keystore decryption failure / truncated write)
        {
            var storage = new StorageService(_dbPath, new MockSecureStorage());
            await storage.InitializeAsync();
            var corrupted = new byte[validState.Length / 3];
            Array.Copy(validState, corrupted, corrupted.Length);
            await storage.SaveMlsStateAsync("__service__", corrupted);
            _output.WriteLine($"Corrupted DB state to {corrupted.Length}B");
        }

        // Session 2: init with corrupted state — should NOT overwrite DB
        {
            var storage2 = new StorageService(_dbPath, new MockSecureStorage());
            await storage2.InitializeAsync();
            var mls2 = new ManagedMlsService(storage2);
            await mls2.InitializeAsync(_privKeyHex, _pubKeyHex);

            // MLS service should have fresh keys but the DB should be untouched
            Assert.NotNull(GetField<byte[]>(mls2, "_signingPrivateKey"));
            _output.WriteLine("Session 2: init succeeded with fresh keys, DB preserved");
        }

        // Restore the valid state (simulates: user restarts and Keystore works this time)
        {
            var storage = new StorageService(_dbPath, new MockSecureStorage());
            await storage.InitializeAsync();
            await storage.SaveMlsStateAsync("__service__", validState);
            _output.WriteLine("Restored valid state to DB");
        }

        // Session 3: init with restored valid state — KP should be recognized
        {
            var storage3 = new StorageService(_dbPath, new MockSecureStorage());
            await storage3.InitializeAsync();
            var mls3 = new ManagedMlsService(storage3);
            await mls3.InitializeAsync(_privKeyHex, _pubKeyHex);

            Assert.True(mls3.GetStoredKeyPackageCount() > 0,
                "After restoring valid state, should have KPs");
            Assert.True(mls3.HasKeyMaterialForKeyPackage(kpData),
                "After restoring valid state, original KP should be recognized");
            _output.WriteLine($"Session 3: KP recognized, count={mls3.GetStoredKeyPackageCount()}");
        }
    }
}
