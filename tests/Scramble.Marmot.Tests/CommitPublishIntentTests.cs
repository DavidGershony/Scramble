using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Types;
using Microsoft.Data.Sqlite;
using Scramble.Marmot;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Storage.Sqlite;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Publish intent for a commit, written down so a restart can classify it.
/// </summary>
/// <remarks>
/// <para>
/// <c>StagedCommit.Publishing()</c> draws the line crash recovery turns on:
/// before it the commit is ours alone and clearing it costs nothing, after it a
/// relay may hold it and clearing it locally is the one move that guarantees a
/// fork. The flag is a private field in an in-memory object, so on its own it
/// says nothing across a restart. These tests are about the row that does.
/// </para>
/// <para>
/// Two things are under test and they are different. One is the order the
/// writes go in — every window has to err towards believing a commit may be out
/// there, because that costs a relay query while the opposite forgets a commit
/// the rest of the group has already applied. The other is the three-way
/// outcome: a transport that refuses is a definite no, a transport that times
/// out or throws is <i>indeterminate</i>, and only the first authorises
/// discarding anything.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class CommitPublishIntentTests
{
    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private const ulong Now = 1_760_000_000;
    private static readonly string[] Relays = ["wss://relay.example.com"];
    private static readonly DateTimeOffset Clock = DateTimeOffset.UnixEpoch;

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

    /// <summary>
    /// An in-memory store that records what the world looked like at the moment
    /// of each write.
    /// </summary>
    /// <remarks>
    /// The ordering is what is being tested, and it is invisible to a store that
    /// only remembers rows: both orderings end with the same row and the same
    /// group. Observing from inside the write is what tells them apart.
    /// </remarks>
    private sealed class TracingStore : ICommitPublishAttemptStorage
    {
        private readonly Dictionary<GroupId, CommitPublishAttempt> _rows = new();

        /// <summary>What the world said at the moment of the write.</summary>
        public Func<string>? Observe { get; set; }

        public List<string> Trace { get; } = new();

        /// <summary>How many writes succeed before the store starts failing.</summary>
        public int WritesBeforeFailure { get; set; } = int.MaxValue;

        public bool FailClears { get; set; }

        public int Writes { get; private set; }

        public int Count => _rows.Count;

        public Task PutCommitPublishAttemptAsync(
            CommitPublishAttempt attempt, CancellationToken ct = default)
        {
            Trace.Add($"put {attempt.State} while {Observe?.Invoke() ?? "?"}");

            if (Writes++ >= WritesBeforeFailure)
                throw new IOException("the database is gone");

            _rows[attempt.GroupId] = attempt;
            return Task.CompletedTask;
        }

        public Task<CommitPublishAttempt?> GetCommitPublishAttemptAsync(
            GroupId groupId, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(groupId, out CommitPublishAttempt? row) ? row : null);

        public Task<IReadOnlyList<CommitPublishAttempt>> ListCommitPublishAttemptsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CommitPublishAttempt>>(_rows.Values.ToList());

        public Task<bool> ClearCommitPublishAttemptAsync(
            GroupId groupId, CancellationToken ct = default)
        {
            Trace.Add($"clear while {Observe?.Invoke() ?? "?"}");

            if (FailClears)
                throw new IOException("the database is gone");

            return Task.FromResult(_rows.Remove(groupId));
        }

        public CommitPublishAttempt? Row(GroupId groupId) =>
            _rows.TryGetValue(groupId, out CommitPublishAttempt? row) ? row : null;
    }

    private sealed class StubRelay : ICommitRelay
    {
        public Func<CommitPublishOutcome> Answer { get; set; } =
            () => CommitPublishOutcome.Accepted;

        /// <summary>Runs at the moment the bytes would leave the device.</summary>
        public Action? OnPublish { get; set; }

        public int Calls { get; private set; }

        public Task<CommitPublishOutcome> PublishAsync(
            string envelope, CancellationToken ct = default)
        {
            Calls++;
            OnPublish?.Invoke();
            return Task.FromResult(Answer());
        }
    }

    private sealed record Fixture(
        CreatedGroup Group, GroupId GroupId, TracingStore Store, StubRelay Relay,
        CommitPublisher Publisher);

    private async Task<Fixture> NewAsync()
    {
        CreatedGroup group = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        var store = new TracingStore();
        var relay = new StubRelay();

        return new Fixture(
            group,
            new GroupId(group.GroupId),
            store,
            relay,
            new CommitPublisher(store, relay, () => Clock));
    }

    /// <summary>A real wrapped commit, so the envelope is genuinely not the MLS bytes.</summary>
    private static string Wrap(Fixture f, StagedCommit staged) =>
        GroupHandshake.Wrap(f.Group.Group, new NostrGroupPeeler(), staged.Commit);

    private static MessageId MlsDigestOf(StagedCommit staged) =>
        MessageId.FromMlsBytes(
            TlsCodec.Serialize(
                new MlsMessage(WireFormat.MlsPublicMessage, staged.Commit).WriteTo));

    // ---- Write ordering ----

    [Fact]
    public async Task TheAttemptIsRecordedBeforeTheCommitReachesATransport()
    {
        // The whole mechanism. A crash after the bytes go out and before the
        // row is written leaves a commit that may be on a relay and nothing at
        // all that says so -- the next session finds the group settled at an
        // epoch everyone else has left, and cannot even detect the fork.
        Fixture f = await NewAsync();
        using StagedCommit staged = MarmotSelfUpdate.Stage(f.Group.Group);

        CommitPublishState? seenAtSendTime = null;
        f.Relay.OnPublish = () => seenAtSendTime = f.Store.Row(f.GroupId)?.State;

        await f.Publisher.PublishAsync(f.GroupId, staged, Wrap(f, staged));

        Assert.Equal(CommitPublishState.HandedToTransport, seenAtSendTime);
    }

    [Fact]
    public async Task ACommitIsNotDeclaredPublishedUntilTheRecordIsDurable()
    {
        // The other half of that ordering, and the reason the durable write is
        // awaited rather than started. Publishing() disarms disposal, so
        // declaring it first and then failing the write would strand a commit
        // nobody ever sent -- and a stranded commit blocks every later commit
        // on the group, including the retry.
        Fixture f = await NewAsync();
        f.Store.WritesBeforeFailure = 0;

        try
        {
            using StagedCommit staged = MarmotSelfUpdate.Stage(f.Group.Group);
            await f.Publisher.PublishAsync(f.GroupId, staged, Wrap(f, staged));
        }
        catch (IOException)
        {
            // Swallowed on purpose: the point is what the group looks like now.
        }

        Assert.Equal(0, f.Relay.Calls);
        Assert.False(f.Group.Group.HasPendingCommit);
        Assert.Equal(0u, f.Group.Group.Epoch);

        // And the retry works, which is the outcome that actually matters.
        using StagedCommit retry = MarmotSelfUpdate.Stage(f.Group.Group);
        retry.Publishing();
        retry.Applied();

        Assert.Equal(1u, f.Group.Group.Epoch);
    }

    [Fact]
    public async Task AnAcceptedCommitIsRecordedAcceptedBeforeItIsAppliedLocally()
    {
        // The window a crash must not resolve as abandonment. Once the relay
        // has taken the commit the rest of the group moves on, and we cannot
        // reissue it: MLS refuses to let a member process a commit it authored,
        // so our own bytes coming back off the relay are no help.
        Fixture f = await NewAsync();
        f.Store.Observe = () => $"epoch {f.Group.Group.Epoch}";
        f.Relay.Answer = () => CommitPublishOutcome.Accepted;

        using StagedCommit staged = MarmotSelfUpdate.Stage(f.Group.Group);
        await f.Publisher.PublishAsync(f.GroupId, staged, Wrap(f, staged));

        Assert.Contains("put Accepted while epoch 0", f.Store.Trace);
        Assert.Equal(1u, f.Group.Group.Epoch);
    }

    [Fact]
    public async Task AnAcceptedCommitKeepsItsRowForWhoeverOwnsTheDurableMove()
    {
        // This asserted the row was cleared here, and that was the defect: the
        // apply it waited for is an in-memory one, while the move a restart can
        // see -- the checkpoint and the live state -- belongs to the caller and
        // has not happened yet. A crash in between came back with no attempt
        // row, which ClassifyAsync reads as "nobody saw this commit", and the
        // commit was abandoned though the whole group had applied it.
        Fixture f = await NewAsync();

        using StagedCommit staged = MarmotSelfUpdate.Stage(f.Group.Group);

        Assert.Equal(
            CommitPublishOutcome.Accepted,
            await f.Publisher.PublishAsync(f.GroupId, staged, Wrap(f, staged)));

        Assert.Equal(1u, f.Group.Group.Epoch);
        Assert.Equal(CommitPublishState.Accepted, f.Store.Row(f.GroupId)?.State);
        Assert.Equal(new EpochId(1), f.Store.Row(f.GroupId)?.NewEpoch);

        // It goes when that move lands -- MarmotSessionHost.ConfirmAsync
        // writes the checkpoint and the live state and only then drops it.
    }

    [Fact]
    public async Task ARefusedPublishLeavesNoRowBehind()
    {
        // A refusal has no durable consequence pending -- nothing further will
        // be written about this commit -- so the row has nothing left to
        // outlive and goes immediately. An accepted one is the opposite case,
        // and used to be treated the same way; see the test above.
        Fixture f = await NewAsync();
        f.Relay.Answer = () => CommitPublishOutcome.Rejected;

        using StagedCommit staged = MarmotSelfUpdate.Stage(f.Group.Group);
        CommitPublishOutcome outcome =
            await f.Publisher.PublishAsync(f.GroupId, staged, Wrap(f, staged));

        Assert.Equal(CommitPublishOutcome.Rejected, outcome);
        Assert.Equal(0, f.Store.Count);
        Assert.Equal(StrandedCommitVerdict.Abandon, await f.Publisher.ClassifyAsync(f.GroupId));
    }

    // ---- The three outcomes, which are not two ----

    [Fact]
    public async Task ATransportThatThrowsIsIndeterminateAndTheCommitSurvives()
    {
        // The asymmetry this repo already applies to KeyPackages: a timeout is
        // not a rejection. An exception says nothing about what the relay saw,
        // and the safe reading of "nothing" is that it might have seen it.
        // Discarding here is the fork that cannot be repaired from this side.
        Fixture f = await NewAsync();
        f.Relay.Answer = () => throw new TimeoutException("the relay never answered");

        using StagedCommit staged = MarmotSelfUpdate.Stage(f.Group.Group);
        CommitPublishOutcome outcome =
            await f.Publisher.PublishAsync(f.GroupId, staged, Wrap(f, staged));

        Assert.Equal(CommitPublishOutcome.Indeterminate, outcome);
        Assert.Equal(CommitPublishState.Indeterminate, f.Store.Row(f.GroupId)?.State);

        // Left exactly as it was: still staged, still at the old epoch, and
        // still on record as possibly live.
        Assert.True(f.Group.Group.HasPendingCommit);
        Assert.Equal(0u, f.Group.Group.Epoch);
        Assert.Equal(StrandedCommitVerdict.Reconcile, await f.Publisher.ClassifyAsync(f.GroupId));
    }

    [Fact]
    public async Task AnIndeterminateAnswerIsTreatedTheSameAsAThrow()
    {
        // A relay that answers "I cannot tell you" and a transport that dies
        // mid-send are the same fact, and the enum exists so a caller cannot
        // accidentally give them different answers.
        Fixture f = await NewAsync();
        f.Relay.Answer = () => CommitPublishOutcome.Indeterminate;

        using StagedCommit staged = MarmotSelfUpdate.Stage(f.Group.Group);
        CommitPublishOutcome outcome =
            await f.Publisher.PublishAsync(f.GroupId, staged, Wrap(f, staged));

        Assert.Equal(CommitPublishOutcome.Indeterminate, outcome);
        Assert.True(f.Group.Group.HasPendingCommit);
        Assert.Equal(StrandedCommitVerdict.Reconcile, await f.Publisher.ClassifyAsync(f.GroupId));
    }

    [Fact]
    public async Task OnlyARejectionDiscardsTheCommit()
    {
        // The one outcome that authorises the destructive move, because it is
        // the one that says no copy of the commit exists anywhere.
        Fixture f = await NewAsync();
        f.Relay.Answer = () => CommitPublishOutcome.Rejected;

        using StagedCommit staged = MarmotSelfUpdate.Stage(f.Group.Group);
        CommitPublishOutcome outcome =
            await f.Publisher.PublishAsync(f.GroupId, staged, Wrap(f, staged));

        Assert.Equal(CommitPublishOutcome.Rejected, outcome);
        Assert.False(f.Group.Group.HasPendingCommit);
        Assert.Equal(0u, f.Group.Group.Epoch);
        Assert.Equal(0, f.Store.Count);
    }

    // ---- What a restart reads ----

    [Fact]
    public async Task ACommitThatNeverReachedATransportIsAbandoned()
    {
        // The absence of a row is a positive statement, not missing
        // information: no commit of ours left this device, so nobody else can
        // have seen one, and the only consistent outcome is that it never
        // happened.
        Fixture f = await NewAsync();

        using StagedCommit staged = MarmotSelfUpdate.Stage(f.Group.Group);

        Assert.Equal(StrandedCommitVerdict.Abandon, await f.Publisher.ClassifyAsync(f.GroupId));
    }

    [Fact]
    public async Task ACommitKilledBetweenTheHandoffAndTheAnswerIsReconciled()
    {
        // The crash the row exists for. The bytes went out and the process died
        // before learning what happened to them, so neither abandoning nor
        // adopting is sound -- the relay has to be asked.
        Fixture f = await NewAsync();
        f.Store.WritesBeforeFailure = 1;

        using (StagedCommit staged = MarmotSelfUpdate.Stage(f.Group.Group))
        {
            await Assert.ThrowsAsync<IOException>(
                () => f.Publisher.PublishAsync(f.GroupId, staged, Wrap(f, staged)));
        }

        Assert.Equal(CommitPublishState.HandedToTransport, f.Store.Row(f.GroupId)?.State);

        // A fresh publisher over the same store: everything the in-memory
        // objects knew is gone, which is what a restart is.
        var revived = new CommitPublisher(f.Store, new StubRelay(), () => Clock);
        Assert.Equal(StrandedCommitVerdict.Reconcile, await revived.ClassifyAsync(f.GroupId));
    }

    [Fact]
    public void TheVerdictIsAbandonForARejectionAndNothingElse()
    {
        // The mapping stated on its own, because it is the part item 3 will
        // branch on and the two middle cases look adjacent while wanting
        // opposite moves.
        var groupId = StorageFixture.NewGroupId();
        CommitPublishAttempt handed = CommitPublishAttempt.HandedToTransport(
            groupId, StorageFixture.NewMessageId(), new EpochId(4), Clock);

        Assert.Equal(StrandedCommitVerdict.Reconcile, handed.Verdict);
        Assert.Equal(
            StrandedCommitVerdict.Reconcile,
            handed.Resolved(CommitPublishState.Indeterminate, Clock).Verdict);
        Assert.Equal(
            StrandedCommitVerdict.Adopt,
            handed.Resolved(CommitPublishState.Accepted, Clock).Verdict);
        Assert.Equal(
            StrandedCommitVerdict.Abandon,
            handed.Resolved(CommitPublishState.Rejected, Clock).Verdict);

        Assert.Equal(StrandedCommitVerdict.Abandon, CommitPublishAttempt.VerdictFor(null));
    }

    // ---- The id a relay can be asked about ----

    [Fact]
    public async Task TheRecordedIdIsTheDigestOfTheMlsBytesAndNotOfTheEnvelope()
    {
        // The whole value of storing an id is that a reconciling session can
        // ask a relay "do you have this?". Hashing the transport envelope
        // yields 64 hex characters that look exactly like a digest and that no
        // peer can reproduce -- a relay is free to re-serialise the envelope --
        // so the query would match nothing and report a live commit as absent.
        Fixture f = await NewAsync();
        f.Relay.Answer = () => CommitPublishOutcome.Indeterminate;

        using StagedCommit staged = MarmotSelfUpdate.Stage(f.Group.Group);
        string envelope = Wrap(f, staged);
        MessageId envelopeDigest =
            MessageId.FromMlsBytes(System.Text.Encoding.UTF8.GetBytes(envelope));

        await f.Publisher.PublishAsync(f.GroupId, staged, envelope);

        Assert.Equal(MlsDigestOf(staged), f.Store.Row(f.GroupId)!.CommitId);
        Assert.NotEqual(envelopeDigest, f.Store.Row(f.GroupId)!.CommitId);
    }

    [Fact]
    public async Task TheRecordedEpochIsTheOneTheCommitProducesNotTheOneItLeaves()
    {
        // What lets a restart tell "never applied" from "applied, and only the
        // clear was lost": if live state already stands at this epoch, the row
        // is stale rather than actionable.
        Fixture f = await NewAsync();
        f.Relay.Answer = () => CommitPublishOutcome.Indeterminate;

        using StagedCommit first = MarmotSelfUpdate.Stage(f.Group.Group);
        first.Publishing();
        first.Applied();

        using StagedCommit staged = MarmotSelfUpdate.Stage(f.Group.Group);
        await f.Publisher.PublishAsync(f.GroupId, staged, Wrap(f, staged));

        Assert.Equal(1u, f.Group.Group.Epoch);
        Assert.Equal(new EpochId(2), f.Store.Row(f.GroupId)!.NewEpoch);
    }

    // ---- The record's own rules ----

    [Fact]
    public void AnAnswerCannotBeOverwrittenOrUnsaid()
    {
        var handed = CommitPublishAttempt.HandedToTransport(
            StorageFixture.NewGroupId(), StorageFixture.NewMessageId(), new EpochId(1), Clock);

        // "Handed to transport" is what the row says before an answer arrives,
        // never an answer itself -- writing it back would turn a relay's
        // definite no into "ask it again".
        Assert.Throws<ArgumentException>(
            () => handed.Resolved(CommitPublishState.HandedToTransport, Clock));

        // And a second answer would let a retry's rejection overwrite an
        // earlier acceptance, which authorises discarding a commit that is live.
        CommitPublishAttempt accepted = handed.Resolved(CommitPublishState.Accepted, Clock);
        Assert.Throws<InvalidOperationException>(
            () => accepted.Resolved(CommitPublishState.Rejected, Clock));

        // The handoff time is the handoff's, not the answer's.
        Assert.Equal(handed.HandedOffAt, accepted.HandedOffAt);
    }

    [Fact]
    public void AnAttemptWithNoCommitIdIsRefused()
    {
        // A row with nothing to ask a relay about is a row that can only be
        // read as "reconcile" and never acted on -- a group frozen on a
        // question nobody can pose.
        Assert.Throws<ArgumentException>(
            () => CommitPublishAttempt.HandedToTransport(
                StorageFixture.NewGroupId(), new MessageId([]), new EpochId(1), Clock));
    }

    // ---- Across the database ----

    [Fact]
    public async Task TheAttemptSurvivesTheDatabase()
    {
        using var fixture = new StorageFixture();
        var groupId = StorageFixture.NewGroupId();
        MessageId commitId = StorageFixture.NewMessageId();

        CommitPublishAttempt handed = CommitPublishAttempt.HandedToTransport(
            groupId, commitId, new EpochId(7), Clock);

        await fixture.Provider.PutCommitPublishAttemptAsync(handed);

        CommitPublishAttempt? read =
            await fixture.Provider.GetCommitPublishAttemptAsync(groupId);

        Assert.NotNull(read);
        Assert.Equal(commitId, read!.CommitId);
        Assert.Equal(new EpochId(7), read.NewEpoch);
        Assert.Equal(CommitPublishState.HandedToTransport, read.State);
        Assert.Equal(StrandedCommitVerdict.Reconcile, read.Verdict);

        // Resolving is a write to the same row: a group has one staged commit,
        // so a second row for it could only describe one that no longer exists.
        await fixture.Provider.PutCommitPublishAttemptAsync(
            handed.Resolved(CommitPublishState.Accepted, Clock.AddSeconds(3)));

        Assert.Single(await fixture.Provider.ListCommitPublishAttemptsAsync());

        CommitPublishAttempt resolved =
            (await fixture.Provider.GetCommitPublishAttemptAsync(groupId))!;

        Assert.Equal(CommitPublishState.Accepted, resolved.State);
        Assert.Equal(StrandedCommitVerdict.Adopt, resolved.Verdict);
        Assert.Equal(Clock, resolved.HandedOffAt);
        Assert.Equal(Clock.AddSeconds(3), resolved.UpdatedAt);

        Assert.True(await fixture.Provider.ClearCommitPublishAttemptAsync(groupId));
        Assert.False(await fixture.Provider.ClearCommitPublishAttemptAsync(groupId));
        Assert.Null(await fixture.Provider.GetCommitPublishAttemptAsync(groupId));
    }

    [Fact]
    public async Task AStateThisBuildDoesNotKnowIsRefusedRatherThanReadAsAbsent()
    {
        // A row a newer build wrote. Reading it as absent is the dangerous
        // failure and not the conservative one: absent is the positive
        // statement "no commit of ours ever left this device", which is the
        // only thing that authorises discarding one. So an uninterpretable row
        // would license exactly the fork this table exists to prevent.
        string path = Path.Combine(Path.GetTempPath(), $"marmot-publish-{Guid.NewGuid():N}.db");

        try
        {
            using var provider = new SqliteMarmotStorageProvider($"Data Source={path}");

            // Written the only way it could ever appear: another writer on the
            // same file.
            using (var raw = new SqliteConnection($"Data Source={path}"))
            {
                raw.Open();
                using SqliteCommand cmd = raw.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO marmot_commit_publish_attempts
                        (group_id, commit_id, new_epoch, state, handed_off_at, updated_at)
                    VALUES (x'07', x'aabb', 3, 9,
                            '2026-01-01T00:00:00.0000000+00:00',
                            '2026-01-01T00:00:00.0000000+00:00');";
                cmd.ExecuteNonQuery();
            }

            InvalidOperationException ex =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => provider.GetCommitPublishAttemptAsync(new GroupId([0x07])));

            Assert.Contains("Unknown commit publish state 9", ex.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A stray temp file is not worth failing a test run over.
            }
        }
    }
}
