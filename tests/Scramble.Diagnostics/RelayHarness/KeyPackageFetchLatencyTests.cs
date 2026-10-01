using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Scramble.Core;
using Scramble.Core.Configuration;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
using Xunit;

namespace Scramble.Diagnostics.RelayHarness;

/// <summary>
/// Relay-list discovery must not gate a KeyPackage fetch.
///
/// FetchKeyPackagesAsync resolves where the target publishes KeyPackages before it
/// queries anything: kind 10051, and kind 10002 as its fallback when there is no
/// kind 10051. Both lookups fan out to <see cref="NostrConstants.DiscoveryRelays"/>,
/// and both used to be awaited to completion, in series, before a single kind-30443
/// query went out. A discovery relay that accepts a REQ and never finishes it
/// therefore cost one full QueryRelayAsync timeout (10s) per lookup — 20s of dead
/// wait, on relays that for a fresh account hold nothing, in front of an answer the
/// connected relay had all along.
///
/// That is not hypothetical: it took the required integration gate red. A public
/// discovery relay was measured answering the same lookup in 1.5s, in 4.3s, and not
/// within 10s at all, while the KeyPackage audit a user runs from Settings sat on
/// "Fetching KeyPackages from relays...".
/// </summary>
[Trait("Category", "RelayHarness")]
public class KeyPackageFetchLatencyTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();
    private FaultyRelay _silentDiscoveryRelay = null!;

    public KeyPackageFetchLatencyTests(ITestOutputHelper output)
    {
        _output = output;
        TestRelayConfig.ApplyToProcess();
    }

    public async ValueTask InitializeAsync()
    {
        _silentDiscoveryRelay = new FaultyRelay();
        // Reachable and willing, but no query it accepts ever completes.
        _silentDiscoveryRelay.Faults.DropEose = true;
        await _silentDiscoveryRelay.StartAsync();
        _output.WriteLine($"silent discovery relay at {_silentDiscoveryRelay.WsUrl}");
    }

    public async ValueTask DisposeAsync()
    {
        // Restore what the assembly's module initializer set, so the relay set does not
        // point at a disposed harness for whatever test class runs next.
        NostrConstants.SetDiscoveryRelays(TestRelayConfig.RelayUrl);
        await _silentDiscoveryRelay.DisposeAsync();

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var p in _dbPaths)
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch { }
        }
    }

    [Fact]
    public async Task FetchKeyPackages_DiscoveryRelayNeverAnswers_StillReturnsFromConnectedRelay()
    {
        // A second harness relay stands in for the relay the account actually uses, so
        // the measurement is about the discovery path and not about any live relay.
        await using var ownRelay = new FaultyRelay();
        await ownRelay.StartAsync();

        NostrConstants.SetDiscoveryRelays(_silentDiscoveryRelay.WsUrl);

        using var nostr = new NostrService();
        var (privKey, pubKey, _, _) = nostr.GenerateKeyPair();

        var dbPath = Path.Combine(Path.GetTempPath(), $"kp_latency_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var mls = DarkMatterMlsServiceFactory.Create(storage);
        await mls.InitializeAsync(privKey, pubKey);

        await nostr.ConnectAsync(ownRelay.WsUrl);
        Assert.Contains(ownRelay.WsUrl, nostr.ConnectedRelayUrls);

        // A real kind-30443 on the connected relay, minted by the engine the app
        // registers, so the fetch has something it can actually parse back.
        var keyPackage = await mls.GenerateKeyPackageAsync();
        var eventId = await nostr.PublishKeyPackageAsync(
            keyPackage.Data, privKey, keyPackage.NostrTags);
        _output.WriteLine($"published {eventId[..16]} to {ownRelay.WsUrl}");

        var sw = Stopwatch.StartNew();
        var found = (await nostr.FetchKeyPackagesAsync(pubKey, limit: 100)).ToList();
        sw.Stop();
        _output.WriteLine($"FetchKeyPackagesAsync: {sw.ElapsedMilliseconds}ms -> {found.Count} KeyPackage(s)");

        // This account has published no kind 10051 and no kind 10002, so both lookups go
        // to the silent relay and neither can ever answer. Awaited in series that is two
        // 10s query timeouts; 9s is below even one of them, so this fails if either is
        // back on the critical path.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(9),
            $"FetchKeyPackagesAsync waited {sw.Elapsed.TotalSeconds:F1}s on a discovery relay " +
            "that never answers — relay-list discovery is gating the fetch again");

        // And the fetch still returns the KeyPackage: giving up on discovery may cost
        // coverage of relays we never knew about, never the relays we are connected to.
        Assert.Single(found);
        Assert.Equal(eventId, found[0].NostrEventId);
    }
}
