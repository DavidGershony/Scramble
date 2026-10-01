using System.Runtime.CompilerServices;
using Scramble.Core;
using Scramble.Core.Configuration;

namespace Scramble.Diagnostics.TestHelpers;

/// <summary>
/// Shared relay configuration for diagnostic tests.
/// Defaults to wss://relay2.angor.io. Override via SCRAMBLE_TEST_RELAY env var
/// (e.g. "ws://localhost:7777" when running a local Docker relay).
/// </summary>
public static class TestRelayConfig
{
    /// <summary>
    /// Primary test relay URL used by all diagnostic tests that need a real Nostr relay.
    /// </summary>
    public static readonly string RelayUrl =
        Environment.GetEnvironmentVariable("SCRAMBLE_TEST_RELAY")
        ?? "wss://relay2.angor.io";

    /// <summary>
    /// Test relay as a single-element array, for APIs that expect string[].
    /// </summary>
    public static readonly string[] RelayUrls = new[] { RelayUrl };

    /// <summary>
    /// Points this process at <see cref="RelayUrl"/> for everything a test can reach:
    /// private-IP relays allowed, and relay-list discovery confined to the test relay.
    /// Call it from a test's constructor instead of <c>SetAllowLocalRelays</c> alone.
    /// </summary>
    /// <remarks>
    /// Confining discovery matters because <see cref="NostrConstants.BuiltInDiscoveryRelays"/>
    /// are third-party hosts on the public internet, and a relay-list lookup blocks the
    /// operation that asked for it until the slowest relay in the set answers or its query
    /// times out (10s). A test account has published no kind-10051 and no kind-10002, so
    /// both lookups run to that limit and neither result is cached -- on a CI runner the
    /// KeyPackage audit spent 10.5s of its 15s budget waiting on relays that could not
    /// possibly hold the answer. The test relay is the only relay that can.
    /// </remarks>
    public static void ApplyToProcess()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        NostrConstants.SetDiscoveryRelays(RelayUrl);
    }

    /// <summary>
    /// Confines discovery once for the whole assembly, so a test that does not call
    /// <see cref="ApplyToProcess"/> still discovers through the test relay and nothing
    /// depends on which test class ran first.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than <see cref="ApplyToProcess"/>: a module initializer runs
    /// before xunit v3's stdout handshake, and touching <see cref="ProfileConfiguration"/>
    /// here brings up its static Serilog logger, whose console sink prints a startup banner
    /// that the runner then fails to parse as JSON ("Test process did not return valid
    /// JSON"). <see cref="NostrConstants"/> holds no logger, so setting the relay set is
    /// safe this early.
    /// </remarks>
    [ModuleInitializer]
    internal static void ConfineDiscoveryForAssembly()
        => NostrConstants.SetDiscoveryRelays(RelayUrl);
}
