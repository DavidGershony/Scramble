using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot.Engine;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Engine.Session;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Marmot.Tests;

/// <summary>
/// Sending an application message, and what happens when it cannot go yet.
/// </summary>
/// <remarks>
/// <para>
/// Written against the brief rather than the implementation, and before reading
/// it — the two were built from one contract and not from each other. What is
/// tested here is therefore what the rules demand, not the shape the code
/// happened to take.
/// </para>
/// <para>
/// The wire format is not what this risks: <c>GroupMessages.Send</c> is proven
/// against a live peer in the interop suite. What is new is the composition —
/// durability before the wire, the queue, the drain, and the gate that stops a
/// queued message reaching a group we have been evicted from.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class MessageSendTests : IDisposable
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

    /// <summary>A relay that answers however it is told, and watches storage.</summary>
    private sealed class ProbeRelay : IMessageRelay
    {
        private readonly Queue<MessageSendOutcome> _answers = new();
        private readonly MessageSendOutcome _default;

        public ProbeRelay(MessageSendOutcome answer) => _default = answer;

        public ProbeRelay(params MessageSendOutcome[] answers)
        {
            foreach (MessageSendOutcome a in answers)
                _answers.Enqueue(a);

            _default = MessageSendOutcome.Accepted;
        }

        /// <summary>Envelopes handed over, in order.</summary>
        public List<string> Envelopes { get; } = [];

        /// <summary>Asked at the moment of each send, for the ordering rule.</summary>
        public Func<Task<int>>? QueuedRightNow { get; set; }

        /// <summary>What that question answered, per send.</summary>
        public List<int> QueuedAtSendTime { get; } = [];

        public async Task<MessageSendOutcome> SendAsync(
            string envelope, CancellationToken ct = default)
        {
            Envelopes.Add(envelope);

            if (QueuedRightNow is not null)
                QueuedAtSendTime.Add(await QueuedRightNow());

            return _answers.Count > 0 ? _answers.Dequeue() : _default;
        }
    }

    private sealed class UnreachableCommitRelay : ICommitRelay
    {
        public Task<CommitPublishOutcome> PublishAsync(
            string envelope, CancellationToken ct = default) =>
            throw new InvalidOperationException("no commits in these tests");
    }

    private sealed record Pair(
        MarmotSession Us,
        LocalSigner OurSigner,
        MlsGroup Them,
        GroupId GroupId,
        MarmotSessionHost Host);

    private MarmotSessionHost Host(IMessageRelay messages) =>
        new(_fixture.Provider, _cs, new UnreachableCommitRelay(), messages,
            ConvergencePolicy.V1, () => _now);

    /// <summary>Us as a session, and one other member who is just an MLS group.</summary>
    private async Task<Pair> PairAsync(IMessageRelay messages)
    {
        var ourSigner = new LocalSigner();

        CreatedGroup us = await MarmotGroupBuilder.CreateAsync(
            _cs, ourSigner, "Rakes", "", Now, Relays);

        var bundle = await MarmotKeyPackageBuilder.CreateAsync(_cs, new LocalSigner(), Now);

        StagedCommit staged = MarmotGroupInvite.Add(us.Group, _cs, [bundle.KeyPackage]);
        staged.Applied();

        MlsGroup them = MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey,
            config: MarmotGroupSettings.Create());

        // The host is kept, not discarded. It builds its own EpochManager
        // internally, so a manager constructed out here is a different object
        // that nothing in the engine ever reads -- and a helper mutating it
        // would appear to unsettle the group while changing nothing at all.
        MarmotSessionHost host = Host(messages);
        MarmotSession session = await host.AdoptAsync(us.ToRecord(_now), us.Group);

        return new Pair(session, ourSigner, them, new GroupId(us.GroupId), host);
    }

    private MarmotAppEvent Chat(Pair pair, string text) =>
        MarmotAppEvent.Chat(pair.OurSigner.Hex, _now.ToUnixTimeSeconds(), text);

    /// <summary>What the other member reads out of an envelope we sent.</summary>
    /// <remarks>
    /// Decrypted rather than compared as a string. An envelope that round-trips
    /// as text proves the queue kept bytes; only decrypting it on somebody
    /// else's group proves we sent a message they can read.
    /// </remarks>
    private static string ReadOnThem(Pair pair, string envelope)
    {
        var peeler = new NostrGroupPeeler();

        byte[] mlsBytes = peeler
            .Peel(envelope, _ => GroupMessages.ExporterSecret(pair.Them))
            .MlsBytes;

        return GroupMessages.Receive(pair.Them, mlsBytes).Event.Content;
    }

    private Task<int> QueueDepth(GroupId groupId) =>
        _fixture.Provider.ListIntentsAsync(groupId).ContinueWith(t => t.Result.Count);

    /// <summary>Puts the group somewhere it cannot send from.</summary>
    private static PendingStateRef Unsettle(Pair pair)
    {
        EpochManager epochs = pair.Host.Epochs.Epochs;
        var epoch = new EpochId(pair.Us.Group.Epoch);
        PendingStateRef pending = epochs.NextPendingRef();

        epochs.SetStable(pair.GroupId, epoch);
        epochs.BeginPending(
            pair.GroupId, epoch, new EpochId(epoch.Value + 1),
            new StagedCommitHandle([1]), pending, PendingKind.GroupEvolution);

        return pending;
    }

    private static void Resettle(Pair pair, PendingStateRef pending) =>
        pair.Host.Epochs.Epochs.RollbackPublish(pending);

    // ---- Rule 1: durable before the wire ----

    [Fact]
    public async Task TheMessageIsDurableBeforeItReachesATransport()
    {
        // The window that must not lose a message: handed to a relay and not
        // yet written down. A crash there has to leave the message queued, and
        // the only way to see the ordering from outside is to ask storage at
        // the moment the transport is called.
        var relay = new ProbeRelay(MessageSendOutcome.Accepted);
        Pair pair = await PairAsync(relay);

        relay.QueuedRightNow = () => QueueDepth(pair.GroupId);

        await pair.Us.SendAsync(Chat(pair, "durable first"));

        Assert.Equal([1], relay.QueuedAtSendTime);
    }

    // ---- Rules 2-4: what each outcome does ----

    [Fact]
    public async Task ASendFromASettledGroupGoesOutAndLeavesNothingQueued()
    {
        var relay = new ProbeRelay(MessageSendOutcome.Accepted);
        Pair pair = await PairAsync(relay);

        SendResult result = await pair.Us.SendAsync(Chat(pair, "hello"));

        Assert.Equal(SendDisposition.Sent, result.Disposition);
        Assert.Equal(0, result.Queued);
        Assert.Equal("hello", ReadOnThem(pair, Assert.Single(relay.Envelopes)));
        Assert.Empty(await _fixture.Provider.ListIntentsAsync(pair.GroupId));
    }

    [Fact]
    public async Task ASendFromAnUnsettledGroupQueuesWithoutTouchingTheTransport()
    {
        // Mid-publish the group is about to move. The message waits rather than
        // going out against a state the group is leaving.
        var relay = new ProbeRelay(MessageSendOutcome.Accepted);
        Pair pair = await PairAsync(relay);
        Unsettle(pair);

        SendResult result = await pair.Us.SendAsync(Chat(pair, "not yet"));

        Assert.Equal(SendDisposition.Queued, result.Disposition);
        Assert.Empty(relay.Envelopes);
        Assert.Single(await _fixture.Provider.ListIntentsAsync(pair.GroupId));
    }

    [Theory]
    [InlineData(MessageSendOutcome.Rejected)]
    [InlineData(MessageSendOutcome.Indeterminate)]
    public async Task ASendTheRelayDidNotAcceptStaysQueued(MessageSendOutcome outcome)
    {
        // Both are retryable for a message, and deliberately indistinguishable
        // here. A commit needs to tell them apart because it cannot be
        // reissued; a message can be, since dedup is content-derived and the
        // receiver drops the duplicate.
        var relay = new ProbeRelay(outcome);
        Pair pair = await PairAsync(relay);

        SendResult result = await pair.Us.SendAsync(Chat(pair, "try again later"));

        Assert.Equal(SendDisposition.Queued, result.Disposition);
        Assert.Single(await _fixture.Provider.ListIntentsAsync(pair.GroupId));
    }

    // ---- Rules 5 and 7: the drain ----

    [Fact]
    public async Task AMessageQueuedWhileUnsettledIsSentOnceTheGroupSettles()
    {
        // The promise queueing makes. Nothing about the message changed; what
        // changed is that the group can send again.
        var relay = new ProbeRelay(MessageSendOutcome.Accepted);
        Pair pair = await PairAsync(relay);

        PendingStateRef pending = Unsettle(pair);
        await pair.Us.SendAsync(Chat(pair, "sent when it could be"));
        Assert.Empty(relay.Envelopes);

        Resettle(pair, pending);

        DrainResult drained = await pair.Us.DrainAsync();

        Assert.Equal(1, drained.Sent);
        Assert.Equal(0, drained.Queued);
        Assert.Equal("sent when it could be", ReadOnThem(pair, Assert.Single(relay.Envelopes)));
        Assert.Empty(await _fixture.Provider.ListIntentsAsync(pair.GroupId));
    }

    [Fact]
    public async Task ADrainSendsInTheOrderTheMessagesWereQueued()
    {
        // Conversation order is the one property a user can see directly, and a
        // queue that drains out of order is worse than one that drains slowly.
        var relay = new ProbeRelay(MessageSendOutcome.Accepted);
        Pair pair = await PairAsync(relay);

        PendingStateRef pending = Unsettle(pair);

        foreach (string text in new[] { "first", "second", "third" })
        {
            await pair.Us.SendAsync(Chat(pair, text));
            _now = _now.AddSeconds(1);
        }

        Resettle(pair, pending);

        DrainResult drained = await pair.Us.DrainAsync();

        Assert.Equal(3, drained.Sent);
        Assert.Equal(
            new[] { "first", "second", "third" },
            relay.Envelopes.Select(e => ReadOnThem(pair, e)));
    }

    [Fact]
    public async Task ADrainStopsAtTheFirstMessageTheRelayWouldNotTake()
    {
        // Order matters more than draining eagerly: pushing past a refusal
        // delivers later messages before earlier ones, and the earlier one is
        // still coming.
        var relay = new ProbeRelay(
            MessageSendOutcome.Accepted, MessageSendOutcome.Indeterminate);

        Pair pair = await PairAsync(relay);
        PendingStateRef pending = Unsettle(pair);

        foreach (string text in new[] { "one", "two", "three" })
        {
            await pair.Us.SendAsync(Chat(pair, text));
            _now = _now.AddSeconds(1);
        }

        Resettle(pair, pending);

        DrainResult drained = await pair.Us.DrainAsync();

        Assert.Equal(1, drained.Sent);
        Assert.Equal(2, drained.Queued);
        Assert.Equal(2, relay.Envelopes.Count);
        Assert.Equal("one", ReadOnThem(pair, relay.Envelopes[0]));
    }

    // ---- Rule 6: the eviction gate ----

    [Fact]
    public async Task AGroupWeHaveBeenRemovedFromRefusesToSendAndQueuesNothing()
    {
        var relay = new ProbeRelay(MessageSendOutcome.Accepted);
        Pair pair = await PairAsync(relay);

        GroupRecord record = (await _fixture.Provider.GetGroupAsync(pair.GroupId))!;
        await _fixture.Provider.PutGroupAsync(record with { Removed = true });

        SendResult result = await pair.Us.SendAsync(Chat(pair, "nobody to hear it"));

        Assert.Equal(SendDisposition.Refused, result.Disposition);
        Assert.Empty(relay.Envelopes);
        Assert.Empty(await _fixture.Provider.ListIntentsAsync(pair.GroupId));
    }

    [Fact]
    public async Task DrainingAGroupWeHaveLeftDropsTheQueueRatherThanSendingIt()
    {
        // The rule the storage contract states outright: queued sends must
        // never be drained into a group we have left. They were written for a
        // conversation this device is no longer part of.
        var relay = new ProbeRelay(MessageSendOutcome.Accepted);
        Pair pair = await PairAsync(relay);

        PendingStateRef pending = Unsettle(pair);
        await pair.Us.SendAsync(Chat(pair, "queued before the eviction"));
        await pair.Us.SendAsync(Chat(pair, "and this one too"));
        Resettle(pair, pending);

        GroupRecord record = (await _fixture.Provider.GetGroupAsync(pair.GroupId))!;
        await _fixture.Provider.PutGroupAsync(record with { Removed = true });

        DrainResult drained = await pair.Us.DrainAsync();

        Assert.Equal(0, drained.Sent);
        Assert.Equal(2, drained.Dropped);
        Assert.Empty(relay.Envelopes);
        Assert.Empty(await _fixture.Provider.ListIntentsAsync(pair.GroupId));
    }

    // ---- Rule 8 ----

    [Fact]
    public async Task AFailedAttemptIsCountedOnTheMessageItFailedFor()
    {
        // Diagnostic only -- it bounds nothing, because the delivery window and
        // the eviction gate are what decide a queued message's fate. Worth
        // seeing all the same.
        var relay = new ProbeRelay(MessageSendOutcome.Indeterminate);
        Pair pair = await PairAsync(relay);

        await pair.Us.SendAsync(Chat(pair, "counted"));

        QueuedOutboundIntent queued =
            Assert.Single(await _fixture.Provider.ListIntentsAsync(pair.GroupId));

        Assert.Equal(1, queued.Attempts);
        Assert.Equal("app-message", queued.IntentKind);
    }
}
