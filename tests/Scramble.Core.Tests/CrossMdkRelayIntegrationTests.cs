using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scramble.Core.Marmot;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;
using Scramble.Core.Configuration;
using Scramble.Core.Tests.TestHelpers;
namespace Scramble.Core.Tests;

/// <summary>
/// Cross-MDK integration tests proving that the Rust (native) and Managed (C#) MLS
/// implementations can interoperate through a real Nostr relay.
///
/// Requires: docker compose -f docker-compose.test.yml up -d  (relay on wss://relay2.angor.io)
/// </summary>
[Trait("Category", "Relay")]
public class CrossMdkRelayIntegrationTests : IAsyncLifetime
{
    private const string RelayUrl = "wss://relay2.angor.io";

    private readonly ITestOutputHelper _output;

    // User A — Rust/native MLS backend (sender/inviter)
    private NostrService _nostrServiceA = null!;
    private StorageService _storageA = null!;
    private MlsService _mlsServiceA = null!;
    private MessageService _messageServiceA = null!;
    private string _pubKeyA = null!;
    private string _privKeyA = null!;
    private string _dbPathA = null!;

    // User B — Managed/C# MLS backend (receiver/invitee)
    private NostrService _nostrServiceB = null!;
    private StorageService _storageB = null!;
    private ManagedMlsService _managedMlsServiceB = null!;
    private MessageService _messageServiceB = null!;
    private string _pubKeyB = null!;
    private string _privKeyB = null!;
    private string _dbPathB = null!;

    public CrossMdkRelayIntegrationTests(ITestOutputHelper output)
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        // ── User A (Rust MLS) ──
        _nostrServiceA = new NostrService();
        var keysA = _nostrServiceA.GenerateKeyPair();
        _privKeyA = keysA.privateKeyHex;
        _pubKeyA = keysA.publicKeyHex;

        _dbPathA = Path.Combine(Path.GetTempPath(), $"scramble_crossmdk_A_{Guid.NewGuid()}.db");
        _storageA = new StorageService(_dbPathA, new MockSecureStorage());
        await _storageA.InitializeAsync();
        await _storageA.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = _pubKeyA,
            PrivateKeyHex = _privKeyA,
            Npub = "npub1userA",
            Nsec = "nsec1userA",
            DisplayName = "User A (Rust)",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        _mlsServiceA = new MlsService();
        _messageServiceA = new MessageService(_storageA, _nostrServiceA, _mlsServiceA);
        await _messageServiceA.InitializeAsync();

        // ── User B (Managed/C# MLS) ──
        _nostrServiceB = new NostrService();
        var keysB = _nostrServiceB.GenerateKeyPair();
        _privKeyB = keysB.privateKeyHex;
        _pubKeyB = keysB.publicKeyHex;

        _dbPathB = Path.Combine(Path.GetTempPath(), $"scramble_crossmdk_B_{Guid.NewGuid()}.db");
        _storageB = new StorageService(_dbPathB, new MockSecureStorage());
        await _storageB.InitializeAsync();
        await _storageB.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = _pubKeyB,
            PrivateKeyHex = _privKeyB,
            Npub = "npub1userB",
            Nsec = "nsec1userB",
            DisplayName = "User B (Managed)",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        _managedMlsServiceB = new ManagedMlsService(_storageB);
        _messageServiceB = new MessageService(_storageB, _nostrServiceB, _managedMlsServiceB);
        await _messageServiceB.InitializeAsync();

        // Connect both to the relay
        await _nostrServiceA.ConnectAsync(RelayUrl);
        await _nostrServiceB.ConnectAsync(RelayUrl);

        // Publish NIP-65 relay lists so FetchRelayListAsync finds them on localhost
        // instead of timing out trying to reach external discovery relays
        var relayPrefs = new List<RelayPreference>
        {
            new() { Url = RelayUrl, Usage = RelayUsage.Both }
        };
        await _nostrServiceA.PublishRelayListAsync(relayPrefs, _privKeyA);
        await _nostrServiceB.PublishRelayListAsync(relayPrefs, _privKeyB);

        await Task.Delay(1000);
    }

    public async ValueTask DisposeAsync()
    {
        _messageServiceA?.Dispose();
        _messageServiceB?.Dispose();

        await _nostrServiceA.DisconnectAsync();
        await _nostrServiceB.DisconnectAsync();
        (_nostrServiceA as IDisposable)?.Dispose();
        (_nostrServiceB as IDisposable)?.Dispose();

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        TryDeleteFile(_dbPathA);
        TryDeleteFile(_dbPathB);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 1: Random bytes rejected by Managed MLS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Test that accepting an invite with random (non-MLS) welcome data throws
    /// an exception from the Managed/C# MLS backend.
    /// Mirrors the Rust-side test in FullStackRelayIntegrationTests.
    /// </summary>
    [Fact]
    public async Task AcceptInvite_RandomBytes_ManagedMls_SkipsAndBumpsCounter()
    {
        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // User B stores a real KeyPackage; the managed CanProcessWelcome gate will still
        // reject random bytes because PreviewWelcome fails for every stored KeyPackage.
        var keyPackage = await _managedMlsServiceB.GenerateKeyPackageAsync();
        _output.WriteLine($"User B generated KeyPackage ({keyPackage.Data.Length} bytes)");

        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        await Task.Delay(500);

        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _messageServiceB.NewInvites
            .Take(1)
            .Subscribe(invite => inviteTcs.TrySetResult(invite));

        var randomData = new byte[256];
        RandomNumberGenerator.Fill(randomData);
        await _nostrServiceA.PublishWelcomeAsync(randomData, _pubKeyB, _privKeyA, "fake-kp-event-id".PadLeft(64, '0'));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() => inviteTcs.TrySetCanceled());

        // The gate should silently dismiss the welcome and increment the skipped counter
        // instead of raising a PendingInvite.
        await Assert.ThrowsAsync<TaskCanceledException>(() => inviteTcs.Task);

        var skipped = await _storageB.GetSkippedInviteCountAsync();
        Assert.True(skipped >= 1, "skipped-invite counter should have incremented");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 2: Cross-MDK interop — Rust creates group, Managed accepts
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// End-to-end cross-MDK interoperability test:
    /// - User A (Rust/native MLS) creates a group and adds User B
    /// - User B (Managed/C# MLS) receives the Welcome through the real relay
    /// - User B processes the Welcome and joins the group
    /// - Both users can encrypt/decrypt messages across MDK implementations
    ///
    /// </summary>
    [Fact]
    public async Task CrossMdk_RustCreatesGroup_ManagedAcceptsWelcome_ThroughRelay()
    {
        _output.WriteLine($"User A (Rust) pubkey:    {_pubKeyA}");
        _output.WriteLine($"User B (Managed) pubkey: {_pubKeyB}");

        // ── Phase 1: User B (Managed) generates and publishes KeyPackage ──
        var keyPackageB = await _managedMlsServiceB.GenerateKeyPackageAsync();
        Assert.True(keyPackageB.Data.Length >= 64, $"KeyPackage should be >= 64 bytes, got {keyPackageB.Data.Length}");
        _output.WriteLine($"User B generated KeyPackage: {keyPackageB.Data.Length} bytes, {keyPackageB.NostrTags?.Count} tags");

        // Publish KeyPackage to relay
        var kpEventId = await _nostrServiceB.PublishKeyPackageAsync(
            keyPackageB.Data, _privKeyB, keyPackageB.NostrTags);
        Assert.Equal(64, kpEventId.Length);
        _output.WriteLine($"User B published KeyPackage to relay: {kpEventId}");

        await Task.Delay(1000); // Let relay store it

        // ── Phase 2: User A (Rust) creates a group ──
        var groupInfo = await _mlsServiceA.CreateGroupAsync("Cross-MDK Test Group", new[] { "wss://relay.test" });
        Assert.NotNull(groupInfo.GroupId);
        _output.WriteLine($"User A created group: {Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant()[..16]}...");

        // Save chat record for User A
        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Cross-MDK Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _pubKeyA },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageA.SaveChatAsync(chatA);

        // ── Phase 3: User A fetches User B's KeyPackage from relay ──
        var fetchedKPs = (await _nostrServiceA.FetchKeyPackagesAsync(_pubKeyB)).ToList();
        Assert.NotEmpty(fetchedKPs);
        var fetchedKP = fetchedKPs[0];
        Assert.NotNull(fetchedKP.EventJson);
        _output.WriteLine($"User A fetched User B's KeyPackage from relay: {fetchedKP.NostrEventId}");
        _output.WriteLine($"EventJson tags: {System.Text.Json.JsonDocument.Parse(fetchedKP.EventJson).RootElement.GetProperty("tags").GetRawText()}");

        // ── Phase 4: User A adds User B to the group (Rust MLS) ──
        var welcome = await _mlsServiceA.AddMemberAsync(groupInfo.GroupId, fetchedKP);
        Assert.NotNull(welcome.WelcomeData);
        Assert.True(welcome.WelcomeData.Length > 0);
        _output.WriteLine($"User A added User B: welcome={welcome.WelcomeData.Length} bytes, commit={welcome.CommitData?.Length} bytes");

        // ── Phase 5: User B subscribes to Welcomes and User A publishes Welcome ──
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        await Task.Delay(500);

        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _messageServiceB.NewInvites
            .Take(1)
            .Subscribe(invite => inviteTcs.TrySetResult(invite));

        var welcomeEventId = await _nostrServiceA.PublishWelcomeAsync(
            welcome.WelcomeData, _pubKeyB, _privKeyA, kpEventId);
        Assert.Equal(64, welcomeEventId.Length);
        _output.WriteLine($"User A published Welcome to relay: {welcomeEventId}");

        // ── Phase 6: User B receives Welcome via relay → PendingInvite ──
        using var inviteCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        inviteCts.Token.Register(() => inviteTcs.TrySetCanceled());

        var pendingInvite = await inviteTcs.Task;
        Assert.NotNull(pendingInvite);
        Assert.Equal(_pubKeyA, pendingInvite.SenderPublicKey);
        // NostrEventId is the rumor's own id, not the gift wrap event id
        Assert.NotEmpty(pendingInvite.NostrEventId);
        Assert.Equal(64, pendingInvite.NostrEventId.Length);
        _output.WriteLine($"User B received invite: {pendingInvite.Id}, sender={pendingInvite.SenderPublicKey[..16]}...");

        // ── Diagnostic: trace Welcome data through extraction ──
        _output.WriteLine($"WelcomeData raw ({pendingInvite.WelcomeData.Length} bytes): {Convert.ToHexString(pendingInvite.WelcomeData[..Math.Min(32, pendingInvite.WelcomeData.Length)])}...");
        _output.WriteLine($"WelcomeData first byte: 0x{pendingInvite.WelcomeData[0]:X2} (JSON='{{':0x7B, MLSMsg:0x00)");

        // ── Phase 7: User B accepts invite (Managed MLS processes Rust Welcome) ──
        var chatB = await _messageServiceB.AcceptInviteAsync(pendingInvite.Id);
        Assert.NotNull(chatB);
        Assert.Equal(ChatType.Group, chatB.Type);
        Assert.NotNull(chatB.MlsGroupId);
        _output.WriteLine($"User B accepted invite! Chat: {chatB.Id}, group: {Convert.ToHexString(chatB.MlsGroupId!).ToLowerInvariant()[..16]}...");

        // Verify invite was cleaned up
        var remaining = (await _storageB.GetPendingInvitesAsync()).ToList();
        Assert.DoesNotContain(remaining, i => i.Id == pendingInvite.Id);

        // ── Phase 8: Cross-MDK message exchange ──
        // User A (Rust) encrypts → User B (Managed) decrypts
        var ciphertextA = await _mlsServiceA.EncryptMessageAsync(groupInfo.GroupId, "Hello from Rust!");
        Assert.NotNull(ciphertextA);
        Assert.True(ciphertextA.Length > 0);
        _output.WriteLine($"User A encrypted: {ciphertextA.Length} bytes");

        var decryptedByB = await _managedMlsServiceB.DecryptMessageAsync(chatB.MlsGroupId!, ciphertextA);
        Assert.Equal("Hello from Rust!", decryptedByB.Plaintext);
        _output.WriteLine($"User B decrypted: \"{decryptedByB.Plaintext}\"");

        // User B (Managed) encrypts → User A (Rust) decrypts
        var ciphertextB = await _managedMlsServiceB.EncryptMessageAsync(chatB.MlsGroupId!, "Hello from Managed!");
        Assert.NotNull(ciphertextB);
        Assert.True(ciphertextB.Length > 0);
        _output.WriteLine($"User B encrypted: {ciphertextB.Length} bytes");

        var decryptedByA = await _mlsServiceA.DecryptMessageAsync(groupInfo.GroupId, ciphertextB);
        Assert.Equal("Hello from Managed!", decryptedByA.Plaintext);
        _output.WriteLine($"User A decrypted: \"{decryptedByA.Plaintext}\"");

        _output.WriteLine("CROSS-MDK TEST PASSED: Rust ↔ Managed bidirectional message exchange");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Temp file - will be cleaned up eventually
        }
    }
}
