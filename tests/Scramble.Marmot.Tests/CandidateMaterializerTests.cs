using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Turning stored commits into branches that can be compared.
/// </summary>
/// <remarks>
/// A commit's tip epoch and committer are what applying it produces, not
/// anything in its header, so a branch cannot be scored without being built.
/// Building one means returning to the epoch it forks from — which an MLS group
/// cannot do — so everything here runs on restored throwaway copies, and the
/// live group is not touched until a winner is known.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class CandidateMaterializerTests
{
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;
    /// <summary>
    /// The live branch's tip class, stated rather than defaulted.
    /// </summary>
    /// <remarks>
    /// Every fixture here builds its current branch from ordinary commits, so
    /// this is the true value and not a placeholder. It is written at each call
    /// because the parameter has no default: a branch whose own tip is
    /// misreported competes on the wrong terms, and that is worth one word per
    /// call site.
    /// </remarks>
    private const CommitOrderingPriority Ordinary = CommitOrderingPriority.Ordinary;

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

    /// <summary>An archive of exported group states, keyed by epoch.</summary>
    private sealed class Archive
    {
        private readonly Dictionary<ulong, byte[]> _byEpoch = [];
        private readonly ICipherSuite _cs;

        public Archive(ICipherSuite cs) => _cs = cs;

        public int Restores { get; private set; }

        public void Capture(MlsGroup group) => _byEpoch[group.Epoch] = group.Export();

        public void Forget(ulong epoch) => _byEpoch.Remove(epoch);

        public MlsGroup? Restore(EpochId epoch)
        {
            if (!_byEpoch.TryGetValue(epoch.Value, out byte[]? blob))
                return null;

            Restores++;
            return MlsGroup.Import(blob, _cs);
        }
    }

    private sealed record Fixture(
        CreatedGroup Alice, MlsGroup Bob, MlsGroup Carol, Archive Archive);

    /// <summary>Three members at one epoch, with every epoch archived.</summary>
    private async Task<Fixture> TrioAsync()
    {
        var archive = new Archive(_cs);

        CreatedGroup alice = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        var bobBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);
        var carolBundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);

        StagedCommit staged = MarmotGroupInvite.Add(
            alice.Group, _cs, [bobBundle.KeyPackage, carolBundle.KeyPackage]);
        staged.Applied();

        MlsGroup Join(MarmotKeyPackageBundle bundle) => MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey,
            config: MarmotGroupSettings.Create());

        MlsGroup bob = Join(bobBundle);
        MlsGroup carol = Join(carolBundle);

        archive.Capture(carol);

        return new Fixture(alice, bob, carol, archive);
    }

    private static byte[] Wire(PublicMessage commit) =>
        TlsCodec.Serialize(new MlsMessage(WireFormat.MlsPublicMessage, commit).WriteTo);

    /// <summary>
    /// An empty commit from a member, recorded against the epoch it was built
    /// from.
    /// </summary>
    /// <remarks>
    /// The source epoch must be read <i>before</i> merging. Afterwards the
    /// author has advanced, and recording that later epoch would describe the
    /// commit as forking from the state it produced rather than the state it
    /// was built on -- which is a chain that starts one step ahead of itself
    /// and therefore matches nothing.
    /// </remarks>
    private static StoredCommit Commit(MlsGroup author, bool ours = false)
    {
        var sourceEpoch = new EpochId(author.Epoch);

        var (commit, _) = author.CommitPublic();
        author.MergePendingCommit();

        byte[] wire = Wire(commit);
        return new StoredCommit(MessageId.FromMlsBytes(wire), sourceEpoch, wire, ours);
    }

    private CandidateMaterializer NewMaterializer(Archive archive) =>
        new(ConvergencePolicy.V1, archive.Restore);

    private static IReadOnlyList<AppWitness> NoWitnesses(MlsGroup _) => [];

    // ---- Building a branch ----

    [Fact]
    public async Task ACompetingCommitBecomesAScorableBranch()
    {
        // The tip epoch and committer are only knowable by applying it, which is
        // the whole reason this class exists.
        Fixture f = await TrioAsync();
        StoredCommit fromAlice = Commit(f.Alice.Group);

        MaterializationResult result = NewMaterializer(f.Archive)
            .Materialize(f.Carol, "genesis", Ordinary, [fromAlice], NoWitnesses);

        BranchCandidate built = Assert.Single(
            result.Candidates, c => !string.Equals(c.Id, "genesis", StringComparison.Ordinal));

        Assert.Equal(f.Carol.Epoch, built.ForkEpoch);
        Assert.Equal(f.Carol.Epoch + 1, built.TipEpoch);
        Assert.Empty(result.Refused);
    }

    [Fact]
    public async Task TheBranchWeAreOnAlwaysCompetes()
    {
        // Leaving it out would let a late-arriving commit win by being the only
        // candidate, which is how a group gets talked off a history it has
        // already delivered.
        Fixture f = await TrioAsync();
        StoredCommit fromAlice = Commit(f.Alice.Group);

        MaterializationResult result = NewMaterializer(f.Archive)
            .Materialize(f.Carol, "genesis", Ordinary, [fromAlice], NoWitnesses);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, c => c.Id == "genesis");
    }

    [Fact]
    public async Task TheLiveGroupIsNotTouchedByMaterializing()
    {
        // A materializer that mutated the live group to evaluate a branch would
        // leave the member on whichever candidate it happened to try last.
        Fixture f = await TrioAsync();
        ulong before = f.Carol.Epoch;

        StoredCommit fromAlice = Commit(f.Alice.Group);
        NewMaterializer(f.Archive).Materialize(f.Carol, "genesis", Ordinary, [fromAlice], NoWitnesses);

        Assert.Equal(before, f.Carol.Epoch);
        Assert.Equal(3, f.Carol.GetMembers().Count);
    }

    [Fact]
    public async Task AChainOfCommitsBecomesOneDeeperBranch()
    {
        // Depth is the first selection rule, so a chain that scored as separate
        // one-commit branches would lose to a single commit it should beat.
        Fixture f = await TrioAsync();

        StoredCommit first = Commit(f.Alice.Group);
        StoredCommit second = Commit(f.Alice.Group);

        MaterializationResult result = NewMaterializer(f.Archive)
            .Materialize(f.Carol, "genesis", Ordinary, [first, second], NoWitnesses);

        BranchCandidate built = Assert.Single(result.Candidates, c => c.Id != "genesis");

        Assert.Equal(f.Carol.Epoch + 2, built.TipEpoch);
        Assert.Equal(2, result.CommitsApplied);
    }

    // ---- Refusals ----

    [Fact]
    public async Task ACommitForkingFromAPrunedEpochIsRefused()
    {
        Fixture f = await TrioAsync();
        StoredCommit fromAlice = Commit(f.Alice.Group);

        f.Archive.Forget(f.Carol.Epoch);

        MaterializationResult result = NewMaterializer(f.Archive)
            .Materialize(f.Carol, "genesis", Ordinary, [fromAlice], NoWitnesses);

        Assert.Equal(
            MaterializationRefusal.NoSnapshot,
            Assert.Single(result.Refused).Reason);

        // Only the branch we hold survives, which is the safe outcome: we keep
        // what we have rather than adopting something we cannot verify.
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task ACommitThatDoesNotApplyIsNotABranch()
    {
        // Refusing it here keeps an unprocessable message out of a comparison it
        // could otherwise win on depth alone.
        Fixture f = await TrioAsync();

        StoredCommit good = Commit(f.Alice.Group);
        byte[] wire = (byte[])good.Wire.Clone();
        wire[^1] ^= 0xff;

        var corrupt = new StoredCommit(
            MessageId.FromMlsBytes(wire), good.SourceEpoch, wire, IsOurs: false);

        MaterializationResult result = NewMaterializer(f.Archive)
            .Materialize(f.Carol, "genesis", Ordinary, [corrupt], NoWitnesses);

        Assert.Equal(
            MaterializationRefusal.DoesNotApply,
            Assert.Single(result.Refused).Reason);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task OurOwnCommitIsRefusedRatherThanGuessedAt()
    {
        // MLS will not process a commit it authored, and the state it would
        // produce is the branch we are already on. Inventing a candidate for it
        // would mean scoring a branch we never built.
        Fixture f = await TrioAsync();
        StoredCommit ours = Commit(f.Alice.Group, ours: true);

        MaterializationResult result = NewMaterializer(f.Archive)
            .Materialize(f.Carol, "genesis", Ordinary, [ours], NoWitnesses);

        Assert.Equal(
            MaterializationRefusal.UnreplayableOwnCommit,
            Assert.Single(result.Refused).Reason);
        Assert.Single(result.Candidates);
    }

    // ---- The replay budget ----

    [Fact]
    public void TheBudgetIsDerivedFromTheRewindHorizon()
    {
        // Not a tuned number. The horizon bounds how far back a branch may
        // legitimately fork, so a pass never needs more than that per branch.
        var materializer = new CandidateMaterializer(ConvergencePolicy.V1, _ => null);

        Assert.Equal(
            (int)ConvergencePolicy.V1MaxRewindCommits * CandidateMaterializer.MaxBranchesPerPass,
            materializer.ReplayBudget);
    }

    [Fact]
    public async Task AFloodOfCommitsCannotSpendUnboundedWork()
    {
        // The security bound. Each commit costs a full MLS application and they
        // arrive from the network, so without a cap a peer can hand us a chain
        // of plausible-looking commits and make us pay for all of them before we
        // learn they are worthless.
        Fixture f = await TrioAsync();
        var materializer = NewMaterializer(f.Archive);

        var flood = new List<StoredCommit>();
        for (int i = 0; i < materializer.ReplayBudget + 20; i++)
            flood.Add(Commit(f.Alice.Group));

        MaterializationResult result = materializer.Materialize(
            f.Carol, "genesis", Ordinary, flood, NoWitnesses);

        Assert.True(
            result.CommitsApplied <= materializer.ReplayBudget,
            $"Applied {result.CommitsApplied} commits against a budget of "
            + $"{materializer.ReplayBudget}.");

        Assert.Contains(
            result.Refused, r => r.Reason == MaterializationRefusal.BudgetExhausted);
    }

    // ---- Reorg ----

    [Fact]
    public async Task ReorgRebuildsTheWinningBranch()
    {
        Fixture f = await TrioAsync();
        StoredCommit fromAlice = Commit(f.Alice.Group);

        var materializer = NewMaterializer(f.Archive);
        MaterializationResult result = materializer.Materialize(
            f.Carol, "genesis", Ordinary, [fromAlice], NoWitnesses);

        BranchCandidate winner = result.Candidates.Single(c => c.Id != "genesis");
        MlsGroup rebuilt = materializer.Reorg(winner, [fromAlice]);

        Assert.Equal(winner.TipEpoch, rebuilt.Epoch);
    }

    [Fact]
    public async Task ReorgRebuildsAWholeChainNotJustTheTip()
    {
        Fixture f = await TrioAsync();
        StoredCommit first = Commit(f.Alice.Group);
        StoredCommit second = Commit(f.Alice.Group);

        var materializer = NewMaterializer(f.Archive);
        MaterializationResult result = materializer.Materialize(
            f.Carol, "genesis", Ordinary, [first, second], NoWitnesses);

        BranchCandidate winner = result.Candidates.Single(c => c.Id != "genesis");
        MlsGroup rebuilt = materializer.Reorg(winner, [first, second]);

        Assert.Equal(f.Carol.Epoch + 2, rebuilt.Epoch);
    }

    [Fact]
    public async Task ReorgLeavesTheCallerHoldingTheOldGroupWhenItCannotFinish()
    {
        // The only always-recoverable outcome. Staying on a losing branch is a
        // disagreement later convergence resolves; a half-applied reorg is a
        // group whose state matches nobody's.
        Fixture f = await TrioAsync();
        StoredCommit first = Commit(f.Alice.Group);
        StoredCommit second = Commit(f.Alice.Group);

        var materializer = NewMaterializer(f.Archive);
        MaterializationResult result = materializer.Materialize(
            f.Carol, "genesis", Ordinary, [first, second], NoWitnesses);

        BranchCandidate winner = result.Candidates.Single(c => c.Id != "genesis");
        ulong before = f.Carol.Epoch;

        // The middle of the chain is missing, so the branch cannot be rebuilt.
        Assert.Throws<InvalidOperationException>(
            () => materializer.Reorg(winner, [second]));

        Assert.Equal(before, f.Carol.Epoch);
        Assert.Equal(3, f.Carol.GetMembers().Count);
    }

    [Fact]
    public async Task ReorgRefusesWhenTheForkEpochIsGone()
    {
        Fixture f = await TrioAsync();
        StoredCommit fromAlice = Commit(f.Alice.Group);

        var materializer = NewMaterializer(f.Archive);
        MaterializationResult result = materializer.Materialize(
            f.Carol, "genesis", Ordinary, [fromAlice], NoWitnesses);

        BranchCandidate winner = result.Candidates.Single(c => c.Id != "genesis");
        f.Archive.Forget(winner.ForkEpoch);

        var ex = Assert.Throws<InvalidOperationException>(
            () => materializer.Reorg(winner, [fromAlice]));

        Assert.Contains("no longer retained", ex.Message);
    }

    [Fact]
    public async Task ReorgRefusesAnArchiveThatReturnsTheWrongEpoch()
    {
        // The post-condition, and it is reachable rather than decorative. The
        // replay loop advances one epoch per commit so it cannot overshoot on
        // its own -- but it starts wherever the archive puts it, and an archive
        // that hands back a later epoch than asked for produces a group that
        // never enters the loop and is not the branch that was scored.
        //
        // Returning it anyway would put the member on a history nobody selected
        // while reporting that convergence succeeded.
        Fixture f = await TrioAsync();
        StoredCommit fromAlice = Commit(f.Alice.Group);

        MaterializationResult result = NewMaterializer(f.Archive)
            .Materialize(f.Carol, "genesis", Ordinary, [fromAlice], NoWitnesses);

        BranchCandidate winner = result.Candidates.Single(c => c.Id != "genesis");

        // An archive that ignores the epoch and always returns a later state.
        MlsGroup ahead = f.Archive.Restore(new EpochId(winner.ForkEpoch))!;
        ahead.ProcessCommit(ReadCommit(fromAlice.Wire));
        ahead.ProcessCommit(ReadCommit(Commit(f.Alice.Group).Wire));

        var wrong = new CandidateMaterializer(ConvergencePolicy.V1, _ => ahead);

        var ex = Assert.Throws<InvalidOperationException>(
            () => wrong.Reorg(winner, [fromAlice]));

        Assert.Contains("not the", ex.Message);
    }

    private static PublicMessage ReadCommit(byte[] wire) =>
        (PublicMessage)MlsMessage.ReadFrom(new TlsReader(wire)).Body;

    // ---- Witnesses ----

    [Fact]
    public async Task WitnessesAreTakenFromTheBuiltBranchNotItsName()
    {
        // A witness is evidence only if the branch can actually decrypt it, so
        // the callback receives the materialized state rather than an id. Taking
        // them by branch id is what made the harness diverge four ways.
        Fixture f = await TrioAsync();
        StoredCommit fromAlice = Commit(f.Alice.Group);

        var seen = new List<ulong>();

        NewMaterializer(f.Archive).Materialize(
            f.Carol,
            "genesis",
            Ordinary,
            [fromAlice],
            probe =>
            {
                seen.Add(probe.Epoch);
                return [];
            });

        // Once for the built branch at its tip, once for the live group.
        Assert.Contains(f.Carol.Epoch + 1, seen);
        Assert.Contains(f.Carol.Epoch, seen);
    }
}
