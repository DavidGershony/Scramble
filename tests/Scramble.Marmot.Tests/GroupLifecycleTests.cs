using Scramble.Marmot.AppComponents;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The group-lifecycle component, <c>0x800c</c>.
/// </summary>
/// <remarks>
/// One byte, and every rejection below is a case a lenient decoder would wave
/// through. It is signed group state that decides whether the group is usable
/// at all, so a decoder that guesses has forked its view of the group from
/// everyone else's — which is why an unknown value is an error rather than
/// something to carry opaquely, the way an unknown *component* would be.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class GroupLifecycleTests
{
    [Theory]
    [InlineData(GroupLifecycleState.Active, 0)]
    [InlineData(GroupLifecycleState.Disbanded, 1)]
    public void TheStateIsExactlyOneByte(GroupLifecycleState state, byte encoded)
    {
        Assert.Equal(new[] { encoded }, GroupLifecycle.Encode(state));
        Assert.Equal(state, GroupLifecycle.Decode([encoded]));
    }

    [Fact]
    public void AnEmptyPayloadIsRefusedRatherThanReadAsTheDefault()
    {
        // Active is 0, so "empty means the default" is the tempting reading and
        // it is wrong: upstream refuses these bytes, and a group we consider
        // active while everyone else refuses it is the worst outcome available.
        var ex = Assert.Throws<AppComponentException>(() => GroupLifecycle.Decode([]));
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void AnUnknownStateIsRefusedRatherThanCarriedOpaquely()
    {
        var ex = Assert.Throws<AppComponentException>(() => GroupLifecycle.Decode([2]));
        Assert.Contains("unknown", ex.Message);
    }

    [Fact]
    public void ATrailingByteIsRefusedRatherThanIgnored()
    {
        var ex = Assert.Throws<AppComponentException>(() => GroupLifecycle.Decode([0, 0]));
        Assert.Contains("trailing", ex.Message);
    }

    [Fact]
    public void TheComponentIsKnownSoAGroupRequiringItCanBeJoined()
    {
        // The point of un-deferring it. Every group the reference implementation
        // creates lists 0x800c in default_group_components(), so while it was
        // absent from the known set, CurrentProfile.Validate refused every such
        // group as requiring something unsupported — which is to say, all of
        // them.
        Assert.Contains(AppComponent.GroupLifecycle, CurrentProfile.KnownGroupComponents);
        Assert.Equal("marmot.group.lifecycle.v1", AppComponent.SchemaOf(AppComponent.GroupLifecycle));
    }

    [Fact]
    public void ItIsNotAProfileRequiredComponent()
    {
        // It becomes required through group creation, not through the profile.
        // Upstream's CURRENT_PROFILE_REQUIRED_APP_COMPONENTS is {0x8003, 0x8009}
        // and nothing else, so a group that does not require 0x800c is still
        // valid and must not be refused for lacking it.
        Assert.DoesNotContain(AppComponent.GroupLifecycle, CurrentProfile.RequiredComponents);
        Assert.DoesNotContain(AppComponent.GroupLifecycle, CurrentProfile.RequiredGroupStateComponents);
    }

    [Fact]
    public void AGroupRequiringItValidatesWhenItCarriesTheState()
    {
        var context = ContextRequiring(
            [AppComponent.GroupAdminPolicy, AppComponent.AccountIdentityProof, AppComponent.GroupLifecycle],
            withLifecycleState: true);

        IReadOnlySet<ushort> required = CurrentProfile.Validate(context);

        Assert.Contains(AppComponent.GroupLifecycle, required);
    }

    [Fact]
    public void AGroupRequiringItWithoutStateIsRefused()
    {
        var context = ContextRequiring(
            [AppComponent.GroupAdminPolicy, AppComponent.AccountIdentityProof, AppComponent.GroupLifecycle],
            withLifecycleState: false);

        var ex = Assert.Throws<AppComponentException>(() => CurrentProfile.Validate(context));
        Assert.Contains("no GroupContext state", ex.Message);
    }

    [Fact]
    public void MalformedLifecycleStateFailsGroupValidation()
    {
        var context = ContextRequiring(
            [AppComponent.GroupAdminPolicy, AppComponent.AccountIdentityProof, AppComponent.GroupLifecycle],
            withLifecycleState: true);

        // Corrupt bytes under a required component's own schema must fail, or
        // the state is only being carried rather than validated.
        context.Dictionary.Set(AppComponent.GroupLifecycle, new byte[] { 9 });

        Assert.Throws<AppComponentException>(() => CurrentProfile.Validate(context));
    }

    [Fact]
    public void TheStateCannotBeRemoved()
    {
        // Not merely "still required, so keep it": removal is refused outright,
        // because a group whose lifecycle state vanished is one no member can
        // decide about.
        var ex = Assert.Throws<AppComponentException>(
            () => AppComponentIntegrity.ValidateRemoval(
                AppComponent.GroupLifecycle, new HashSet<ushort>()));

        Assert.Contains("cannot be removed", ex.Message);
    }

    private static GroupContextView ContextRequiring(
        ushort[] required, bool withLifecycleState)
    {
        var dictionary = new AppDataDictionary();
        dictionary.SetComponentList(new SortedSet<ushort>(required));
        dictionary.Set(
            AppComponent.GroupAdminPolicy,
            AdminPolicy.Create([new byte[32]]).Encode());

        if (withLifecycleState)
        {
            dictionary.Set(
                AppComponent.GroupLifecycle,
                GroupLifecycle.Encode(GroupLifecycleState.Active));
        }

        return new GroupContextView(
            new HashSet<ushort> { AppDataDictionary.ExtensionType },
            new HashSet<ushort> { 0x0008 },
            dictionary);
    }
}
