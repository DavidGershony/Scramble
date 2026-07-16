using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// Integration tests that connect to a real local Nostr relay (Docker).
/// Requires: docker compose -f docker-compose.test.yml up -d
/// Relay: scsibug/nostr-rs-relay on wss://relay2.angor.io
/// </summary>
[Trait("Category", "Relay")]
public class RelayIntegrationTests : IAsyncLifetime
{
    private const string RelayUrl = "wss://relay2.angor.io";

    private NostrService _nostrServiceA = null!;
    private NostrService _nostrServiceB = null!;
    private string _pubKeyA = null!;
    private string _privKeyA = null!;
    private string _pubKeyB = null!;
    private string _privKeyB = null!;

    public async ValueTask InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _nostrServiceA = new NostrService();
        _nostrServiceB = new NostrService();

        // Generate real keypairs
        var keysA = _nostrServiceA.GenerateKeyPair();
        _privKeyA = keysA.privateKeyHex;
        _pubKeyA = keysA.publicKeyHex;

        var keysB = _nostrServiceB.GenerateKeyPair();
        _privKeyB = keysB.privateKeyHex;
        _pubKeyB = keysB.publicKeyHex;

        // Connect both users to the relay
        await _nostrServiceA.ConnectAsync(RelayUrl);
        await _nostrServiceB.ConnectAsync(RelayUrl);

        // Give the connections time to establish
        await Task.Delay(1000);
    }

    public async ValueTask DisposeAsync()
    {
        await _nostrServiceA.DisconnectAsync();
        await _nostrServiceB.DisconnectAsync();
        (_nostrServiceA as IDisposable)?.Dispose();
        (_nostrServiceB as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task ConnectToRelay_ShouldSucceed()
    {
        // Connection was established in InitializeAsync.
        // Verify by publishing a small event - if not connected, this would throw.
        var data = new byte[64];
        RandomNumberGenerator.Fill(data);
        var groupId = Convert.ToHexString(new byte[16]).ToLowerInvariant();

        var eventId = await _nostrServiceA.PublishGroupMessageAsync(data, groupId, _privKeyA);
        Assert.NotNull(eventId);
        Assert.Equal(64, eventId.Length);
    }

    [Fact]
    public async Task PublishKeyPackage_ShouldReturnEventId()
    {
        var keyPackageData = new byte[256];
        RandomNumberGenerator.Fill(keyPackageData);

        var kpRefHex = Convert.ToHexString(keyPackageData[..32]).ToLowerInvariant();
        var tags = new List<List<string>>
        {
            new() { "encoding", "base64" },
            new() { "mls_protocol_version", "1.0" },
            new() { "mls_ciphersuite", "0x0001" },
            new() { "i", kpRefHex },
            new() { "d", kpRefHex }
        };

        var eventId = await _nostrServiceA.PublishKeyPackageAsync(keyPackageData, _privKeyA, tags);

        Assert.NotNull(eventId);
        Assert.NotEmpty(eventId);
        Assert.Equal(64, eventId.Length); // Nostr event IDs are 32-byte hex
    }

    [Fact]
    public async Task PublishAndFetchKeyPackage_UserBSeesUserAPackage()
    {
        // User A publishes a KeyPackage
        var keyPackageData = new byte[256];
        RandomNumberGenerator.Fill(keyPackageData);

        var kpRefHex = Convert.ToHexString(keyPackageData[..32]).ToLowerInvariant();
        var tags = new List<List<string>>
        {
            new() { "encoding", "base64" },
            new() { "mls_protocol_version", "1.0" },
            new() { "mls_ciphersuite", "0x0001" },
            new() { "i", kpRefHex },
            new() { "d", kpRefHex }
        };

        var eventId = await _nostrServiceA.PublishKeyPackageAsync(keyPackageData, _privKeyA, tags);
        Assert.NotEmpty(eventId);

        // Give the relay time to store and index the event
        await Task.Delay(2000);

        // User B fetches User A's KeyPackages
        var keyPackages = await _nostrServiceB.FetchKeyPackagesAsync(_pubKeyA);
        var kpList = keyPackages.ToList();

        Assert.NotEmpty(kpList);
        Assert.Contains(kpList, kp => kp.Data.Length > 0);
    }

    [Fact]
    public async Task PublishWelcome_ShouldReturnEventId()
    {
        var welcomeData = new byte[512];
        RandomNumberGenerator.Fill(welcomeData);

        var eventId = await _nostrServiceA.PublishWelcomeAsync(
            welcomeData, _pubKeyB, _privKeyA, "test-kp-event-id".PadLeft(64, '0'));

        Assert.NotNull(eventId);
        Assert.NotEmpty(eventId);
        Assert.Equal(64, eventId.Length);
    }

    [Fact]
    public async Task PublishWelcome_UserBReceivesViaSubscription()
    {
        // User B subscribes to welcomes
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        await Task.Delay(500);

        // Set up event listener before publishing
        var receivedEvent = new TaskCompletionSource<NostrEventReceived>();
        using var sub = _nostrServiceB.Events
            .Where(e => e.Kind == 444)
            .Take(1)
            .Subscribe(e => receivedEvent.TrySetResult(e));

        // User A publishes a welcome for User B
        var welcomeData = new byte[128];
        RandomNumberGenerator.Fill(welcomeData);

        var eventId = await _nostrServiceA.PublishWelcomeAsync(
            welcomeData, _pubKeyB, _privKeyA, "test-kp-event-id".PadLeft(64, '0'));

        Assert.NotEmpty(eventId);

        // Wait for User B to receive the event (with timeout)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() => receivedEvent.TrySetCanceled());

        var received = await receivedEvent.Task;

        Assert.Equal(444, received.Kind);
        Assert.Equal(_pubKeyA, received.PublicKey);
        Assert.NotEmpty(received.Content);

        // Verify the welcome data roundtrips correctly
        var decodedWelcome = Convert.FromBase64String(received.Content);
        Assert.Equal(welcomeData, decodedWelcome);
    }

    [Fact]
    public async Task PublishGroupMessage_ShouldReturnEventId()
    {
        var encryptedData = new byte[64];
        RandomNumberGenerator.Fill(encryptedData);
        var groupId = Convert.ToHexString(new byte[16]).ToLowerInvariant();

        var eventId = await _nostrServiceA.PublishGroupMessageAsync(
            encryptedData, groupId, _privKeyA);

        Assert.NotNull(eventId);
        Assert.NotEmpty(eventId);
        Assert.Equal(64, eventId.Length);
    }

    [Fact]
    public async Task FullRelayRoundtrip_KeyPackageAndWelcome()
    {
        // Phase 1: User A publishes a KeyPackage
        var keyPackageData = new byte[256];
        RandomNumberGenerator.Fill(keyPackageData);

        var kpRefHex = Convert.ToHexString(keyPackageData[..32]).ToLowerInvariant();
        var tags = new List<List<string>>
        {
            new() { "encoding", "base64" },
            new() { "mls_protocol_version", "1.0" },
            new() { "mls_ciphersuite", "0x0001" },
            new() { "i", kpRefHex },
            new() { "d", kpRefHex }
        };

        var kpEventId = await _nostrServiceA.PublishKeyPackageAsync(keyPackageData, _privKeyA, tags);
        Assert.Equal(64, kpEventId.Length);

        // Give the relay time to store and index the event
        await Task.Delay(2000);

        // Phase 2: User B fetches User A's KeyPackage
        var fetchedKPs = (await _nostrServiceB.FetchKeyPackagesAsync(_pubKeyA)).ToList();
        Assert.NotEmpty(fetchedKPs);

        // Phase 3: User B subscribes to welcomes, User A sends welcome
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        await Task.Delay(500);

        var welcomeReceived = new TaskCompletionSource<NostrEventReceived>();
        using var sub = _nostrServiceB.Events
            .Where(e => e.Kind == 444)
            .Take(1)
            .Subscribe(e => welcomeReceived.TrySetResult(e));

        var welcomeData = new byte[512];
        RandomNumberGenerator.Fill(welcomeData);
        var welcomeEventId = await _nostrServiceA.PublishWelcomeAsync(
            welcomeData, _pubKeyB, _privKeyA, kpEventId);
        Assert.Equal(64, welcomeEventId.Length);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() => welcomeReceived.TrySetCanceled());

        var receivedWelcome = await welcomeReceived.Task;
        Assert.Equal(444, receivedWelcome.Kind);
        Assert.Equal(_pubKeyA, receivedWelcome.PublicKey);

        // Verify welcome data integrity
        var decodedWelcome = Convert.FromBase64String(receivedWelcome.Content);
        Assert.Equal(welcomeData, decodedWelcome);

        // Phase 4: Verify the 'p' tag points to User B
        var pTag = receivedWelcome.Tags.FirstOrDefault(t => t.Count > 1 && t[0] == "p");
        Assert.NotNull(pTag);
        Assert.Equal(_pubKeyB, pTag![1]);
    }
}
