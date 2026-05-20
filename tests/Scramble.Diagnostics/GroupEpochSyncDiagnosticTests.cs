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
/// Diagnostic tests for MLS epoch synchronization in 3+ user groups.
/// Tests every pairwise decrypt direction to find exactly where desync occurs.
///
/// Scenario: Alice creates group, adds Bob, then adds Charlie.
/// After both joins, all three send messages.
/// We verify each member can decrypt each other member's messages.
///
/// Runs with both managed (C#) and rust (MDK) backends to compare behavior.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "EpochSync")]
public class GroupEpochSyncDiagnosticTests : IAsyncLifetime
{
    private static string RelayUrl => TestRelayConfig.RelayUrl;

    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();
    private readonly List<NostrService> _nostrServices = new();
    private readonly List<MessageService> _messageServices = new();

    public GroupEpochSyncDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public ValueTask InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        return ValueTask.CompletedTask;
    }

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

    private record UserCtx(
        string Name, string Backend,
        string PubKeyHex, string PrivKeyHex,
        NostrService NostrService,
        StorageService Storage,
        IMlsService MlsService,
        MessageService MessageService,
        string DbPath);

    private async Task<UserCtx> CreateUser(string name, string backend = "managed")
    {
        var nostrService = new NostrService();
        _nostrServices.Add(nostrService);
        var keys = nostrService.GenerateKeyPair();
        var dbPath = Path.Combine(Path.GetTempPath(), $"oc_epoch_{name}_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);

        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();
        await storage.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = keys.publicKeyHex,
            PrivateKeyHex = keys.privateKeyHex,
            Npub = keys.npub,
            Nsec = keys.nsec,
            DisplayName = name,
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        IMlsService mlsService = backend switch
        {
            "managed" => new ManagedMlsService(storage),
            "rust" => new MlsService(storage),
            _ => throw new ArgumentException($"Unknown backend '{backend}'")
        };

        var messageService = new MessageService(storage, nostrService, mlsService);
        _messageServices.Add(messageService);
        await messageService.InitializeAsync();

        _output.WriteLine($"[{backend}] Created '{name}': {keys.publicKeyHex[..16]}...");
        return new UserCtx(name, backend, keys.publicKeyHex, keys.privateKeyHex,
            nostrService, storage, mlsService, messageService, dbPath);
    }

    /// <summary>
    /// Core 3-user epoch sync test. Parameterized by backend so we can run both.
    /// </summary>
    private async Task Run3UserEpochSyncTest(string aliceBackend, string bobBackend, string charlieBackend)
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine($"  3-user epoch sync: Alice({aliceBackend}) Bob({bobBackend}) Charlie({charlieBackend})");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var alice = await CreateUser("Alice", aliceBackend);
        var bob = await CreateUser("Bob", bobBackend);
        var charlie = await CreateUser("Charlie", charlieBackend);

        // ── Step 1: Alice creates group ──
        _output.WriteLine("\n[Step 1] Alice creates group");
        var groupInfo = await alice.MlsService.CreateGroupAsync("Epoch Test", new[] { "wss://relay.test" });
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        var nostrGroupId = alice.MlsService.GetNostrGroupId(groupInfo.GroupId);
        _output.WriteLine($"  Group: {groupIdHex[..16]}..., epoch={groupInfo.Epoch}");

        // ── Step 2: Bob publishes KeyPackage ──
        _output.WriteLine("\n[Step 2] Bob publishes KeyPackage");
        var kpBob = WrapKeyPackageWithEventJson(await bob.MlsService.GenerateKeyPackageAsync(), bob.PubKeyHex);

        // ── Step 3: Alice adds Bob → commit (epoch 0→1), welcome for Bob ──
        _output.WriteLine("\n[Step 3] Alice adds Bob");
        var welcomeBob = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, kpBob);
        var aliceInfoAfterBob = await alice.MlsService.GetGroupInfoAsync(groupInfo.GroupId);
        _output.WriteLine($"  Alice epoch after adding Bob: {aliceInfoAfterBob?.Epoch}");
        _output.WriteLine($"  CommitData: {welcomeBob.CommitData?.Length ?? 0} bytes");
        _output.WriteLine($"  WelcomeData: {welcomeBob.WelcomeData.Length} bytes");

        // ── Step 4: Bob processes Welcome ──
        _output.WriteLine("\n[Step 4] Bob processes Welcome");
        var bobGroupInfo = await bob.MlsService.ProcessWelcomeAsync(welcomeBob.WelcomeData, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        _output.WriteLine($"  Bob joined: groupId={Convert.ToHexString(bobGroupInfo.GroupId).ToLowerInvariant()[..16]}..., epoch={bobGroupInfo.Epoch}");

        // ── Step 5: Charlie publishes KeyPackage ──
        _output.WriteLine("\n[Step 5] Charlie publishes KeyPackage");
        var kpCharlie = WrapKeyPackageWithEventJson(await charlie.MlsService.GenerateKeyPackageAsync(), charlie.PubKeyHex);

        // ── Step 6: Alice adds Charlie → commit (epoch 1→2), welcome for Charlie ──
        _output.WriteLine("\n[Step 6] Alice adds Charlie");
        var welcomeCharlie = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, kpCharlie);
        var aliceInfoAfterCharlie = await alice.MlsService.GetGroupInfoAsync(groupInfo.GroupId);
        _output.WriteLine($"  Alice epoch after adding Charlie: {aliceInfoAfterCharlie?.Epoch}");
        _output.WriteLine($"  CommitData: {welcomeCharlie.CommitData?.Length ?? 0} bytes");

        // ── Step 7: Bob MUST process the commit that adds Charlie ──
        _output.WriteLine("\n[Step 7] Bob processes commit (adding Charlie)");
        if (welcomeCharlie.CommitData is { Length: > 0 })
        {
            try
            {
                var commitResult = await bob.MlsService.DecryptMessageAsync(bobGroupInfo.GroupId, welcomeCharlie.CommitData);
                _output.WriteLine($"  Bob commit result: IsCommit={commitResult.IsCommit}");
                var bobInfoAfterCommit = await bob.MlsService.GetGroupInfoAsync(bobGroupInfo.GroupId);
                _output.WriteLine($"  Bob epoch after commit: {bobInfoAfterCommit?.Epoch}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  BOB COMMIT FAILED: {ex.GetType().Name}: {ex.Message}");
                _output.WriteLine("  *** This is likely the root cause of interop failures ***");
            }
        }
        else
        {
            _output.WriteLine("  WARNING: No commit data from AddMember — Bob won't advance epoch");
        }

        // ── Step 8: Charlie processes Welcome ──
        _output.WriteLine("\n[Step 8] Charlie processes Welcome");
        var charlieGroupInfo = await charlie.MlsService.ProcessWelcomeAsync(welcomeCharlie.WelcomeData, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        _output.WriteLine($"  Charlie joined: epoch={charlieGroupInfo.Epoch}");

        // ── Epoch check ──
        var aliceEpoch = (await alice.MlsService.GetGroupInfoAsync(groupInfo.GroupId))?.Epoch;
        var bobEpoch = (await bob.MlsService.GetGroupInfoAsync(bobGroupInfo.GroupId))?.Epoch;
        var charlieEpoch = (await charlie.MlsService.GetGroupInfoAsync(charlieGroupInfo.GroupId))?.Epoch;
        _output.WriteLine($"\n  EPOCH CHECK: Alice={aliceEpoch}, Bob={bobEpoch}, Charlie={charlieEpoch}");
        if (aliceEpoch != bobEpoch || bobEpoch != charlieEpoch)
            _output.WriteLine("  *** EPOCH MISMATCH — messages will fail across epoch boundaries ***");

        // ── Step 9: Each user sends a message, verify all pairwise decryptions ──
        _output.WriteLine("\n[Step 9] Round-trip messaging");

        // Alice sends
        var aliceMsg = await alice.MlsService.EncryptMessageAsync(groupInfo.GroupId, "Hello from Alice");
        var aliceMsgBytes = ExtractMip03Bytes(aliceMsg);
        _output.WriteLine($"  Alice encrypted: {aliceMsgBytes.Length} bytes");

        // Bob decrypts Alice
        await TryDecrypt("Bob", bob.MlsService, bobGroupInfo.GroupId, aliceMsgBytes, "Alice's message");
        // Charlie decrypts Alice
        await TryDecrypt("Charlie", charlie.MlsService, charlieGroupInfo.GroupId, aliceMsgBytes, "Alice's message");

        // Bob sends
        var bobMsg = await bob.MlsService.EncryptMessageAsync(bobGroupInfo.GroupId, "Hello from Bob");
        var bobMsgBytes = ExtractMip03Bytes(bobMsg);
        _output.WriteLine($"  Bob encrypted: {bobMsgBytes.Length} bytes");

        // Alice decrypts Bob
        await TryDecrypt("Alice", alice.MlsService, groupInfo.GroupId, bobMsgBytes, "Bob's message");
        // Charlie decrypts Bob
        await TryDecrypt("Charlie", charlie.MlsService, charlieGroupInfo.GroupId, bobMsgBytes, "Bob's message");

        // Charlie sends
        var charlieMsg = await charlie.MlsService.EncryptMessageAsync(charlieGroupInfo.GroupId, "Hello from Charlie");
        var charlieMsgBytes = ExtractMip03Bytes(charlieMsg);
        _output.WriteLine($"  Charlie encrypted: {charlieMsgBytes.Length} bytes");

        // Alice decrypts Charlie
        await TryDecrypt("Alice", alice.MlsService, groupInfo.GroupId, charlieMsgBytes, "Charlie's message");
        // Bob decrypts Charlie
        await TryDecrypt("Bob", bob.MlsService, bobGroupInfo.GroupId, charlieMsgBytes, "Charlie's message");

        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  Epoch sync test COMPLETE");
        _output.WriteLine("═══════════════════════════════════════════════════════════");
    }

    private async Task TryDecrypt(string who, IMlsService mlsService, byte[] groupId, byte[] ciphertext, string label)
    {
        try
        {
            var result = await mlsService.DecryptMessageAsync(groupId, ciphertext);
            if (result.IsCommit)
                _output.WriteLine($"  {who} → {label}: COMMIT (epoch transition)");
            else
                _output.WriteLine($"  {who} → {label}: OK \"{result.Plaintext}\"");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  {who} → {label}: FAIL {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Build a synthetic kind-30443 Nostr event JSON for a KeyPackage so AddMemberAsync can parse it.
    /// In production this comes from the relay; in tests we build it locally.
    /// </summary>
    private static KeyPackage WrapKeyPackageWithEventJson(KeyPackage kp, string ownerPubKeyHex)
    {
        var content = Convert.ToBase64String(kp.Data);
        var tags = kp.NostrTags ?? new List<List<string>>();
        // Ensure relays tag has at least one URL (Rust MDK validates this)
        var relaysTag = tags.FirstOrDefault(t => t.Count >= 1 && t[0] == "relays");
        if (relaysTag != null && relaysTag.Count <= 1)
            relaysTag.Add(RelayUrl);
        var tagsJson = JsonSerializer.Serialize(tags);

        // Generate valid 64-char hex strings for id and sig (Rust MDK validates format)
        var fakeId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var fakeSig = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)).ToLowerInvariant();

        // Build a minimal but valid kind-30443 event JSON
        var eventJson = $$"""
        {
            "id": "{{fakeId}}",
            "pubkey": "{{ownerPubKeyHex}}",
            "created_at": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},
            "kind": 30443,
            "tags": {{tagsJson}},
            "content": "{{content}}",
            "sig": "{{fakeSig}}"
        }
        """;

        kp.EventJson = eventJson;
        kp.NostrEventId = fakeId;
        return kp;
    }

    private byte[] ExtractMip03Bytes(byte[] encryptedEventJson)
    {
        // The encrypted event is a JSON Nostr event with base64 content
        try
        {
            using var doc = JsonDocument.Parse(encryptedEventJson);
            var content = doc.RootElement.GetProperty("content").GetString()!;
            return Convert.FromBase64String(content);
        }
        catch
        {
            // Already raw bytes
            return encryptedEventJson;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test variants: all managed, all rust, mixed
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EpochSync_3Users_AllManaged()
    {
        await Run3UserEpochSyncTest("managed", "managed", "managed");
    }

    [Fact]
    public async Task EpochSync_3Users_AllRust()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "scramble_native.dll")),
            "Rust native DLL not found — skip rust backend tests");
        await Run3UserEpochSyncTest("rust", "rust", "rust");
    }

    [Fact]
    public async Task EpochSync_3Users_MixedManagedRust()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "scramble_native.dll")),
            "Rust native DLL not found — skip rust backend tests");
        // Alice=managed, Bob=rust, Charlie=managed — simulates OC managed ↔ OC rust interop
        await Run3UserEpochSyncTest("managed", "rust", "managed");
    }

    [Fact]
    public async Task EpochSync_3Users_RustCreatorManagedMembers()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "scramble_native.dll")),
            "Rust native DLL not found — skip rust backend tests");
        await Run3UserEpochSyncTest("rust", "managed", "managed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4-user variant
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EpochSync_4Users_AllManaged()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  4-user epoch sync: all managed");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var alice = await CreateUser("Alice", "managed");
        var bob = await CreateUser("Bob", "managed");
        var charlie = await CreateUser("Charlie", "managed");
        var dana = await CreateUser("Dana", "managed");

        // Alice creates group
        var groupInfo = await alice.MlsService.CreateGroupAsync("Epoch Test 4", new[] { "wss://relay.test" });
        _output.WriteLine($"Group created: epoch={groupInfo.Epoch}");

        // Add Bob
        var kpBob = WrapKeyPackageWithEventJson(await bob.MlsService.GenerateKeyPackageAsync(), bob.PubKeyHex);
        var welcomeBob = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, kpBob);
        var bobGroupInfo = await bob.MlsService.ProcessWelcomeAsync(welcomeBob.WelcomeData, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        _output.WriteLine($"Bob joined: epoch={bobGroupInfo.Epoch}");

        // Add Charlie — Bob must process commit
        var kpCharlie = WrapKeyPackageWithEventJson(await charlie.MlsService.GenerateKeyPackageAsync(), charlie.PubKeyHex);
        var welcomeCharlie = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, kpCharlie);
        if (welcomeCharlie.CommitData is { Length: > 0 })
            await TryDecrypt("Bob", bob.MlsService, bobGroupInfo.GroupId, welcomeCharlie.CommitData, "commit(+Charlie)");
        var charlieGroupInfo = await charlie.MlsService.ProcessWelcomeAsync(welcomeCharlie.WelcomeData, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        _output.WriteLine($"Charlie joined: epoch={charlieGroupInfo.Epoch}");

        // Add Dana — Alice, Bob, Charlie must all process commit
        var kpDana = WrapKeyPackageWithEventJson(await dana.MlsService.GenerateKeyPackageAsync(), dana.PubKeyHex);
        var welcomeDana = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, kpDana);
        if (welcomeDana.CommitData is { Length: > 0 })
        {
            await TryDecrypt("Bob", bob.MlsService, bobGroupInfo.GroupId, welcomeDana.CommitData, "commit(+Dana)");
            await TryDecrypt("Charlie", charlie.MlsService, charlieGroupInfo.GroupId, welcomeDana.CommitData, "commit(+Dana)");
        }
        var danaGroupInfo = await dana.MlsService.ProcessWelcomeAsync(welcomeDana.WelcomeData, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        _output.WriteLine($"Dana joined: epoch={danaGroupInfo.Epoch}");

        // Epoch check
        var epochs = new[]
        {
            ("Alice", (await alice.MlsService.GetGroupInfoAsync(groupInfo.GroupId))?.Epoch),
            ("Bob", (await bob.MlsService.GetGroupInfoAsync(bobGroupInfo.GroupId))?.Epoch),
            ("Charlie", (await charlie.MlsService.GetGroupInfoAsync(charlieGroupInfo.GroupId))?.Epoch),
            ("Dana", (await dana.MlsService.GetGroupInfoAsync(danaGroupInfo.GroupId))?.Epoch),
        };
        _output.WriteLine($"\nEPOCH CHECK: {string.Join(", ", epochs.Select(e => $"{e.Item1}={e.Item2}"))}");

        // All send + all decrypt
        _output.WriteLine("\n--- Round-trip messaging ---");
        var users = new[] { (alice, groupInfo.GroupId), (bob, bobGroupInfo.GroupId), (charlie, charlieGroupInfo.GroupId), (dana, danaGroupInfo.GroupId) };

        foreach (var (sender, senderGroupId) in users)
        {
            var msg = await sender.MlsService.EncryptMessageAsync(senderGroupId, $"Hello from {sender.Name}");
            var msgBytes = ExtractMip03Bytes(msg);

            foreach (var (receiver, receiverGroupId) in users)
            {
                if (receiver.Name == sender.Name) continue;
                await TryDecrypt(receiver.Name, receiver.MlsService, receiverGroupId, msgBytes, $"{sender.Name}'s message");
            }
        }

        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
    }
}
