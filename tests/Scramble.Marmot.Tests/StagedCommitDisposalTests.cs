using DotnetMls.Crypto;
using DotnetMls.Group;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The window between staging a commit and publishing it.
/// </summary>
/// <remarks>
/// <para>
/// A staged commit lives on the MLS group, and the group refuses to stage a
/// second one. So a path that stages a commit and then throws — while wrapping
/// it for transport, while writing a record, while talking to a relay — used to
/// leave the group unable to commit anything ever again, <i>including the
/// retry</i>. The first symptom is the retry failing for a reason that has
/// nothing to do with why the original attempt did.
/// </para>
/// <para>
/// Disposal closes that window, and stops closing it the moment publishing
/// starts: after that a peer may hold the commit, and clearing it locally is
/// what causes a fork rather than what prevents one.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class StagedCommitDisposalTests
{
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];

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
        MarmotGroupBuilder.CreateAsync(_cs, new LocalSigner(), "Rakes", "", Now, Relays);

    private Task<MarmotKeyPackageBundle> NewInviteeAsync() =>
        MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);

    [Fact]
    public async Task AnAbandonedCommitDoesNotStrandTheGroup()
    {
        // The bug this exists for. Without disposal the group keeps a pending
        // commit forever and every later commit fails -- including the retry,
        // which then fails for a reason unrelated to the original failure.
        CreatedGroup group = await NewGroupAsync();
        var invitee = await NewInviteeAsync();

        try
        {
            using StagedCommit staged = MarmotGroupInvite.Add(
                group.Group, _cs, [invitee.KeyPackage]);

            throw new InvalidOperationException("the publish blew up");
        }
        catch (InvalidOperationException)
        {
            // Swallowed on purpose: the point is what the group looks like now.
        }

        Assert.False(group.Group.HasPendingCommit);

        // And the retry works, which is the outcome that actually matters.
        using StagedCommit retry = MarmotGroupInvite.Add(
            group.Group, _cs, [invitee.KeyPackage]);
        retry.Applied();

        Assert.Equal(2, group.Group.GetMembers().Count);
    }

    [Fact]
    public async Task DisposalAfterApplyingChangesNothing()
    {
        CreatedGroup group = await NewGroupAsync();
        var invitee = await NewInviteeAsync();

        using (StagedCommit staged = MarmotGroupInvite.Add(
            group.Group, _cs, [invitee.KeyPackage]))
        {
            staged.Applied();
        }

        Assert.Equal(1u, group.Group.Epoch);
        Assert.Equal(2, group.Group.GetMembers().Count);
        Assert.False(group.Group.HasPendingCommit);
    }

    [Fact]
    public async Task DisposalAfterDiscardingChangesNothing()
    {
        CreatedGroup group = await NewGroupAsync();
        var invitee = await NewInviteeAsync();

        using (StagedCommit staged = MarmotGroupInvite.Add(
            group.Group, _cs, [invitee.KeyPackage]))
        {
            staged.Discard();
        }

        Assert.Equal(0u, group.Group.Epoch);
        Assert.Single(group.Group.GetMembers());
        Assert.False(group.Group.HasPendingCommit);
    }

    [Fact]
    public async Task OncePublishingHasStartedDisposalLeavesTheCommitAlone()
    {
        // The safety net has to stop here. Past this point a relay may hold the
        // commit and other members may have applied it; clearing it locally
        // would leave us alone on the old epoch, insisting nothing happened.
        // A stranded pending commit is recoverable, a silent fork is not.
        CreatedGroup group = await NewGroupAsync();
        var invitee = await NewInviteeAsync();

        try
        {
            using StagedCommit staged = MarmotGroupInvite.Add(
                group.Group, _cs, [invitee.KeyPackage]);

            staged.Publishing();

            throw new TimeoutException("the relay never answered");
        }
        catch (TimeoutException)
        {
        }

        Assert.True(
            group.Group.HasPendingCommit,
            "A commit that may have reached a peer must not be cleared by disposal.");
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        CreatedGroup group = await NewGroupAsync();
        var invitee = await NewInviteeAsync();

        StagedCommit staged = MarmotGroupInvite.Add(group.Group, _cs, [invitee.KeyPackage]);

        staged.Dispose();
        staged.Dispose();

        Assert.False(group.Group.HasPendingCommit);
        Assert.Equal(0u, group.Group.Epoch);
    }

    [Fact]
    public async Task ASelfUpdateIsCoveredTheSameWay()
    {
        // Self-update refuses to stage when a commit is already pending, so an
        // orphan from an earlier attempt would block key rotation entirely --
        // the one operation a group should always be able to perform.
        CreatedGroup group = await NewGroupAsync();

        try
        {
            using StagedCommit staged = MarmotSelfUpdate.Stage(group.Group);
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException)
        {
        }

        using StagedCommit rotation = MarmotSelfUpdate.Stage(group.Group);
        rotation.Applied();

        Assert.Equal(1u, group.Group.Epoch);
    }
}
