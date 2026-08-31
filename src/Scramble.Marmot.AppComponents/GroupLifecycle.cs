namespace Scramble.Marmot.AppComponents;

/// <summary>
/// Whether a group is still live.
/// </summary>
/// <remarks>
/// Terminal in one direction: a disbanded group never becomes active again.
/// The transition rule is a commit-time concern and lives with the engine; this
/// type is only the state and its encoding.
/// </remarks>
public enum GroupLifecycleState
{
    Active = 0,
    Disbanded = 1,
}

/// <summary>
/// <c>marmot.group.lifecycle.v1</c> (<c>0x800c</c>) — one byte saying whether
/// the group is active or disbanded.
/// </summary>
/// <remarks>
/// <para>
/// <b>This component was deferred at P4 and that was wrong.</b> It looked
/// heavyweight because the disband <i>protocol</i> is, and because
/// <c>group-lifecycle-v1.md</c> is the one v1 document that mentions PSKs. The
/// state itself is a single byte, and it is not optional:
/// <c>default_group_components()</c> lists it beside the group profile and the
/// admin policy, so <b>every group the reference implementation creates
/// requires it</b>, and <c>do_create_group</c> refuses any invitee whose leaf
/// does not advertise it. Deferring it did not defer a feature — it made
/// create, join and invite impossible in both directions.
/// </para>
/// <para>
/// Encoding is exact and unusually strict, so the obvious permissive reading is
/// wrong in three ways: the payload is <b>one byte</b>, an empty payload is an
/// error rather than a default, an unknown value is an error rather than
/// something to carry opaquely, and a trailing byte is an error rather than
/// padding. Read off
/// <c>crates/traits/src/app_components/lifecycle.rs</c> at
/// <c>wn-agent-v0.9.10</c>, whose own test pins all four.
/// </para>
/// <para>
/// Note it is <b>not</b> a Current-profile <i>required</i> component the way
/// <c>0x8003</c> and <c>0x8009</c> are — upstream's
/// <c>CURRENT_PROFILE_REQUIRED_APP_COMPONENTS</c> lists only those two. It
/// becomes required through group creation rather than through the profile, so
/// a group that does not require it is still valid and must not be refused.
/// </para>
/// </remarks>
public static class GroupLifecycle
{
    /// <summary>The component id.</summary>
    /// <remarks>
    /// Aliases <see cref="AppComponent.GroupLifecycle"/> rather than repeating
    /// the literal. Every id in the registry traces to one line of
    /// <c>crates/traits/src/app_components/mod.rs</c>, and a second copy here
    /// would be a second thing to get wrong.
    /// </remarks>
    public const ushort ComponentId = AppComponent.GroupLifecycle;

    /// <summary>The schema name.</summary>
    public const string Schema = AppComponent.GroupLifecycleSchema;

    /// <summary>Encodes the state as its single byte.</summary>
    public static byte[] Encode(GroupLifecycleState state) => state switch
    {
        GroupLifecycleState.Active => [0],
        GroupLifecycleState.Disbanded => [1],
        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state, "Unknown group-lifecycle state."),
    };

    /// <summary>
    /// Decodes the single byte.
    /// </summary>
    /// <remarks>
    /// Every rejection below is a case a lenient decoder would wave through, and
    /// each would leave us disagreeing with every peer about signed group state.
    /// An unknown value in particular must not be treated as "some future state,
    /// carry it": this component decides whether the group is usable at all, so
    /// guessing is worse than refusing.
    /// </remarks>
    /// <exception cref="AppComponentException">Empty, unknown, or over-long.</exception>
    public static GroupLifecycleState Decode(ReadOnlySpan<byte> data) => data switch
    {
        [0] => GroupLifecycleState.Active,
        [1] => GroupLifecycleState.Disbanded,
        [] => throw new AppComponentException("The group-lifecycle state is empty."),
        [var value] => throw new AppComponentException(
            $"The group-lifecycle state 0x{value:x2} is unknown."),
        _ => throw new AppComponentException("The group-lifecycle state has trailing bytes."),
    };

    /// <summary>Whether a group in this state still accepts commits.</summary>
    public static bool IsActive(GroupLifecycleState state) => state == GroupLifecycleState.Active;
}
