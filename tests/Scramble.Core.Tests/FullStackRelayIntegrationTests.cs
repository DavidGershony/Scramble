using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
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
/// Full-stack integration tests using a real Docker relay (wss://relay2.angor.io),
/// real NostrService, real StorageService, real MlsService, and real MessageService.
/// These tests prove the complete invite delivery pipeline works end-to-end.
///
/// Requires: docker compose -f docker-compose.test.yml up -d
/// </summary>
[Trait("Category", "Relay")]
public class FullStackRelayIntegrationTests : IAsyncLifetime
{
    private const string RelayUrl = "wss://relay2.angor.io";

    private readonly ITestOutputHelper _output;

    // User A (sender/inviter)
    private NostrService _nostrServiceA = null!;
    private StorageService _storageA = null!;
    private IMlsService _mlsServiceA = null!;
    private MessageService _messageServiceA = null!;
    private string _pubKeyA = null!;
    private string _privKeyA = null!;
    private string _dbPathA = null!;

    // User B (receiver/invitee)
    private NostrService _nostrServiceB = null!;
    private StorageService _storageB = null!;
    private IMlsService _mlsServiceB = null!;
    private MessageService _messageServiceB = null!;
    private string _pubKeyB = null!;
    private string _privKeyB = null!;
    private string _dbPathB = null!;

    public FullStackRelayIntegrationTests(ITestOutputHelper output)
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _output = output;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private async Task SetupUsers(string backend)
    {
        // ── User A setup ──
        _nostrServiceA = new NostrService();
        var keysA = _nostrServiceA.GenerateKeyPair();
        _privKeyA = keysA.privateKeyHex;
        _pubKeyA = keysA.publicKeyHex;

        _dbPathA = Path.Combine(Path.GetTempPath(), $"scramble_fullstack_A_{Guid.NewGuid()}.db");
        _storageA = new StorageService(_dbPathA, new MockSecureStorage());
        await _storageA.InitializeAsync();
        await _storageA.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = _pubKeyA,
            PrivateKeyHex = _privKeyA,
            Npub = $"npub1userA",
            Nsec = $"nsec1userA",
            DisplayName = "User A",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        _mlsServiceA = CreateMlsService(backend, _storageA);
        _messageServiceA = new MessageService(_storageA, _nostrServiceA, _mlsServiceA);

        // ── User B setup ──
        _nostrServiceB = new NostrService();
        var keysB = _nostrServiceB.GenerateKeyPair();
        _privKeyB = keysB.privateKeyHex;
        _pubKeyB = keysB.publicKeyHex;

        _dbPathB = Path.Combine(Path.GetTempPath(), $"scramble_fullstack_B_{Guid.NewGuid()}.db");
        _storageB = new StorageService(_dbPathB, new MockSecureStorage());
        await _storageB.InitializeAsync();
        await _storageB.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = _pubKeyB,
            PrivateKeyHex = _privKeyB,
            Npub = $"npub1userB",
            Nsec = $"nsec1userB",
            DisplayName = "User B",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        _mlsServiceB = CreateMlsService(backend, _storageB);
        _messageServiceB = new MessageService(_storageB, _nostrServiceB, _mlsServiceB);

        // Initialize MessageService for both users (subscribes to Events, inits MLS)
        await _messageServiceA.InitializeAsync();
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

        await Task.Delay(1000); // Let connections stabilize
    }

    private static IMlsService CreateMlsService(string backend, IStorageService storage) => backend switch
    {
        "managed" => new ManagedMlsService(storage),
        "rust" => new MlsService(storage),
        _ => throw new ArgumentException($"Unknown backend '{backend}'. Use 'rust' or 'managed'.")
    };

    public async ValueTask DisposeAsync()
    {
        _messageServiceA?.Dispose();
        _messageServiceB?.Dispose();

        if (_nostrServiceA != null) await _nostrServiceA.DisconnectAsync();
        if (_nostrServiceB != null) await _nostrServiceB.DisconnectAsync();
        (_nostrServiceA as IDisposable)?.Dispose();
        (_nostrServiceB as IDisposable)?.Dispose();

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (_dbPathA != null) TryDeleteFile(_dbPathA);
        if (_dbPathB != null) TryDeleteFile(_dbPathB);
    }

    /// <summary>
    /// The critical test: User A publishes a Welcome (kind 444) to the real relay,
    /// User B has subscribed to welcomes, and MessageService should create a PendingInvite.
    /// This tests the FULL pipeline: publish → relay → WebSocket → Events observable →
    /// MessageService.OnNostrEventReceived → HandleWelcomeEventAsync → PendingInvite.
    /// </summary>
    [Theory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task FullStack_WelcomePublished_ArriveAsPendingInvite(string backend)
    {
        await SetupUsers(backend);

        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // Step 1: B generates a real KeyPackage and publishes it so A can invite B for real.
        // (Required after the CanProcessWelcome gate — random bytes no longer surface as invites.)
        var keyPackageB = await _mlsServiceB.GenerateKeyPackageAsync();
        var kpEventId = await _nostrServiceB.PublishKeyPackageAsync(
            keyPackageB.Data, _privKeyB, keyPackageB.NostrTags);
        _output.WriteLine($"User B published KeyPackage: {kpEventId}");
        await Task.Delay(500);

        // Step 2: User B subscribes to welcomes via the real relay
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        _output.WriteLine("User B subscribed to welcomes");
        await Task.Delay(500);

        // Step 3: Set up observer for PendingInvite from MessageService
        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _messageServiceB.NewInvites
            .Take(1)
            .Subscribe(invite => inviteTcs.TrySetResult(invite));

        // Step 4: User A creates a group and adds B via B's real KeyPackage, producing a real welcome
        var groupInfoA = await _mlsServiceA.CreateGroupAsync("Arrive Test Group", new[] { RelayUrl });
        var fetchedKP = (await _nostrServiceA.FetchKeyPackagesAsync(_pubKeyB)).First();
        var add = await _mlsServiceA.AddMemberAsync(groupInfoA.GroupId, fetchedKP);
        _output.WriteLine($"User A produced real welcome: {add.WelcomeData.Length} bytes");

        var eventId = await _nostrServiceA.PublishWelcomeAsync(add.WelcomeData, _pubKeyB, _privKeyA, fetchedKP.NostrEventId ?? "unknown");
        Assert.Equal(64, eventId.Length);
        _output.WriteLine($"Welcome published with eventId: {eventId}");

        // Step 5: Verify the PendingInvite was created by MessageService
        using var inviteCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        inviteCts.Token.Register(() => inviteTcs.TrySetCanceled());

        var invite = await inviteTcs.Task;
        _output.WriteLine($"PendingInvite confirmed: sender={invite.SenderPublicKey[..16]}..., welcomeDataLen={invite.WelcomeData.Length}");

        Assert.Equal(_pubKeyA, invite.SenderPublicKey);
        Assert.NotEmpty(invite.NostrEventId);
        Assert.Equal(64, invite.NostrEventId.Length);
        Assert.Equal(add.WelcomeData, invite.WelcomeData);

        // Step 6: Verify invite was persisted to storage
        var storedInvites = (await _storageB.GetPendingInvitesAsync()).ToList();
        Assert.Contains(storedInvites, i => !string.IsNullOrEmpty(i.NostrEventId) && i.NostrEventId.Length == 64);
    }

    /// <summary>
    /// Test that RescanInvitesAsync (used by the Rescan button in UI) can find
    /// Welcome events that were published BEFORE the subscription was set up.
    /// </summary>
    [Theory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task FullStack_RescanInvites_FindsHistoricalWelcomes(string backend)
    {
        await SetupUsers(backend);

        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // B needs a stored KeyPackage so CanProcessWelcome can succeed during rescan.
        var keyPackageB = await _mlsServiceB.GenerateKeyPackageAsync();
        await _nostrServiceB.PublishKeyPackageAsync(keyPackageB.Data, _privKeyB, keyPackageB.NostrTags);
        await Task.Delay(500);

        // Step 1: A creates a group and publishes a real welcome for B BEFORE B subscribes.
        var groupInfoA = await _mlsServiceA.CreateGroupAsync("Rescan Test Group", new[] { RelayUrl });
        var fetchedKP = (await _nostrServiceA.FetchKeyPackagesAsync(_pubKeyB)).First();
        var add = await _mlsServiceA.AddMemberAsync(groupInfoA.GroupId, fetchedKP);

        var kpEventIdRescan = fetchedKP.NostrEventId ?? "unknown";
        var eventId = await _nostrServiceA.PublishWelcomeAsync(add.WelcomeData, _pubKeyB, _privKeyA, kpEventIdRescan);
        _output.WriteLine($"Welcome published with eventId: {eventId}");

        await Task.Delay(1000); // Let relay store it

        // Step 2: User B rescans for invites (fetches historical events)
        await _messageServiceB.RescanInvitesAsync();

        // Step 3: Verify the invite was found and stored
        var storedInvites = (await _storageB.GetPendingInvitesAsync()).ToList();
        Assert.NotEmpty(storedInvites);
        Assert.Contains(storedInvites, i => !string.IsNullOrEmpty(i.NostrEventId) && i.NostrEventId.Length == 64);
    }

    /// <summary>
    /// Test that accepting an invite with random (non-MLS) welcome data throws
    /// "invalid welcome message" from the Rust/native MLS backend.
    /// The relay transport works fine — it's the MLS parsing that correctly rejects garbage.
    /// </summary>
    [Theory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task FullStack_AcceptInvite_RandomBytes_ThrowsInvalidWelcome(string backend)
    {
        await SetupUsers(backend);

        // Under managed, the CanProcessWelcome gate in MessageService filters random welcomes
        // before they become PendingInvites (and bumps the skipped-invite counter). Only the
        // rust backend's pass-through gate lets the garbage through to AcceptInvite.
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        await Task.Delay(500);

        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _messageServiceB.NewInvites
            .Take(1)
            .Subscribe(invite => inviteTcs.TrySetResult(invite));

        var welcomeData = new byte[256];
        RandomNumberGenerator.Fill(welcomeData);
        await _nostrServiceA.PublishWelcomeAsync(welcomeData, _pubKeyB, _privKeyA, "fake-kp-event-id".PadLeft(64, '0'));

        if (backend == "managed")
        {
            using var skipCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            skipCts.Token.Register(() => inviteTcs.TrySetCanceled());
            await Assert.ThrowsAsync<TaskCanceledException>(() => inviteTcs.Task);
            var skipped = await _storageB.GetSkippedInviteCountAsync();
            Assert.True(skipped >= 1, "skipped-invite counter should have incremented");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => inviteTcs.TrySetCanceled());
        var invite = await inviteTcs.Task;
        await Assert.ThrowsAnyAsync<Exception>(() => _messageServiceB.AcceptInviteAsync(invite.Id));
    }

    /// <summary>
    /// Full end-to-end test: User A creates a real MLS group, adds User B using
    /// a real KeyPackage, publishes the Welcome through the relay, User B receives
    /// and accepts the invite, creating a real group chat.
    /// Both users use the Rust/native MLS backend.
    /// </summary>
    [Theory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task FullStack_AcceptInvite_RealMlsData_CreatesGroupChat(string backend)
    {
        await SetupUsers(backend);

        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // Step 1: User B generates a real KeyPackage and publishes it to the relay
        var keyPackageB = await _mlsServiceB.GenerateKeyPackageAsync();
        Assert.True(keyPackageB.Data.Length >= 64);
        _output.WriteLine($"User B generated KeyPackage: {keyPackageB.Data.Length} bytes");

        var kpEventId = await _nostrServiceB.PublishKeyPackageAsync(
            keyPackageB.Data, _privKeyB, keyPackageB.NostrTags);
        Assert.Equal(64, kpEventId.Length);
        _output.WriteLine($"User B published KeyPackage: {kpEventId}");

        await Task.Delay(1000); // Let relay store it

        // Step 2: User A creates a group
        var groupInfo = await _mlsServiceA.CreateGroupAsync("Full Stack Test Group", new[] { "wss://relay.test" });
        Assert.NotNull(groupInfo.GroupId);
        _output.WriteLine($"User A created group: {Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant()[..16]}...");

        // Save chat record for User A
        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Full Stack Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _pubKeyA },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageA.SaveChatAsync(chatA);

        // Step 3: User A fetches User B's KeyPackage from the relay
        var fetchedKPs = (await _nostrServiceA.FetchKeyPackagesAsync(_pubKeyB)).ToList();
        Assert.NotEmpty(fetchedKPs);
        _output.WriteLine($"User A fetched KeyPackage from relay: {fetchedKPs[0].NostrEventId}");

        // Step 4: User A adds User B to the group (real MLS operation)
        var welcome = await _mlsServiceA.AddMemberAsync(groupInfo.GroupId, fetchedKPs[0]);
        Assert.NotNull(welcome.WelcomeData);
        Assert.True(welcome.WelcomeData.Length > 0);
        _output.WriteLine($"User A added User B: welcome={welcome.WelcomeData.Length} bytes");

        // Step 5: User B subscribes to welcomes
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        await Task.Delay(500);

        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _messageServiceB.NewInvites
            .Take(1)
            .Subscribe(invite => inviteTcs.TrySetResult(invite));

        // Step 6: User A publishes the real Welcome through the relay
        var welcomeEventId = await _nostrServiceA.PublishWelcomeAsync(
            welcome.WelcomeData, _pubKeyB, _privKeyA, kpEventId);
        Assert.Equal(64, welcomeEventId.Length);
        _output.WriteLine($"Welcome published: {welcomeEventId}");

        // Step 7: Wait for invite
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => inviteTcs.TrySetCanceled());
        var invite = await inviteTcs.Task;
        _output.WriteLine($"Invite received: {invite.Id}");

        // Step 8: Accept the invite (real MLS ProcessWelcome)
        var chatB = await _messageServiceB.AcceptInviteAsync(invite.Id);
        _output.WriteLine($"Chat created: id={chatB.Id}, name={chatB.Name}, type={chatB.Type}");

        Assert.NotNull(chatB);
        Assert.Equal(ChatType.Group, chatB.Type);
        Assert.NotNull(chatB.MlsGroupId);

        // Step 9: Verify invite was cleaned up
        var remaining = (await _storageB.GetPendingInvitesAsync()).ToList();
        Assert.DoesNotContain(remaining, i => i.Id == invite.Id);

        // Step 10: Verify chat was persisted
        var savedChat = await _storageB.GetChatAsync(chatB.Id);
        Assert.NotNull(savedChat);
        Assert.Equal(ChatType.Group, savedChat!.Type);

        // Step 11: Verify bidirectional message exchange
        var ciphertextA = await _mlsServiceA.EncryptMessageAsync(groupInfo.GroupId, "Hello from A!");
        var decryptedByB = await _mlsServiceB.DecryptMessageAsync(chatB.MlsGroupId!, ciphertextA);
        Assert.Equal("Hello from A!", decryptedByB.Plaintext);
        _output.WriteLine($"A→B message verified: \"{decryptedByB.Plaintext}\"");

        var ciphertextB = await _mlsServiceB.EncryptMessageAsync(chatB.MlsGroupId!, "Hello from B!");
        var decryptedByA = await _mlsServiceA.DecryptMessageAsync(groupInfo.GroupId, ciphertextB);
        Assert.Equal("Hello from B!", decryptedByA.Plaintext);
        _output.WriteLine($"B→A message verified: \"{decryptedByA.Plaintext}\"");

        _output.WriteLine("FULL STACK TEST PASSED: KeyPackage → Relay → AddMember → Welcome → Relay → AcceptInvite → Chat → Messages");
    }

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
