using DotnetMls.Crypto;
using DotnetMls.Group;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.Ingest;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Engine.Session;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Ingest;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Receiving an envelope sealed under an epoch the group has left.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every test here goes in as an envelope, not as MLS bytes.</b> That is the
/// whole subject: the failure being closed happens at the transport layer,
/// before ingest is reached, and a test that handed ingest peeled bytes would
/// have stepped straight over it. The existing session tests all peel with the
/// sender's own group, so none of them could ever have seen it.
/// </para>
/// <para>
/// The sender is an ordinary <see cref="MlsGroup"/> rather than a second
/// session, so nothing it does is arranged by the code under test.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class EpochBoundaryPeelTests : IDisposable
{
    private readonly StorageFixture _fixture = new();
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];

    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddSeconds(Now);

    public void Dispose() => _fixture.Dispose();

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

        public string Hex => Convert.ToHexString(AccountPublicKey.Span).ToLowerInvariant();

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(Secret, template.ComputeId()));
    }

    private sealed class UnreachableRelay : ICommitRelay
    {
        public Task<CommitPublishOutcome> PublishAsync(
            string envelope, CancellationToken ct = default) =>
            throw new NotSupportedException("Nothing here publishes a commit of ours.");
    }

    private MarmotSessionHost Host() =>
        new(_fixture.Provider, _cs, new UnreachableRelay(), ConvergencePolicy.V1, () => _now);

    private sealed record Pair(
        MarmotSession Us, MlsGroup Them, LocalSigner TheirSigner, GroupId GroupId);

    /// <summary>Us as a session, and one other member who is just an MLS group.</summary>
    private async Task<Pair> PairAsync()
    {
        CreatedGroup us = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        var theirSigner = new LocalSigner();
        var bundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, theirSigner, Now);

        StagedCommit staged = MarmotGroupInvite.Add(us.Group, _cs, [bundle.KeyPackage]);
        staged.Applied();

        MlsGroup them = MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey,
            config: MarmotGroupSettings.Create());

        MarmotSession session = await Host().AdoptAsync(us.ToRecord(_now), us.Group);

        return new Pair(session, them, theirSigner, new GroupId(us.GroupId));
    }

    private static readonly NostrGroupPeeler Peeler = new();

    /// <summary>An application message from the member who is not a session.</summary>
    private static string Says(Pair pair, string text) =>
        GroupMessages.Send(
            pair.Them,
            Peeler,
            MarmotAppEvent.Chat(pair.TheirSigner.Hex, (long)Now, text),
            pair.TheirSigner.AccountPublicKey.Span);

    /// <summary>
    /// A commit from the other member, wrapped at the epoch it was built in.
    /// </summary>
    /// <remarks>
    /// Wrapped before it is applied, which is forced rather than stylistic:
    /// wrapping afterwards would seal it under the key of the epoch the commit
    /// creates, and no recipient could peel it.
    /// </remarks>
    private static string Commits(MlsGroup sender)
    {
        using StagedCommit staged = MarmotSelfUpdate.Stage(sender);
        string envelope = GroupHandshake.Wrap(sender, Peeler, staged.Commit);
        staged.Publishing();
        staged.Applied();
        return envelope;
    }

    /// <summary>Moves us and them forward together by one of their commits.</summary>
    private static async Task StepAsync(Pair pair)
    {
        IngestResult applied = await pair.Us.ReceiveAsync(Commits(pair.Them));
        Assert.IsType<IngestOutcome.Processed>(applied.Outcome);
    }

    // ---- The live epoch still works ----

    [Fact]
    public async Task AMessageSentAtTheEpochWeAreOnArrivesThroughTheLiveKey()
    {
        // The common case, pinned so a fallback cannot quietly become the only
        // path that works.
        Pair pair = await PairAsync();

        IngestResult result = await pair.Us.ReceiveAsync(Says(pair, "still here"));

        Assert.IsType<IngestOutcome.Processed>(result.Outcome);
        Assert.Equal("still here", result.Message!.Event.Content);
    }

    // ---- The exit criterion ----

    [Fact]
    public async Task AMessageSealedUnderTheEpochWeJustLeftStillArrives()
    {
        // The whole subject. They speak, the group moves on, and the envelope
        // they sent is sealed under a key the live group cannot derive: MLS
        // retains a past epoch's secret tree so the inner message stays
        // readable, and destroys that epoch's exporter secret on purpose, so
        // the wrap around it does not.
        Pair pair = await PairAsync();

        string spoken = Says(pair, "before the commit");
        await StepAsync(pair);

        Assert.Equal(2UL, pair.Us.Group.Epoch);

        IngestResult result = await pair.Us.ReceiveAsync(spoken);

        Assert.IsType<IngestOutcome.Processed>(result.Outcome);
        Assert.Equal("before the commit", result.Message!.Event.Content);
    }

    [Fact]
    public async Task ACompetingCommitFromTheEpochWeLeftReachesConvergenceRatherThanVanishing()
    {
        // The case that matters more than chat does. A competing commit is
        // framed at the epoch it forks from and sealed under that epoch's key,
        // so without the fallback a fork is invisible at the transport layer:
        // nothing is stored, ConvergencePass reads an empty candidate list, and
        // a split group reports itself settled.
        Pair pair = await PairAsync();

        // A second copy of them, standing at the same epoch, commits something
        // else. Two commits from one epoch is exactly a fork.
        MlsGroup twin = MlsGroup.Import(pair.Them.Export(), _cs);

        string competing = Commits(twin);
        await StepAsync(pair);

        IngestResult result = await pair.Us.ReceiveAsync(competing);

        Assert.IsType<IngestOutcome.TransportDeferred>(result.Outcome);

        MessageRecord stored = Assert.Single(
            await _fixture.Provider.ListMessagesByStateAsync(
                pair.GroupId, MessageRecordState.Retryable));

        // Filed under the epoch it was framed at, which is where its branch
        // forks, and not under the epoch we happened to be standing on.
        Assert.Equal(1UL, stored.SourceEpoch.Value);
    }

    [Fact]
    public async Task AMessageFromTwoEpochsBackArrivesAfterTheKeysHaveMovedTwice()
    {
        // One boundary could be crossed by a set of keys built once and never
        // refreshed. Two cannot.
        Pair pair = await PairAsync();

        string spoken = Says(pair, "two ago");
        await StepAsync(pair);
        await StepAsync(pair);

        Assert.Equal(3UL, pair.Us.Group.Epoch);

        IngestResult result = await pair.Us.ReceiveAsync(spoken);

        Assert.IsType<IngestOutcome.Processed>(result.Outcome);
        Assert.Equal("two ago", result.Message!.Event.Content);
    }

    [Fact]
    public async Task EachRetainedEpochIsTriedUnderItsOwnKeyAndNotUnderSomeOtherEpochs()
    {
        // Added because the earlier cases did not distinguish "the right
        // epoch's key" from "any retained epoch's key": with one message in
        // flight, deriving every retained key from a single checkpoint passed
        // them all. Two messages from two different past epochs cannot both
        // open under one key.
        Pair pair = await PairAsync();

        string atOne = Says(pair, "at one");
        await StepAsync(pair);

        string atTwo = Says(pair, "at two");
        await StepAsync(pair);

        Assert.Equal(3UL, pair.Us.Group.Epoch);

        IngestResult older = await pair.Us.ReceiveAsync(atOne);
        IngestResult newer = await pair.Us.ReceiveAsync(atTwo);

        Assert.Equal("at one", older.Message!.Event.Content);
        Assert.Equal("at two", newer.Message!.Event.Content);
    }

    // ---- Where the fallback stops ----

    [Fact]
    public async Task AMessageOlderThanTheRetainedWindowIsDeferredRatherThanDelivered()
    {
        // The bound is the archive's retention window, which the pinned policy
        // sets equal to the MLS past-epoch window — so the epoch whose transport
        // key we stop keeping is the same epoch whose message keys the group
        // stops keeping. Neither half is arbitrary and neither runs ahead of the
        // other.
        Pair pair = await PairAsync();

        string spoken = Says(pair, "long ago");

        for (int i = 0; i < (int)ConvergencePolicy.V1MaxRewindCommits + 1; i++)
            await StepAsync(pair);

        Assert.Equal(2UL + ConvergencePolicy.V1MaxRewindCommits, pair.Us.Group.Epoch);

        IngestResult result = await pair.Us.ReceiveAsync(spoken);

        Assert.IsType<IngestOutcome.TransportDeferred>(result.Outcome);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task AnEnvelopeAddressedToAnotherGroupIsRefusedRatherThanRetried()
    {
        // Somebody else's traffic is most of what a relay hands a client. It
        // must cost one signature check, not one per retained epoch.
        Pair ours = await PairAsync();
        Pair theirs = await PairAsync();

        await StepAsync(ours);

        IngestResult result = await ours.Us.ReceiveAsync(Says(theirs, "not for you"));

        Assert.Equal(
            new IngestOutcome.Ignored(InputRejectionCategory.WrongRecipient), result.Outcome);
    }

    [Fact]
    public async Task AnEnvelopeThatIsNotAnEventAtAllIsRefusedTerminally()
    {
        // Terminal and retryable are different answers to the caller, and the
        // peeler is the only thing that can tell them apart. Malformed bytes
        // never become valid, so retrying them under five more keys buys
        // nothing.
        Pair pair = await PairAsync();

        IngestResult result = await pair.Us.ReceiveAsync("not json");

        Assert.Equal(
            new IngestOutcome.Ignored(InputRejectionCategory.InvalidEncoding), result.Outcome);
    }

    // ---- The cache behind it ----

    [Fact]
    public async Task TheRetainedKeysAreCachedUntilTheyAreInvalidated()
    {
        // Rebuilding costs one MlsGroup.Import per retained epoch, so it is paid
        // once per epoch the group stands at rather than once per envelope.
        // What the epoch check cannot catch is a reorg landing on an epoch
        // numbered the same as the one it left, where every key above the fork
        // belongs to a branch this member has abandoned — hence an explicit
        // invalidation. That reorg is not reproduced here; what is pinned is
        // that the cache holds and that invalidating it makes the next call
        // rebuild, which is exercised by moving the archive underneath it.
        Pair pair = await PairAsync();
        await StepAsync(pair);
        await StepAsync(pair);

        var keys = new RetainedTransportKeys(Host().Archive);

        Assert.Equal(3, (await keys.NewestFirstAsync(pair.GroupId, pair.Us.Group)).Count);

        await _fixture.Provider.PruneEpochCheckpointsBeforeAsync(pair.GroupId, new EpochId(3));

        Assert.Equal(3, (await keys.NewestFirstAsync(pair.GroupId, pair.Us.Group)).Count);

        keys.Invalidate();

        Assert.Single(await keys.NewestFirstAsync(pair.GroupId, pair.Us.Group));
    }
}
