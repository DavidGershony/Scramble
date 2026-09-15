using Scramble.Marmot.AppComponents;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The machine-readable half of a component refusal.
/// </summary>
/// <remarks>
/// <para>
/// These are about the <i>token</i>, not about the rules that produce it. The
/// rules have their own tests beside them — this file pins the two things those
/// cannot: that the tokens are spelled the way upstream spells them, and that a
/// refusal which is not a capability mismatch does not get classified as one.
/// </para>
/// <para>
/// The second matters more than it looks. A taxonomy that labels everything is
/// the same as one that labels nothing, and the way it gets there is somebody
/// adding the reason to a throw site that was merely nearby.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class CapabilityRejectionTaxonomyTests
{
    /// <summary>
    /// The tokens, written out rather than derived.
    /// </summary>
    /// <remarks>
    /// Deliberately a literal table and not a loop over
    /// <see cref="AppComponentRejections.Kind"/>: the claim being tested is that
    /// these exact strings are upstream's, read out of
    /// <c>EngineError::privacy_safe_kind()</c> in
    /// <c>crates/traits/src/error.rs</c> at <c>fdd398a8</c>. A test that asked
    /// the implementation what it thought the tokens were would agree with any
    /// renaming, which is precisely the drift it exists to catch.
    /// </remarks>
    public static TheoryData<AppComponentRejection, string> UpstreamTokens => new()
    {
        { AppComponentRejection.Unclassified, "unclassified" },
        { AppComponentRejection.MissingRequiredCapabilities, "missing_required_capabilities" },
    };

    [Theory]
    [MemberData(nameof(UpstreamTokens))]
    public void ATokenIsSpelledTheWayUpstreamSpellsIt(AppComponentRejection reason, string token)
    {
        Assert.Equal(token, AppComponentRejections.Kind(reason));
        Assert.Equal(token, new AppComponentException("refused", reason).Kind);
    }

    [Fact]
    public void EveryReasonHasATokenAndNoTwoShareOne()
    {
        var tokens = new HashSet<string>();

        foreach (AppComponentRejection reason in Enum.GetValues<AppComponentRejection>())
        {
            string token = AppComponentRejections.Kind(reason);

            Assert.False(string.IsNullOrWhiteSpace(token));

            // Two reasons sharing a token is worse than one having none: the
            // caller branches correctly and the audit trail cannot tell the two
            // refusals apart afterwards.
            Assert.True(tokens.Add(token), $"{reason} reuses the token '{token}'.");
        }

        // The table above must cover the enum, or a reason could be added,
        // given a token, and never checked against upstream's spelling.
        Assert.Equal(Enum.GetValues<AppComponentRejection>().Length, UpstreamTokens.Count);
    }

    [Fact]
    public void ARefusalSaysNothingAboutItsReasonUnlessTheThrowSiteDid()
    {
        var ex = new AppComponentException("the bytes are not a group profile");

        Assert.Equal(AppComponentRejection.Unclassified, ex.Reason);
        Assert.Equal("unclassified", ex.Kind);
    }

    // ---- The self side: a group that requires what this build cannot honour ----

    /// <summary>
    /// A Current-profile GroupContext, optionally requiring one more component
    /// and optionally with the wrong MLS required-capabilities.
    /// </summary>
    private static GroupContextView Context(
        ushort? alsoRequiring = null, IReadOnlySet<ushort>? extensions = null)
    {
        var required = new HashSet<ushort>
        {
            AppComponent.GroupAdminPolicy,
            AppComponent.AccountIdentityProof,
        };
        if (alsoRequiring is { } extra)
            required.Add(extra);

        var dictionary = new AppDataDictionary();
        dictionary.SetComponentList(required);
        dictionary.Set(AppComponent.GroupAdminPolicy, AdminBytes());

        return new GroupContextView(
            extensions ?? new HashSet<ushort> { AppDataDictionary.ExtensionType },
            new HashSet<ushort> { 0x0008 },
            dictionary);
    }

    private static byte[] AdminBytes() =>
        AdminPolicy.Create([Enumerable.Repeat((byte)0x02, 32).ToArray()]).Encode();

    [Fact]
    public void AGroupRequiringSomethingThisBuildCannotHonourIsAMissingCapability()
    {
        // 0x8007 is not a component this implementation knows. Being handed a
        // group that requires it is the same refusal as refusing an invitee who
        // lacks a component we require, with the sides swapped — and upstream
        // files both under one variant, so we do too.
        var ex = Assert.Throws<AppComponentException>(
            () => CurrentProfile.Validate(Context(alsoRequiring: 0x8007)));

        Assert.Equal(AppComponentRejection.MissingRequiredCapabilities, ex.Reason);
        Assert.Contains("0x8007", ex.Message);
    }

    [Fact]
    public void AGroupThatIsSimplyMalformedIsNotBlamedOnCapabilities()
    {
        // Nothing here is missing a capability: the group's required_capabilities
        // are wrong. Classifying this as a capability mismatch would tell a
        // caller to go looking for an unsupported feature that does not exist,
        // and — at the invite gate, where the same reason appears — to drop an
        // invitee who is not at fault.
        var ex = Assert.Throws<AppComponentException>(
            () => CurrentProfile.Validate(Context(extensions: new HashSet<ushort>())));

        Assert.Equal(AppComponentRejection.Unclassified, ex.Reason);
    }
}
