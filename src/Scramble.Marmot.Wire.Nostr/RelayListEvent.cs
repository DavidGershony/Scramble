using Scramble.Marmot.AppComponents;
using Scramble.Nostr.Crypto;

namespace Scramble.Marmot.Wire.Nostr;

/// <summary>
/// The two relay lists an account must publish to be reachable.
/// </summary>
/// <remarks>
/// <para>
/// Publishing a KeyPackage is not enough to be invitable. A peer looking us up
/// asks two questions — where does this account read, and where can it be
/// messaged — and answers them from these lists. The reference client checks
/// both before it will invite anyone (<c>keys check</c> reports "relay lists
/// and a fetchable KeyPackage"), so an account with a KeyPackage and no lists
/// is one nobody can add to a group.
/// </para>
/// <para>
/// Both are generic Nostr, not Marmot: <c>10002</c> is NIP-65 and <c>10050</c>
/// is the NIP-17 message-relay list. They live here because this is where event
/// shapes are built, and the Marmot relay-URL profile is what validates them —
/// the same profile the routing component uses, so a relay that one accepted
/// and the other refused could not happen.
/// </para>
/// </remarks>
public static class RelayListEvent
{
    /// <summary>NIP-65 relay list: where this account reads and writes.</summary>
    public const int Nip65Kind = 10002;

    /// <summary>NIP-17 message relays: where this account can be reached.</summary>
    public const int MessageRelayKind = 10050;

    /// <summary>The NIP-65 tag name.</summary>
    public const string Nip65Tag = "r";

    /// <summary>The message-relay tag name.</summary>
    public const string MessageRelayTag = "relay";

    /// <summary>
    /// Builds the NIP-65 relay list.
    /// </summary>
    /// <remarks>
    /// Each relay is listed without a read/write marker, which NIP-65 defines as
    /// "both". Marking them would be a claim we cannot honour — Marmot uses one
    /// set of relays for a group in both directions.
    /// </remarks>
    public static NostrEventTemplate BuildNip65(
        string accountPublicKeyHex, IReadOnlyList<string> relays, long createdAt) =>
        Build(accountPublicKeyHex, relays, createdAt, Nip65Kind, Nip65Tag);

    /// <summary>Builds the message-relay list.</summary>
    public static NostrEventTemplate BuildMessageRelays(
        string accountPublicKeyHex, IReadOnlyList<string> relays, long createdAt) =>
        Build(accountPublicKeyHex, relays, createdAt, MessageRelayKind, MessageRelayTag);

    private static NostrEventTemplate Build(
        string accountPublicKeyHex,
        IReadOnlyList<string> relays,
        long createdAt,
        int kind,
        string tagName)
    {
        ArgumentNullException.ThrowIfNull(accountPublicKeyHex);
        ArgumentNullException.ThrowIfNull(relays);

        if (relays.Count == 0)
        {
            // An empty list is not "no preference" — it publishes that the
            // account is unreachable, which is worse than publishing nothing.
            throw new ArgumentException("A relay list must name at least one relay.", nameof(relays));
        }

        var tags = new List<IReadOnlyList<string>>(relays.Count);
        foreach (string relay in relays)
        {
            if (!RelayUrl.IsValid(relay, out string? error))
                throw new ArgumentException(error, nameof(relays));

            tags.Add([tagName, relay]);
        }

        return new NostrEventTemplate(accountPublicKeyHex, createdAt, kind, tags, string.Empty);
    }
}
