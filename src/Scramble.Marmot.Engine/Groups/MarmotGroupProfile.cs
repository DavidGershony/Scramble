using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Identity;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Engine.Groups;

/// <summary>
/// The component set a new Current-profile group requires, and the initial
/// GroupContext state that goes with it.
/// </summary>
/// <remarks>
/// <para>
/// Split from the builder so the rules are testable without constructing an MLS
/// group — the same reason <see cref="CurrentProfile"/> takes a
/// <see cref="GroupContextView"/> rather than a real GroupContext.
/// </para>
/// <para>
/// Read off <c>do_create_group</c> and
/// <c>app_data_dictionary_extension_for_group</c> at <c>wn-agent-v0.9.10</c>.
/// </para>
/// </remarks>
public static class MarmotGroupProfile
{
    /// <summary>
    /// The components a new group requires before negotiation.
    /// </summary>
    /// <remarks>
    /// <c>default_group_components()</c> plus the account-identity proof, which
    /// the Current profile adds. Note <c>0x800c</c> is here: it is not a
    /// <i>profile</i>-required component, but every group upstream creates
    /// requires it — the distinction that cost us a phase (see the handoff §3h).
    /// </remarks>
    public static readonly IReadOnlySet<ushort> DefaultComponents =
        new SortedSet<ushort>
        {
            AppComponent.GroupProfile,
            AppComponent.GroupAdminPolicy,
            AppComponent.NostrRouting,
            AppComponent.GroupLifecycle,
            AccountIdentityProof.ComponentId,
        };

    /// <summary>
    /// Components that must survive negotiation, whatever the members advertise.
    /// </summary>
    /// <remarks>
    /// <b>The reason this is not simply "whatever everyone supports."</b> A group
    /// created without admin-policy bytes has an empty admin set and therefore
    /// frozen membership: every admin-gated operation and every later join fails
    /// closed, permanently. Letting one under-advertising member negotiate that
    /// away produces a group that cannot be repaired, so such a member is
    /// refused instead (mdk#746). The same argument covers the profile, the
    /// lifecycle state and the proof.
    /// </remarks>
    public static IReadOnlySet<ushort> MandatoryComponents => DefaultComponents;

    /// <summary>
    /// One prospective member's advertised component set.
    /// </summary>
    /// <param name="Label">
    /// How to name this member in an error — an npub, a hex account key, or
    /// "creator". Carried so a refusal says <i>who</i> to drop from the invite,
    /// which is the only thing the caller can act on.
    /// </param>
    /// <param name="Components">What their leaf advertises.</param>
    public sealed record MemberComponents(string Label, IReadOnlySet<ushort> Components);

    /// <summary>
    /// Narrows the desired component set to what every member can honour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two checks, and they are not redundant even though both refuse the same
    /// inputs. The per-member guard names the member at fault, which is what
    /// lets a caller drop one invitee and retry; the post-condition names only
    /// the component, and exists so a future refactor that loses the guard
    /// still cannot produce an unusable group. <b>For a single member they
    /// catch the same thing</b> — the guard earns its place once there are
    /// invitees.
    /// </para>
    /// </remarks>
    /// <param name="desired">What the creator wants the group to require.</param>
    /// <param name="members">Every member's advertised set, the creator's included.</param>
    /// <exception cref="AppComponentException">
    /// A member does not advertise a mandatory component.
    /// </exception>
    public static IReadOnlySet<ushort> Negotiate(
        IReadOnlySet<ushort> desired, IEnumerable<MemberComponents> members)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(members);

        var negotiated = new SortedSet<ushort>(desired);

        foreach (MemberComponents member in members)
        {
            foreach (ushort mandatory in MandatoryComponents)
            {
                if (!member.Components.Contains(mandatory))
                {
                    throw new AppComponentException(
                        $"Member {member.Label} does not advertise mandatory component " +
                        $"0x{mandatory:x4}, so it cannot be negotiated out and they cannot join.");
                }
            }

            negotiated.IntersectWith(member.Components);
        }

        foreach (ushort mandatory in MandatoryComponents)
        {
            if (!negotiated.Contains(mandatory))
            {
                throw new AppComponentException(
                    $"Mandatory component 0x{mandatory:x4} was negotiated out of a new group.");
            }
        }

        return negotiated;
    }

    /// <summary>
    /// Builds the initial GroupContext <c>app_data_dictionary</c>.
    /// </summary>
    /// <remarks>
    /// A component's state is written only when the group actually requires it,
    /// which is why this takes the negotiated set rather than the desired one.
    /// The account-identity proof is required but gets no entry: it is
    /// LeafNode-only, and its presence in a GroupContext dictionary is an error
    /// rather than redundancy.
    /// </remarks>
    /// <param name="required">The negotiated required-component set.</param>
    /// <param name="name">Group name, for <c>0x8001</c>.</param>
    /// <param name="description">Group description, for <c>0x8001</c>.</param>
    /// <param name="admins">Admin account keys, for <c>0x8003</c>.</param>
    /// <param name="routing">
    /// Transport routing, for <c>0x8004</c>: the <c>nostr_group_id</c> every
    /// kind-445 message for this group is addressed to, and the relays it lives
    /// on.
    /// </param>
    public static MarmotDictionary BuildDictionary(
        IReadOnlySet<ushort> required,
        string name,
        string description,
        IEnumerable<byte[]> admins,
        NostrRouting routing)
    {
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(admins);
        ArgumentNullException.ThrowIfNull(routing);

        var dictionary = new MarmotDictionary();
        dictionary.SetComponentList(required);

        if (required.Contains(AppComponent.GroupProfile))
            dictionary.Set(AppComponent.GroupProfile, new GroupProfile(name, description).Encode());

        if (required.Contains(AppComponent.GroupAdminPolicy))
            dictionary.Set(AppComponent.GroupAdminPolicy, AdminPolicy.Create(admins).Encode());

        // Not optional, and its absence is not a missing feature. A peer reads
        // the transport group id and relays out of this component and nothing
        // else, so a group without it cannot be addressed at all — the reference
        // client refuses it outright with "group is missing
        // marmot.transport.nostr.routing.v1".
        if (required.Contains(AppComponent.NostrRouting))
            dictionary.Set(AppComponent.NostrRouting, routing.Encode());

        if (required.Contains(AppComponent.GroupLifecycle))
        {
            dictionary.Set(
                AppComponent.GroupLifecycle,
                GroupLifecycle.Encode(GroupLifecycleState.Active));
        }

        return dictionary;
    }

    /// <summary>
    /// The <c>required_capabilities</c> a new Current-profile group carries.
    /// </summary>
    /// <remarks>
    /// Extension <c>app_data_dictionary</c> and proposal <c>app_data_update</c>,
    /// and nothing else. The required <i>components</i> do not appear here —
    /// they live in the dictionary's own requirement list, which is the part
    /// that trips people: <c>required_capabilities</c> is MLS's vocabulary and
    /// carries no component ids at all.
    /// </remarks>
    public static RequiredCapabilities BuildRequiredCapabilities() =>
        RequiredCapabilities.Create(
            CurrentProfile.RequiredExtensionTypes,
            CurrentProfile.RequiredProposalTypes);
}
