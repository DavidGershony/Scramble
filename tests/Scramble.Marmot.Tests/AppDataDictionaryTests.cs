using Scramble.Marmot.AppComponents;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The app-data dictionary and the Current-profile group invariants.
/// </summary>
/// <remarks>
/// The invariant tests are written against a resulting state rather than a
/// diff, because that is how the rules are defined: a commit that drops a
/// required component is invalid whether or not it re-serialises anything.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class AppDataDictionaryTests
{
    private static readonly byte[] AdminBytes =
        AdminPolicy.Create([Enumerable.Repeat((byte)0x01, 32).ToArray()]).Encode();

    // -- The dictionary --

    [Fact]
    public void EntriesRoundTrip()
    {
        var dictionary = new AppDataDictionary();
        dictionary.Set(AppComponent.GroupProfile, new GroupProfile("Team", "").Encode());
        dictionary.Set(AppComponent.GroupAdminPolicy, AdminBytes);

        var decoded = AppDataDictionary.Decode(dictionary.Encode());

        Assert.Equal(2, decoded.Count);
        Assert.Equal(AdminBytes, decoded.Get(AppComponent.GroupAdminPolicy));
        Assert.Equal(
            new GroupProfile("Team", ""),
            GroupProfile.Decode(decoded.Get(AppComponent.GroupProfile)!));
    }

    [Fact]
    public void AnEmptyDictionaryRoundTrips()
    {
        var decoded = AppDataDictionary.Decode(new AppDataDictionary().Encode());

        Assert.Equal(0, decoded.Count);
    }

    [Fact]
    public void EntriesEncodeAsAnIdThenLengthPrefixedBytes()
    {
        var dictionary = new AppDataDictionary();
        dictionary.Set(0x8001, [0xaa, 0xbb]);

        // 5 entry bytes: two for the id, one length prefix, two of data.
        Assert.Equal([0x05, 0x80, 0x01, 0x02, 0xaa, 0xbb], dictionary.Encode());
    }

    [Fact]
    public void EntriesAreEmittedOrderedByIdWhateverOrderTheyWereSetIn()
    {
        var dictionary = new AppDataDictionary();
        dictionary.Set(AppComponent.MessageRetention, MessageRetention.Disabled.Encode());
        dictionary.Set(AppComponent.GroupProfile, new GroupProfile("a", "").Encode());

        Assert.Equal(
            [AppComponent.GroupProfile, AppComponent.MessageRetention],
            dictionary.ComponentIds);
    }

    [Fact]
    public void SettingAnExistingIdReplacesIt()
    {
        var dictionary = new AppDataDictionary();
        dictionary.Set(0x8001, [0x01]);
        dictionary.Set(0x8001, [0x02]);

        Assert.Equal(1, dictionary.Count);
        Assert.Equal([0x02], dictionary.Get(0x8001));
    }

    [Fact]
    public void RemovingReportsWhetherAnEntryWasThere()
    {
        var dictionary = new AppDataDictionary();
        dictionary.Set(0x8001, [0x01]);

        Assert.True(dictionary.Remove(0x8001));
        Assert.False(dictionary.Remove(0x8001));
        Assert.Null(dictionary.Get(0x8001));
    }

    [Fact]
    public void OutOfOrderEntriesAreRejectedRatherThanReordered()
    {
        // A dictionary lives inside signed group state, so a member that tidied
        // one up would hold bytes nobody else has.
        byte[] encoded = [0x08, 0x80, 0x02, 0x01, 0xbb, 0x80, 0x01, 0x01, 0xaa];

        var ex = Assert.Throws<AppComponentException>(() => AppDataDictionary.Decode(encoded));
        Assert.Contains("ordered", ex.Message);
    }

    [Fact]
    public void DuplicateEntriesAreRejectedRatherThanCollapsed()
    {
        byte[] encoded = [0x08, 0x80, 0x01, 0x01, 0xaa, 0x80, 0x01, 0x01, 0xbb];

        var ex = Assert.Throws<AppComponentException>(() => AppDataDictionary.Decode(encoded));
        Assert.Contains("more than one entry", ex.Message);
    }

    [Fact]
    public void ADictionaryWithTrailingBytesIsRejected()
    {
        byte[] encoded = [.. new AppDataDictionary().Encode(), 0xff];

        Assert.Throws<AppComponentException>(() => AppDataDictionary.Decode(encoded));
    }

    [Fact]
    public void ATruncatedEntryIsRejected()
    {
        // Declares four bytes of entries but the id alone is cut short.
        Assert.Throws<AppComponentException>(() => AppDataDictionary.Decode([0x04, 0x80]));
    }

    [Fact]
    public void AnEntryIdWithNoLengthIsRejected()
    {
        Assert.Throws<AppComponentException>(() => AppDataDictionary.Decode([0x02, 0x80, 0x01]));
    }

    // -- MLS length encoding --

    [Fact]
    public void AnMlsLengthBeyondThirtyBitsCannotBeEncoded()
    {
        // RFC 9420 §2.1.2 defines only the 1, 2 and 4-byte forms and treats a
        // 0b11 prefix as invalid, so the 8-byte QUIC form the component
        // payloads allow is unusable here. The two agree at every realistic
        // size, which is precisely why this needs pinning.
        var output = new List<byte>();

        AppDataDictionary.WriteMlsLength(AppDataDictionary.MaxMlsLength, output);
        Assert.Equal(4, output.Count);

        Assert.Throws<AppComponentException>(
            () => AppDataDictionary.WriteMlsLength(AppDataDictionary.MaxMlsLength + 1, []));
    }

    // -- The component list entry --

    [Fact]
    public void TheComponentListRoundTripsThroughItsOwnEntry()
    {
        var dictionary = new AppDataDictionary();
        var ids = new HashSet<ushort> { AppComponent.GroupAdminPolicy, AppComponent.AccountIdentityProof };
        dictionary.SetComponentList(ids);

        Assert.Equal(ids, AppDataDictionary.Decode(dictionary.Encode()).ComponentList());
    }

    [Fact]
    public void AMissingComponentListIsNullRatherThanEmpty()
    {
        // The difference matters: an absent list is an invalid group, while an
        // empty one is merely a group that requires nothing.
        Assert.Null(new AppDataDictionary().ComponentList());

        var dictionary = new AppDataDictionary();
        dictionary.SetComponentList(new HashSet<ushort>());
        Assert.Empty(dictionary.ComponentList()!);
    }

    // -- Current-profile invariants --

    private static GroupContextView Conformant(
        IReadOnlySet<ushort>? extensions = null,
        IReadOnlySet<ushort>? proposals = null,
        Action<AppDataDictionary>? adjust = null)
    {
        var dictionary = new AppDataDictionary();
        dictionary.SetComponentList(new HashSet<ushort>
        {
            AppComponent.GroupAdminPolicy,
            AppComponent.AccountIdentityProof,
        });
        dictionary.Set(AppComponent.GroupAdminPolicy, AdminBytes);
        adjust?.Invoke(dictionary);

        return new GroupContextView(
            extensions ?? new HashSet<ushort> { AppDataDictionary.ExtensionType },
            proposals ?? new HashSet<ushort> { 0x0008 },
            dictionary);
    }

    [Fact]
    public void AConformantCurrentProfileGroupValidates()
    {
        var required = CurrentProfile.Validate(Conformant());

        Assert.Contains(AppComponent.GroupAdminPolicy, required);
        Assert.Contains(AppComponent.AccountIdentityProof, required);
    }

    [Fact]
    public void TheAppDataDictionaryExtensionMustBeRequired()
    {
        var ex = Assert.Throws<AppComponentException>(
            () => CurrentProfile.Validate(Conformant(extensions: new HashSet<ushort>())));

        Assert.Contains("app_data_dictionary", ex.Message);
    }

    [Fact]
    public void TheAppDataUpdateProposalMustBeRequired()
    {
        // A hard blocker on create and join alike: a client that cannot handle
        // the proposal cannot participate at all.
        var ex = Assert.Throws<AppComponentException>(
            () => CurrentProfile.Validate(Conformant(proposals: new HashSet<ushort> { 0x000a })));

        Assert.Contains("app_data_update", ex.Message);
    }

    [Fact]
    public void AGroupWithNoComponentRequirementListIsInvalid()
    {
        var ex = Assert.Throws<AppComponentException>(() => CurrentProfile.Validate(
            Conformant(adjust: d => d.Remove(AppComponent.AppComponents))));

        Assert.Contains("app_components", ex.Message);
    }

    [Theory]
    [InlineData(AppComponent.GroupAdminPolicy)]
    [InlineData(AppComponent.AccountIdentityProof)]
    public void BothMandatoryComponentsMustBeRequired(ushort componentId)
    {
        var view = Conformant(adjust: d => d.SetComponentList(
            new HashSet<ushort> { AppComponent.GroupAdminPolicy, AppComponent.AccountIdentityProof }
                .Where(id => id != componentId).ToHashSet()));

        var ex = Assert.Throws<AppComponentException>(() => CurrentProfile.Validate(view));
        Assert.Contains($"0x{componentId:x4}", ex.Message);
    }

    [Fact]
    public void TheAdminPolicyMustHaveGroupContextState()
    {
        // Required and stateful. Requiring it without carrying its bytes would
        // leave the group with no admin list at all.
        var ex = Assert.Throws<AppComponentException>(() => CurrentProfile.Validate(
            Conformant(adjust: d => d.Remove(AppComponent.GroupAdminPolicy))));

        Assert.Contains("no GroupContext state", ex.Message);
    }

    [Fact]
    public void TheLeafOnlyAccountProofMustNotAppearInTheGroupContext()
    {
        // It binds one member's account key to one leaf's signature key, so as
        // group state it is meaningless — an error, not harmless clutter.
        var ex = Assert.Throws<AppComponentException>(() => CurrentProfile.Validate(
            Conformant(adjust: d => d.Set(AppComponent.AccountIdentityProof, [0x00]))));

        Assert.Contains("leaf-only", ex.Message);
    }

    [Fact]
    public void ARequiredComponentWeCannotHonourIsRejectedRatherThanIgnored()
    {
        // Joining and silently ignoring state the group considers mandatory is
        // the outcome this prevents.
        var view = Conformant(adjust: d =>
        {
            d.SetComponentList(new HashSet<ushort>
            {
                AppComponent.GroupAdminPolicy,
                AppComponent.AccountIdentityProof,
                0x8006, // agent text stream over QUIC, deferred to P12
            });
        });

        var ex = Assert.Throws<AppComponentException>(() => CurrentProfile.Validate(view));
        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public void ARequiredComponentWithoutStateIsRejected()
    {
        var view = Conformant(adjust: d => d.SetComponentList(new HashSet<ushort>
        {
            AppComponent.GroupAdminPolicy,
            AppComponent.AccountIdentityProof,
            AppComponent.NostrRouting,
        }));

        var ex = Assert.Throws<AppComponentException>(() => CurrentProfile.Validate(view));
        Assert.Contains("0x8004", ex.Message);
    }

    [Fact]
    public void AnOptionalComponentMayCarryStateWithoutBeingRequired()
    {
        var view = Conformant(adjust: d =>
            d.Set(AppComponent.MessageRetention, new MessageRetention(900).Encode()));

        _ = CurrentProfile.Validate(view);
    }

    [Fact]
    public void ARecognisedEntryCarryingUndecodableBytesIsRejected()
    {
        // How corrupt state would otherwise reach the group: bytes in the
        // dictionary that never passed their component's validator.
        var view = Conformant(adjust: d =>
            d.Set(AppComponent.MessageRetention, [0x01, 0x02]));

        Assert.Throws<AppComponentException>(() => CurrentProfile.Validate(view));
    }

    [Fact]
    public void AnUnrecognisedEntryThatIsNotRequiredIsLeftAlone()
    {
        // Forward compatibility: a component we do not know, that the group
        // does not require, is none of our business.
        var view = Conformant(adjust: d => d.Set(0x8007, [0xde, 0xad]));

        _ = CurrentProfile.Validate(view);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheFrozenEncryptedMediaComponentIsRefusedWhetherRequiredOrMerelyPresent(
        bool required)
    {
        // Frozen upstream, so a group carrying 0x8008 is one every current peer
        // already refuses. Present-but-not-required is the case that would
        // otherwise slip through as an unknown optional component.
        var view = Conformant(adjust: d =>
        {
            d.Set(AppComponent.EncryptedMediaV1Frozen, [0x00]);
            if (required)
            {
                d.SetComponentList(new HashSet<ushort>
                {
                    AppComponent.GroupAdminPolicy,
                    AppComponent.AccountIdentityProof,
                    AppComponent.EncryptedMediaV1Frozen,
                });
            }
        });

        var ex = Assert.Throws<AppComponentException>(() => CurrentProfile.Validate(view));
        // "frozen", not merely the id: without this rule the required case
        // would still throw — as an unsupported required component — and the
        // present-only case would not throw at all.
        Assert.Contains("frozen", ex.Message);
        Assert.Contains("0x8008", ex.Message);
    }

    [Fact]
    public void SafeAadStateInTheGroupContextIsRefusedRatherThanCarried()
    {
        // Known-and-refused, which is not the same as unknown: the draft gives
        // the component no GroupContext payload, and upstream rejects these
        // bytes outright.
        var view = Conformant(adjust: d => d.Set(AppComponent.SafeAad, []));

        var ex = Assert.Throws<AppComponentException>(() => CurrentProfile.Validate(view));
        Assert.Contains("safe_aad", ex.Message);
    }

    [Fact]
    public void TheErrorNamesWhatWasBeingValidated()
    {
        var ex = Assert.Throws<AppComponentException>(() => CurrentProfile.Validate(
            Conformant(extensions: new HashSet<ushort>()), "Welcome"));

        Assert.Contains("Welcome", ex.Message);
    }
}
