using System.Text.Json;

namespace Scramble.Marmot.Wire.Nostr;

/// <summary>
/// The kind-445 group message event: build, parse, and the exact tag shape.
/// </summary>
/// <remarks>
/// <para>
/// The tag shape is a hard conformance rule, not a style preference. A
/// conformant kind-445 event carries <b>exactly one</b> <c>h</c> tag holding
/// 32 bytes of lowercase hex, <b>at most one</b> NIP-40 <c>expiration</c> tag,
/// and <b>no other tags at all</b>. Receivers reject anything else outright.
/// </para>
/// <para>
/// This is why the previous implementation's <c>encoding</c> tag has to go:
/// against a current peer, every message carrying it is dropped at the
/// envelope, before any MLS processing. Tag order is not significant.
/// </para>
/// </remarks>
public static class GroupMessageEvent
{
    public const int Kind = 445;

    public const string GroupTag = "h";
    public const string ExpirationTag = "expiration";

    /// <summary>Length of the routing id in bytes.</summary>
    public const int TransportGroupIdLength = 32;

    /// <summary>
    /// Builds the tag set for a kind-445 event.
    /// </summary>
    /// <param name="expiresAt">
    /// Optional NIP-40 expiration, as unsigned Unix seconds. Relay-facing
    /// deletion metadata only — never a validity check on the message.
    /// </param>
    public static IReadOnlyList<IReadOnlyList<string>> BuildTags(
        ReadOnlySpan<byte> transportGroupId,
        long? expiresAt = null)
    {
        if (transportGroupId.Length != TransportGroupIdLength)
            throw new ArgumentException(
                $"Routing id must be {TransportGroupIdLength} bytes.", nameof(transportGroupId));
        if (expiresAt is < 0)
            throw new ArgumentOutOfRangeException(
                nameof(expiresAt), "Expiration must be an unsigned Unix timestamp.");

        var tags = new List<IReadOnlyList<string>>(2)
        {
            new[] { GroupTag, Convert.ToHexString(transportGroupId).ToLowerInvariant() },
        };

        if (expiresAt is { } expiry)
            tags.Add(new[] { ExpirationTag, expiry.ToString() });

        return tags;
    }

    /// <summary>
    /// Validates the tag shape of an inbound kind-445 event and returns its
    /// routing id.
    /// </summary>
    /// <remarks>
    /// The expiration tag's syntax is checked but deliberately not compared to
    /// the local clock: it is relay deletion metadata, and treating it as a
    /// validity window would drop messages over clock skew.
    /// </remarks>
    /// <exception cref="PeelFailedException">The tag shape is not conformant.</exception>
    public static byte[] ReadTransportGroupId(IReadOnlyList<IReadOnlyList<string>> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        byte[]? transportGroupId = null;
        bool sawExpiration = false;

        foreach (var tag in tags)
        {
            if (tag.Count == 0)
                throw Malformed("a kind-445 tag must have a name and a value");

            switch (tag[0])
            {
                case GroupTag:
                    if (transportGroupId is not null)
                        throw Malformed("a kind-445 event must carry exactly one h tag");
                    if (tag.Count != 2)
                        throw Malformed("the kind-445 h tag must have exactly a name and a value");
                    transportGroupId = ParseRoutingId(tag[1]);
                    break;

                case ExpirationTag:
                    if (sawExpiration)
                        throw Malformed("a kind-445 event must carry at most one expiration tag");
                    sawExpiration = true;
                    if (tag.Count != 2)
                        throw Malformed("the kind-445 expiration tag must have exactly a name and a value");
                    if (!ulong.TryParse(tag[1], out _))
                        throw Malformed("the kind-445 expiration tag must be an unsigned Unix timestamp");
                    break;

                default:
                    throw Malformed($"a kind-445 event must carry no tag other than h or expiration, found '{tag[0]}'");
            }
        }

        return transportGroupId ?? throw Malformed("a kind-445 event must carry an h tag");
    }

    private static byte[] ParseRoutingId(string value)
    {
        if (value.Length != TransportGroupIdLength * 2)
            throw Malformed("the kind-445 h tag must be 32 bytes of lowercase hex");

        foreach (char c in value)
        {
            // Lowercase only: uppercase hex would change the tag bytes and so
            // the event id, even though it decodes to the same routing id.
            bool lowerHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!lowerHex)
                throw Malformed("the kind-445 h tag must be 32 bytes of lowercase hex");
        }

        return Convert.FromHexString(value);
    }

    /// <summary>Reads the tag array out of a Nostr event JSON document.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReadTags(JsonElement element)
    {
        if (!element.TryGetProperty("tags", out var tagsElement)
            || tagsElement.ValueKind != JsonValueKind.Array)
        {
            throw Malformed("the event has no tags array");
        }

        var tags = new List<IReadOnlyList<string>>();
        foreach (var tagElement in tagsElement.EnumerateArray())
        {
            if (tagElement.ValueKind != JsonValueKind.Array)
                throw Malformed("every tag must be an array");

            var values = new List<string>();
            foreach (var value in tagElement.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String)
                    throw Malformed("every tag element must be a string");
                values.Add(value.GetString()!);
            }

            tags.Add(values);
        }

        return tags;
    }

    private static PeelFailedException Malformed(string reason) => new(reason);
}
