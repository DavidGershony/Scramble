namespace Scramble.Marmot.AppComponents;

/// <summary>
/// <c>marmot.transport.nostr.routing.v1</c> (<c>0x8004</c>) — where a group's
/// messages are delivered.
/// </summary>
/// <remarks>
/// <para>
/// Required for any Nostr-routed group. The transport reads
/// <see cref="TransportGroupId"/> and <see cref="Relays"/> from here and
/// derives them from nothing else — not from the MLS group id, not from account
/// ids, not from relay URLs.
/// </para>
/// <para>
/// The routing id is opaque and MUST come from cryptographically secure
/// randomness, both at creation and at every rotation. Deriving it from an
/// account id, member id, public key, MLS group id, KeyPackage id, message id
/// or relay URL is forbidden: it appears in the clear on every kind-445 event,
/// so anything it is derived from is public too.
/// </para>
/// <para>
/// A rotation replaces the routing id, and the group stays reachable at the old
/// address for as long as any epoch that used it sits inside a retained-history
/// window. That is why a routing id must map to a group through an index rather
/// than by assumed equality — the mapping is genuinely many-to-one over time.
/// </para>
/// </remarks>
public sealed record NostrRouting
{
    /// <summary>Length of the routing id, in bytes.</summary>
    public const int TransportGroupIdLength = 32;

    /// <summary>Maximum relays in one routing state.</summary>
    public const int MaxRelays = 16;

    /// <summary>
    /// Upper bound on the encoded relay vector, used to reject an oversize
    /// length prefix before anything is allocated.
    /// </summary>
    /// <remarks>
    /// Each entry is at most a two-byte varint prefix plus the URL bound.
    /// </remarks>
    public const int MaxRelayVectorLength = MaxRelays * (RelayUrl.MaxLength + 2);

    private NostrRouting(byte[] transportGroupId, IReadOnlyList<string> relays)
    {
        TransportGroupId = transportGroupId;
        Relays = relays;
    }

    /// <summary>The 32-byte routing handle kind-445 events carry in their <c>h</c> tag.</summary>
    public byte[] TransportGroupId { get; }

    /// <summary>The canonical relay list: sorted by UTF-8 bytes, unique, non-empty.</summary>
    public IReadOnlyList<string> Relays { get; }

    /// <summary>
    /// Builds a routing state, canonicalising the relay list.
    /// </summary>
    /// <remarks>
    /// Sorting and de-duplicating here rather than rejecting is the one place
    /// that is safe to normalise, because this is the producer path: the caller
    /// has not yet committed to bytes. A <i>decoder</i> must reject the same
    /// input instead — see <see cref="Decode"/>.
    /// </remarks>
    public static NostrRouting Create(ReadOnlySpan<byte> transportGroupId, IEnumerable<string> relays)
    {
        ArgumentNullException.ThrowIfNull(relays);

        if (transportGroupId.Length != TransportGroupIdLength)
        {
            throw new AppComponentException(
                $"The routing id must be {TransportGroupIdLength} bytes.");
        }

        var canonical = new List<string>();
        foreach (string relay in relays)
        {
            RelayUrl.Require(relay);
            canonical.Add(relay);
        }

        canonical.Sort(RelayUrl.CompareByBytes);

        var deduplicated = new List<string>(canonical.Count);
        foreach (string relay in canonical)
        {
            if (deduplicated.Count == 0 || !string.Equals(deduplicated[^1], relay, StringComparison.Ordinal))
                deduplicated.Add(relay);
        }

        // Re-checked after dedup, not before: a list that was over the limit
        // only because of repeats is within it once canonicalised.
        RequireRelayCount(deduplicated.Count);

        return new NostrRouting(transportGroupId.ToArray(), deduplicated);
    }

    /// <summary>
    /// Encodes the component: the routing id, then a length-prefixed vector of
    /// length-prefixed relay URLs.
    /// </summary>
    public byte[] Encode()
    {
        var relayEntries = new List<byte>();
        foreach (string relay in Relays)
            ComponentCodec.WriteVarBytes(System.Text.Encoding.UTF8.GetBytes(relay), relayEntries);

        var output = new List<byte>(TransportGroupIdLength + relayEntries.Count + 8);
        output.AddRange(TransportGroupId);
        ComponentCodec.WriteVarint((ulong)relayEntries.Count, output);
        output.AddRange(relayEntries);

        return output.ToArray();
    }

    /// <summary>
    /// Decodes and validates the component.
    /// </summary>
    /// <remarks>
    /// An unsorted or duplicated relay list is <b>rejected</b>, not sorted.
    /// This is signed group state: normalising it locally would leave this
    /// member holding a different list from the one every other member sees,
    /// which is the fork the canonical form exists to prevent.
    /// </remarks>
    /// <exception cref="AppComponentException">The bytes are not a valid routing state.</exception>
    public static NostrRouting Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < TransportGroupIdLength)
            throw new AppComponentException("The routing component is missing its routing id.");

        byte[] transportGroupId = bytes[..TransportGroupIdLength].ToArray();

        var cursor = bytes[TransportGroupIdLength..];
        byte[] relayVector = ComponentCodec.ReadVarBytes(
            ref cursor, MaxRelayVectorLength, "Nostr relay vector");
        ComponentCodec.RequireSpent(cursor, "routing");

        var relays = new List<string>();
        var relayCursor = new ReadOnlySpan<byte>(relayVector);
        while (!relayCursor.IsEmpty)
        {
            if (relays.Count == MaxRelays)
                throw new AppComponentException($"A routing state carries at most {MaxRelays} relays.");

            byte[] relay = ComponentCodec.ReadVarBytes(
                ref relayCursor, RelayUrl.MaxLength, "Nostr relay URL");

            if (relay.Length == 0)
                throw new AppComponentException("A relay URL must not be empty.");

            relays.Add(DecodeUtf8(relay));
        }

        RequireRelayCount(relays.Count);
        RequireCanonicalOrder(relays);
        foreach (string relay in relays)
            RelayUrl.Require(relay);

        return new NostrRouting(transportGroupId, relays);
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        try
        {
            // Throwing rather than substituting U+FFFD: a replacement character
            // would produce a string that re-encodes to different bytes from
            // the ones the group signed.
            return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (ArgumentException ex)
        {
            throw new AppComponentException($"A relay URL is not valid UTF-8: {ex.Message}");
        }
    }

    private static void RequireRelayCount(int count)
    {
        if (count == 0)
            throw new AppComponentException("A routing state must carry at least one relay.");
        if (count > MaxRelays)
            throw new AppComponentException($"A routing state carries at most {MaxRelays} relays.");
    }

    private static void RequireCanonicalOrder(IReadOnlyList<string> relays)
    {
        for (int i = 1; i < relays.Count; i++)
        {
            int order = RelayUrl.CompareByBytes(relays[i - 1], relays[i]);
            if (order > 0)
                throw new AppComponentException("Relay URLs must be sorted by their bytes.");
            if (order == 0)
                throw new AppComponentException($"Relay URL '{relays[i]}' appears more than once.");
        }
    }
}
