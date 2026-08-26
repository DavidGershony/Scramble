using Scramble.Marmot.AppComponents;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// What a commit may do to the GroupContext's component state.
/// </summary>
/// <remarks>
/// The attack these rules exist for is a commit that carries no
/// <c>AppDataUpdate</c> at all — MLS's own guard returns early on one — and
/// hands over a resulting GroupContext with the admin set quietly swapped or
/// the dictionary gone. So the tests that matter most are the ones where the
/// resulting state is <i>plausible</i> and simply nobody proposed it.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class AppComponentIntegrityTests
{
    private static byte[] Admins(params byte[] fills) =>
        AdminPolicy.Create(fills.Select(fill => Enumerable.Repeat(fill, 32).ToArray())).Encode();

    private static readonly byte[] AdminBytes = Admins(0x01);
    private static readonly byte[] OtherAdminBytes = Admins(0x02);

    private static readonly IReadOnlySet<ushort> BaseRequired = new HashSet<ushort>
    {
        AppComponent.GroupAdminPolicy,
        AppComponent.AccountIdentityProof,
    };

    /// <summary>A conformant dictionary: the requirement list plus admin state.</summary>
    private static AppDataDictionary Dictionary(
        IReadOnlySet<ushort>? required = null,
        Action<AppDataDictionary>? adjust = null)
    {
        var dictionary = new AppDataDictionary();
        dictionary.SetComponentList(required ?? BaseRequired);
        dictionary.Set(AppComponent.GroupAdminPolicy, AdminBytes);
        adjust?.Invoke(dictionary);
        return dictionary;
    }

    private static StagedCommitView Commit(params AppDataUpdate[] updates) =>
        new(
            [.. updates.Select(update =>
                new StagedProposal(CommitProposalKind.AppDataUpdate, null, update))],
            HasUpdatePathLeaf: true);

    // -- The dictionary and its protected entries --

    [Fact]
    public void ACommitThatChangesNothingIsFine()
    {
        AppComponentIntegrity.ValidateStagedCommit(Commit(), Dictionary(), Dictionary());
    }

    [Fact]
    public void DroppingTheAppDataDictionaryIsRejected()
    {
        // The whole extension gone: no admin list, so every admin-gated
        // operation is frozen with no way back.
        var ex = Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateStagedCommit(Commit(), Dictionary(), null));

        Assert.Contains("app_data_dictionary", ex.Message);
    }

    [Fact]
    public void DroppingTheRequirementListIsRejected()
    {
        var resulting = Dictionary();
        resulting.Remove(AppComponent.AppComponents);

        var ex = Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateStagedCommit(
                Commit(AppDataUpdate.Remove(AppComponent.AppComponents)),
                Dictionary(),
                resulting));

        Assert.Contains("app_components", ex.Message);
    }

    [Fact]
    public void DroppingARequiredComponentsStateIsRejectedEvenWhenProposed()
    {
        // Proposing it is not enough. The entry is required in the resulting
        // epoch, so it may not disappear in that epoch's own commit.
        var resulting = Dictionary();
        resulting.Remove(AppComponent.GroupAdminPolicy);

        var ex = Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateStagedCommit(
                Commit(AppDataUpdate.Remove(AppComponent.GroupAdminPolicy)),
                Dictionary(),
                resulting));

        Assert.Contains("drops required component 0x8003", ex.Message);
    }

    [Fact]
    public void TheLeafOnlyAccountProofIsNotExpectedAsGroupState()
    {
        // It is in every group's required set and never in its dictionary.
        // Without the exemption this would reject every conformant commit.
        AppComponentIntegrity.ValidateStagedCommit(Commit(), Dictionary(), Dictionary());

        Assert.Contains(AppComponent.AccountIdentityProof, BaseRequired);
    }

    [Fact]
    public void OneCommitMayUnrequireAndRemoveAnOptionalComponent()
    {
        // The atomic pair the resulting-set rule exists to permit.
        var current = Dictionary(
            required: new HashSet<ushort>(BaseRequired) { AppComponent.NostrRouting },
            adjust: d => d.Set(AppComponent.NostrRouting, [0xaa]));

        var resulting = Dictionary();
        byte[] resultingList = resulting.Get(AppComponent.AppComponents)!;

        AppComponentIntegrity.ValidateStagedCommit(
            Commit(
                AppDataUpdate.Remove(AppComponent.NostrRouting),
                AppDataUpdate.Update(AppComponent.AppComponents, resultingList)),
            current,
            resulting);
    }

    // -- The diff must be accounted for --

    [Fact]
    public void RewritingComponentBytesWithNoProposalAtAllIsRejected()
    {
        // The GroupContextExtensions attack: every entry still present, the
        // admin set swapped, and not one AppDataUpdate in the commit.
        var resulting = Dictionary(adjust: d =>
            d.Set(AppComponent.GroupAdminPolicy, OtherAdminBytes));

        var ex = Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateStagedCommit(Commit(), Dictionary(), resulting));

        Assert.Contains("outside an AppDataUpdate proposal", ex.Message);
    }

    [Fact]
    public void RewritingComponentBytesTheProposalDoesNotNameIsRejected()
    {
        // A proposal for the right component is not a blank cheque: the
        // resulting bytes must be the bytes it proposed.
        var resulting = Dictionary(adjust: d =>
            d.Set(AppComponent.GroupAdminPolicy, OtherAdminBytes));

        var ex = Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateStagedCommit(
                Commit(AppDataUpdate.Update(AppComponent.GroupAdminPolicy, Admins(0x03))),
                Dictionary(),
                resulting));

        Assert.Contains("0x8003", ex.Message);
    }

    [Fact]
    public void RewritingComponentBytesTheProposalDoesNameIsFine()
    {
        var resulting = Dictionary(adjust: d =>
            d.Set(AppComponent.GroupAdminPolicy, OtherAdminBytes));

        AppComponentIntegrity.ValidateStagedCommit(
            Commit(AppDataUpdate.Update(AppComponent.GroupAdminPolicy, OtherAdminBytes)),
            Dictionary(),
            resulting);
    }

    [Fact]
    public void AddingAnEntryNobodyProposedIsRejected()
    {
        var resulting = Dictionary(adjust: d =>
            d.Set(AppComponent.MessageRetention, new MessageRetention(900).Encode()));

        var ex = Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateStagedCommit(Commit(), Dictionary(), resulting));

        Assert.Contains("0x8005", ex.Message);
    }

    [Fact]
    public void AnUpdateProposalDoesNotAccountForAnEntryThatDisappeared()
    {
        // Removal is null, not empty. An operation writing zero bytes is a
        // different resulting value from no entry at all, and only a Remove
        // accounts for the second.
        var current = Dictionary(adjust: d => d.Set(AppComponent.MessageRetention, [0x01]));
        var resulting = Dictionary();

        var ex = Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateStagedCommit(
                Commit(AppDataUpdate.Update(AppComponent.MessageRetention, [])),
                current,
                resulting));

        Assert.Contains("0x8005", ex.Message);
    }

    [Fact]
    public void AnAppDataUpdateProposalWithoutItsOperationIsRejected()
    {
        // Fails closed. Reading it as "no operation" would make the proposal
        // invisible to the rule that has to account for every change.
        var commit = new StagedCommitView(
            [new StagedProposal(CommitProposalKind.AppDataUpdate)],
            HasUpdatePathLeaf: true);

        var ex = Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateStagedCommit(commit, Dictionary(), Dictionary()));

        Assert.Contains("without its operation", ex.Message);
    }

    // -- The proposal batch --

    [Fact]
    public void TwoOperationsForOneComponentAreRejected()
    {
        // Nothing in the wire format fixes their order, so members could
        // disagree about which one won.
        var ex = Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateUpdateBatch(
                Commit(
                    AppDataUpdate.Update(AppComponent.GroupAdminPolicy, AdminBytes),
                    AppDataUpdate.Update(AppComponent.GroupAdminPolicy, OtherAdminBytes)),
                BaseRequired));

        Assert.Contains("more than one AppDataUpdate", ex.Message);
    }

    [Fact]
    public void RemovingAStillRequiredComponentIsRejected()
    {
        var required = new HashSet<ushort>(BaseRequired) { AppComponent.NostrRouting };

        var ex = Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateUpdateBatch(
                Commit(AppDataUpdate.Remove(AppComponent.NostrRouting)),
                required));

        Assert.Contains("still required", ex.Message);
    }

    [Fact]
    public void ARemovalIsJudgedAgainstTheResultingListEvenWhenItComesFirst()
    {
        // The operation that unrequires the component may be staged after the
        // removal it authorises, which is why the list is resolved across the
        // whole batch before any operation is judged.
        var required = new HashSet<ushort>(BaseRequired) { AppComponent.NostrRouting };
        byte[] resultingList = ComponentCodec.EncodeComponentsList(BaseRequired);

        IReadOnlySet<ushort> resulting = AppComponentIntegrity.ValidateUpdateBatch(
            Commit(
                AppDataUpdate.Remove(AppComponent.NostrRouting),
                AppDataUpdate.Update(AppComponent.AppComponents, resultingList)),
            required);

        Assert.Equal(BaseRequired, resulting);
    }

    [Theory]
    [InlineData(AppComponent.AppComponents)]
    [InlineData(AppComponent.SafeAad)]
    [InlineData((ushort)0x800c)] // group lifecycle
    public void SomeComponentsCanNeverBeRemoved(ushort componentId)
    {
        Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateUpdateBatch(
                Commit(AppDataUpdate.Remove(componentId)),
                BaseRequired));
    }

    [Fact]
    public void AnUpdateCarryingUndecodableBytesIsRejected()
    {
        // The other half of the pair: an update-backed change is still a
        // change to bytes that have to decode.
        Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateUpdateBatch(
                Commit(AppDataUpdate.Update(AppComponent.GroupAdminPolicy, [0x01, 0x02])),
                BaseRequired));
    }

    [Theory]
    [InlineData(AppComponent.AccountIdentityProof)]
    [InlineData(AppComponent.EncryptedMediaV1Frozen)]
    [InlineData(AppComponent.SafeAad)]
    public void SomeComponentsCanNeverHoldGroupState(ushort componentId)
    {
        Assert.Throws<AppComponentException>(() =>
            AppComponentIntegrity.ValidateUpdateBatch(
                Commit(AppDataUpdate.Update(componentId, [0x00])),
                BaseRequired));
    }

    [Fact]
    public void AnUnknownOptionalComponentStaysOpaque()
    {
        // Deliberately the opposite of the required-set rule. Refusing a commit
        // over an optional component we have not heard of would strand us
        // outside a group everyone else is still in.
        AppComponentIntegrity.ValidateUpdateBatch(
            Commit(AppDataUpdate.Update(0x8007, [0xde, 0xad])),
            BaseRequired);
    }

    [Fact]
    public void ABatchWithNoRequirementChangeReturnsTheCurrentSet()
    {
        IReadOnlySet<ushort> resulting = AppComponentIntegrity.ValidateUpdateBatch(
            Commit(AppDataUpdate.Update(AppComponent.GroupAdminPolicy, OtherAdminBytes)),
            BaseRequired);

        Assert.Equal(BaseRequired, resulting);
    }
}
