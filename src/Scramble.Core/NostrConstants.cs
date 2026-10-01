namespace Scramble.Core;

/// <summary>
/// Default and discovery relay URLs used throughout the app.
/// Centralizes relay configuration to avoid hardcoded strings scattered across the codebase.
/// </summary>
public static class NostrConstants
{
    /// <summary>
    /// Default relays used when no user-specific relay list is available.
    /// </summary>
    public static readonly string[] DefaultRelays =
    {
        "wss://relay.angor.io",
        "wss://relay2.angor.io",
        "wss://nos.lol"
    };

    /// <summary>
    /// The relays that index relay-list metadata (kind 10002 / 10050 / 10051)
    /// out of the box.
    /// </summary>
    public static readonly string[] BuiltInDiscoveryRelays =
    {
        "wss://purplepag.es",
        "wss://relay.damus.io"
    };

    /// <summary>
    /// Discovery relays that index relay-list metadata. Used to look up relay
    /// preferences for any user.
    /// </summary>
    /// <remarks>
    /// Settable because every relay-list lookup blocks the operation that asked for
    /// it, and the built-in entries are third-party hosts on the public internet. A
    /// test suite pinned to one relay has to discover through that relay only:
    /// reaching purplepag.es from a CI runner cost the KeyPackage audit the slowest
    /// of two internet round trips -- 10.5s measured, against a 15s budget, looking
    /// for a relay list that by construction does not exist -- and took a required
    /// gate red on third-party latency.
    /// </remarks>
    public static string[] DiscoveryRelays { get; private set; } = BuiltInDiscoveryRelays;

    /// <summary>
    /// Replaces the discovery relay set for this process. Call with no arguments to
    /// restore <see cref="BuiltInDiscoveryRelays"/>.
    /// </summary>
    public static void SetDiscoveryRelays(params string[] relays)
        => DiscoveryRelays = relays.Length > 0 ? relays : BuiltInDiscoveryRelays;
}
