using System.Diagnostics;
using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Types;
using Scramble.Marmot;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage.Sqlite;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// Leaving a group, against the reference client, in both directions.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the first handshake message either side has put on the wire.</b>
/// Everything before it bootstrapped a group from a Welcome and then exchanged
/// application messages; no commit or proposal had ever crossed between the two
/// implementations. So these tests carry more than leaving: they are the first
/// evidence that our <c>PublicMessage</c> framing, our kind-445 wrap of a
/// handshake, and our reading of one, all agree with the reference.
/// </para>
/// <para>
/// Leaving is also the membership change that cannot be done alone — RFC 9420
/// §12.2 needs the committer to remain a member — so each direction exercises
/// both halves: one side proposes and the other commits.
/// </para>
/// </remarks>
[Trait("Category", "DarkMatterInterop")]
[Collection(DarkMatterInteropCollection.Name)]
public class LeaveInteropTests : IDisposable
{
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(90);
    private const string PeerRelay = "ws://127.0.0.1:7777";

    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private readonly List<string> _log = [];
    private readonly NostrGroupPeeler _peeler = new();
    private readonly InteropRelayClient _relay = new(InteropRelayClient.DefaultRelayUrl);
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"marmot-leave-{Guid.NewGuid():N}.db");

    private SqliteMarmotStorageProvider? _storage;

    public void Dispose()
    {
        _storage?.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // A stray temp file is not worth failing a run over.
        }
    }

    private static ulong Now() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private string Log() => string.Join('\n', _log);

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

    // ---- The reference client leaves a group we host ----

    [Fact]
    public async Task WeCommitTheReferenceClientsDeparture()
    {
        var peer = new MdkCliDockerClient(_log.Add);
        Assert.SkipUnless(await peer.IsReadyAsync(), "The mdk-cli interop peer is not running.");

        await peer.StartDaemonAsync(PeerRelay);
        string peerPubkey = await peer.CreateIdentityAsync();
        await peer.PublishKeyPackageAsync();

        var us = new LocalSigner();
        CreatedGroup group = await HostAGroupWithThePeerAsync(peer, peerPubkey, us);
        string groupIdHex = Convert.ToHexString(group.GroupId).ToLowerInvariant();

        Assert.Equal(2, group.Group.GetMembers().Count);

        // The peer asks to leave. It cannot commit that itself, so all this puts
        // on the wire is a SelfRemove proposal — built by the reference
        // implementation, which is the point of reading it with our own codec.
        await peer.LeaveGroupAsync(groupIdHex);

        ReceivedHandshake? request = await WaitForHandshakeAsync(
            group.Group, HandshakeOutcome.ProposalCached);

        Assert.True(request is not null, $"No departure request arrived.\n{Log()}");

        // Ours to commit, because theirs cannot be.
        StagedInvite staged = Assert.IsType<StagedInvite>(
            MarmotGroupLeave.CommitDepartures(group.Group));

        Assert.Equal(
            peerPubkey,
            Convert.ToHexString(Assert.Single(staged.AddedAccounts)).ToLowerInvariant());

        // Publish-before-apply, wrapped under the epoch the peer is still at.
        await _relay.PublishAsync(
            GroupHandshake.Wrap(group.Group, _peeler, staged.Commit), RelayTimeout);
        staged.Applied();

        Assert.Equal(1, group.Group.GetMembers().Count);
        Assert.DoesNotContain(
            group.Group.GetMembers(),
            m => Convert.ToHexString(m.identity).Equals(peerPubkey, StringComparison.OrdinalIgnoreCase));
    }

    // ---- We leave a group the reference client hosts ----

    [Fact]
    public async Task TheReferenceClientCommitsOurDeparture()
    {
        var peer = new MdkCliDockerClient(_log.Add);
        Assert.SkipUnless(await peer.IsReadyAsync(), "The mdk-cli interop peer is not running.");

        await peer.StartDaemonAsync(PeerRelay);
        await peer.CreateIdentityAsync();

        var us = new LocalSigner();
        _storage = new SqliteMarmotStorageProvider($"Data Source={_dbPath}");

        JoinedGroup joined = await JoinAPeerGroupAsync(peer, us);
        string groupIdHex = Convert.ToHexString(joined.GroupId).ToLowerInvariant();

        // We ask to leave. The peer has to accept a proposal we built, cache it,
        // and commit it -- three things it has never been asked to do with our
        // bytes before.
        await _relay.PublishAsync(
            GroupHandshake.WrapProposal(
                joined.Group, _peeler, MarmotGroupLeave.Request(joined.Group)),
            RelayTimeout);

        // And the commit it produces has to reach us, and tell us we are out.
        // A removed member cannot apply the commit that removes them -- the
        // UpdatePath encrypts path secrets only to those who remain -- so this
        // is reported rather than applied.
        ReceivedHandshake? removal = await WaitForHandshakeAsync(
            joined.Group,
            HandshakeOutcome.RemovedByCommit,
            beforeEachPoll: async () =>
            {
                await peer.SyncAsync();
                await peer.RunMaintenanceAsync();
            });

        Assert.True(
            removal is not null,
            $"The peer never committed our departure from {groupIdHex}.\n{Log()}");

        // The peer's own membership list is the independent confirmation: our
        // group state is deliberately untouched, so it cannot be the witness.
        Assert.False(
            MdkCliDockerClient.ContainsPubkey(await peer.MembersAsync(groupIdHex), us.Hex),
            $"The peer still lists us as a member.\n{Log()}");
    }

    // ---- Setup shared by the two directions ----

    /// <summary>Creates a Scramble group with the peer in it.</summary>
    private async Task<CreatedGroup> HostAGroupWithThePeerAsync(
        MdkCliDockerClient peer, string peerPubkey, LocalSigner us)
    {
        var envelopes = await WaitForKeyPackageAsync(peerPubkey);
        KeyPackagePublication publication = KeyPackageEvent.Parse(envelopes[^1]);
        var validated = KeyPackagePublicationValidator.Validate(publication, _cs);

        var carried = MlsMessage.ReadFrom(new TlsReader(publication.KeyPackageBytes));
        var peerKeyPackage = (KeyPackage)carried.Body;

        CreatedGroup group = await MarmotGroupBuilder.CreateAsync(
            _cs, us, "Leave interop", "", Now(), [PeerRelay]);

        StagedInvite staged = MarmotGroupInvite.Add(group.Group, _cs, [peerKeyPackage]);
        string groupIdHex = Convert.ToHexString(group.GroupId).ToLowerInvariant();

        await _relay.PublishAsync(
            WelcomePublication.Wrap(
                us.Secret,
                us.AccountPublicKey.Span,
                validated.CredentialIdentity,
                Convert.FromHexString(publication.EventIdHex),
                [PeerRelay],
                staged.Welcome,
                (long)Now()),
            RelayTimeout);
        staged.Applied();

        Assert.True(
            await WaitForAsync(async () =>
            {
                await peer.SyncAsync();
                return MdkCliDockerClient.ContainsGroupId(await peer.InvitesAsync(), groupIdHex);
            }),
            $"The peer never saw the invite.\n{Log()}");

        await peer.AcceptInviteAsync(groupIdHex);

        Assert.True(
            await WaitForAsync(async () =>
                MdkCliDockerClient.ContainsGroupId(await peer.GroupsAsync(), groupIdHex)),
            $"The peer never joined.\n{Log()}");

        return group;
    }

    /// <summary>Becomes discoverable, then joins a group the peer creates.</summary>
    private async Task<JoinedGroup> JoinAPeerGroupAsync(MdkCliDockerClient peer, LocalSigner us)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await PublishAsync(us, RelayListEvent.BuildNip65(us.Hex, [PeerRelay], now));
        await PublishAsync(us, RelayListEvent.BuildMessageRelays(us.Hex, [PeerRelay], now));

        var publisher = new KeyPackagePublisher(_cs, us, _storage!, new RelayPublisher(_relay, RelayTimeout));
        await publisher.PublishAsync((ulong)now);

        Assert.True(
            await WaitForAsync(async () =>
            {
                await peer.SyncAsync();
                return await peer.CanInviteAsync(us.Hex);
            }),
            $"The reference client could not resolve our account.\n{Log()}");

        await peer.CreateGroupAsync($"leave group {Guid.NewGuid():N}"[..24], us.Hex);

        JoinedGroup? joined = await WaitForWelcomeAsync(us);
        Assert.True(joined is not null, $"No Welcome we could join from arrived.\n{Log()}");

        return joined!;
    }

    // ---- Polling ----

    /// <summary>
    /// Polls the group's transport address for a handshake with this outcome.
    /// </summary>
    /// <remarks>
    /// Every envelope is tried and failures are logged rather than thrown:
    /// application messages and stale epochs share the address, and a real
    /// failure is only visible against that background.
    /// </remarks>
    private async Task<ReceivedHandshake?> WaitForHandshakeAsync(
        DotnetMls.Group.MlsGroup group,
        HandshakeOutcome expected,
        Func<Task>? beforeEachPoll = null)
    {
        string transportIdHex = Convert.ToHexString(
            GroupMessages.TransportGroupId(group)).ToLowerInvariant();

        var clock = Stopwatch.StartNew();
        var tried = new HashSet<string>();

        while (clock.Elapsed < SettleTimeout)
        {
            if (beforeEachPoll is not null)
                await beforeEachPoll();

            var envelopes = await _relay.FetchAsync(
                new Dictionary<string, object>
                {
                    ["kinds"] = new[] { 445 },
                    ["#h"] = new[] { transportIdHex },
                },
                RelayTimeout);

            foreach (string envelope in envelopes)
            {
                if (!tried.Add(envelope))
                    continue;

                try
                {
                    PeeledMessage peeled = _peeler.Peel(
                        envelope, _ => GroupMessages.ExporterSecret(group));

                    ReceivedHandshake received = GroupHandshake.Receive(group, peeled.MlsBytes);
                    _log.Add($"handshake: {received.Outcome}");

                    if (received.Outcome == expected)
                        return received;
                }
                catch (Exception ex)
                {
                    _log.Add($"handshake peel: {ex.Message}");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return null;
    }

    private async Task<JoinedGroup?> WaitForWelcomeAsync(LocalSigner us)
    {
        var clock = Stopwatch.StartNew();
        var tried = new HashSet<string>();

        while (clock.Elapsed < SettleTimeout)
        {
            var envelopes = await _relay.FetchAsync(
                new Dictionary<string, object>
                {
                    ["kinds"] = new[] { 1059 },
                    ["#p"] = new[] { us.Hex },
                },
                RelayTimeout);

            foreach (string envelope in envelopes)
            {
                if (!tried.Add(envelope))
                    continue;

                try
                {
                    return await GroupJoin.JoinFromEnvelopeAsync(
                        _cs, envelope, us.Secret, _storage!);
                }
                catch (Exception ex)
                {
                    _log.Add($"join attempt: {ex.Message}");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> WaitForKeyPackageAsync(string pubkey)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < SettleTimeout)
        {
            var envelopes = await _relay.FetchAsync(
                new Dictionary<string, object>
                {
                    ["kinds"] = new[] { 30443 },
                    ["authors"] = new[] { pubkey },
                },
                RelayTimeout);

            if (envelopes.Count > 0)
                return envelopes;

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new InvalidOperationException($"The peer never published a KeyPackage.\n{Log()}");
    }

    private async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < SettleTimeout)
        {
            if (await condition())
                return true;

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return false;
    }

    private async Task PublishAsync(LocalSigner signer, NostrEventTemplate template)
    {
        byte[] id = template.ComputeId();
        await _relay.PublishAsync(
            NostrEnvelope.Write(template, id, Bip340.Sign(signer.Secret, id)), RelayTimeout);
    }
}
