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
/// Routing an arriving envelope to the group it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here goes in as an envelope with no group named.</b> That is
/// the subject: every other suite in this project picks the destination session
/// itself and hands it bytes, which is precisely the step that did not exist.
/// A test that told the fan-in which group to use would be testing nothing.
/// </para>
/// <para>
/// The sender is an ordinary <see cref="MlsGroup"/> rather than a second
/// session, so nothing it does is arranged by the code under test.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class InboundFanInTests : IDisposable
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

    private MarmotSessionHost Host(ITransportPeeler? peeler = null) =>
        new(_fixture.Provider, _cs, new UnreachableRelay(), null, peeler,
            ConvergencePolicy.V1, () => _now);

    private static readonly NostrGroupPeeler Peeler = new();

    private sealed record Pair(
        MarmotSessionHost Host,
        InboundFanIn FanIn,
        MlsGroup Them,
        LocalSigner TheirSigner,
        GroupId GroupId);

    /// <summary>Us as a fan-in, and one other member who is just an MLS group.</summary>
    private async Task<Pair> PairAsync()
    {
        CreatedGroup us = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        var theirSigner = new LocalSigner();
        MlsGroup them = await AdmitAsync(us.Group, theirSigner);

        MarmotSessionHost host = Host();
        await host.AdoptAsync(us.ToRecord(_now), us.Group);

        return new Pair(host, new InboundFanIn(host), them, theirSigner, new GroupId(us.GroupId));
    }

    /// <summary>Adds a second member to a group and returns them as a live group.</summary>
    private async Task<MlsGroup> AdmitAsync(MlsGroup group, LocalSigner signer)
    {
        MarmotKeyPackageBundle bundle =
            await MarmotKeyPackageBuilder.CreateAsync(_cs, signer, Now);

        StagedCommit staged = MarmotGroupInvite.Add(group, _cs, [bundle.KeyPackage]);
        staged.Applied();

        return MlsGroup.ProcessWelcome(
            _cs, staged.Welcome!, bundle.KeyPackage,
            bundle.PrivateMaterial.InitPrivateKey,
            bundle.PrivateMaterial.LeafPrivateKey,
            bundle.PrivateMaterial.SignaturePrivateKey,
            config: MarmotGroupSettings.Create());
    }

    /// <summary>An application message from the member who is not a session.</summary>
    private static string Says(MlsGroup them, LocalSigner signer, string text) =>
        GroupMessages.Send(
            them,
            Peeler,
            MarmotAppEvent.Chat(signer.Hex, (long)Now, text),
            signer.AccountPublicKey.Span);

    /// <summary>A commit from the other member, wrapped at the epoch it was built in.</summary>
    private static string Commits(MlsGroup sender)
    {
        using StagedCommit staged = MarmotSelfUpdate.Stage(sender);
        string envelope = GroupHandshake.Wrap(sender, Peeler, staged.Commit);
        staged.Publishing();
        staged.Applied();
        return envelope;
    }

    private static IngestResult Ingested(InboundDelivery delivery) =>
        Assert.IsType<InboundDelivery.Delivered>(delivery).Result;

    private static InputRejectionCategory RefusedAs(InboundDelivery delivery) =>
        Assert.IsType<IngestOutcome.Ignored>(
            Assert.IsType<InboundDelivery.Refused>(delivery).Outcome).Category;

    // ---- The path that did not exist ----

    [Fact]
    public async Task AnEnvelopeNamingOnlyARoutingIdReachesTheGroupItBelongsTo()
    {
        // The whole subject. Nothing tells the fan-in which group these bytes
        // are for; the routing index does, and until this work nothing ever
        // wrote to that index so the answer could only ever be "no idea".
        Pair pair = await PairAsync();

        InboundDelivery delivery = await pair.FanIn.ReceiveAsync(
            Says(pair.Them, pair.TheirSigner, "the fan-in works"));

        var delivered = Assert.IsType<InboundDelivery.Delivered>(delivery);

        Assert.Equal(pair.GroupId, delivered.GroupId);
        Assert.IsType<IngestOutcome.Processed>(delivered.Result.Outcome);
        Assert.Equal("the fan-in works", delivered.Result.Message!.Event.Content);
    }

    [Fact]
    public async Task ACommitArrivesThroughTheSamePathAndMovesTheGroup()
    {
        // A commit is the case that matters more than chat: it is what a
        // convergence pass reads, and one that never reaches a group is a fork
        // this member cannot see.
        Pair pair = await PairAsync();

        InboundDelivery delivery = await pair.FanIn.ReceiveAsync(Commits(pair.Them));

        var processed = Assert.IsType<IngestOutcome.Processed>(Ingested(delivery).Outcome);
        Assert.Equal(2UL, processed.Epoch.Value);
    }

    [Fact]
    public async Task TheSameSessionServesEveryEnvelopeForOneGroup()
    {
        Pair pair = await PairAsync();

        await pair.FanIn.ReceiveAsync(Says(pair.Them, pair.TheirSigner, "one"));
        await pair.FanIn.ReceiveAsync(Says(pair.Them, pair.TheirSigner, "two"));

        // One session, not two. A second session over one group id holds its own
        // MlsGroup and writes its own live state, so the loser's epoch is
        // silently discarded -- a fork reached without any peer's help.
        Assert.Equal(1, pair.FanIn.OpenSessions);
    }

    // ---- Somebody else's traffic ----

    [Fact]
    public async Task AnEnvelopeForAGroupWeDoNotHaveIsRefusedAndOpensNothing()
    {
        // The common case, by a wide margin: almost every kind-445 event on a
        // relay belongs to a group we are not in. It must be cheap and it must
        // not throw.
        Pair pair = await PairAsync();

        var strangerSigner = new LocalSigner();
        CreatedGroup stranger = await MarmotGroupBuilder.CreateAsync(
            _cs, strangerSigner, "Elsewhere", "", Now, Relays);
        MlsGroup theirMember = await AdmitAsync(stranger.Group, new LocalSigner());

        InboundDelivery delivery = await pair.FanIn.ReceiveAsync(
            Says(theirMember, strangerSigner, "not for you"));

        Assert.Equal(InputRejectionCategory.UnknownGroup, RefusedAs(delivery));

        // No session was opened. That is the assertion with teeth: a fan-in
        // that resolved the address after opening a group, or that fell back to
        // trying groups it holds, would pay an MlsGroup.Import for every
        // stranger on the relay.
        Assert.Equal(0, pair.FanIn.OpenSessions);
    }

    [Fact]
    public async Task ARoutingRowForAGroupWeNoLongerHoldIsRefusedRatherThanThrown()
    {
        // The index outliving its group. Resolution succeeds and the open does
        // not, and the two must give the same quiet answer -- a throw here
        // comes out of a subscription loop and takes every other group with it.
        Pair pair = await PairAsync();

        var strangerSigner = new LocalSigner();
        CreatedGroup stranger = await MarmotGroupBuilder.CreateAsync(
            _cs, strangerSigner, "Orphan", "", Now, Relays);
        MlsGroup theirMember = await AdmitAsync(stranger.Group, new LocalSigner());

        await _fixture.Provider.PutRoutingAsync(
            GroupMessages.TransportGroupId(stranger.Group),
            StorageFixture.NewGroupId(),
            new EpochId(0));

        InboundDelivery delivery = await pair.FanIn.ReceiveAsync(
            Says(theirMember, strangerSigner, "no record behind me"));

        Assert.Equal(InputRejectionCategory.UnknownGroup, RefusedAs(delivery));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"id\":\"beef\"}")]
    [InlineData("[]")]
    public async Task AMalformedEnvelopeIsRefusedRatherThanThrown(string envelope)
    {
        Pair pair = await PairAsync();

        InboundDelivery delivery = await pair.FanIn.ReceiveAsync(envelope);

        Assert.Equal(InputRejectionCategory.InvalidEncoding, RefusedAs(delivery));
    }

    // ---- The pre-filter, and what it is not ----

    [Fact]
    public async Task ARedeliveredEnvelopeStopsBeforeAnySessionIsOpened()
    {
        Pair pair = await PairAsync();
        string envelope = Says(pair.Them, pair.TheirSigner, "say it twice");

        Assert.IsType<IngestOutcome.Processed>(
            Ingested(await pair.FanIn.ReceiveAsync(envelope)).Outcome);

        // Emptied deliberately, so an opened session would show. That is what
        // makes the assertion about the pre-filter rather than about ingest's
        // content check, which would also have answered Duplicate -- one group
        // import later.
        //
        // A second fan-in would no longer do this: the cache lives on the host,
        // which is what stops two owners of one group existing at all.
        pair.Host.Clear();

        Assert.Equal(
            InputRejectionCategory.Duplicate, RefusedAs(await pair.FanIn.ReceiveAsync(envelope)));

        Assert.Equal(0, pair.FanIn.OpenSessions);
    }

    [Fact]
    public async Task ThePreFilterIsNotTheDeduplicationAndCannotBecomeIt()
    {
        // The same MLS message under a second envelope. This is routine -- a
        // relay re-serialises, a sender republishes -- and it is the case a
        // transport-keyed dedup gets wrong in the dangerous direction: it lets
        // the second copy through to be applied twice.
        Pair pair = await PairAsync();

        byte[] secret = GroupMessages.ExporterSecret(pair.Them);
        byte[] address = GroupMessages.TransportGroupId(pair.Them);

        string first = Says(pair.Them, pair.TheirSigner, "once, under two wrappers");
        PeeledMessage peeled = Peeler.Peel(first, _ => secret);
        string second = Peeler.WrapGroupMessage(peeled.MlsBytes, address, secret);

        Assert.IsType<IngestOutcome.Processed>(
            Ingested(await pair.FanIn.ReceiveAsync(first)).Outcome);

        // Two envelopes, two ids. The pre-filter has never seen the second one
        // and cannot be what refuses it.
        PeeledMessage secondPeeled = Peeler.Peel(second, _ => secret);
        Assert.NotEqual(peeled.TransportId, secondPeeled.TransportId);
        Assert.False(await _fixture.Provider.HasTransportSeenAsync(secondPeeled.TransportId!));

        InboundDelivery delivery = await pair.FanIn.ReceiveAsync(second);

        // It reached the group -- Delivered, not Refused -- and the group
        // refused it on content. That is the whole distinction.
        IngestResult result = Ingested(delivery);
        var ignored = Assert.IsType<IngestOutcome.Ignored>(result.Outcome);
        Assert.Equal(InputRejectionCategory.Duplicate, ignored.Category);
    }

    // ---- Registration ----

    [Fact]
    public async Task AdoptingAGroupRegistersItsAddressAndLaterCommitsDoNotRewriteIt()
    {
        Pair pair = await PairAsync();

        RoutingIndexRecord? registered =
            await _fixture.Provider.CurrentRoutingAsync(pair.GroupId);

        Assert.NotNull(registered);
        ulong atAdopt = registered.FirstEpoch.Value;

        // Two commits, neither of which touches the routing component.
        await pair.FanIn.ReceiveAsync(Commits(pair.Them));
        await pair.FanIn.ReceiveAsync(Commits(pair.Them));

        MarmotSession session = (await pair.FanIn.SessionForAsync(pair.GroupId))!;
        Assert.True(session.Group.Epoch > atAdopt, "the group should have moved on");

        IReadOnlyList<RoutingIndexRecord> all =
            await _fixture.Provider.ListRoutingAsync(pair.GroupId);

        // Still one address, still first current at the epoch it was adopted
        // at. Re-registering an unchanged address at each new epoch would
        // rewrite FirstEpoch forward, so the group would report its address as
        // having become current at whatever epoch it last moved through.
        Assert.Single(all);
        Assert.True(all[0].IsCurrent);
        Assert.Equal(atAdopt, all[0].FirstEpoch.Value);
    }

    [Fact]
    public async Task OpeningAGroupWhoseRoutingRowIsMissingPutsItBack()
    {
        // The crash window inside AdoptAsync, and the only thing that can heal
        // it: a group missing from the index receives nothing, and the only
        // other writer of routing is a state advance -- which needs a receive.
        CreatedGroup us = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Half-adopted", "", Now, Relays);

        var groupId = new GroupId(us.GroupId);
        await _fixture.Provider.PutGroupAsync(us.ToRecord(_now));

        Assert.Null(await _fixture.Provider.CurrentRoutingAsync(groupId));

        MarmotSessionHost host = Host();
        Assert.True((await host.OpenAsync(groupId)).Opened);

        RoutingIndexRecord? healed = await _fixture.Provider.CurrentRoutingAsync(groupId);

        Assert.NotNull(healed);
        Assert.Equal(GroupMessages.TransportGroupId(us.Group), healed.TransportGroupId);
    }

    // ---- Cache staleness ----

    [Fact]
    public async Task ASessionTheRestOfTheProcessHasMovedPastIsReopened()
    {
        Pair pair = await PairAsync();

        // Warm the cache at epoch 1.
        Assert.IsType<IngestOutcome.Processed>(
            Ingested(await pair.FanIn.ReceiveAsync(
                Says(pair.Them, pair.TheirSigner, "before"))).Outcome);

        // Another owner moves the group on and writes it down. OpenAsync is
        // used on purpose: it is the one door documented as bypassing
        // ownership, so it is the only way left to manufacture in a test what a
        // second process on the same database would do for real. Nothing inside
        // this host can produce it any more, which is the point of moving the
        // cache onto the host.
        MarmotSession other = (await pair.Host.OpenAsync(pair.GroupId)).Require();
        Assert.IsType<IngestOutcome.Processed>(
            (await other.ReceiveAsync(Commits(pair.Them))).Outcome);

        // Sealed under the new epoch's key. A stale cached session holds the
        // old one, so it would defer this rather than read it.
        InboundDelivery delivery = await pair.FanIn.ReceiveAsync(
            Says(pair.Them, pair.TheirSigner, "after"));

        IngestResult result = Ingested(delivery);
        Assert.IsType<IngestOutcome.Processed>(result.Outcome);
        Assert.Equal("after", result.Message!.Event.Content);
    }

    // ---- Welcomes ----

    [Fact]
    public async Task AWelcomeIsHandedBackRatherThanRoutedOrRefused()
    {
        // A Welcome names a group we are not in, so no routing id resolves it.
        // Reporting it as an unknown group would be the wrong answer to the
        // right question, and reporting it as malformed -- which is what a
        // peeler with no account secret has to say -- would be worse.
        var inviter = new LocalSigner();
        var invitee = new LocalSigner();

        CreatedGroup group = await MarmotGroupBuilder.CreateAsync(
            _cs, inviter, "Rakes", "", Now, Relays);

        MarmotKeyPackageBundle bundle =
            await MarmotKeyPackageBuilder.CreateAsync(_cs, invitee, Now);

        StagedCommit staged = MarmotGroupInvite.Add(group.Group, _cs, [bundle.KeyPackage]);
        staged.Applied();

        byte[] keyPackageEventId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        string envelope = WelcomePublication.Wrap(
            inviter.Secret,
            inviter.AccountPublicKey.Span,
            invitee.AccountPublicKey.Span,
            keyPackageEventId,
            Relays,
            staged.Welcome!,
            (long)Now);

        var fanIn = new InboundFanIn(Host(new NostrGroupPeeler(invitee.Secret)));

        var welcome = Assert.IsType<InboundDelivery.Welcome>(await fanIn.ReceiveAsync(envelope));

        Assert.Equal(PeeledContentKind.Welcome, welcome.Peeled.Kind);
        Assert.Equal(keyPackageEventId, welcome.Peeled.Welcome!.KeyPackageEventId);
        Assert.Equal(inviter.Hex, welcome.Peeled.Welcome.SenderPublicKeyHex);
    }
}
