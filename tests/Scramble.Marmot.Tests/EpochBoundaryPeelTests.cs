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
        SaysFrom(pair.Them, pair.TheirSigner, text);

    /// <summary>
    /// The same, from a given copy of that member — so a test can speak from one
    /// standing at an epoch the session has not reached.
    /// </summary>
    private static string SaysFrom(MlsGroup sender, LocalSigner signer, string text) =>
        GroupMessages.Send(
            sender,
            Peeler,
            MarmotAppEvent.Chat(signer.Hex, (long)Now, text),
            signer.AccountPublicKey.Span);

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

    // ---- The epoch we have not reached yet ----

    [Fact]
    public async Task AMessageSealedUnderAnEpochWeHaveNotReachedSurvivesUntilTheCommitArrives()
    {
        // The mirror of AMessageSealedUnderTheEpochWeJustLeftStillArrives, and the
        // half nothing covered. A passive member is behind by construction: every
        // other member's commit moves the group without them, so a message sent at
        // the new epoch is sealed under a key they cannot derive and will not hold
        // until the commit reaches them.
        //
        // Reported from a real 45-member group -- joined at epoch 98, four members
        // added by other people, messages sent throughout, not one displayed.
        Pair pair = await PairAsync();

        // Their commit, which we deliberately do not see yet.
        string commit = Commits(pair.Them);
        Assert.Equal(2UL, pair.Them.Epoch);
        Assert.Equal(1UL, pair.Us.Group.Epoch);

        // Spoken at the epoch their commit created.
        string spoken = Says(pair, "after their commit");

        IngestResult early = await pair.Us.ReceiveAsync(spoken);

        // It cannot be read yet, and that much is correct and unavoidable.
        Assert.IsType<IngestOutcome.TransportDeferred>(early.Outcome);
        Assert.Null(early.Message);

        // But TransportDeferred promises the bytes were kept, and nothing in the
        // message store can keep them: every record there is keyed by a content
        // id over MLS bytes, and there are none until the envelope peels.
        HeldEnvelope kept = Assert.Single(
            await _fixture.Provider.ListHeldEnvelopesAsync(pair.GroupId));

        // Held at where WE stood, which is the fact that bounds retention -- not
        // at the epoch the message was sent in, which nothing has read.
        Assert.Equal(1UL, kept.HeldAtEpoch.Value);

        IngestResult applied = await pair.Us.ReceiveAsync(commit);
        Assert.IsType<IngestOutcome.Processed>(applied.Outcome);
        Assert.Equal(2UL, pair.Us.Group.Epoch);

        ReplayResult replay = await pair.Us.ReplayAsync();

        Assert.Contains(replay.Delivered, m => m.Event.Content == "after their commit");
    }

    [Fact]
    public async Task OneSweepIsNotEnoughWhenAHeldCommitIsWhatUnlocksAHeldMessage()
    {
        // A commit framed at an epoch we have not reached is held here too, and
        // peeling it advances the group -- so a message refused earlier in the
        // same sweep can be readable by the time that sweep ends. One pass would
        // leave it for the next drain, and a passive member has no next drain to
        // count on: their epoch moves only when somebody else commits.
        Pair pair = await PairAsync();

        // Two commits from them. The first is sealed at epoch 1, which we can
        // open; the second at epoch 2, which we cannot until the first applies.
        string first = Commits(pair.Them);
        string second = Commits(pair.Them);
        Assert.Equal(3UL, pair.Them.Epoch);

        string spoken = SaysFrom(pair.Them, pair.TheirSigner, "at three");

        // Worst order on purpose: the message, then the commit it needs, then
        // the commit that one needs. Nothing is readable until the last arrives.
        Assert.IsType<IngestOutcome.TransportDeferred>(
            (await pair.Us.ReceiveAsync(spoken)).Outcome);
        Assert.IsType<IngestOutcome.TransportDeferred>(
            (await pair.Us.ReceiveAsync(second)).Outcome);

        Assert.Equal(2, (await _fixture.Provider.ListHeldEnvelopesAsync(pair.GroupId)).Count);

        Assert.IsType<IngestOutcome.Processed>(
            (await pair.Us.ReceiveAsync(first)).Outcome);
        Assert.Equal(2UL, pair.Us.Group.Epoch);

        ReplayResult replay = await pair.Us.ReplayAsync();

        // Both held envelopes resolved in the one pass: the commit took us to
        // epoch 3, and the re-sweep then opened the message.
        Assert.Equal(3UL, pair.Us.Group.Epoch);
        Assert.Contains(replay.Delivered, m => m.Event.Content == "at three");
        Assert.Empty(await _fixture.Provider.ListHeldEnvelopesAsync(pair.GroupId));
    }

    // ---- What bounds the held store ----

    [Fact]
    public async Task AnEnvelopeStillUnopenableAfterTheWindowIsGivenUpOnRatherThanKept()
    {
        // Held bytes are not kept forever. An envelope still unopenable this
        // many epochs after it was held is not waiting on a commit we have yet
        // to see -- we have moved that far and did not find the key -- and a
        // message that old could not be delivered even if it did peel.
        Pair pair = await PairAsync();

        (MlsGroup twin, _) = Ahead(pair.Them);

        // Sealed under an epoch we are never handed the commit for: the commit
        // that would open it is discarded here, and the steps below fork away
        // from that branch instead.
        Assert.IsType<IngestOutcome.TransportDeferred>(
            (await pair.Us.ReceiveAsync(SaysFrom(twin, pair.TheirSigner, "never"))).Outcome);

        Assert.Single(await _fixture.Provider.ListHeldEnvelopesAsync(pair.GroupId));

        for (int i = 0; i < (int)ConvergencePolicy.V1MaxRewindCommits + 1; i++)
            await StepAsync(pair);

        await pair.Us.ReplayAsync();

        Assert.Empty(await _fixture.Provider.ListHeldEnvelopesAsync(pair.GroupId));
    }

    [Fact]
    public async Task TheHeldStoreIsCappedAndTheCapKeepsWhatArrivedFirst()
    {
        // A routing id is public and on every kind-445 event, so anyone can
        // publish a correctly signed envelope to one of our groups that no key
        // will ever open. The cap is what makes that cost bounded.
        //
        // It keeps the oldest rather than evicting for the newest, which is the
        // half worth pinning: evicting oldest-first would let a flood push out
        // the legitimate message that arrived before it, and that is precisely
        // what an attacker would be buying.
        Pair pair = await PairAsync();

        (MlsGroup twin, string unlock) = Ahead(pair.Them);

        await pair.Us.ReceiveAsync(SaysFrom(twin, pair.TheirSigner, "first in"));

        for (int i = 0; i < MarmotSession.MaxHeldEnvelopesPerGroup + 8; i++)
            await pair.Us.ReceiveAsync(SaysFrom(twin, pair.TheirSigner, $"flood {i}"));

        Assert.Equal(
            MarmotSession.MaxHeldEnvelopesPerGroup,
            await _fixture.Provider.CountHeldEnvelopesAsync(pair.GroupId));

        // The one that arrived first is still there, so the flood cost it
        // nothing. Checked by delivering it: the content is inside bytes the
        // store has never read, so the row alone cannot say which it is.
        Assert.IsType<IngestOutcome.Processed>(
            (await pair.Us.ReceiveAsync(unlock)).Outcome);

        ReplayResult replay = await pair.Us.ReplayAsync();

        Assert.Contains(replay.Delivered, m => m.Event.Content == "first in");
    }

    [Fact]
    public async Task AHeldEnvelopeIsNotRetriedAtAnEpochItHasAlreadyFailedAt()
    {
        // What makes a flood of unopenable junk free rather than a cost paid on
        // every pass. The key an envelope wants is derived from group state, so
        // asking again where nothing has moved asks a question already answered.
        Pair pair = await PairAsync();

        (MlsGroup twin, _) = Ahead(pair.Them);

        await pair.Us.ReceiveAsync(SaysFrom(twin, pair.TheirSigner, "held"));

        HeldEnvelope first = Assert.Single(
            await _fixture.Provider.ListHeldEnvelopesAsync(pair.GroupId));
        Assert.Equal(1, first.Attempts);

        await pair.Us.ReplayAsync();
        await pair.Us.ReplayAsync();

        HeldEnvelope after = Assert.Single(
            await _fixture.Provider.ListHeldEnvelopesAsync(pair.GroupId));

        // Untouched: two further passes at one epoch cost no peel at all.
        Assert.Equal(1, after.Attempts);
        Assert.Equal(first.UpdatedAt, after.UpdatedAt);
    }

    /// <summary>
    /// A copy of a member standing one epoch ahead of the session, and the
    /// commit that would bring the session level with it.
    /// </summary>
    /// <remarks>
    /// The only way to produce an envelope sealed under an epoch we cannot
    /// reach: the twin speaks from the far side of a commit the session has not
    /// been handed. Made from an export so the original is left where the
    /// session expects it, and the commit is returned rather than dropped
    /// because it is sealed at the epoch <i>before</i> it — the one envelope in
    /// the arrangement the session can still open, and so the only way back.
    /// </remarks>
    private (MlsGroup Twin, string Unlock) Ahead(MlsGroup member)
    {
        MlsGroup twin = MlsGroup.Import(member.Export(), _cs);
        return (twin, Commits(twin));
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
