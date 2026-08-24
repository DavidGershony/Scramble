using Scramble.Nostr.Crypto;

namespace Scramble.Marmot.Wire.Nostr;

/// <summary>
/// A validated kind-444 Welcome rumor.
/// </summary>
/// <param name="KeyPackageEventId">
/// The KeyPackage event this Welcome consumed, as 32 raw bytes. The join path
/// needs it to find which of our published KeyPackages was used, since the
/// private material for it must be consumed exactly once.
/// </param>
/// <param name="Relays">Group relays the new member should use from now on.</param>
/// <param name="WelcomeBytes">The MLS Welcome message.</param>
/// <param name="SenderPublicKeyHex">The inviter, taken from the verified seal.</param>
public sealed record WelcomeRumor(
    byte[] KeyPackageEventId,
    IReadOnlyList<string> Relays,
    byte[] WelcomeBytes,
    string SenderPublicKeyHex);

/// <summary>
/// The kind-444 Welcome rumor carried inside a NIP-59 gift wrap.
/// </summary>
/// <remarks>
/// Both tags are routing-significant, so each must appear exactly once —
/// duplicates are rejected rather than resolved by taking the first, because
/// "take the first" lets an attacker prepend a tag and steer the join.
/// </remarks>
public static class WelcomeEvent
{
    public const int Kind = 444;

    public const string KeyPackageEventTag = "e";
    public const string RelaysTag = "relays";

    public const int KeyPackageEventIdLength = 32;

    /// <summary>Upper bound on relays in a Welcome, so a rumor cannot fan out unboundedly.</summary>
    public const int MaxRelays = 16;

    /// <summary>Upper bound on a single relay URL, in bytes.</summary>
    public const int MaxRelayUrlLength = 512;

    /// <summary>
    /// Validates a rumor and extracts the Welcome.
    /// </summary>
    /// <exception cref="PeelFailedException">The rumor is not a conformant Welcome.</exception>
    public static WelcomeRumor Read(Rumor rumor)
    {
        ArgumentNullException.ThrowIfNull(rumor);

        if (rumor.Kind != Kind)
            throw new PeelFailedException($"Expected a kind-{Kind} Welcome rumor, got kind {rumor.Kind}.");

        byte[] keyPackageEventId = ParseKeyPackageEventId(SingleTagValue(rumor, KeyPackageEventTag));
        IReadOnlyList<string> relays = ValidateRelays(SingleTagValues(rumor, RelaysTag));

        byte[] welcomeBytes;
        try
        {
            welcomeBytes = Convert.FromBase64String(rumor.Content);
        }
        catch (FormatException ex)
        {
            throw new PeelFailedException($"The Welcome rumor's content is not base64: {ex.Message}");
        }

        if (welcomeBytes.Length == 0)
            throw new PeelFailedException("The Welcome rumor carried no MLS Welcome bytes.");

        return new WelcomeRumor(keyPackageEventId, relays, welcomeBytes, rumor.PublicKeyHex);
    }

    /// <summary>Builds the tag set for an outbound Welcome rumor.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> BuildTags(
        ReadOnlySpan<byte> keyPackageEventId,
        IReadOnlyList<string> relays)
    {
        if (keyPackageEventId.Length != KeyPackageEventIdLength)
            throw new ArgumentException(
                $"The KeyPackage event id must be {KeyPackageEventIdLength} bytes.",
                nameof(keyPackageEventId));

        ValidateRelays(relays);

        var relayTag = new List<string> { RelaysTag };
        relayTag.AddRange(relays);

        return new IReadOnlyList<string>[]
        {
            new[] { KeyPackageEventTag, Convert.ToHexString(keyPackageEventId).ToLowerInvariant() },
            relayTag,
        };
    }

    private static byte[] ParseKeyPackageEventId(string value)
    {
        if (value.Length != KeyPackageEventIdLength * 2)
            throw new PeelFailedException(
                $"The Welcome rumor's {KeyPackageEventTag} tag must be {KeyPackageEventIdLength} bytes of hex.");

        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            throw new PeelFailedException(
                $"The Welcome rumor's {KeyPackageEventTag} tag is not valid hex.");
        }
    }

    private static IReadOnlyList<string> ValidateRelays(IReadOnlyList<string> relays)
    {
        if (relays.Count == 0)
            throw new PeelFailedException("A Welcome rumor must list at least one relay.");

        if (relays.Count > MaxRelays)
            throw new PeelFailedException(
                $"A Welcome rumor lists {relays.Count} relays; the limit is {MaxRelays}.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string relay in relays)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(relay) > MaxRelayUrlLength)
                throw new PeelFailedException(
                    $"A Welcome relay URL exceeds {MaxRelayUrlLength} bytes.");

            RequireRelayUrl(relay);

            // Relay URLs are compared as exact byte strings downstream, so a
            // repeated entry is a list that means something different from what
            // it appears to say. The spec calls such a list malformed.
            if (!seen.Add(relay))
                throw new PeelFailedException($"A Welcome rumor lists '{relay}' more than once.");
        }

        return relays;
    }

    /// <summary>
    /// Applies the Marmot relay-URL profile.
    /// </summary>
    /// <remarks>
    /// Beyond requiring ws or wss, the profile forbids userinfo and fragment
    /// components. Both matter here because this list arrives from whoever
    /// invited us, before any trust decision: credentials embedded in a URL
    /// would be handed to whatever connects, and a fragment produces a distinct
    /// string for what is really the same relay.
    /// </remarks>
    private static void RequireRelayUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new PeelFailedException($"'{value}' is not an absolute URL.");

        if (uri.Scheme is not ("ws" or "wss"))
            throw new PeelFailedException($"'{value}' is not a ws or wss relay URL.");

        if (string.IsNullOrEmpty(uri.Host))
            throw new PeelFailedException($"'{value}' has no host.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new PeelFailedException($"'{value}' must not carry userinfo.");

        if (!string.IsNullOrEmpty(uri.Fragment))
            throw new PeelFailedException($"'{value}' must not carry a fragment.");
    }

    /// <summary>
    /// The single value of a tag that must carry exactly one.
    /// </summary>
    /// <remarks>
    /// Extra values are rejected rather than ignored: the spec says a tag with
    /// values beyond the one it defines makes the event malformed, and silently
    /// taking the first would let an attacker append a value that some other
    /// implementation reads instead.
    /// </remarks>
    private static string SingleTagValue(Rumor rumor, string name)
    {
        var values = SingleTagValues(rumor, name);
        return values.Count switch
        {
            1 => values[0],
            0 => throw new PeelFailedException($"The Welcome rumor's {name} tag has no value."),
            _ => throw new PeelFailedException(
                $"The Welcome rumor's {name} tag must carry exactly one value."),
        };
    }

    private static IReadOnlyList<string> SingleTagValues(Rumor rumor, string name)
    {
        IReadOnlyList<string>? found = null;
        foreach (var tag in rumor.Tags)
        {
            if (tag.Count == 0 || tag[0] != name)
                continue;

            if (found is not null)
                throw new PeelFailedException(
                    $"A Welcome rumor must carry exactly one {name} tag.");

            found = tag;
        }

        if (found is null)
            throw new PeelFailedException($"A Welcome rumor must carry a {name} tag.");

        return found.Skip(1).ToArray();
    }
}
