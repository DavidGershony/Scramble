namespace Scramble.Core.Models;

/// <summary>
/// What a manual "fetch missing messages" pass actually did.
/// </summary>
/// <remarks>
/// <para>
/// The point of reporting rather than silently re-subscribing: an empty group looks
/// identical whatever the cause, and the causes need opposite responses. A group with
/// three routing addresses where we were listening on one was never asked for its
/// history. A group with one address, correctly subscribed, that still returns nothing
/// has nothing on the relay to return. A group that returns events none of which decrypt
/// is missing a commit. Told apart, each is actionable; conflated, all three are "no
/// messages".
/// </para>
/// <para>
/// Deliberately protocol-neutral — counts, epochs and hex strings, no engine types — so
/// it can cross into the ViewModels under the Dark Matter cutover rules.
/// </para>
/// </remarks>
public sealed class HistoryFetchReport
{
    /// <summary>Every routing address the group has used, current first, as hex.</summary>
    public List<string> KnownAddresses { get; init; } = new();

    /// <summary>The address the app had stored for this chat before the pass, as hex.</summary>
    public string? PreviouslySubscribedAddress { get; init; }

    /// <summary>
    /// Whether the stored address was not the group's current one.
    /// </summary>
    /// <remarks>
    /// The single most useful bit here. The app writes a chat's address once at creation
    /// and the group's address rotates, so a stale value means every filter it built was
    /// asking about somewhere the group no longer publishes.
    /// </remarks>
    public bool StoredAddressWasStale { get; init; }

    /// <summary>Messages stored for the chat before the pass.</summary>
    public int MessagesBefore { get; init; }

    /// <summary>Messages stored for the chat after waiting for the relay.</summary>
    public int MessagesAfter { get; init; }

    /// <summary>The group's epoch at the time of the pass.</summary>
    public ulong Epoch { get; init; }

    /// <summary>How long the pass waited for events to arrive and be processed.</summary>
    public TimeSpan Waited { get; init; }

    /// <summary>Set when the pass could not run at all, e.g. a chat with no MLS group.</summary>
    public string? Error { get; init; }

    /// <summary>Messages that arrived and were stored during the pass.</summary>
    public int Gained => MessagesAfter - MessagesBefore;

    /// <summary>
    /// One line a user can read, and act on.
    /// </summary>
    /// <remarks>
    /// Says what was asked and what came back, in that order, because "we asked the wrong
    /// place" and "we asked the right place and it was empty" are the two answers worth
    /// distinguishing and the counts alone do not distinguish them.
    /// </remarks>
    public string Summary
    {
        get
        {
            if (Error != null) return Error;

            var asked = KnownAddresses.Count == 1
                ? "1 address"
                : $"{KnownAddresses.Count} addresses";

            var stale = StoredAddressWasStale
                ? " The address this chat had stored was out of date, so earlier messages were never requested."
                : string.Empty;

            if (Gained > 0)
                return $"Asked {asked} for full history at epoch {Epoch}. Recovered {Gained} message(s).{stale}";

            if (KnownAddresses.Count > 1 || StoredAddressWasStale)
                return $"Asked {asked} for full history at epoch {Epoch}. Nothing new arrived in {Waited.TotalSeconds:0}s."
                       + stale
                       + " If messages exist, they are from epochs joined after they were sent and cannot be decrypted.";

            return $"Asked {asked} for full history at epoch {Epoch}. Nothing new arrived in {Waited.TotalSeconds:0}s — "
                   + "the relay has nothing for this group beyond what you already have.";
        }
    }
}
