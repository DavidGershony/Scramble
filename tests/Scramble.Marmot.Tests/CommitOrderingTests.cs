using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Classifying somebody else's commit for branch ordering.
/// </summary>
/// <remarks>
/// <para>
/// The rule itself lives in <c>CommitAuthorization</c> and has its own tests.
/// What is checked here is the part that had no caller until now: reading a
/// real commit off the wire and deciding which class it is. A classifier that
/// answers the same thing for everything does not make the tie-break
/// conservative, it deletes a rule the whole group orders by — priority sits
/// above both the committer and the digest, so every decision silently drops
/// to the rule below it.
/// </para>
/// <para>
/// The ordering matters most where it is invisible: two members that classify
/// the same commit differently pick different branches from an identical
/// candidate set, with no error anywhere.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class CommitOrderingTests
{
    private static readonly string[] Relays = ["wss://relay.example"];
    private static readonly ulong Now = 1_700_000_000;

    private readonly ICipherSuite _cs = new CipherSuite0x0001();

    private sealed class LocalSigner : IAccountIdentityProofSigner
    {
        public LocalSigner()
        {
            var (secret, publicKey) = Bip340.GenerateKeyPair();
            Secret = secret;
            AccountPublicKey = publicKey;
        }

        public byte[] Secret { get; }

        public ReadOnlyMemory<byte> AccountPublicKey { get; }

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(Secret, template.ComputeId()));
    }

    /// <summary>
    /// Three members, with the third's account key to hand.
    /// </summary>
    /// <remarks>
    /// Three rather than two because a removal has to be classified by somebody
    /// it does not remove: the member being removed cannot materialise that
    /// commit as a branch at all, so a pair would only ever test the refusal.
    /// </remarks>
    private async Task<(CreatedGroup Host, MlsGroup Guest, MlsGroup Spare, byte[] SpareAccount)>
        TrioAsync()
    {
        CreatedGroup host = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        MarmotKeyPackageBundle guestBundle =
            await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);

        var spareSigner = new LocalSigner();
        MarmotKeyPackageBundle spareBundle =
            await MarmotKeyPackageBuilder.CreateAsync(_cs, spareSigner, Now);

        StagedCommit staged = MarmotGroupInvite.Add(
            host.Group, _cs, [guestBundle.KeyPackage, spareBundle.KeyPackage]);

        staged.Applied();

        MlsGroup Join(MarmotKeyPackageBundle bundle) => MlsGroup.ProcessWelcome(
            _cs,
            staged.Welcome!,
            bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey,
            config: MarmotGroupSettings.Create());

        return (host, Join(guestBundle), Join(spareBundle), spareSigner.AccountPublicKey.ToArray());
    }

    [Fact]
    public async Task ASelfUpdateIsOrdinary()
    {
        // The shape a non-admin is allowed to commit: the committer's own path
        // and nothing else. If this came back privileged, every routine leaf
        // rotation would outrank a real admin action in a race.
        var (host, guest, _, _) = await TrioAsync();

        var (commit, _) = guest.CommitPublic();

        Assert.Equal(
            CommitOrderingPriority.Ordinary, CommitOrdering.PriorityOf(host.Group, commit));
    }

    [Fact]
    public async Task RemovingSomebodyElseIsPrivileged()
    {
        // Only an admin may do this, so it must outrank an ordinary commit when
        // the two race. This is the case a hardcoded "ordinary" loses.
        var (host, guest, _, spareAccount) = await TrioAsync();

        using StagedCommit staged = MarmotGroupInvite.Remove(host.Group, [spareAccount]);

        Assert.Equal(
            CommitOrderingPriority.Privileged,
            CommitOrdering.PriorityOf(guest, staged.Commit));
    }

    [Fact]
    public async Task ACommitOfSomebodyElsesSelfRemoveIsOrdinary()
    {
        // The second allowed non-admin shape, and the one that can only be read
        // before the commit is applied: the commit cites the proposal by hash,
        // so the classification depends on the proposal cache that applying it
        // clears.
        var (host, guest, _, _) = await TrioAsync();

        PublicMessage request = MarmotGroupLeave.Request(guest);
        GroupHandshake.Receive(host.Group, Serialize(request));

        using StagedCommit? departure = MarmotGroupLeave.CommitDepartures(host.Group);
        Assert.NotNull(departure);

        Assert.Equal(
            CommitOrderingPriority.Ordinary,
            CommitOrdering.PriorityOf(host.Group, departure!.Commit));
    }

    [Fact]
    public async Task AReferenceWeCannotResolveIsPrivileged()
    {
        // Fails closed, and the direction is the point. We cannot see what the
        // proposal was, so we cannot know the commit is one of the two shapes a
        // non-admin may make — and a commit we cannot resolve will not apply
        // anyway, so the branch is refused long before this is compared.
        var (host, guest, spare, _) = await TrioAsync();

        PublicMessage request = MarmotGroupLeave.Request(guest);
        GroupHandshake.Receive(host.Group, Serialize(request));

        using StagedCommit? departure = MarmotGroupLeave.CommitDepartures(host.Group);

        // Asked of the member the proposal never reached. Not the leaver: it
        // caches its own request, precisely so it can read the commit that
        // removes it.
        Assert.Equal(
            CommitOrderingPriority.Privileged,
            CommitOrdering.PriorityOf(spare, departure!.Commit));
    }

    [Fact]
    public async Task AProposalIsNotClassifiedAtAll()
    {
        // Null rather than a class. A caller that handed a proposal to the
        // branch ordering has made a mistake, and a default would carry it.
        var (host, guest, _, _) = await TrioAsync();

        PublicMessage request = MarmotGroupLeave.Request(guest);

        Assert.Null(CommitOrdering.PriorityOf(host.Group, request));
    }

    [Fact]
    public async Task TheClassReachesTheCandidateThatIsScored()
    {
        // The wiring, not the rule. A materializer that reads the class
        // correctly and then files every branch as ordinary is exactly as
        // broken as one that cannot read it.
        var (host, guest, _, spareAccount) = await TrioAsync();

        var archive = new Dictionary<ulong, byte[]> { [host.Group.Epoch] = guest.Export() };


        using StagedCommit staged = MarmotGroupInvite.Remove(host.Group, [spareAccount]);
        byte[] wire = Serialize(staged.Commit);
        staged.Publishing();
        staged.Applied();

        var materializer = new CandidateMaterializer(
            ConvergencePolicy.V1,
            epoch => archive.TryGetValue(epoch.Value, out byte[]? blob)
                ? MlsGroup.Import(blob, _cs)
                : null);

        MaterializationResult result = materializer.Materialize(
            guest,
            "genesis",
            CommitOrderingPriority.Ordinary,
            [new StoredCommit(
                MessageId.FromMlsBytes(wire),
                new EpochId(guest.Epoch),
                wire,
                IsOurs: false)],
            _ => []);

        BranchCandidate theirs = Assert.Single(
            result.Candidates.Where(c => c.Id != "genesis"));

        Assert.Equal(CommitOrderingPriority.Privileged, theirs.TipPriority);
    }

    private static byte[] Serialize(PublicMessage message) =>
        TlsCodec.Serialize(new MlsMessage(WireFormat.MlsPublicMessage, message).WriteTo);
}
