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
}
