namespace Scramble.Marmot.AppComponents;

/// <summary>
/// One <c>AppDataUpdate</c> proposal's operation: new bytes for a component,
/// or its removal.
/// </summary>
/// <remarks>
/// <see cref="Data"/> is null for a removal, and that null is load-bearing —
/// <see cref="AppComponentIntegrity"/> matches a dictionary entry that
/// disappeared against exactly this shape. An empty array is a different thing:
/// a component whose state is zero bytes.
/// </remarks>
public sealed class AppDataUpdate
{
    private AppDataUpdate(ushort componentId, byte[]? data)
    {
        ComponentId = componentId;
        Data = data;
    }

    /// <summary>The component this operation targets.</summary>
    public ushort ComponentId { get; }

    /// <summary>The resulting bytes, or null for a removal.</summary>
    public byte[]? Data { get; }

    /// <summary>Whether this operation removes the component's state.</summary>
    public bool IsRemove => Data is null;

    /// <summary>An operation writing <paramref name="data"/> to a component.</summary>
    public static AppDataUpdate Update(ushort componentId, ReadOnlySpan<byte> data) =>
        new(componentId, data.ToArray());

    /// <summary>An operation removing a component's state.</summary>
    public static AppDataUpdate Remove(ushort componentId) => new(componentId, null);
}

/// <summary>
/// What a commit is allowed to do to the GroupContext's component state.
/// </summary>
/// <remarks>
/// <para>
/// MLS's own guard checks the resulting dictionary against a commit's
/// <c>AppDataUpdate</c> proposals and <b>returns early when there are none</b>.
/// So a commit that carries no <c>AppDataUpdate</c> at all — a
/// <c>GroupContextExtensions</c> proposal, say — can hand over a resulting
/// GroupContext whose extensions were rewritten wholesale, and MLS will accept
/// it. That is the hole this closes. The two shapes it stops:
/// </para>
/// <list type="bullet">
/// <item>the <c>app_data_dictionary</c> extension, or a required component's
/// entry, simply disappearing — leaving a group with no admin list, which
/// freezes every admin-gated operation permanently, with no way back;</item>
/// <item>every entry staying present while its bytes are replaced — swapping
/// the admin set, or writing profile/routing/retention state that never passed
/// a component validator.</item>
/// </list>
/// <para>
/// The rule is therefore about the <i>diff</i>, and it is a whitelist: any
/// entry whose bytes differ from the current epoch's must be accounted for by
/// one of this commit's own <c>AppDataUpdate</c> operations. A change nobody
/// proposed is not a change this member accepts.
/// </para>
/// <para>
/// <b>This pairs with <see cref="ValidateUpdateBatch"/> and does not replace
/// it.</b> The integrity rule proves a change was proposed; the batch rule
/// proves what was proposed decodes and is legal to remove. Run both on every
/// staged commit — either alone leaves half the door open, since an
/// <c>AppDataUpdate</c> carrying corrupt bytes is perfectly "update-backed".
/// </para>
/// </remarks>
public static class AppComponentIntegrity
{
    /// <summary>
    /// The deferred group-lifecycle component, named here only to refuse its
    /// removal.
    /// </summary>
    /// <remarks>
    /// Its state is not something this implementation writes or understands, but
    /// "remove it" is a decision, and upstream refuses it. A group we would not
    /// join for requiring 0x800c is a separate matter from a commit that strips
    /// it out from under members who do support it.
    /// </remarks>

    /// <summary>
    /// Checks a staged commit's resulting component state against the current
    /// epoch's.
    /// </summary>
    /// <param name="commit">
    /// The staged commit. Its <c>AppDataUpdate</c> proposals are the only
    /// authority for a changed entry.
    /// </param>
    /// <param name="current">
    /// The current epoch's GroupContext dictionary, or null if it has none.
    /// </param>
    /// <param name="resulting">
    /// The dictionary the commit's resulting GroupContext would carry, or null
    /// if the commit drops the extension.
    /// </param>
    /// <exception cref="AppComponentException">The commit is not permitted.</exception>
    public static void ValidateStagedCommit(
        StagedCommitView commit,
        AppDataDictionary? current,
        AppDataDictionary? resulting)
    {
        ArgumentNullException.ThrowIfNull(commit);

        if (current is not null && resulting is null)
        {
            throw new AppComponentException(
                "The commit is invalid: its resulting GroupContext drops the app_data_dictionary.");
        }

        IReadOnlySet<ushort> protectedIds = ProtectedComponents(resulting);

        foreach (ushort componentId in protectedIds)
        {
            // The account proof is required by the GroupContext and stored on a
            // LeafNode, so there is no group entry of it to protect. Requiring
            // one here would reject every conformant commit.
            if (CurrentProfile.LeafOnlyComponents.Contains(componentId))
                continue;

            if (resulting?.Contains(componentId) != true)
            {
                throw new AppComponentException(
                    $"The commit is invalid: its resulting GroupContext drops required " +
                    $"component 0x{componentId:x4}.");
            }
        }

        // Keyed by component id because one id may legitimately carry several
        // operations; any one of them may account for the resulting value.
        var operations = new Dictionary<ushort, List<AppDataUpdate>>();
        foreach (AppDataUpdate update in UpdatesOf(commit))
        {
            if (!operations.TryGetValue(update.ComponentId, out List<AppDataUpdate>? forId))
                operations[update.ComponentId] = forId = [];

            forId.Add(update);
        }

        var componentIds = new SortedSet<ushort>();
        foreach (ushort id in current?.ComponentIds ?? [])
            componentIds.Add(id);
        foreach (ushort id in resulting?.ComponentIds ?? [])
            componentIds.Add(id);

        foreach (ushort componentId in componentIds)
        {
            byte[]? before = current?.Get(componentId);
            byte[]? after = resulting?.Get(componentId);

            if (SameBytes(before, after))
                continue;

            bool proposed =
                operations.TryGetValue(componentId, out List<AppDataUpdate>? forId)
                && forId.Exists(operation => SameBytes(operation.Data, after));

            if (!proposed)
            {
                throw new AppComponentException(
                    $"The commit is invalid: its resulting GroupContext changes component " +
                    $"0x{componentId:x4} outside an AppDataUpdate proposal.");
            }
        }
    }

    /// <summary>
    /// Validates all of a commit's <c>AppDataUpdate</c> operations together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Removability is decided against the <i>resulting</i> required set, not
    /// the current one. That is what lets one commit atomically unrequire a
    /// component and remove its state — legal, and indistinguishable from an
    /// illegal removal if you look only at the epoch you are leaving.
    /// </para>
    /// <para>
    /// Which is why this is a batch and not a per-proposal check: the operation
    /// that rewrites the requirement list may arrive after the removal it
    /// authorises, so the list is resolved across the whole batch first and the
    /// operations judged against it second.
    /// </para>
    /// </remarks>
    /// <param name="commit">The staged commit.</param>
    /// <param name="currentRequired">
    /// The current epoch's required-component set, which stands unless one of
    /// these operations replaces it.
    /// </param>
    /// <returns>The required-component set the batch results in.</returns>
    /// <exception cref="AppComponentException">An operation is not permitted.</exception>
    public static IReadOnlySet<ushort> ValidateUpdateBatch(
        StagedCommitView commit,
        IReadOnlySet<ushort> currentRequired)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(currentRequired);

        List<AppDataUpdate> updates = [.. UpdatesOf(commit)];
        IReadOnlySet<ushort> resultingRequired = currentRequired;
        var seen = new HashSet<ushort>();

        foreach (AppDataUpdate update in updates)
        {
            if (!seen.Add(update.ComponentId))
            {
                // Two operations on one component in one commit leave the
                // resulting value dependent on an ordering the wire format does
                // not fix, so members could disagree about it.
                throw new AppComponentException(
                    $"The commit carries more than one AppDataUpdate operation for " +
                    $"component 0x{update.ComponentId:x4}.");
            }

            if (update.ComponentId != AppComponent.AppComponents)
                continue;

            if (update.IsRemove)
            {
                throw new AppComponentException(
                    "The app_components requirement list cannot be removed.");
            }

            resultingRequired = ComponentCodec.DecodeComponentsList(update.Data);
        }

        foreach (AppDataUpdate update in updates)
        {
            if (update.IsRemove)
                ValidateRemoval(update.ComponentId, resultingRequired);
            else
                ValidateGroupStateBytes(update.ComponentId, update.Data);
        }

        return resultingRequired;
    }

    /// <summary>
    /// Whether a component's state may be removed, given the resulting
    /// required-component set.
    /// </summary>
    /// <exception cref="AppComponentException">It may not.</exception>
    public static void ValidateRemoval(ushort componentId, IReadOnlySet<ushort> resultingRequired)
    {
        ArgumentNullException.ThrowIfNull(resultingRequired);

        switch (componentId)
        {
            case AppComponent.AppComponents:
                throw new AppComponentException(
                    "The app_components requirement list cannot be removed.");
            case AppComponent.SafeAad:
                throw new AppComponentException(
                    "safe_aad (0x0002) has no GroupContext state in this profile.");
            case AppComponent.GroupLifecycle:
                throw new AppComponentException(
                    $"The group-lifecycle component 0x{AppComponent.GroupLifecycle:x4} cannot be removed.");
        }

        if (resultingRequired.Contains(componentId))
        {
            throw new AppComponentException(
                $"Component 0x{componentId:x4} is still required in the resulting epoch, " +
                "so its GroupContext state cannot be removed.");
        }
    }

    /// <summary>
    /// Decodes bytes an operation would write into the GroupContext dictionary.
    /// </summary>
    /// <remarks>
    /// An id this implementation does not know stays <b>opaque and accepted</b>.
    /// That is deliberate and it is the opposite of the required-set rule, which
    /// rejects an unknown component: refusing a commit because it touches an
    /// optional component we have not heard of would strand us outside a group
    /// every other member is happily still in — whereas joining a group that
    /// <i>requires</i> something we cannot honour is a lie about our
    /// capabilities. Refused ids are refused because a peer would refuse them
    /// too, not because they are unfamiliar.
    /// </remarks>
    /// <exception cref="AppComponentException">
    /// The bytes do not decode, or the component may not hold group state.
    /// </exception>
    public static void ValidateGroupStateBytes(ushort componentId, ReadOnlySpan<byte> data)
    {
        switch (componentId)
        {
            case AppComponent.AccountIdentityProof:
                throw new AppComponentException(
                    "The account-identity proof is LeafNode-only and has no GroupContext state.");
            case AppComponent.EncryptedMediaV1Frozen:
                throw new AppComponentException(
                    $"The frozen encrypted-media component 0x{componentId:x4} is not permitted.");
        }

        if (CurrentProfile.KnownGroupComponents.Contains(componentId))
            CurrentProfile.ValidateComponentBytes(componentId, data);
    }

    /// <summary>
    /// The <c>AppDataUpdate</c> operations a commit carries.
    /// </summary>
    /// <remarks>
    /// Fails closed on a proposal classified as an <c>AppDataUpdate</c> whose
    /// operation was never read off the wire. Treating that as "no operation"
    /// would make the proposal invisible to both rules here — and an
    /// unaccounted-for dictionary change is exactly what one of them exists to
    /// catch.
    /// </remarks>
    private static IEnumerable<AppDataUpdate> UpdatesOf(StagedCommitView commit)
    {
        foreach (StagedProposal proposal in commit.Proposals)
        {
            if (proposal.Kind != CommitProposalKind.AppDataUpdate)
                continue;

            yield return proposal.Update ?? throw new AppComponentException(
                "An AppDataUpdate proposal was staged without its operation.");
        }
    }

    private static bool SameBytes(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    /// <summary>
    /// The component entries the resulting GroupContext must still carry.
    /// </summary>
    /// <remarks>
    /// Derived from the <b>resulting</b> required set rather than the current
    /// one, which is what permits an authorized commit to unrequire an optional
    /// component and drop its state in one step, while still rejecting a
    /// required entry that vanishes.
    /// </remarks>
    private static IReadOnlySet<ushort> ProtectedComponents(AppDataDictionary? resulting)
    {
        byte[]? list = resulting?.Get(AppComponent.AppComponents);
        if (list is null)
        {
            // Current profile only: every group requires the list, so a
            // resulting GroupContext without one is not a group we are in.
            throw new AppComponentException(
                "The commit is invalid: its resulting GroupContext drops the " +
                "app_components requirement list.");
        }

        var protectedIds = new SortedSet<ushort>(ComponentCodec.DecodeComponentsList(list))
        {
            AppComponent.AppComponents,
        };

        return protectedIds;
    }
}
