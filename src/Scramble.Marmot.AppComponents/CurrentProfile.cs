namespace Scramble.Marmot.AppComponents;

/// <summary>
/// What a Current-profile GroupContext must look like, in terms this project
/// can check without an MLS library.
/// </summary>
/// <remarks>
/// The engine reads these four things off a real GroupContext and hands them
/// over. Keeping the validator on this side of the seam means the rules are
/// testable now, before any MLS type exists, and stay testable without building
/// a group to exercise them.
/// </remarks>
/// <param name="RequiredExtensionTypes">
/// The MLS extension types in <c>required_capabilities</c>.
/// </param>
/// <param name="RequiredProposalTypes">
/// The MLS proposal types in <c>required_capabilities</c>.
/// </param>
/// <param name="Dictionary">The GroupContext's <c>app_data_dictionary</c>.</param>
public sealed record GroupContextView(
    IReadOnlySet<ushort> RequiredExtensionTypes,
    IReadOnlySet<ushort> RequiredProposalTypes,
    AppDataDictionary Dictionary);

/// <summary>
/// The Current-profile group invariants.
/// </summary>
/// <remarks>
/// <para>
/// These hold on <b>every</b> commit's resulting epoch, not only on commits
/// that carry component bytes. A commit that drops <c>0x8003</c> from the
/// required set, deletes its dictionary entry, or removes a listed admin's last
/// member leaf is invalid whether or not it re-serialises any component — which
/// is why this validates a resulting state rather than a diff.
/// </para>
/// <para>
/// Legacy is not modelled. Scramble builds only the Current profile: the
/// account-identity proof moved from extension <c>0xf2f1</c> to component
/// <c>0x8009</c> with no fallback, and the decision not to implement the old
/// construction is settled.
/// </para>
/// </remarks>
public static class CurrentProfile
{
    /// <summary>
    /// MLS extension types a Current-profile group must require:
    /// <c>app_data_dictionary</c>.
    /// </summary>
    public static readonly IReadOnlySet<ushort> RequiredExtensionTypes =
        new HashSet<ushort> { AppDataDictionary.ExtensionType };

    /// <summary>
    /// MLS proposal types a Current-profile group must require:
    /// <c>app_data_update</c> (<c>0x0008</c>).
    /// </summary>
    /// <remarks>
    /// A hard blocker on both create and join — it is a
    /// <c>RequiredCapabilities</c> entry on every Current-profile group, so a
    /// client that cannot produce or verify the proposal cannot participate at
    /// all, not merely miss a feature.
    /// </remarks>
    public static readonly IReadOnlySet<ushort> RequiredProposalTypes =
        new HashSet<ushort> { 0x0008 };

    /// <summary>
    /// Components every Current-profile group must list as required:
    /// admin-policy and the account-identity proof.
    /// </summary>
    public static readonly IReadOnlySet<ushort> RequiredComponents =
        new HashSet<ushort> { AppComponent.GroupAdminPolicy, AppComponent.AccountIdentityProof };

    /// <summary>
    /// Required components that must also have GroupContext state: admin-policy.
    /// </summary>
    public static readonly IReadOnlySet<ushort> RequiredGroupStateComponents =
        new HashSet<ushort> { AppComponent.GroupAdminPolicy };

    /// <summary>
    /// Components that are required but live only in a LeafNode: the
    /// account-identity proof.
    /// </summary>
    /// <remarks>
    /// The proof binds one member's account key to one leaf's signature key, so
    /// it is meaningless as group state — and its presence in a GroupContext
    /// dictionary is an error rather than harmless clutter.
    /// </remarks>
    public static readonly IReadOnlySet<ushort> LeafOnlyComponents =
        new HashSet<ushort> { AppComponent.AccountIdentityProof };

    /// <summary>
    /// Component ids this implementation can validate as group state.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes the deferred components. A group requiring one of
    /// them is one we cannot honour, and saying so is the honest outcome — the
    /// alternative is joining and silently ignoring state the group considers
    /// mandatory.
    /// </remarks>
    public static readonly IReadOnlySet<ushort> KnownGroupComponents =
        new HashSet<ushort>
        {
            AppComponent.AppComponents,
            AppComponent.SafeAad,
            AppComponent.GroupProfile,
            AppComponent.GroupAdminPolicy,
            AppComponent.NostrRouting,
            AppComponent.MessageRetention,
        };

    /// <summary>
    /// Validates a resulting GroupContext and returns its required-component set.
    /// </summary>
    /// <param name="context">What the engine read off the GroupContext.</param>
    /// <param name="what">
    /// What is being validated, for the error message — "group", "Welcome",
    /// "staged commit".
    /// </param>
    /// <exception cref="AppComponentException">An invariant does not hold.</exception>
    public static IReadOnlySet<ushort> Validate(GroupContextView context, string what = "group")
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (ushort extensionType in RequiredExtensionTypes)
        {
            if (!context.RequiredExtensionTypes.Contains(extensionType))
            {
                throw new AppComponentException(
                    $"Invalid Current-profile {what}: extension 0x{extensionType:x4} " +
                    "(app_data_dictionary) is not a required capability.");
            }
        }

        foreach (ushort proposalType in RequiredProposalTypes)
        {
            if (!context.RequiredProposalTypes.Contains(proposalType))
            {
                throw new AppComponentException(
                    $"Invalid Current-profile {what}: proposal 0x{proposalType:x4} " +
                    "(app_data_update) is not a required capability.");
            }
        }

        IReadOnlySet<ushort> required = context.Dictionary.ComponentList()
            ?? throw new AppComponentException(
                $"Invalid Current-profile {what}: no app_components requirement list.");

        // Frozen, not deferred: a Current-profile group may neither require
        // 0x8008 nor hold its state, so this is checked before anything else
        // about the required set. Without it the id would fall through as an
        // unknown optional component and be carried silently.
        if (required.Contains(AppComponent.EncryptedMediaV1Frozen)
            || context.Dictionary.Contains(AppComponent.EncryptedMediaV1Frozen))
        {
            throw new AppComponentException(
                $"Invalid Current-profile {what}: the frozen encrypted-media component " +
                $"0x{AppComponent.EncryptedMediaV1Frozen:x4} is not permitted.");
        }

        foreach (ushort componentId in RequiredComponents)
        {
            if (!required.Contains(componentId))
            {
                throw new AppComponentException(
                    $"Invalid Current-profile {what}: component 0x{componentId:x4} is not required.");
            }
        }

        foreach (ushort componentId in LeafOnlyComponents)
        {
            if (context.Dictionary.Contains(componentId))
            {
                throw new AppComponentException(
                    $"Invalid Current-profile {what}: leaf-only component 0x{componentId:x4} " +
                    "appears in the GroupContext.");
            }
        }

        foreach (ushort componentId in RequiredGroupStateComponents)
        {
            if (!context.Dictionary.Contains(componentId))
            {
                throw new AppComponentException(
                    $"Invalid Current-profile {what}: required component 0x{componentId:x4} " +
                    "has no GroupContext state.");
            }
        }

        // Every component the group requires must be one we can actually
        // honour, and must carry state. Leaf-only components are exempt from
        // the state check by definition — their state lives on a leaf.
        foreach (ushort componentId in required)
        {
            if (LeafOnlyComponents.Contains(componentId))
                continue;

            if (!KnownGroupComponents.Contains(componentId))
            {
                throw new AppComponentException(
                    $"Invalid Current-profile {what}: required component 0x{componentId:x4} " +
                    "is not supported by this implementation.");
            }

            if (!context.Dictionary.Contains(componentId))
            {
                throw new AppComponentException(
                    $"Invalid Current-profile {what}: required component 0x{componentId:x4} " +
                    "has no GroupContext state.");
            }
        }

        // Every entry we recognise must decode. A dictionary carrying bytes
        // that never passed a component validator is how corrupt state would
        // otherwise reach the group.
        foreach (ushort componentId in context.Dictionary.ComponentIds)
        {
            if (KnownGroupComponents.Contains(componentId))
                ValidateComponentBytes(componentId, context.Dictionary.Get(componentId)!);
        }

        return required;
    }

    /// <summary>
    /// Decodes one component's bytes under its own schema, discarding the result.
    /// </summary>
    /// <exception cref="AppComponentException">The bytes are not valid for that component.</exception>
    public static void ValidateComponentBytes(ushort componentId, ReadOnlySpan<byte> data)
    {
        switch (componentId)
        {
            case AppComponent.AppComponents:
                ComponentCodec.DecodeComponentsList(data);
                break;
            case AppComponent.GroupProfile:
                GroupProfile.Decode(data);
                break;
            case AppComponent.GroupAdminPolicy:
                AdminPolicy.Decode(data);
                break;
            case AppComponent.NostrRouting:
                NostrRouting.Decode(data);
                break;
            case AppComponent.MessageRetention:
                MessageRetention.Decode(data);
                break;
            case AppComponent.SafeAad:
                // Known, and refused. The draft gives the component no
                // GroupContext payload, and upstream's validator answers
                // "safe_aad group-component state is not supported yet" for
                // exactly these bytes — so accepting them would put us in a
                // group every current peer rejects. Known-and-refused is not
                // the same as unknown: an unknown optional component stays
                // opaque, this one is an error.
                throw new AppComponentException(
                    "safe_aad (0x0002) has no GroupContext state in this profile.");
            default:
                throw new AppComponentException(
                    $"0x{componentId:x4} is not a component this implementation validates.");
        }
    }
}
