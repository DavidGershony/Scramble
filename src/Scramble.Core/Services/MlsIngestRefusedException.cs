using Scramble.Marmot.Ingest;

namespace Scramble.Core.Services;

/// <summary>
/// Thrown when the engine declined an inbound MLS message rather than delivering it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A refusal is not a failure, and the caller has to be able to tell.</b>
/// Most of what a relay hands a client is something it should decline — other
/// members' traffic, its own echoes, duplicates, commits from before it joined —
/// and <see cref="IngestOutcome"/> exists precisely so those are distinguishable
/// from a signature that does not verify. <c>IMlsService.DecryptMessageAsync</c>
/// has to answer with a message or an exception, so the classification travels
/// on the exception instead of being flattened into its text.
/// </para>
/// <para>
/// <b>Why not a substring match on the message.</b> That is what the app did
/// before the Dark Matter flip: it recognised the expected "commit from before I
/// joined" case by looking for <c>"UnprocessableResult"</c> and <c>"epoch"</c> in
/// marmot-cs's wording. A predicate keyed on another component's prose stops
/// matching the moment that component is replaced, and it does so silently — the
/// expected case starts being reported to the user as a decryption failure, which
/// is exactly what happened at the flip. The outcome is a value; classify on it.
/// </para>
/// </remarks>
public class MlsIngestRefusedException : InvalidOperationException
{
    /// <summary>What ingest decided about the message.</summary>
    public IngestOutcome Outcome { get; }

    public MlsIngestRefusedException(IngestOutcome outcome, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Outcome = outcome;
    }
}
