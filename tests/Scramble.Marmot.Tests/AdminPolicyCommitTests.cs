using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using Scramble.Nostr.Crypto;
using Xunit;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Staging a commit that changes the group's admin set.
/// </summary>
/// <remarks>
/// <para>
/// The first <c>AppDataUpdate</c> this engine builds rather than merely
/// validates, and it targets the component a group cannot recover from losing.
/// So the tests split three ways: what a peer reads off the bytes (an inline
/// <c>0x0008</c> proposal for <c>0x8003</c>, classified Privileged), what the
/// resulting epoch looks like to a second member who processes the commit, and
/// the refusals — an empty set, a phantom admin, a non-admin committer.
/// </para>
/// <para>
/// <b>The second member is not decoration.</b> A single-device test can only
/// ever compare our own state with itself; the admin set means something
/// because another member computes the same one from the same commit, and that
/// is what <see cref="TheOtherMemberReachesTheSameAdminSet"/> checks.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class AdminPolicyCommitTests
{
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;

    private static readonly string[] TestRelays = ["wss://relay.example.com"];

    private sealed class LocalSigner : IAccountIdentityProofSigner
    {
        private readonly byte[] _secret;

        public LocalSigner()
        {
            var (secret, publicKey) = Bip340.GenerateKeyPair();
            _secret = secret;
            AccountPublicKey = publicKey;
        }

        public ReadOnlyMemory<byte> AccountPublicKey { get; }

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(_secret, template.ComputeId()));
    }

    private Task<CreatedGroup> NewGroupAsync() =>
        MarmotGroupBuilder.CreateAsync(_cs, new LocalSigner(), "Rakes", "", Now, TestRelays);

    /// <summary>A two-member group: Alice created it, Bob joined by Welcome.</summary>
    private sealed record Pair(
        CreatedGroup Alice, byte[] AliceAccount, MlsGroup Bob, byte[] BobAccount);

    private async Task<Pair> NewPairAsync()
    {
        CreatedGroup alice = await NewGroupAsync();
        MarmotKeyPackageBundle bob =
            await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);

        StagedCommit staged = MarmotGroupInvite.Add(alice.Group, _cs, [bob.KeyPackage]);
        staged.Applied();

        MlsGroup bobGroup = MlsGroup.ProcessWelcome(
            _cs,
            staged.Welcome!,
            bob.KeyPackage,
            bob.PrivateMaterial.InitPrivateKey,
            bob.PrivateMaterial.LeafPrivateKey,
            bob.PrivateMaterial.SignaturePrivateKey);

        var credential = Assert.IsType<BasicCredential>(bob.KeyPackage.LeafNode.Credential);

        return new Pair(
            alice, alice.Admins.Single(), bobGroup, credential.Identity);
    }

    /// <summary>The admin keys a group's signed state currently lists.</summary>
    private static IReadOnlyList<byte[]> AdminsOf(MlsGroup group)
    {
        foreach (Extension extension in group.GroupContext.Extensions)
        {
            if (extension.ExtensionType != MarmotDictionary.ExtensionType)
                continue;

            byte[] bytes = MarmotDictionary.Decode(extension.ExtensionData)
                .Get(AppComponent.GroupAdminPolicy)!;

            return AdminPolicy.Decode(bytes).Admins;
        }

        throw new Xunit.Sdk.XunitException("The group has no app_data_dictionary.");
    }

    private static Commit CommitOf(StagedCommit staged) =>
        Commit.ReadFrom(new TlsReader(staged.Commit.Content.Content));

    private static byte[] RequiredCapabilitiesBytesOf(MlsGroup group) =>
        group.GroupContext.Extensions
            .Single(e => e.ExtensionType == (ushort)ExtensionType.RequiredCapabilities)
            .ExtensionData;

    // ---- What the commit is, on the wire ----

    [Fact]
    public async Task StagingDoesNotAdvanceTheGroup()
    {
        Pair pair = await NewPairAsync();
        ulong before = pair.Alice.Group.Epoch;

        StagedCommit staged = MarmotGroupAdminPolicy.Stage(
            pair.Alice.Group, _cs, [pair.AliceAccount, pair.BobAccount]);

        // Publish-before-apply. A governance change applied locally and never
        // published leaves the committer alone in an epoch nobody reaches,
        // believing they changed who governs the group.
        Assert.Equal(before, pair.Alice.Group.Epoch);
        Assert.Single(AdminsOf(pair.Alice.Group));

        // Nobody joins and nobody leaves.
        Assert.Null(staged.Welcome);
        Assert.Empty(staged.AffectedAccounts);
    }

    [Fact]
    public async Task TheCommitCarriesOneInlineAppDataUpdateForTheAdminComponent()
    {
        Pair pair = await NewPairAsync();

        StagedCommit staged = MarmotGroupAdminPolicy.Stage(
            pair.Alice.Group, _cs, [pair.AliceAccount, pair.BobAccount]);

        Commit commit = CommitOf(staged);
        var inline = Assert.IsType<InlineProposal>(Assert.Single(commit.Proposals));
        var update = Assert.IsType<AppDataUpdateProposal>(inline.Proposal);

        Assert.Equal((ushort)ProposalType.AppDataUpdate, (ushort)update.ProposalType);
        Assert.Equal(AppComponent.GroupAdminPolicy, update.ComponentId);

        // Update, never Remove. A removal of 0x8003 is invalid at every
        // privilege level: a group without an admin list has nobody authorized
        // to commit anything, including a commit that would restore it.
        Assert.Equal(AppDataUpdateOperationType.Update, update.Operation);

        Assert.Equal(
            new[] { pair.AliceAccount, pair.BobAccount }.OrderBy(k => k, ByteOrder.Instance),
            AdminPolicy.Decode(update.Data).Admins.AsEnumerable());
    }

    [Fact]
    public async Task TheCommitIsPrivileged()
    {
        Pair pair = await NewPairAsync();

        StagedCommit staged = MarmotGroupAdminPolicy.Stage(
            pair.Alice.Group, _cs, [pair.AliceAccount, pair.BobAccount]);

        // Not a nicety: priority outranks committer and digest in branch
        // selection, so a governance change that classified as Ordinary could
        // lose a same-epoch race to a routine self-update.
        Assert.Equal(CommitOrderingPriority.Privileged, staged.OrderingPriority);
    }

    /// <summary>
    /// The two-registries rule, as a regression guard rather than a live one.
    /// </summary>
    /// <remarks>
    /// <b>Not load-bearing against this implementation, and saying so beats
    /// letting it take credit.</b> No mutation of the staging path can make
    /// this fail on its own terms: nothing in it writes
    /// <c>required_capabilities</c> at all, so the before/after comparison is
    /// checking that code which does not exist stays non-existent. It is here
    /// for the edit that adds it — folding an admin grant into an invite, say,
    /// reaches for a <c>GroupContextExtensions</c> proposal, and that is the
    /// one shape that can move these bytes. Every mutation that took this test
    /// down (M5–M7, M10, M11) did so by making staging throw, which is a
    /// different test failing in this one's clothing.
    /// </remarks>
    [Fact]
    public async Task ComponentIdsDoNotLeakIntoRequiredCapabilities()
    {
        Pair pair = await NewPairAsync();
        byte[] before = RequiredCapabilitiesBytesOf(pair.Alice.Group);

        MarmotGroupAdminPolicy
            .Stage(pair.Alice.Group, _cs, [pair.AliceAccount, pair.BobAccount])
            .Applied();

        // Two registries. required_capabilities is MLS's vocabulary —
        // extension and proposal types — and a component id has no business in
        // it; the group's component requirements live in the dictionary's own
        // 0x0001 list. An admin change touches neither.
        byte[] after = RequiredCapabilitiesBytesOf(pair.Alice.Group);
        Assert.Equal(before, after);

        RequiredCapabilities capabilities =
            RequiredCapabilities.FromExtensions(pair.Alice.Group.GroupContext.Extensions)!;
        Assert.DoesNotContain(AppComponent.GroupAdminPolicy, capabilities.ExtensionTypes);
        Assert.DoesNotContain(AppComponent.GroupAdminPolicy, capabilities.ProposalTypes);
    }

    [Fact]
    public async Task TheRequirementListIsUnchanged()
    {
        Pair pair = await NewPairAsync();

        MarmotGroupAdminPolicy
            .Stage(pair.Alice.Group, _cs, [pair.AliceAccount, pair.BobAccount])
            .Applied();

        Assert.Equal(
            pair.Alice.Required, MarmotGroupBuilder.ValidateCreated(pair.Alice.Group));
    }

    // ---- What the group looks like afterwards ----

    [Fact]
    public async Task ApplyingRewritesTheAdminSet()
    {
        Pair pair = await NewPairAsync();
        ulong before = pair.Alice.Group.Epoch;

        MarmotGroupAdminPolicy
            .Stage(pair.Alice.Group, _cs, [pair.AliceAccount, pair.BobAccount])
            .Applied();

        Assert.Equal(before + 1, pair.Alice.Group.Epoch);
        Assert.Equal(2, AdminsOf(pair.Alice.Group).Count);
        Assert.Contains(AdminsOf(pair.Alice.Group), a => a.SequenceEqual(pair.BobAccount));
    }

    /// <summary>
    /// Both members compute the same admin set from the same commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This checks agreement, not correctness, and the difference is
    /// visible under mutation.</b> Staging the wrong set — M13, where only the
    /// last admin reached the policy — left this green, because both members
    /// read the wrong set from the same bytes and agreed about it perfectly.
    /// <see cref="ApplyingRewritesTheAdminSet"/> and
    /// <see cref="TheCommitCarriesOneInlineAppDataUpdateForTheAdminComponent"/>
    /// are what caught that. What this adds is the only thing they cannot see:
    /// that a second member's own commit processing reaches our state at all.
    /// </para>
    /// <para>
    /// Its unique half could only be mutation-checked by breaking the library's
    /// receive path, which is off limits — <c>lib/dotnet-mls</c> takes no edit
    /// without explicit permission. So: exercised, not mutation-proven, and it
    /// is still the only test here that runs <c>ProcessCommit</c> over these
    /// bytes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheOtherMemberReachesTheSameAdminSet()
    {
        Pair pair = await NewPairAsync();

        StagedCommit staged = MarmotGroupAdminPolicy.Stage(
            pair.Alice.Group, _cs, [pair.AliceAccount, pair.BobAccount]);

        // The receiving half, computed by the library's own commit processing
        // from the published bytes rather than by anything this slice wrote.
        // Two members disagreeing about the admin set is the failure that stays
        // silent until one of them commits something the other refuses.
        pair.Bob.ProcessCommit(staged.Commit);
        staged.Applied();

        Assert.Equal(pair.Alice.Group.Epoch, pair.Bob.Epoch);

        IReadOnlyList<byte[]> mine = AdminsOf(pair.Alice.Group);
        IReadOnlyList<byte[]> theirs = AdminsOf(pair.Bob);

        Assert.Equal(mine.Count, theirs.Count);
        for (int i = 0; i < mine.Count; i++)
            Assert.Equal(mine[i], theirs[i]);

        Assert.Contains(theirs, a => a.SequenceEqual(pair.BobAccount));
    }

    [Fact]
    public async Task DiscardingLeavesTheGroupWhereItWas()
    {
        Pair pair = await NewPairAsync();
        ulong before = pair.Alice.Group.Epoch;

        MarmotGroupAdminPolicy
            .Stage(pair.Alice.Group, _cs, [pair.AliceAccount, pair.BobAccount])
            .Discard();

        Assert.Equal(before, pair.Alice.Group.Epoch);
        Assert.False(pair.Alice.Group.HasPendingCommit);
        Assert.Single(AdminsOf(pair.Alice.Group));

        // And the group is still usable afterwards.
        MarmotGroupAdminPolicy
            .Stage(pair.Alice.Group, _cs, [pair.AliceAccount, pair.BobAccount])
            .Applied();

        Assert.Equal(before + 1, pair.Alice.Group.Epoch);
    }

    // ---- An admin stepping down ----

    [Fact]
    public async Task AnAdminCanRemoveItselfWhileAnotherRemains()
    {
        Pair pair = await NewPairAsync();

        MarmotGroupAdminPolicy
            .Stage(pair.Alice.Group, _cs, [pair.AliceAccount, pair.BobAccount])
            .Applied();

        // The ordering CommitAuthorization.RequireNoAdminSelfRemove exists to
        // force: an admin hands over first, and only then leaves.
        MarmotGroupAdminPolicy
            .Stage(pair.Alice.Group, _cs, [pair.BobAccount])
            .Applied();

        byte[] remaining = Assert.Single(AdminsOf(pair.Alice.Group));
        Assert.Equal(pair.BobAccount, remaining);
    }

    // ---- The refusals ----

    /// <summary>Admin depletion, refused.</summary>
    /// <remarks>
    /// <b>It does not pin where the refusal comes from, because two guards
    /// stand behind it.</b> Disabling <see cref="AdminPolicy.Create"/>'s
    /// non-empty check alone leaves this green — the rehearsal's
    /// Current-profile pass decodes the resulting component and
    /// <see cref="AdminPolicy.Decode"/> refuses a zero-length list. Only
    /// disabling both makes it fail, which is what was checked. That is the
    /// rule being enforced twice rather than a test covering nothing, and it
    /// is the right shape for the one commit a group cannot recover from.
    /// </remarks>
    [Fact]
    public async Task AnEmptyAdminSetIsRefused()
    {
        Pair pair = await NewPairAsync();

        // A group that reaches an epoch with no admins is frozen for good: v1
        // has no succession and every repair is itself admin-gated.
        Assert.Throws<AppComponentException>(
            () => MarmotGroupAdminPolicy.Stage(pair.Alice.Group, _cs, []));

        Assert.False(pair.Alice.Group.HasPendingCommit);
        Assert.Single(AdminsOf(pair.Alice.Group));
    }

    [Fact]
    public async Task AnAdminWhoIsNotAMemberIsRefused()
    {
        Pair pair = await NewPairAsync();
        byte[] stranger = new LocalSigner().AccountPublicKey.ToArray();

        // A phantom admin becomes active the moment a matching leaf appears,
        // with no commit any member observed granting it.
        var ex = Assert.Throws<AppComponentException>(
            () => MarmotGroupAdminPolicy.Stage(
                pair.Alice.Group, _cs, [pair.AliceAccount, stranger]));

        Assert.Contains("member leaf", ex.Message);
        Assert.False(pair.Alice.Group.HasPendingCommit);
    }

    [Fact]
    public async Task AnAdminKeyThatIsNotAnAccountKeyIsRefused()
    {
        Pair pair = await NewPairAsync();

        // Thirty-two bytes that are not a curve point. Such an entry could
        // never authorise anything, and once in signed state only another admin
        // commit could take it out.
        byte[] notAKey = Enumerable.Repeat((byte)0xff, 32).ToArray();
        Assert.False(Bip340.IsValidXOnlyPublicKey(notAKey));

        var ex = Assert.Throws<AppComponentException>(
            () => MarmotGroupAdminPolicy.Stage(
                pair.Alice.Group, _cs, [pair.AliceAccount, notAKey]));

        Assert.Contains("x-only", ex.Message);
    }

    [Fact]
    public async Task ANonAdminCannotChangeTheAdminPolicy()
    {
        Pair pair = await NewPairAsync();

        // Bob is a member and not an admin. A commit he built would be refused
        // by every peer — and he would have published and applied it first.
        var ex = Assert.Throws<AppComponentException>(
            () => MarmotGroupAdminPolicy.Stage(
                pair.Bob, _cs, [pair.AliceAccount, pair.BobAccount]));

        Assert.Contains("active admin", ex.Message);
        Assert.False(pair.Bob.HasPendingCommit);
    }

    [Fact]
    public async Task AnUnchangedAdminSetIsRefused()
    {
        Pair pair = await NewPairAsync();

        // Still a real commit that advances the epoch and reads to every member
        // as a governance change. A settings screen saving an untouched list
        // should not produce one.
        var ex = Assert.Throws<ArgumentException>(
            () => MarmotGroupAdminPolicy.Stage(pair.Alice.Group, _cs, [pair.AliceAccount]));

        Assert.Contains("already", ex.Message);
        Assert.False(pair.Alice.Group.HasPendingCommit);
    }

    [Fact]
    public async Task ReorderedAndDuplicatedInputProducesTheSameBytes()
    {
        Pair pair = await NewPairAsync();

        StagedCommit staged = MarmotGroupAdminPolicy.Stage(
            pair.Alice.Group,
            _cs,
            [pair.BobAccount, pair.AliceAccount, pair.BobAccount]);

        var inline = Assert.IsType<InlineProposal>(Assert.Single(CommitOf(staged).Proposals));
        var update = Assert.IsType<AppDataUpdateProposal>(inline.Proposal);

        // Decode is the strict half — it refuses an unsorted or duplicated list
        // rather than canonicalising — so this passing is the proof the producer
        // canonicalised.
        Assert.Equal(2, AdminPolicy.Decode(update.Data).Admins.Count);
    }

    [Fact]
    public async Task AStagedCommitBlocksASecondOne()
    {
        Pair pair = await NewPairAsync();

        using StagedCommit first = MarmotGroupAdminPolicy.Stage(
            pair.Alice.Group, _cs, [pair.AliceAccount, pair.BobAccount]);

        // Two pending commits cannot both be published, and picking one
        // silently would discard the other.
        Assert.Throws<InvalidOperationException>(
            () => MarmotGroupAdminPolicy.Stage(
                pair.Alice.Group, _cs, [pair.BobAccount]));
    }

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
