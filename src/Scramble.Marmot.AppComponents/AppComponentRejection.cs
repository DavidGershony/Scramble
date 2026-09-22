namespace Scramble.Marmot.AppComponents;

/// <summary>
/// Why a component or capability refusal happened, in a form a caller can
/// branch on.
/// </summary>
/// <remarks>
/// <para>
/// <b>An exception message is for a human reading a log; it is not a
/// contract.</b> A caller that has to tell "this invitee cannot join, because
/// their leaf does not advertise something this group requires" from every
/// other refusal would otherwise have to match on substrings — and those
/// sentences get rewritten whenever somebody improves one. That is the failure
/// this prevents: a UI branch that silently stops matching the day a message is
/// reworded, with no test and no compiler to notice.
/// </para>
/// <para>
/// It also has to cross a boundary. <c>Scramble.Core</c> is where this gets
/// turned into something a UI can render (P11), and the cutover rules stop
/// <c>Scramble.Marmot</c> types at the service layer — so the reason must be
/// translatable into a protocol-neutral model. An enum with a stable token is;
/// a sentence is not.
/// </para>
/// <para>
/// <b>Only what this build actually produces is named.</b> Upstream's
/// <c>EngineError</c> (<c>crates/traits/src/error.rs</c>) carries twenty-odd
/// variants — hydration, disband, forked epochs, storage — that are either not
/// ours or never raised through this exception. Copying the list would leave
/// names no code path can reach, and a caller writing a branch for one would
/// never exercise it. <c>IngestOutcome.cs</c> deliberately does the opposite
/// for its own variants, and the reason it gives is exactly the test to apply
/// here: there, an omitted variant would be <i>collapsed into a neighbouring
/// one</i> at the call site and quietly mis-handled. Here an omitted reason
/// stays <see cref="Unclassified"/>, which claims nothing.
/// </para>
/// </remarks>
public enum AppComponentRejection
{
    /// <summary>
    /// No machine-readable reason. Terminal, and unexplained beyond its message.
    /// </summary>
    /// <remarks>
    /// The default, and — unlike upstream's <c>EngineError::Other</c>, whose
    /// doc says it "should be empty in practice" — this one is genuinely
    /// populated: most refusals in this assembly are malformed-bytes and
    /// invalid-commit errors that no caller branches on, and giving each a
    /// token would be inventing a taxonomy rather than matching one. A caller
    /// must treat it as "refused, and I cannot say more", never as a default
    /// that happens to mean something.
    /// </remarks>
    Unclassified = 0,

    /// <summary>
    /// Somebody does not support something a group requires of its members.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream: <c>EngineError::MissingRequiredCapabilities { required, had }</c>,
    /// token <c>missing_required_capabilities</c>.
    /// </para>
    /// <para>
    /// <b>One reason covers both directions, because upstream's does.</b>
    /// <c>crates/cgka-engine/src/group_lifecycle.rs</c> raises this same variant
    /// for an invitee whose KeyPackage lacks what the group requires
    /// (<c>required_caps.missing_from(&amp;had)</c>) and for <i>us</i> being
    /// handed a group that requires what this build cannot honour
    /// (<c>self_missing</c>). Splitting them would produce a token no upstream
    /// row can carry, which is the opposite of matching a taxonomy. Which side
    /// is at fault is recoverable from the call that failed: validating an
    /// invitee blames the invitee, validating a group we joined blames us.
    /// </para>
    /// <para>
    /// MLS version and ciphersuite advertisements fold in here too, although
    /// upstream's <c>GroupCapabilities</c> has no field for either. The refusal
    /// is the same one in the same place — a leaf that does not advertise what
    /// membership demands — and giving it a private token would claim a
    /// distinction upstream does not make.
    /// </para>
    /// </remarks>
    MissingRequiredCapabilities = 1,
}

/// <summary>
/// The stable token for an <see cref="AppComponentRejection"/>.
/// </summary>
/// <remarks>
/// Mirrors upstream's <c>EngineError::privacy_safe_kind()</c>
/// (<c>crates/traits/src/error.rs</c>), whose own doc explains what the token
/// buys: it "lives beside the enum rather than in any one consumer so the
/// engine's forensic audit rows, the app runtime's tracing fields, and the
/// daemon's status JSON all say the same word for the same failure and an
/// operator can join them." Two implementations of one protocol have the same
/// problem one level up — when a peer refuses us and we refuse a peer, the two
/// reports should use one word — so the tokens here are upstream's spelling,
/// not a parallel vocabulary.
///
/// It is also the shape that survives the <c>Scramble.Presentation</c>
/// boundary: a string is not a Marmot type.
/// </remarks>
public static class AppComponentRejections
{
    /// <summary>The token for a reason.</summary>
    public static string Kind(AppComponentRejection reason) => reason switch
    {
        AppComponentRejection.Unclassified => "unclassified",
        AppComponentRejection.MissingRequiredCapabilities => "missing_required_capabilities",

        // Unreachable for any declared value; it exists so that adding a reason
        // without giving it a token fails loudly here rather than shipping a
        // refusal whose token is some neighbouring reason's.
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "No token is defined for this rejection reason."),
    };
}
