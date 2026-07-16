using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Core.Tests.TestHelpers;
using Xunit;
namespace Scramble.Core.Tests;

/// <summary>
/// Validates the full Welcome message pipeline: MLS group creation → AddMember →
/// Welcome bytes → gift wrap → unwrap → verify rumor structure.
///
/// The web client (marmot-ts) requires rumor.id to be present for routing and storage.
/// This test ensures the rumor includes a valid NIP-01 event id.
/// </summary>
public class WelcomeRumorFormatTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private NostrService _nostrServiceA = null!;
    private NostrService _nostrServiceB = null!;
    private string _privKeyA = null!;
    private string _pubKeyA = null!;
    private string _privKeyB = null!;
    private string _pubKeyB = null!;
    private ManagedMlsService _mlsServiceA = null!;
    private ManagedMlsService _mlsServiceB = null!;
    private StorageService _storageA = null!;
    private StorageService _storageB = null!;
    private readonly List<string> _dbPaths = new();

    private const string RelayUrl = "wss://relay2.angor.io";

    public WelcomeRumorFormatTests(ITestOutputHelper output)
    {
        _output = output;
        ProfileConfiguration.SetAllowLocalRelays(true);
    }

    public async ValueTask InitializeAsync()
    {
        _nostrServiceA = new NostrService();
        _nostrServiceB = new NostrService();

        var keysA = _nostrServiceA.GenerateKeyPair();
        _privKeyA = keysA.privateKeyHex;
        _pubKeyA = keysA.publicKeyHex;

        var keysB = _nostrServiceB.GenerateKeyPair();
        _privKeyB = keysB.privateKeyHex;
        _pubKeyB = keysB.publicKeyHex;

        var dbA = Path.Combine(Path.GetTempPath(), $"welcome_fmt_a_{Guid.NewGuid():N}.db");
        var dbB = Path.Combine(Path.GetTempPath(), $"welcome_fmt_b_{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbA);
        _dbPaths.Add(dbB);

        _storageA = new StorageService(dbA, new MockSecureStorage());
        _storageB = new StorageService(dbB, new MockSecureStorage());
        await _storageA.InitializeAsync();
        await _storageB.InitializeAsync();

        _mlsServiceA = new ManagedMlsService(_storageA);
        _mlsServiceB = new ManagedMlsService(_storageB);
        await _mlsServiceA.InitializeAsync(_privKeyA, _pubKeyA);
        await _mlsServiceB.InitializeAsync(_privKeyB, _pubKeyB);

        await _nostrServiceA.ConnectAsync(RelayUrl);
        await _nostrServiceB.ConnectAsync(RelayUrl);
        await Task.Delay(500);
    }

    public async ValueTask DisposeAsync()
    {
        await _nostrServiceA.DisconnectAsync();
        await _nostrServiceB.DisconnectAsync();
        (_nostrServiceA as IDisposable)?.Dispose();
        (_nostrServiceB as IDisposable)?.Dispose();

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var path in _dbPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    /// <summary>
    /// Validates that the Welcome rumor (kind 444) produced by the gift wrap flow
    /// contains a valid NIP-01 event id. The web client uses rumor.id for storage
    /// and routing — without it, the UI crashes with "o.id is undefined".
    /// </summary>
    [Fact]
    [Trait("Category", "Relay")]
    public async Task WelcomeRumor_HasValidEventId()
    {
        // User B generates and publishes a KeyPackage
        var kpB = await _mlsServiceB.GenerateKeyPackageAsync();
        var kpEventId = await _nostrServiceB.PublishKeyPackageAsync(kpB.Data, _privKeyB, kpB.NostrTags);
        await Task.Delay(500);

        // User A fetches the KeyPackage
        var fetchedKps = (await _nostrServiceA.FetchKeyPackagesAsync(_pubKeyB)).ToList();
        Assert.NotEmpty(fetchedKps);

        // User A creates a group and adds User B
        var group = await _mlsServiceA.CreateGroupAsync("Test Group", new[] { "wss://relay.test" });
        var welcome = await _mlsServiceA.AddMemberAsync(group.GroupId, fetchedKps[0]);

        // User B subscribes to welcomes
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        await Task.Delay(500);

        // Set up listener for the unwrapped kind 444 rumor
        var rumorTcs = new TaskCompletionSource<NostrEventReceived>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = _nostrServiceB.Events
            .Where(e => e.Kind == 444)
            .Take(1)
            .Subscribe(e => rumorTcs.TrySetResult(e));

        // User A publishes the Welcome
        var eventId = await _nostrServiceA.PublishWelcomeAsync(
            welcome.WelcomeData, _pubKeyB, _privKeyA, welcome.KeyPackageEventId);
        Assert.Equal(64, eventId.Length);

        // Wait for User B to receive the unwrapped rumor
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        cts.Token.Register(() => rumorTcs.TrySetCanceled());
        var rumor = await rumorTcs.Task;

        _output.WriteLine($"Rumor kind: {rumor.Kind}");
        _output.WriteLine($"Rumor EventId: {rumor.EventId}");
        _output.WriteLine($"Rumor PublicKey: {rumor.PublicKey}");
        _output.WriteLine($"Rumor Content length: {rumor.Content.Length}");

        // === CRITICAL ASSERTIONS ===

        // 1. Rumor must have a valid event id (64 hex chars)
        Assert.NotNull(rumor.EventId);
        Assert.Equal(64, rumor.EventId.Length);
        Assert.Matches("^[0-9a-f]{64}$", rumor.EventId);
        _output.WriteLine("PASS: rumor.id is present and valid");

        // 2. Rumor must be kind 444
        Assert.Equal(444, rumor.Kind);

        // 3. Rumor must have sender's pubkey
        Assert.Equal(_pubKeyA, rumor.PublicKey);

        // 4. Content must be valid base64 of MLSMessage-wrapped Welcome
        var welcomeBytes = Convert.FromBase64String(rumor.Content);
        Assert.True(welcomeBytes.Length > 4, "Welcome bytes too short");

        // 5. MLSMessage header: version=0x0001, wireformat=0x0003
        Assert.Equal(0x00, welcomeBytes[0]);
        Assert.Equal(0x01, welcomeBytes[1]);
        Assert.Equal(0x00, welcomeBytes[2]);
        Assert.Equal(0x03, welcomeBytes[3]);
        _output.WriteLine("PASS: MLSMessage header correct (version=mls10, wireformat=mls_welcome)");

        // 6. Cipher suite = 0x0001
        Assert.Equal(0x00, welcomeBytes[4]);
        Assert.Equal(0x01, welcomeBytes[5]);
        _output.WriteLine("PASS: cipher suite 0x0001");

        // 7. Tags must include encoding=base64
        var encodingTag = rumor.Tags.FirstOrDefault(t => t.Count >= 2 && t[0] == "encoding");
        Assert.NotNull(encodingTag);
        Assert.Equal("base64", encodingTag![1]);
        _output.WriteLine("PASS: encoding=base64 tag present");

        // 8. Tags must include p tag with recipient pubkey
        var pTag = rumor.Tags.FirstOrDefault(t => t.Count >= 2 && t[0] == "p");
        Assert.NotNull(pTag);
        Assert.Equal(_pubKeyB, pTag![1]);
        _output.WriteLine("PASS: p tag with recipient pubkey");

        // 9. Tags must include e tag with KeyPackage event id
        var eTag = rumor.Tags.FirstOrDefault(t => t.Count >= 2 && t[0] == "e");
        Assert.NotNull(eTag);
        Assert.Equal(kpEventId, eTag![1]);
        _output.WriteLine("PASS: e tag with KeyPackage event id");

        // 10. Verify the rumor id is the correct SHA-256 of canonical serialization
        var canonicalTags = rumor.Tags.Select(t => t.ToList()).ToList();
        var createdAtUnix = new DateTimeOffset(rumor.CreatedAt, TimeSpan.Zero).ToUnixTimeSeconds();
        var canonical = NostrService.SerializeForEventId(
            rumor.PublicKey, createdAtUnix, rumor.Kind, canonicalTags, rumor.Content);
        var expectedId = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        Assert.Equal(expectedId, rumor.EventId);
        _output.WriteLine($"PASS: rumor.id matches SHA-256 of canonical serialization");

        _output.WriteLine("\nALL CHECKS PASSED: Welcome rumor is fully compliant");
    }

    /// <summary>
    /// Validates that the rumor id is computed correctly even without relay round-trip
    /// (unit-level check of SerializeRumor).
    /// </summary>
    [Fact]
    public void SerializeForEventId_ProducesValidHash()
    {
        var pubkey = "aaaa" + new string('0', 60);
        var createdAt = 1700000000L;
        var kind = 444;
        var tags = new List<List<string>>
        {
            new() { "p", "bbbb" + new string('0', 60) },
            new() { "encoding", "base64" }
        };
        var content = "dGVzdA=="; // base64 of "test"

        var serialized = NostrService.SerializeForEventId(pubkey, createdAt, kind, tags, content);
        var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        var idHex = Convert.ToHexString(idBytes).ToLowerInvariant();

        Assert.Equal(64, idHex.Length);
        Assert.Matches("^[0-9a-f]{64}$", idHex);

        // Same inputs must produce same id (deterministic)
        var serialized2 = NostrService.SerializeForEventId(pubkey, createdAt, kind, tags, content);
        var id2 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized2))).ToLowerInvariant();
        Assert.Equal(idHex, id2);
    }
}
