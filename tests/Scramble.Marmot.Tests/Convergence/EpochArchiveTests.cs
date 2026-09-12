using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests.Convergence;

/// <summary>
/// The archive a forked group is rebuilt from.
/// </summary>
/// <remarks>
/// What is checked here is not that bytes round-trip — the storage tests cover
/// that — but that what comes back is a <i>usable</i> group: one somebody else's
/// commit can be replayed onto, and one that is nobody else's copy. Those are
/// the two properties candidate evaluation rests on, and both fail silently.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class EpochArchiveTests : IDisposable
{
    private readonly StorageFixture _fixture = new();
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];

    private readonly DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddSeconds(Now);

    public void Dispose() => _fixture.Dispose();

    private EpochArchive NewArchive() =>
        new(_fixture.Provider, _cs, ConvergencePolicy.V1, () => _now);

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

    private sealed record Pair(CreatedGroup Alice, MlsGroup Bob, GroupId GroupId);

    private async Task<Pair> PairAsync()
    {
        CreatedGroup alice = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        var bundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);
        StagedCommit staged = MarmotGroupInvite.Add(alice.Group, _cs, [bundle.KeyPackage]);
        staged.Applied();

        MlsGroup bob = MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey,
            config: MarmotGroupSettings.Create());

        return new Pair(alice, bob, new GroupId(alice.GroupId));
    }

    private static byte[] Wire(PublicMessage commit) =>
        TlsCodec.Serialize(new MlsMessage(WireFormat.MlsPublicMessage, commit).WriteTo);

    // ---- What comes back ----

    [Fact]
    public async Task ARestoredEpochStandsWhereItDidAndKnowsWhoWasThere()
    {
        Pair pair = await PairAsync();
        EpochArchive archive = NewArchive();

        await archive.CaptureAsync(pair.GroupId, pair.Bob, CommitOrderingPriority.Ordinary);

        EpochWindow window = await archive.LoadWindowAsync(
            pair.GroupId, new EpochId(pair.Bob.Epoch));

        MlsGroup restored = window.Restore(new EpochId(pair.Bob.Epoch))!;

        Assert.Equal(pair.Bob.Epoch, restored.Epoch);
        Assert.Equal(pair.Bob.MyLeafIndex, restored.MyLeafIndex);
        Assert.Equal(pair.Bob.GetMembers().Count, restored.GetMembers().Count);
    }

    [Fact]
    public async Task AnotherMembersCommitReplaysOntoARestoredEpoch()
    {
        // The property the whole archive exists for. A branch is scored by being
        // built, and building one means applying a commit somebody else made to
        // a state we have already left -- so a restored copy that cannot take a
        // commit is an archive that has kept nothing useful.
        Pair pair = await PairAsync();
        EpochArchive archive = NewArchive();

        ulong forkEpoch = pair.Bob.Epoch;
        await archive.CaptureAsync(pair.GroupId, pair.Bob, CommitOrderingPriority.Ordinary);

        var (hers, _) = pair.Alice.Group.CommitPublic();
        pair.Alice.Group.MergePendingCommit();

        // We move on too, so the live group can no longer take her commit.
        var (_, _) = pair.Bob.CommitPublic();
        pair.Bob.MergePendingCommit();

        EpochWindow window = await archive.LoadWindowAsync(
            pair.GroupId, new EpochId(pair.Bob.Epoch));

        MlsGroup probe = window.Restore(new EpochId(forkEpoch))!;
        var message = MlsMessage.ReadFrom(new TlsReader(Wire(hers)));
        probe.ProcessCommit((PublicMessage)message.Body);

        Assert.Equal(forkEpoch + 1, probe.Epoch);
        Assert.Equal(forkEpoch + 1, pair.Alice.Group.Epoch);
    }

    [Fact]
    public async Task EachRestoreIsItsOwnCopy()
    {
        // Two branches forking from one epoch are evaluated against that epoch
        // in turn. Handing both the same instance would let the first branch's
        // commits count towards the second, and the second would be scored on a
        // history nobody published.
        Pair pair = await PairAsync();
        EpochArchive archive = NewArchive();

        ulong forkEpoch = pair.Bob.Epoch;
        await archive.CaptureAsync(pair.GroupId, pair.Bob, CommitOrderingPriority.Ordinary);

        var (hers, _) = pair.Alice.Group.CommitPublic();
        pair.Alice.Group.MergePendingCommit();

        EpochWindow window = await archive.LoadWindowAsync(pair.GroupId, new EpochId(forkEpoch));

        MlsGroup first = window.Restore(new EpochId(forkEpoch))!;
        var message = MlsMessage.ReadFrom(new TlsReader(Wire(hers)));
        first.ProcessCommit((PublicMessage)message.Body);

        MlsGroup second = window.Restore(new EpochId(forkEpoch))!;

        Assert.Equal(forkEpoch + 1, first.Epoch);
        Assert.Equal(forkEpoch, second.Epoch);
    }

    [Fact]
    public async Task AnEpochNeverArchivedRestoresToNull()
    {
        Pair pair = await PairAsync();
        EpochArchive archive = NewArchive();

        await archive.CaptureAsync(pair.GroupId, pair.Bob, CommitOrderingPriority.Ordinary);

        EpochWindow window = await archive.LoadWindowAsync(
            pair.GroupId, new EpochId(pair.Bob.Epoch));

        Assert.Null(window.Restore(new EpochId(pair.Bob.Epoch + 1)));
        Assert.Null(window.TipPriorityAt(new EpochId(pair.Bob.Epoch + 1)));
    }

    [Fact]
    public async Task TheClassOfTheCommitThatMadeAnEpochIsKeptWithIt()
    {
        // The live branch's tip class cannot be recomputed later -- applying the
        // commit cleared the cache its proposal references resolve against -- so
        // if it is not remembered here it is not available at all.
        Pair pair = await PairAsync();
        EpochArchive archive = NewArchive();

        await archive.CaptureAsync(pair.GroupId, pair.Bob, CommitOrderingPriority.Privileged);

        EpochWindow window = await archive.LoadWindowAsync(
            pair.GroupId, new EpochId(pair.Bob.Epoch));

        Assert.Equal(
            CommitOrderingPriority.Privileged,
            window.TipPriorityAt(new EpochId(pair.Bob.Epoch)));
    }

    // ---- The window ----

    [Fact]
    public void TheHorizonIsTheOneBranchSelectionApplies()
    {
        // Not one epoch tighter. A branch forking at tip - MaxRewindCommits is
        // eligible, so its fork epoch has to be restorable, or the policy and
        // the archive disagree about what this member is allowed to adopt.
        EpochArchive archive = NewArchive();
        var tip = new EpochId(20);

        EpochId oldest = archive.OldestRetainedFor(tip);

        Assert.Equal(20 - ConvergencePolicy.V1MaxRewindCommits, oldest.Value);

        var forkedAtTheLimit = new BranchCandidate(
            "b", oldest.Value, tip.Value, CommitOrderingPriority.Ordinary, [1], [2], []);

        Assert.True(BranchSelection.IsEligible(tip.Value, forkedAtTheLimit, ConvergencePolicy.V1));
    }

    [Fact]
    public void TheHorizonSaturatesRatherThanWrapping()
    {
        // Epochs are unsigned, and a young group sits below the horizon for its
        // first few commits. Wrapping here would ask storage for everything
        // above epoch 2^64 - 3 and retain nothing at all.
        EpochArchive archive = NewArchive();

        Assert.Equal(0UL, archive.OldestRetainedFor(new EpochId(0)).Value);
        Assert.Equal(0UL, archive.OldestRetainedFor(new EpochId(2)).Value);
    }

    [Fact]
    public async Task CapturingDropsWhatHasFallenOutOfHorizon()
    {
        // Pruning rides along with capture rather than running on its own
        // schedule: an archive trimmed separately is either holding key material
        // for branches the policy already refuses, or -- the failure that
        // matters -- quietly short of the horizon when a fork arrives.
        Pair pair = await PairAsync();
        EpochArchive archive = NewArchive();

        await archive.CaptureAsync(pair.GroupId, pair.Bob, CommitOrderingPriority.Ordinary);

        for (int i = 0; i < 8; i++)
        {
            var (commit, _) = pair.Alice.Group.CommitPublic();
            pair.Alice.Group.MergePendingCommit();

            var message = MlsMessage.ReadFrom(new TlsReader(Wire(commit)));
            pair.Bob.ProcessCommit((PublicMessage)message.Body);

            await archive.CaptureAsync(pair.GroupId, pair.Bob, CommitOrderingPriority.Ordinary);
        }

        var tip = new EpochId(pair.Bob.Epoch);
        EpochWindow window = await archive.LoadWindowAsync(pair.GroupId, tip);

        Assert.Equal(
            Enumerable
                .Range(0, (int)ConvergencePolicy.V1MaxRewindCommits + 1)
                .Select(i => tip.Value - (ulong)i)
                .Order(),
            window.Epochs.Select(e => e.Value));

        Assert.Null(
            await _fixture.Provider.GetEpochCheckpointAsync(
                pair.GroupId, new EpochId(tip.Value - ConvergencePolicy.V1MaxRewindCommits - 1)));
    }
}
