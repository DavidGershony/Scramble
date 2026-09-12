using System.Text;
using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.Ingest;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Ingest;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// The door every inbound message comes through.
/// </summary>
/// <remarks>
/// Most of what a relay hands a client should be declined — other people's
/// traffic, own echoes, duplicates under fresh envelopes, messages from before
/// it joined. So most of what is checked here is <i>refusals</i>, and that each
/// one is classified rather than thrown: a client that reports routine
/// declines as errors looks broken while working, and buries the failures that
/// matter.
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class MessageIngestTests : IDisposable
{
    private readonly StorageFixture _fixture = new();
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];

    private readonly DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddSeconds(Now);
    private readonly EpochManager _epochs = new();

    public void Dispose() => _fixture.Dispose();

    private MessageIngest NewIngest() => new(_fixture.Provider, _epochs, () => _now);

    private EpochArchive NewArchive() =>
        new(_fixture.Provider, _cs, ConvergencePolicy.V1, () => _now);

    private MessageIngest NewIngest(EpochArchive archive) =>
        new(_fixture.Provider, _epochs, () => _now, archive);

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

    private sealed record Pair(
        LocalSigner AliceSigner, CreatedGroup Alice, GroupId GroupId, MlsGroup Bob);

    /// <summary>A creator and a joined member, with the group record stored.</summary>
    private async Task<Pair> PairAsync()
    {
        var aliceSigner = new LocalSigner();

        CreatedGroup alice = await MarmotGroupBuilder.CreateAsync(
            _cs, aliceSigner, "Rakes", "", Now, Relays);

        var bundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);
        StagedCommit staged = MarmotGroupInvite.Add(alice.Group, _cs, [bundle.KeyPackage]);
        staged.Applied();

        MlsGroup bob = MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey,
            config: MarmotGroupSettings.Create());

        var groupId = new GroupId(alice.GroupId);
        await _fixture.Provider.PutGroupAsync(alice.ToRecord(_now));
        _epochs.SetStable(groupId, new EpochId(bob.Epoch));

        return new Pair(aliceSigner, alice, groupId, bob);
    }

    private sealed record Trio(
        CreatedGroup Alice, MlsGroup Bob, MlsGroup Carol, GroupId GroupId);

    /// <summary>
    /// A creator and two joined members.
    /// </summary>
    /// <remarks>
    /// Three rather than two wherever a commit removes somebody: the member
    /// being removed cannot ingest the commit that removes them, so a pair only
    /// ever reaches the removal path and never the classification one.
    /// </remarks>
    private async Task<Trio> TrioAsync()
    {
        var aliceSigner = new LocalSigner();

        CreatedGroup alice = await MarmotGroupBuilder.CreateAsync(
            _cs, aliceSigner, "Rakes", "", Now, Relays);

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

        var groupId = new GroupId(alice.GroupId);
        await _fixture.Provider.PutGroupAsync(alice.ToRecord(_now));
        _epochs.SetStable(groupId, new EpochId(carol.Epoch));

        return new Trio(alice, bob, carol, groupId);
    }

    private static byte[] Serialize(PublicMessage message) =>
        TlsCodec.Serialize(new MlsMessage(WireFormat.MlsPublicMessage, message).WriteTo);

    /// <summary>The MLS bytes of an application message from Alice.</summary>
    /// <remarks>
    /// Peeled with the <i>sender's</i> secret, deliberately. The kind-445 wrap
    /// is a separate layer from MLS, and peeling with the receiver's key would
    /// make a message from an epoch they cannot reach fail here in the helper
    /// rather than inside ingest -- testing the transport instead of the thing
    /// under test.
    /// </remarks>
    private static byte[] AppMessage(Pair pair, string text)
    {
        var peeler = new NostrGroupPeeler();

        string envelope = GroupMessages.Send(
            pair.Alice.Group,
            peeler,
            MarmotAppEvent.Chat(pair.AliceSigner.Hex, (long)Now, text),
            pair.AliceSigner.AccountPublicKey.Span);

        return peeler
            .Peel(envelope, _ => GroupMessages.ExporterSecret(pair.Alice.Group))
            .MlsBytes;
    }

    // ---- The happy path ----

    [Fact]
    public async Task AnApplicationMessageIsAppliedAndReturned()
    {
        Pair pair = await PairAsync();
        byte[] wire = AppMessage(pair, "hello");

        IngestResult result = await NewIngest().IngestAsync(pair.Bob, pair.GroupId, wire);

        var processed = Assert.IsType<IngestOutcome.Processed>(result.Outcome);
        Assert.Equal(pair.GroupId, processed.GroupId);
        Assert.True(result.Outcome.Advanced);
        Assert.Equal("hello", result.Message!.Event.Content);
    }

    [Fact]
    public async Task WhatWasSeenIsRecordedUnderItsContentId()
    {
        Pair pair = await PairAsync();
        byte[] wire = AppMessage(pair, "hello");

        await NewIngest().IngestAsync(pair.Bob, pair.GroupId, wire, transportId: "evt-1");

        MessageRecord stored =
            (await _fixture.Provider.GetMessageAsync(MessageId.FromMlsBytes(wire)))!;

        Assert.Equal(MessageRecordState.Processed, stored.State);
        Assert.Equal("evt-1", stored.TransportId);
        Assert.Equal(wire, stored.Wire);
    }

    // ---- Deduplication ----

    [Fact]
    public async Task TheSameMessageTwiceIsIgnoredTheSecondTime()
    {
        Pair pair = await PairAsync();
        byte[] wire = AppMessage(pair, "once");
        MessageIngest ingest = NewIngest();

        Assert.True((await ingest.IngestAsync(pair.Bob, pair.GroupId, wire)).Outcome.Advanced);

        IngestResult again = await ingest.IngestAsync(pair.Bob, pair.GroupId, wire);

        var ignored = Assert.IsType<IngestOutcome.Ignored>(again.Outcome);
        Assert.Equal(InputRejectionCategory.Duplicate, ignored.Category);
        Assert.Null(again.Message);
    }

    [Fact]
    public async Task DeduplicationSurvivesADifferentTransportEnvelope()
    {
        // The reason the key is content-derived. The same MLS message
        // legitimately arrives under different Nostr event ids, so a transport
        // id can only ever be a pre-filter -- deduplicating on it is the exact
        // mistake the previous engine made.
        Pair pair = await PairAsync();
        byte[] wire = AppMessage(pair, "once");
        MessageIngest ingest = NewIngest();

        await ingest.IngestAsync(pair.Bob, pair.GroupId, wire, transportId: "envelope-a");

        IngestResult again = await ingest.IngestAsync(
            pair.Bob, pair.GroupId, wire, transportId: "envelope-b");

        Assert.Equal(
            InputRejectionCategory.Duplicate,
            Assert.IsType<IngestOutcome.Ignored>(again.Outcome).Category);
    }

    [Fact]
    public async Task ARefusedMessageIsStillRecordedSoItIsCheapToRefuseAgain()
    {
        // Otherwise every redelivery pays the full validation cost, and a flood
        // of malformed traffic becomes a way to keep a client busy.
        Pair pair = await PairAsync();
        byte[] rubbish = [0xff, 0xff, 0xff, 0xff];
        MessageIngest ingest = NewIngest();

        Assert.Equal(
            InputRejectionCategory.InvalidEncoding,
            Assert.IsType<IngestOutcome.Ignored>(
                (await ingest.IngestAsync(pair.Bob, pair.GroupId, rubbish)).Outcome).Category);

        Assert.Equal(
            InputRejectionCategory.Duplicate,
            Assert.IsType<IngestOutcome.Ignored>(
                (await ingest.IngestAsync(pair.Bob, pair.GroupId, rubbish)).Outcome).Category);
    }

    // ---- Refusals ----

    [Fact]
    public async Task AMessageForAGroupWeDoNotHaveIsIgnored()
    {
        Pair pair = await PairAsync();
        byte[] wire = AppMessage(pair, "hello");

        IngestResult result = await NewIngest().IngestAsync(
            pair.Bob, StorageFixture.NewGroupId(), wire);

        Assert.Equal(
            InputRejectionCategory.UnknownGroup,
            Assert.IsType<IngestOutcome.Ignored>(result.Outcome).Category);
    }

    [Fact]
    public async Task UndecodableBytesAreRefusedTerminally()
    {
        // Terminal, not deferred: these bytes will not become valid later, so
        // retrying them forever is just a slower way of dropping them.
        Pair pair = await PairAsync();

        IngestResult result = await NewIngest().IngestAsync(
            pair.Bob, pair.GroupId, [0x01, 0x02, 0x03]);

        Assert.Equal(
            InputRejectionCategory.InvalidEncoding,
            Assert.IsType<IngestOutcome.Ignored>(result.Outcome).Category);
        Assert.False(result.Outcome.IsRetryable);

        MessageRecord stored = (await _fixture.Provider.GetMessageAsync(
            MessageId.FromMlsBytes([0x01, 0x02, 0x03])))!;
        Assert.Equal(MessageRecordState.Failed, stored.State);
    }

    [Fact]
    public async Task AMessageForAGroupWeWereRemovedFromIsALocalState()
    {
        // Not a deferral: no retry fixes it, because our keys belong to an epoch
        // the group has left. The caller has to tell the user, not try again.
        Pair pair = await PairAsync();
        byte[] wire = AppMessage(pair, "hello");

        GroupRecord record = (await _fixture.Provider.GetGroupAsync(pair.GroupId))!;
        await _fixture.Provider.PutGroupAsync(record with { Removed = true });

        IngestResult result = await NewIngest().IngestAsync(pair.Bob, pair.GroupId, wire);

        Assert.Equal(
            LocalIngestState.Removed,
            Assert.IsType<IngestOutcome.LocalState>(result.Outcome).State);
        Assert.False(result.Outcome.IsRetryable);
    }

    // ---- Buffering ----

    [Fact]
    public async Task AMessageArrivingMidPublishIsBufferedNotDropped()
    {
        // Applying an inbound commit while our own is staged and unacknowledged
        // would fork us from the epoch we are about to ask everyone to adopt.
        Pair pair = await PairAsync();
        byte[] wire = AppMessage(pair, "mid-flight");

        var epoch = new EpochId(pair.Bob.Epoch);
        _epochs.BeginPending(
            pair.GroupId,
            epoch,
            new EpochId(epoch.Value + 1),
            new StagedCommitHandle([1, 2, 3]),
            _epochs.NextPendingRef(),
            PendingKind.GroupEvolution);

        Assert.False(_epochs.CanIngest(pair.GroupId));

        IngestResult result = await NewIngest().IngestAsync(pair.Bob, pair.GroupId, wire);

        var buffered = Assert.IsType<IngestOutcome.Buffered>(result.Outcome);
        Assert.Equal(pair.GroupId, buffered.GroupId);

        // Buffered is a promise of a later replay, so the bytes must be kept.
        MessageRecord stored =
            (await _fixture.Provider.GetMessageAsync(MessageId.FromMlsBytes(wire)))!;
        Assert.Equal(MessageRecordState.Created, stored.State);
        Assert.Equal(wire, stored.Wire);
    }

    [Fact]
    public async Task AMessageWeCannotDecryptIsDeferredRatherThanRejected()
    {
        // A later commit may make the epoch reachable, so this is held for
        // retry. Rejecting it would discard a message that was addressed to us.
        Pair pair = await PairAsync();

        // Alice moves on alone; Bob cannot read what she sends next.
        var (_, _) = pair.Alice.Group.CommitPublic();
        pair.Alice.Group.MergePendingCommit();
        byte[] wire = AppMessage(pair, "unreachable");

        IngestResult result = await NewIngest().IngestAsync(pair.Bob, pair.GroupId, wire);

        Assert.IsType<IngestOutcome.TransportDeferred>(result.Outcome);
        Assert.True(result.Outcome.IsRetryable);

        MessageRecord stored =
            (await _fixture.Provider.GetMessageAsync(MessageId.FromMlsBytes(wire)))!;
        Assert.Equal(MessageRecordState.PeelDeferred, stored.State);
    }

    // ---- Handshake ----

    [Fact]
    public async Task ACommitAdvancesTheGroup()
    {
        Pair pair = await PairAsync();
        ulong before = pair.Bob.Epoch;

        var (commit, _) = pair.Alice.Group.CommitPublic();
        byte[] wire = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, commit).WriteTo);
        pair.Alice.Group.MergePendingCommit();

        IngestResult result = await NewIngest().IngestAsync(pair.Bob, pair.GroupId, wire);

        Assert.IsType<IngestOutcome.Processed>(result.Outcome);
        Assert.Equal(before + 1, pair.Bob.Epoch);
    }

    [Fact]
    public async Task ACommitThatRemovesUsMarksTheGroupRemoved()
    {
        // The one message that tells a member they are out is also the one they
        // cannot apply. Ingest has to record that rather than let the caller
        // keep polling a group it is no longer in.
        Pair pair = await PairAsync();

        var (commit, _) = pair.Alice.Group.CommitPublic(
            new List<Proposal> { pair.Alice.Group.ProposeRemove(pair.Bob.MyLeafIndex) });
        byte[] wire = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, commit).WriteTo);
        pair.Alice.Group.MergePendingCommit();

        IngestResult result = await NewIngest().IngestAsync(pair.Bob, pair.GroupId, wire);

        Assert.Equal(
            LocalIngestState.Removed,
            Assert.IsType<IngestOutcome.LocalState>(result.Outcome).State);
        Assert.True((await _fixture.Provider.GetGroupAsync(pair.GroupId))!.Removed);
    }

    [Fact]
    public async Task ACachedProposalIsNotReportedAsHavingAdvancedAnything()
    {
        // A proposal changes no group state. Reporting Processed would tell the
        // caller an epoch moved when it did not.
        Pair pair = await PairAsync();

        PublicMessage request = MarmotGroupLeave.Request(pair.Bob);
        byte[] wire = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, request).WriteTo);

        ulong before = pair.Alice.Group.Epoch;
        var ingest = new MessageIngest(_fixture.Provider, _epochs, () => _now);

        IngestResult result = await ingest.IngestAsync(
            pair.Alice.Group, pair.GroupId, wire);

        Assert.IsType<IngestOutcome.Buffered>(result.Outcome);
        Assert.False(result.Outcome.Advanced);
        Assert.Equal(before, pair.Alice.Group.Epoch);
    }

    // ---- The epoch a record is filed under ----

    [Fact]
    public async Task ACompetingCommitIsFiledUnderTheEpochItWasBuiltFrom()
    {
        // The record's epoch is where convergence will later say the branch
        // forks, so it has to be the epoch the commit was framed against and
        // not the one we had reached by the time it arrived. Filing ours would
        // describe every competing commit as forking from wherever we happened
        // to be -- a branch no member can rebuild, ourselves included.
        Pair pair = await PairAsync();

        ulong forkEpoch = pair.Alice.Group.Epoch;
        var (hers, _) = pair.Alice.Group.CommitPublic();
        byte[] wire = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, hers).WriteTo);
        pair.Alice.Group.MergePendingCommit();

        // We commit from the same epoch and apply ours first, which is what a
        // relay produces when two members are handed each other's commits
        // after having already sent their own. Hers no longer applies.
        var (_, _) = pair.Bob.CommitPublic();
        pair.Bob.MergePendingCommit();
        Assert.NotEqual(forkEpoch, pair.Bob.Epoch);

        IngestResult result = await NewIngest().IngestAsync(pair.Bob, pair.GroupId, wire);
        Assert.IsType<IngestOutcome.TransportDeferred>(result.Outcome);

        MessageRecord stored =
            (await _fixture.Provider.GetMessageAsync(MessageId.FromMlsBytes(wire)))!;

        Assert.Equal(MessageRecordState.Retryable, stored.State);
        Assert.Equal(new EpochId(forkEpoch), stored.SourceEpoch);
        Assert.NotEqual(new EpochId(pair.Bob.Epoch), stored.SourceEpoch);
    }

    [Fact]
    public async Task AMessageFromAnEpochWeCannotReachIsFiledUnderTheSenderEpoch()
    {
        // The application-message half of the same rule. A private message
        // carries its epoch outside the ciphertext precisely so a receiver can
        // tell which keys to reach for before it can read anything, so there is
        // no excuse for recording our own.
        Pair pair = await PairAsync();
        ulong ours = pair.Bob.Epoch;

        var (_, _) = pair.Alice.Group.CommitPublic();
        pair.Alice.Group.MergePendingCommit();
        byte[] wire = AppMessage(pair, "from further on");

        await NewIngest().IngestAsync(pair.Bob, pair.GroupId, wire);

        MessageRecord stored =
            (await _fixture.Provider.GetMessageAsync(MessageId.FromMlsBytes(wire)))!;

        Assert.Equal(MessageRecordState.PeelDeferred, stored.State);
        Assert.Equal(new EpochId(pair.Alice.Group.Epoch), stored.SourceEpoch);
        Assert.NotEqual(new EpochId(ours), stored.SourceEpoch);
    }

    [Fact]
    public async Task ABufferedMessageIsFiledUnderItsOwnEpochAsWell()
    {
        // Buffering happens before anything is decrypted, so this is the record
        // most easily filed under the receiver's epoch by accident -- and it is
        // the one that matters most, because a message buffered mid-publish is
        // arriving exactly when a fork is being created.
        Pair pair = await PairAsync();

        var (_, _) = pair.Alice.Group.CommitPublic();
        pair.Alice.Group.MergePendingCommit();
        byte[] wire = AppMessage(pair, "mid-flight, from ahead");

        var epoch = new EpochId(pair.Bob.Epoch);
        _epochs.BeginPending(
            pair.GroupId,
            epoch,
            new EpochId(epoch.Value + 1),
            new StagedCommitHandle([1, 2, 3]),
            _epochs.NextPendingRef(),
            PendingKind.GroupEvolution);

        IngestResult result = await NewIngest().IngestAsync(pair.Bob, pair.GroupId, wire);
        Assert.IsType<IngestOutcome.Buffered>(result.Outcome);

        MessageRecord stored =
            (await _fixture.Provider.GetMessageAsync(MessageId.FromMlsBytes(wire)))!;

        Assert.Equal(MessageRecordState.Created, stored.State);
        Assert.Equal(new EpochId(pair.Alice.Group.Epoch), stored.SourceEpoch);
        Assert.NotEqual(epoch, stored.SourceEpoch);
    }

    // ---- Keeping enough state to evaluate a fork later ----

    [Fact]
    public async Task AnAppliedCommitArchivesTheEpochItProduced()
    {
        // Written as epochs pass rather than when a fork shows up, because by
        // then the state that a competing branch forks from is already gone --
        // an MLS group cannot rewind to produce it again.
        Pair pair = await PairAsync();
        EpochArchive archive = NewArchive();

        var (commit, _) = pair.Alice.Group.CommitPublic();
        byte[] wire = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, commit).WriteTo);
        pair.Alice.Group.MergePendingCommit();

        await NewIngest(archive).IngestAsync(pair.Bob, pair.GroupId, wire);

        EpochWindow window = await archive.LoadWindowAsync(
            pair.GroupId, new EpochId(pair.Bob.Epoch));

        MlsGroup restored = window.Restore(new EpochId(pair.Bob.Epoch))!;
        Assert.Equal(pair.Bob.Epoch, restored.Epoch);
    }

    [Fact]
    public async Task TheArchivedEpochRemembersTheClassOfTheCommitThatMadeIt()
    {
        // Read before the commit is applied and stored with the epoch it
        // produced. Nothing can recover it afterwards, and a tip filed as
        // ordinary when it was privileged loses a tie-break the rest of the
        // group wins.
        Pair pair = await PairAsync();
        EpochArchive archive = NewArchive();

        var bundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);
        using StagedCommit staged = MarmotGroupInvite.Add(
            pair.Alice.Group, _cs, [bundle.KeyPackage]);

        byte[] wire = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, staged.Commit).WriteTo);
        staged.Publishing();
        staged.Applied();

        await NewIngest(archive).IngestAsync(pair.Bob, pair.GroupId, wire);

        EpochWindow window = await archive.LoadWindowAsync(
            pair.GroupId, new EpochId(pair.Bob.Epoch));

        CommitTip? tip = window.TipAt(new EpochId(pair.Bob.Epoch));

        Assert.NotNull(tip);
        Assert.Equal(CommitOrderingPriority.Privileged, tip.Priority);

        // The digest is the commit's content id over the same MLS bytes, and the
        // committer is the member who made it -- read off the tree before the
        // commit moved it.
        Assert.Equal(MessageId.FromMlsBytes(wire), tip.Commit);
        Assert.Equal(
            pair.AliceSigner.AccountPublicKey.ToArray(),
            tip.Committer);
    }

    [Fact]
    public async Task TheTipIsReadBeforeTheCommitIsApplied()
    {
        // A commit citing its proposals by hash can only be classified while the
        // cache those hashes resolve against is intact, and applying the commit
        // clears it. Read a moment later this departure commit comes back
        // Privileged -- fail-closed, and wrong here: it would rank a routine
        // departure above the admin action it might be racing.
        Trio trio = await TrioAsync();
        EpochArchive archive = NewArchive();
        MessageIngest ingest = NewIngest(archive);

        // Bob asks to leave, and Carol sees the request -- so her cache is the
        // one that can resolve the reference, right up until she applies.
        PublicMessage request = MarmotGroupLeave.Request(trio.Bob);
        byte[] requestWire = Serialize(request);
        await ingest.IngestAsync(trio.Carol, trio.GroupId, requestWire);

        GroupHandshake.Receive(trio.Alice.Group, requestWire);
        using StagedCommit? departure = MarmotGroupLeave.CommitDepartures(trio.Alice.Group);
        Assert.NotNull(departure);

        byte[] commitWire = Serialize(departure!.Commit);
        departure.Publishing();
        departure.Applied();

        await ingest.IngestAsync(trio.Carol, trio.GroupId, commitWire);

        EpochWindow window = await archive.LoadWindowAsync(
            trio.GroupId, new EpochId(trio.Carol.Epoch));

        CommitTip? tip = window.TipAt(new EpochId(trio.Carol.Epoch));

        Assert.NotNull(tip);
        Assert.Equal(CommitOrderingPriority.Ordinary, tip.Priority);
    }

    [Fact]
    public async Task ACachedProposalArchivesNothing()
    {
        // A proposal advances no epoch, so there is no new state to keep. An
        // archive that wrote one anyway would replace the checkpoint for the
        // epoch the group is still on -- with a row claiming a commit produced
        // it, and claiming the wrong class for that commit.
        Pair pair = await PairAsync();
        EpochArchive archive = NewArchive();

        PublicMessage request = MarmotGroupLeave.Request(pair.Bob);
        byte[] wire = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, request).WriteTo);

        await NewIngest(archive).IngestAsync(pair.Alice.Group, pair.GroupId, wire);

        EpochWindow window = await archive.LoadWindowAsync(
            pair.GroupId, new EpochId(pair.Alice.Group.Epoch));

        Assert.Empty(window.Epochs);
    }

    [Fact]
    public async Task IngestWithoutAnArchiveStillWorks()
    {
        // The engine is additive: a caller with no interest in convergence must
        // not be made to construct an archive to receive a message.
        Pair pair = await PairAsync();

        var (commit, _) = pair.Alice.Group.CommitPublic();
        byte[] wire = TlsCodec.Serialize(
            new MlsMessage(WireFormat.MlsPublicMessage, commit).WriteTo);
        pair.Alice.Group.MergePendingCommit();

        IngestResult result = await NewIngest().IngestAsync(pair.Bob, pair.GroupId, wire);

        Assert.IsType<IngestOutcome.Processed>(result.Outcome);
        Assert.Empty(
            await _fixture.Provider.ListEpochCheckpointsAsync(pair.GroupId, new EpochId(0)));
    }

    // ---- The contract callers rely on ----

    [Fact]
    public async Task OnlyDeferralsAreRetryable()
    {
        // Callers branch on this rather than re-deriving it from the variant
        // list, so it has to be right for every outcome ingest can produce.
        Pair pair = await PairAsync();
        byte[] wire = AppMessage(pair, "hello");
        MessageIngest ingest = NewIngest();

        Assert.False((await ingest.IngestAsync(pair.Bob, pair.GroupId, wire)).Outcome.IsRetryable);
        Assert.False((await ingest.IngestAsync(pair.Bob, pair.GroupId, wire)).Outcome.IsRetryable);
        Assert.False(
            (await ingest.IngestAsync(pair.Bob, pair.GroupId, [0x09])).Outcome.IsRetryable);

        Assert.True(new IngestOutcome.TransportDeferred(pair.GroupId).IsRetryable);
        Assert.True(new IngestOutcome.ResourceRefused(pair.GroupId).IsRetryable);
    }
}
