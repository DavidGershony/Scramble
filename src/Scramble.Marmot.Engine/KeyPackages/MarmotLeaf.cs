using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Identity;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Engine.KeyPackages;

/// <summary>
/// What a Current-profile Marmot LeafNode advertises and carries.
/// </summary>
/// <remarks>
/// <para>
/// Two surfaces, and they must agree. The leaf's <b>capabilities</b> say which
/// MLS extension and proposal types this client understands; the leaf's own
/// <b>app_data_dictionary</b> carries per-member state, of which the
/// account-identity proof is the one Marmot requires. A group's
/// <c>required_capabilities</c> is checked against the first, and the proof is
/// read out of the second, so a leaf that gets either wrong is refused at the
/// point of being added rather than later.
/// </para>
/// <para>
/// The values are read off <c>mdk</c> at <c>wn-agent-v0.9.15</c>
/// (<c>cgka-engine/src/capabilities.rs</c> <c>leaf_capabilities</c>,
/// <c>cgka-engine/src/app_components.rs</c>
/// <c>leaf_app_components_extension</c>) rather than inferred from the spec
/// prose, because the interop peer is that code. Note in particular that
/// <c>0x8009</c> is <b>not</b> an advertised extension capability in the
/// Current profile — that was the Legacy profile's shape, and advertising it
/// here would be a Legacy tell.
/// </para>
/// </remarks>
public static class MarmotLeaf
{
    /// <summary>MLS <c>required_capabilities</c>, RFC 9420 §17.3.</summary>
    public const ushort RequiredCapabilitiesExtensionType = 0x0003;

    /// <summary>The <c>app_data_update</c> proposal, draft-ietf-mls-extensions.</summary>
    public const ushort AppDataUpdateProposalType = 0x0008;

    /// <summary>The <c>self_remove</c> proposal, draft-ietf-mls-extensions.</summary>
    /// <remarks>
    /// <c>0x000a</c>, not <c>0x0009</c> — the two draft proposal types are not
    /// adjacent. Advertised because the reference client requires it of every
    /// member, and honestly so only since <c>v0.1.0-beta.11</c>: before that the
    /// library could not decode the type, so a commit carrying one would have
    /// stranded us at that epoch.
    /// </remarks>
    public const ushort SelfRemoveProposalType = 0x000a;

    /// <summary>
    /// MLS extension types a Current-profile leaf advertises.
    /// </summary>
    /// <remarks>
    /// Ascending, because upstream sorts and deduplicates before building the
    /// capabilities. Order is not signed as a set anywhere, but the kind-30443
    /// <c>mls_extensions</c> tag is built from this list and a stable order
    /// makes two publications of the same client byte-comparable.
    /// </remarks>
    public static readonly IReadOnlyList<ushort> ExtensionTypes =
        new[] { RequiredCapabilitiesExtensionType, MarmotDictionary.ExtensionType };

    /// <summary>MLS proposal types a Current-profile leaf advertises.</summary>
    public static readonly IReadOnlyList<ushort> ProposalTypes =
        new[] { AppDataUpdateProposalType, SelfRemoveProposalType };

    /// <summary>
    /// The app components this implementation can honour, before the two that
    /// are unioned in unconditionally.
    /// </summary>
    /// <remarks>
    /// Deliberately the same set the group-state validator will accept, so a
    /// leaf cannot advertise support for a component the engine would then
    /// refuse to join a group over. The deferred ids (media, QUIC, avatar,
    /// lifecycle) are absent from both.
    /// </remarks>
    public static IReadOnlySet<ushort> DefaultSupportedComponents =>
        CurrentProfile.KnownGroupComponents;

    /// <summary>
    /// The component ids a leaf advertises, given what this client supports.
    /// </summary>
    /// <remarks>
    /// <c>0x0001</c> and <c>0x8009</c> are unioned in rather than required of
    /// the caller: the first is the list's own id and the second is what makes
    /// the leaf a Marmot leaf at all. Upstream does the same, and a leaf
    /// missing either is refused by the kind-30443 codec before it reaches a
    /// relay.
    /// </remarks>
    public static IReadOnlySet<ushort> AdvertisedComponents(IReadOnlySet<ushort> supported)
    {
        ArgumentNullException.ThrowIfNull(supported);

        return new SortedSet<ushort>(supported)
        {
            AppComponent.AppComponents,
            AccountIdentityProof.ComponentId,
        };
    }

    /// <summary>
    /// The subset of an advertised set that belongs in the kind-30443
    /// <c>app_components</c> tag.
    /// </summary>
    /// <remarks>
    /// <b>Private-use ids only.</b> The tag must match the decoded leaf's
    /// components <i>exactly</i> — a peer recomputes it and refuses the whole
    /// publication on any difference ("app_components tag does not exactly match
    /// decoded KeyPackage metadata") — but it recomputes it from the leaf list
    /// filtered to <c>&gt;= 0x8000</c>. So <c>0x0001</c> and <c>0x0002</c> live
    /// in the leaf dictionary and must <b>not</b> appear in the tag. Emitting
    /// them makes the account unresolvable: no peer will invite it, and the
    /// failure surfaces as a group that never arrives rather than as a rejected
    /// publish.
    /// </remarks>
    public static IReadOnlySet<ushort> TaggedComponents(IReadOnlySet<ushort> advertised)
    {
        ArgumentNullException.ThrowIfNull(advertised);

        return new SortedSet<ushort>(advertised.Where(AppComponent.IsPrivateUse));
    }

    /// <summary>
    /// Builds the leaf's <c>app_data_dictionary</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three entries, and the middle one surprises people. <c>0x0002</c>
    /// (<c>safe_aad</c>) carries an <b>empty</b> component list here: it names
    /// the components whose messages this leaf protects with safe AAD, and
    /// Marmot v1 has none. Its presence with empty contents is what upstream
    /// emits, and it is not the same thing as <c>safe_aad</c> appearing in a
    /// <i>GroupContext</i> dictionary, which is an error.
    /// </para>
    /// <para>
    /// The proof is required, not optional. A Current-profile leaf without one
    /// is rejected by every peer, so there is no useful "build it without and
    /// attach later" shape to offer.
    /// </para>
    /// </remarks>
    public static MarmotDictionary BuildDictionary(
        IReadOnlySet<ushort> supported, AccountIdentityProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);

        var dictionary = new MarmotDictionary();
        dictionary.SetComponentList(AdvertisedComponents(supported));
        dictionary.Set(AppComponent.SafeAad, ComponentCodec.EncodeComponentsList(new HashSet<ushort>()));
        dictionary.Set(AccountIdentityProof.ComponentId, proof.Encode());
        return dictionary;
    }

    /// <summary>
    /// Wraps a dictionary as the MLS leaf extension that carries it.
    /// </summary>
    public static Extension ToExtension(MarmotDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        return new Extension(MarmotDictionary.ExtensionType, dictionary.Encode());
    }

    /// <summary>
    /// The <c>last_resort_key_package</c> component, draft-ietf-mls-extensions.
    /// </summary>
    /// <remarks>
    /// Lives in the <b>KeyPackage-level</b> <c>app_data_dictionary</c>, not the
    /// leaf's, and its data must be <b>empty</b> — OpenMLS treats non-empty data
    /// there as malformed. It is not the obsolete <c>0x000a</c> extension, and
    /// it is not a leaf extension; both of those readings are wrong and the
    /// second is the tempting one, since every other dictionary we build is a
    /// leaf's.
    /// </remarks>
    public const ushort LastResortComponentId = 0x0004;

    /// <summary>
    /// The KeyPackage-level extension marking a KeyPackage last-resort.
    /// </summary>
    /// <remarks>
    /// A KeyPackage published to a relay is fetched by anyone who wants to
    /// invite us, and kind 30443 is addressable — one live event per slot. So
    /// several people can invite us off one publication, and without this marker
    /// only the first Welcome could be opened. The marker is what says the
    /// KeyPackage is reusable; retaining the private material past first use is
    /// then a storage decision, which
    /// <see cref="Scramble.Marmot.Storage.KeyPackageRecord.LastResort"/> carries.
    /// </remarks>
    public static Extension LastResortExtension()
    {
        var dictionary = new MarmotDictionary();
        dictionary.Set(LastResortComponentId, []);
        return new Extension(MarmotDictionary.ExtensionType, dictionary.Encode());
    }

    /// <summary>
    /// Whether a KeyPackage's own extensions mark it last-resort.
    /// </summary>
    /// <remarks>
    /// Non-empty data under the id is <b>not</b> last-resort: OpenMLS calls that
    /// malformed rather than true, so reading it as a presence flag would accept
    /// a KeyPackage the peer refuses.
    /// </remarks>
    public static bool IsLastResort(KeyPackage keyPackage)
    {
        ArgumentNullException.ThrowIfNull(keyPackage);

        foreach (var extension in keyPackage.Extensions)
        {
            if (extension.ExtensionType != MarmotDictionary.ExtensionType)
                continue;

            byte[]? marker = MarmotDictionary.Decode(extension.ExtensionData)
                .Get(LastResortComponentId);

            if (marker is { Length: 0 })
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a leaf's <c>app_data_dictionary</c>, or null when it carries none.
    /// </summary>
    /// <exception cref="AppComponentException">The extension data is malformed.</exception>
    public static MarmotDictionary? ReadDictionary(LeafNode leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);

        foreach (var extension in leaf.Extensions)
        {
            if (extension.ExtensionType == MarmotDictionary.ExtensionType)
                return MarmotDictionary.Decode(extension.ExtensionData);
        }

        return null;
    }
}
