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
/// What is left of a group when the process dies mid-commit.
/// </summary>
/// <remarks>
/// <para>
/// P9's exit criterion, written as the two kills it names: between staging and
/// publishing, and between publishing and applying. They look adjacent and want
/// opposite answers. A commit that never left this device must be abandoned,
/// because nobody else knows it exists. A commit a relay has must be adopted,
/// because everybody else already has. Guessing wrong in either direction forks
/// the group — one way we advance alone, the other way we stay behind alone.
/// </para>
/// <para>
/// <b>A restart may read only what is durable.</b> That is the whole point, and
/// it is why <see cref="RestartAsync"/> deliberately goes back to storage rather
/// than keeping a reference to anything: an <see cref="MlsGroup"/> is an
/// in-memory object, and a test that quietly carried one across the "crash"
/// would be testing nothing at all.
/// </para>
/// </remarks>
[Trait("Category", "MarmotEngine")]
public class CrashRecoveryTests : IDisposable
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
        MlsGroup Us, MlsGroup Them, LocalSigner TheirSigner, GroupId GroupId);

    /// <summary>Us as the group's creator, and one other member who stays up.</summary>
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

        var groupId = new GroupId(us.GroupId);
        await _fixture.Provider.PutGroupAsync(us.ToRecord(_now));

        // Stands in for the durable copy of live group state that does not
        // exist yet -- see NothingPersistsTheStateAGroupIsActuallyIn, which is
        // the test for that gap. Seeded here so the two crash scenarios below
        // fail on their own merits rather than all tripping on the same prior
        // step and saying nothing about the difference between them.
        await NewArchive().CaptureIfAbsentAsync(groupId, us.Group);

        return new Pair(us.Group, them, theirSigner, groupId);
    }

    private static byte[] Serialize(PublicMessage message) =>
        TlsCodec.Serialize(new MlsMessage(WireFormat.MlsPublicMessage, message).WriteTo);

    private static void Apply(MlsGroup group, byte[] wire)
    {
        var message = MlsMessage.ReadFrom(new TlsReader(wire));
        group.ProcessCommit((PublicMessage)message.Body);
    }

    /// <summary>An application message from the member who stayed up.</summary>
    private static byte[] AppMessage(Pair pair, string text)
    {
        var peeler = new NostrGroupPeeler();

        string envelope = GroupMessages.Send(
            pair.Them,
            peeler,
            MarmotAppEvent.Chat(pair.TheirSigner.Hex, (long)Now, text),
            pair.TheirSigner.AccountPublicKey.Span);

        return peeler
            .Peel(envelope, _ => GroupMessages.ExporterSecret(pair.Them))
            .MlsBytes;
    }

    /// <summary>
    /// The group as a fresh process would find it.
    /// </summary>
    /// <remarks>
    /// <b>Real session-open hydration</b> — <see cref="MarmotSessionHost.OpenAsync"/>
    /// — over a host built from nothing but this test's storage. Nothing is
    /// carried across the "crash": the host is new, the epoch manager inside it
    /// is new, and the <see cref="MlsGroup"/> that comes back is imported from
    /// bytes. A relay that refuses everything is handed in because hydration
    /// must not need one: a restart happens before anything is online, and a
    /// recovery that could only run with a transport would be no recovery at
    /// all.
    /// </remarks>
    private async Task<MlsGroup?> RestartAsync(GroupId groupId)
    {
        var host = new MarmotSessionHost(
            _fixture.Provider, _cs, new UnreachableRelay(), ConvergencePolicy.V1, () => _now);

        await host.RestoreAsync();

        return (await host.OpenAsync(groupId)).Session?.Group;
    }

    /// <summary>A transport that is not there, which is what a restart has.</summary>
    private sealed class UnreachableRelay : ICommitRelay
    {
        public Task<CommitPublishOutcome> PublishAsync(
            string envelope, CancellationToken ct = default) =>
            throw new NotSupportedException("Hydration must not need a relay.");
    }

    // ---- The gap underneath both ----

    [Fact]
    public async Task NothingPersistsTheStateAGroupIsActuallyIn()
    {
        // The epoch archive is fed from one place: the inbound commit path,
        // just before an apply. A group whose own member does all the
        // committing therefore has nothing archived at all -- so there is no
        // question of recovering it well or badly, because there is nothing to
        // recover from. Whatever hydration ends up looking like, this is the
        // step before it.
        CreatedGroup us = await MarmotGroupBuilder.CreateAsync(
            _cs, new LocalSigner(), "Rakes", "", Now, Relays);

        var groupId = new GroupId(us.GroupId);
        await _fixture.Provider.PutGroupAsync(us.ToRecord(_now));

        Assert.NotNull(await RestartAsync(groupId));
    }

    // ---- Killed between staging and publishing ----

    [Fact]
    public async Task AGroupKilledBeforeItPublishedComesBackWhereItWas()
    {
        // Nobody else ever saw this commit, so the only consistent outcome is
        // that it never happened. Coming back at the new epoch would advance us
        // into a state no other member can reach, and every message we sent
        // afterwards would be unreadable by the group we think we are in.
        Pair pair = await PairAsync();
        ulong before = pair.Us.Epoch;

        using (StagedCommit staged = MarmotSelfUpdate.Stage(pair.Us))
        {
            // ...and the process dies here. No Publishing, no Applied, no
            // Discard -- that is what a crash is.
            Assert.NotNull(staged.Commit);
        }

        MlsGroup? revived = await RestartAsync(pair.GroupId);

        Assert.True(revived is not null, "Nothing durable was left to restart from.");
        Assert.Equal(before, revived!.Epoch);

        // Consistent means able to carry on: a commit made from here has to be
        // one the member who stayed up can still apply.
        using StagedCommit next = MarmotSelfUpdate.Stage(revived);
        byte[] wire = Serialize(next.Commit);
        next.Publishing();
        next.Applied();

        Apply(pair.Them, wire);

        Assert.Equal(revived.Epoch, pair.Them.Epoch);
    }

    // ---- Killed between publishing and applying ----

    [Fact]
    public async Task AGroupKilledAfterItPublishedComesBackOnTheEpochItPublished()
    {
        // The mirror case, and the dangerous one. The relay has the commit and
        // the rest of the group has already moved, so abandoning it strands us
        // one epoch behind everyone -- and unlike the case above, we cannot
        // simply reissue it: MLS will not let a member process a commit it
        // authored, so our own bytes coming back off the relay are no help.
        Pair pair = await PairAsync();
        ulong before = pair.Us.Epoch;

        using (StagedCommit staged = MarmotSelfUpdate.Stage(pair.Us))
        {
            byte[] wire = Serialize(staged.Commit);
            staged.Publishing();

            // The relay took it and the group acted on it.
            Apply(pair.Them, wire);
            Assert.Equal(before + 1, pair.Them.Epoch);

            // ...and the process dies before Applied().
        }

        MlsGroup? revived = await RestartAsync(pair.GroupId);

        Assert.True(revived is not null, "Nothing durable was left to restart from.");
        Assert.Equal(before + 1, revived!.Epoch);

        // And it has to be able to read what the group said next, which is the
        // part that makes this consistency rather than bookkeeping.
        ReceivedGroupMessage received =
            GroupMessages.Receive(revived, AppMessage(pair, "carried on without you"));

        Assert.Equal("carried on without you", received.Event.Content);
    }
}
