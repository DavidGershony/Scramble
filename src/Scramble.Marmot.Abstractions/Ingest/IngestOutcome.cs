namespace Scramble.Marmot.Ingest;

/// <summary>Why an input never reached convergence admission.</summary>
/// <remarks>
/// Routing or deduplication proved it cannot affect this device's canonical
/// state. Distinct from <see cref="StaleReason"/>: nothing here is a judgement
/// about group history, only about whether the input was ever ours to consider.
/// </remarks>
public enum InputRejectionCategory
{
    /// <summary>Already ingested, by content id rather than transport id.</summary>
    Duplicate,

    /// <summary>Our own message, echoed back by the relay.</summary>
    OwnEcho,

    /// <summary>Addressed to somebody else.</summary>
    WrongRecipient,

    /// <summary>For a group this device does not have.</summary>
    UnknownGroup,

    /// <summary>Not decodable as the message it claims to be.</summary>
    InvalidEncoding,

    /// <summary>The signature does not verify.</summary>
    InvalidSignature,

    /// <summary>Requires something this build does not implement.</summary>
    UnsupportedRequiredFeature,

    /// <summary>The sender was not permitted to send it.</summary>
    AuthorizationFailed,
}

/// <summary>A local condition that blocked processing.</summary>
/// <remarks>
/// Deliberately separate from every other outcome: the message may be perfectly
/// valid and the group may be perfectly healthy. What is wrong is <i>this
/// device's</i> standing in the group, and no amount of retrying the message
/// changes that.
/// </remarks>
public enum LocalIngestState
{
    /// <summary>We were removed and have not confirmed a rejoin.</summary>
    RejoinConfirmationRequired,

    /// <summary>We are no longer a member of this group.</summary>
    Removed,

    /// <summary>The group is quarantined pending operator action.</summary>
    Quarantined,
}

/// <summary>Why a decodable, well-formed message was still not applied.</summary>
/// <remarks>
/// Every one of these is a statement about where the message sits relative to
/// group history. They are separated because a client logs and explains them
/// very differently — <see cref="LosingBranch"/> is a message that briefly
/// existed and stopped existing, while <see cref="PreMembership"/> is one that
/// was never ours to read.
/// </remarks>
public enum StaleReason
{
    /// <summary>Seen before.</summary>
    AlreadySeen,

    /// <summary>Addressed elsewhere.</summary>
    NotForThisClient,

    /// <summary>No such group here.</summary>
    UnknownGroup,

    /// <summary>Ours, echoed back.</summary>
    OwnEcho,

    /// <summary>From before this device joined; not decryptable and not a failure.</summary>
    PreMembership,

    /// <summary>Older than the earliest history this device retains.</summary>
    BeyondAnchor,

    /// <summary>Forks further back than the rewind horizon permits.</summary>
    BeyondRollbackHorizon,

    /// <summary>Older than the retained application-message window.</summary>
    BeyondAppRetention,

    /// <summary>Belongs to a branch convergence did not select.</summary>
    LosingBranch,

    /// <summary>Does not apply to the history this device actually holds.</summary>
    InvalidAgainstCanonicalState,

    /// <summary>Would remove us, and was not applied for that reason.</summary>
    SelfEvicted,

    /// <summary>Arrived while the group was quarantined.</summary>
    Quarantined,
}

/// <summary>Why a proposal failed Marmot admission.</summary>
public enum ProposalRejectionCategory
{
    /// <summary>The sender may not make this proposal.</summary>
    AuthorizationFailed,

    /// <summary>A proposal type this build cannot apply.</summary>
    UnsupportedProposal,

    /// <summary>Not decodable.</summary>
    InvalidEncoding,

    /// <summary>The signature does not verify.</summary>
    InvalidSignature,

    /// <summary>A self_remove that is not valid as one — inline, or not from its sender.</summary>
    InvalidSelfRemove,
}

/// <summary>
/// What ingesting one transport object did.
/// </summary>
/// <remarks>
/// <para>
/// <b>A refusal is not a failure, and the distinction is the reason this is a
/// type rather than a boolean.</b> Most of what a relay hands a client is
/// something it should decline: other people's messages, its own echoes,
/// duplicates, traffic from before it joined. Reporting those as errors makes a
/// working client look broken and buries the ones that are.
/// </para>
/// <para>
/// So the variants split by <i>what the caller should do</i>. Retry
/// <see cref="TransportDeferred"/> and <see cref="ResourceRefused"/> later.
/// Never retry <see cref="Ignored"/> or <see cref="Stale"/>. Surface
/// <see cref="LocalState"/> to the user, because no retry will fix it.
/// </para>
/// <para>
/// The shape mirrors upstream's <c>IngestOutcome</c>
/// (<c>traits/src/ingest.rs</c>) including the category names, so a disposition
/// can be compared across implementations without translating vocabulary.
/// Variants this build cannot yet produce are named in
/// <see cref="IngestOutcome"/>'s members rather than omitted — a missing
/// variant would silently become some other variant at the call site.
/// </para>
/// </remarks>
public abstract record IngestOutcome
{
    private IngestOutcome()
    {
    }

    /// <summary>Validated, applied, and group state advanced.</summary>
    public sealed record Processed(GroupId GroupId, EpochId Epoch) : IngestOutcome;

    /// <summary>
    /// Stored but not applied, because the group is mid-transition.
    /// </summary>
    /// <remarks>
    /// <b>This outcome promises a replay, and something must keep that
    /// promise.</b> A caller that returns Buffered without scheduling the group
    /// for a later drain has silently dropped the message while reporting that
    /// it kept it.
    /// </remarks>
    public sealed record Buffered(GroupId GroupId, EpochId Epoch) : IngestOutcome;

    /// <summary>Refused before it could affect anything. Never retry.</summary>
    public sealed record Ignored(InputRejectionCategory Category) : IngestOutcome;

    /// <summary>Blocked by this device's standing in the group.</summary>
    public sealed record LocalState(LocalIngestState State) : IngestOutcome;

    /// <summary>
    /// Could not be unwrapped with the transport context available now.
    /// </summary>
    /// <remarks>
    /// Not a rejection: a later epoch, a retained snapshot or a repair may make
    /// it readable. It stays outside convergence until it peels.
    /// </remarks>
    public sealed record TransportDeferred(GroupId GroupId) : IngestOutcome;

    /// <summary>
    /// A local bound stopped us retaining or processing it.
    /// </summary>
    /// <remarks>
    /// Explicitly <b>not</b> a protocol rejection, which is why it is its own
    /// variant: the same message arriving again must not then be dismissed as a
    /// duplicate of something we never actually kept.
    /// </remarks>
    public sealed record ResourceRefused(GroupId GroupId) : IngestOutcome;

    /// <summary>Well-formed, but not applicable to the history we hold.</summary>
    public sealed record Stale(StaleReason Reason) : IngestOutcome;

    /// <summary>A proposal that failed Marmot admission.</summary>
    public sealed record Rejected(ProposalRejectionCategory Category) : IngestOutcome;

    /// <summary>Whether the caller should try this input again later.</summary>
    /// <remarks>
    /// The one question every caller asks, answered here so each does not
    /// re-derive it from the variant list and get it subtly different.
    /// </remarks>
    public bool IsRetryable => this is TransportDeferred or ResourceRefused;

    /// <summary>Whether group state moved as a result.</summary>
    public bool Advanced => this is Processed;
}
