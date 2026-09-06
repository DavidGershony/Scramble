namespace Scramble.Marmot.Tests.Convergence;

/// <summary>What a published message is, for delivery and withholding.</summary>
public enum EnvelopeClass
{
    /// <summary>A Welcome, addressed to one invitee.</summary>
    Welcome,

    /// <summary>A handshake commit, for every current member.</summary>
    Commit,

    /// <summary>An application message, for every current member.</summary>
    App,
}

/// <summary>One published message.</summary>
/// <param name="Publication">
/// The scenario's label for the publication this belongs to. Withholding
/// selects on it, so one invite's commit and its Welcome are separable.
/// </param>
/// <param name="Class">Commit, Welcome or App.</param>
/// <param name="Sender">The publishing client.</param>
/// <param name="Recipient">For a Welcome, its invitee; null otherwise.</param>
/// <param name="Epoch">The epoch it was produced from.</param>
/// <param name="Payload">The MLS bytes.</param>
public sealed record Envelope(
    string Publication,
    EnvelopeClass Class,
    string Sender,
    string? Recipient,
    ulong Epoch,
    byte[] Payload)
{
    /// <summary>The withhold label currently holding this back, if any.</summary>
    public string? Withheld { get; set; }
}

/// <summary>
/// An in-memory relay that can be told to hold messages back.
/// </summary>
/// <remarks>
/// <para>
/// Withholding is the whole reason this exists rather than direct delivery.
/// Convergence only happens when members see different subsets of the same
/// history, and a scenario produces that by holding one commit back until the
/// others have moved on — which is exactly what a real relay does by accident,
/// and what a test cannot produce by being careful.
/// </para>
/// <para>
/// It carries MLS bytes rather than kind-445 envelopes. The transport wrap is
/// covered against a live peer by the interop suite; putting it here would mean
/// a released-late commit needs its old epoch's exporter secret to peel, which
/// is a different unbuilt feature and would make these scenarios fail for a
/// reason that has nothing to do with branch selection.
/// </para>
/// </remarks>
public sealed class ScenarioRelay
{
    private readonly List<Envelope> _published = [];
    private readonly Dictionary<string, HashSet<int>> _delivered = [];

    /// <summary>Everything published so far, in order.</summary>
    public IReadOnlyList<Envelope> Published => _published;

    /// <summary>Publishes a message.</summary>
    public void Publish(Envelope envelope) => _published.Add(envelope);

    /// <summary>Holds back every message matching a publication and class.</summary>
    public void Withhold(string label, string publication, EnvelopeClass envelopeClass)
    {
        foreach (Envelope envelope in _published)
        {
            if (string.Equals(envelope.Publication, publication, StringComparison.Ordinal)
                && envelope.Class == envelopeClass)
            {
                envelope.Withheld = label;
            }
        }
    }

    /// <summary>Releases everything held under a label.</summary>
    public void Release(string label)
    {
        foreach (Envelope envelope in _published)
        {
            if (string.Equals(envelope.Withheld, label, StringComparison.Ordinal))
                envelope.Withheld = null;
        }
    }

    /// <summary>
    /// Everything deliverable to a client that it has not already been handed.
    /// </summary>
    /// <remarks>
    /// A client is never handed its own message back. Our engine has no own-echo
    /// path yet, and a scenario that depended on one would be testing something
    /// this harness does not model.
    /// </remarks>
    public IReadOnlyList<Envelope> TakeFor(string client)
    {
        if (!_delivered.TryGetValue(client, out HashSet<int>? seen))
        {
            seen = [];
            _delivered[client] = seen;
        }

        var batch = new List<Envelope>();

        for (int i = 0; i < _published.Count; i++)
        {
            Envelope envelope = _published[i];

            if (envelope.Withheld is not null || seen.Contains(i))
                continue;

            if (string.Equals(envelope.Sender, client, StringComparison.Ordinal))
            {
                seen.Add(i);
                continue;
            }

            if (envelope.Recipient is { } recipient
                && !string.Equals(recipient, client, StringComparison.Ordinal))
            {
                continue;
            }

            seen.Add(i);
            batch.Add(envelope);
        }

        return batch;
    }
}
