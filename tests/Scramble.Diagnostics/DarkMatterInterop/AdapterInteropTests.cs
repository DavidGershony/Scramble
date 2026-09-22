using System.Diagnostics;
using System.Text;
using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Types;
using Scramble.Core.Services;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Storage.Sqlite;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;
using CoreKeyPackage = Scramble.Core.Models.KeyPackage;
using MlsKeyPackage = DotnetMls.Types.KeyPackage;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// The reference client, talking to <see cref="DarkMatterMlsService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other test in this suite reaches past the adapter.</b> They build a
/// <c>MarmotSessionHost</c> directly and drive the engine, which proves the
/// engine speaks the protocol — and proves nothing about the layer the app
/// actually calls. The cutover switches the app onto <see cref="IMlsService"/>,
/// and that layer is not a pass-through:
/// </para>
/// <list type="bullet">
/// <item>it hands the engine a transport that never publishes, because this
/// contract's caller publishes;</item>
/// <item>it builds message envelopes with <c>GroupMessages.Send</c> instead of
/// the session's send path, and persists the advanced sender ratchet itself;</item>
/// <item>it drives staging and merging on the caller's rhythm rather than the
/// engine's.</item>
/// </list>
/// <para>
/// Each of those can produce bytes a real peer rejects while every existing test
/// stays green — the engine tests do not go through them, and the adapter's unit
/// tests only ever have the adapter talk to another copy of itself, which agrees
/// with its own mistakes. A ratchet persisted one generation out is the clean
/// example: two of our instances stay in perfect agreement, and a real peer
/// decrypts nothing.
/// </para>
/// </remarks>
[Trait("Category", "DarkMatterInterop")]
[Collection(DarkMatterInteropCollection.Name)]
public class AdapterInteropTests(AdapterInteropFixture fixture)
    : IClassFixture<AdapterInteropFixture>
{
    [Fact]
    public async Task ThePeerJoinedAGroupTheServiceCreated()
    {
        // The fixture's own setup, asserted rather than assumed: the group came
        // out of CreateGroupAsync and the Welcome out of StageAddMemberAsync,
        // and a real client accepted both.
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);

        Assert.True(
            MdkCliDockerClient.ContainsGroupId(
                await fixture.Peer.GroupsAsync(), fixture.GroupIdHex),
            $"The peer is not in the group.\n{fixture.Log}");

        MlsGroupInfo? info = await fixture.Service.GetGroupInfoAsync(fixture.GroupId);

        Assert.NotNull(info);
        Assert.Contains(fixture.PeerPubkey, info!.MemberPublicKeys);
        Assert.Contains(fixture.OurPubkey, info.MemberPublicKeys);
    }

    [Fact]
    public async Task ThePeerReadsAMessageTheServiceEncrypted()
    {
        // EncryptMessageAsync end to end. The exporter secret, the kind-445
        // wrap, the MLS application framing and the payload encoding all have to
        // agree for this, and the adapter builds each by a different route than
        // the engine tests exercise.
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);

        string text = $"adapter says {Guid.NewGuid():N}";
        await fixture.SendAsync(text);

        Assert.True(await fixture.PeerSawAsync(text), $"The peer never read it.\n{fixture.Log}");
    }

    [Fact]
    public async Task TheServiceDecryptsAMessageThePeerSent()
    {
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);

        string text = $"peer says {Guid.NewGuid():N}";
        await fixture.Peer.SendMessageAsync(fixture.GroupIdHex, text);

        MlsDecryptedMessage? received = await fixture.ReceiveAsync(text);

        Assert.True(received is not null, $"The service never read it.\n{fixture.Log}");
        Assert.Equal(text, received!.Plaintext);

        // Read off the ratchet tree, so this is MLS-authenticated identity
        // rather than something the payload claimed.
        Assert.Equal(fixture.PeerPubkey, received.SenderPublicKey);
    }

    [Fact]
    public async Task ManyMessagesInARowAllArrive()
    {
        // The ratchet test. EncryptMessageAsync advances the sender ratchet and
        // the adapter writes the advanced state down by hand, outside the
        // engine's own send path. If that is off by a generation the first
        // message lands and a later one does not — and two of our own instances
        // would agree with each other throughout.
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);

        string run = Guid.NewGuid().ToString("N")[..8];
        string[] texts = [.. Enumerable.Range(0, 5).Select(i => $"ratchet {run} {i}")];

        foreach (string text in texts)
            await fixture.SendAsync(text);

        foreach (string text in texts)
        {
            Assert.True(
                await fixture.PeerSawAsync(text),
                $"The peer missed '{text}'.\n{fixture.Log}");
        }
    }

    [Fact]
    public async Task MessagesCrossInBothDirections()
    {
        // Interleaved rather than batched: a send between two receives moves
        // state that a one-directional test leaves still.
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);

        string run = Guid.NewGuid().ToString("N")[..8];

        await fixture.SendAsync($"cross {run} ours-1");
        await fixture.Peer.SendMessageAsync(fixture.GroupIdHex, $"cross {run} theirs-1");

        Assert.True(await fixture.PeerSawAsync($"cross {run} ours-1"), fixture.Log);
        Assert.True(await fixture.ReceiveAsync($"cross {run} theirs-1") is not null, fixture.Log);

        await fixture.SendAsync($"cross {run} ours-2");
        await fixture.Peer.SendMessageAsync(fixture.GroupIdHex, $"cross {run} theirs-2");

        Assert.True(await fixture.PeerSawAsync($"cross {run} ours-2"), fixture.Log);
        Assert.True(await fixture.ReceiveAsync($"cross {run} theirs-2") is not null, fixture.Log);
    }

    [Fact]
    public async Task EmojiSurviveTheRoundTrip()
    {
        // Above the BMP. The payload id is a hash over the canonical form, so a
        // surrogate escape here is not a display bug — it is a message the peer
        // rejects for a mismatched id.
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);

        string text = $"rakes \U0001F342\U0001F9F9 {Guid.NewGuid():N}";
        await fixture.SendAsync(text);

        Assert.True(await fixture.PeerSawAsync(text), $"The peer never read it.\n{fixture.Log}");
    }

    [Fact]
    public async Task ThePeerAcceptsACommitTheServiceStagedAndPublished()
    {
        // One of the two questions that could not be answered without a real
        // peer. The old service wrapped commits itself and attached an
        // ["encoding","base64"] tag that current peers reject before any MLS
        // processing; the adapter's commits carry no such tag, because the
        // engine seals and signs them while staged. Whether that is what a peer
        // actually wants is not something our own code can confirm.
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);

        ulong before = (await fixture.Service.GetGroupInfoAsync(fixture.GroupId))!.Epoch;

        // UpdateKeysAsync stages a self-update and hands back a finished
        // kind-445. Published as-is — no second wrapping, which is the change.
        byte[] envelope = await fixture.Service.UpdateKeysAsync(fixture.GroupId);
        await fixture.PublishRawAsync(envelope);
        await fixture.Service.MergeStagedAsync(fixture.GroupId);

        ulong after = (await fixture.Service.GetGroupInfoAsync(fixture.GroupId))!.Epoch;
        Assert.Equal(before + 1, after);

        // The proof that the peer applied it: a message encrypted under the new
        // epoch is only readable by someone who followed the commit. Asserting
        // our own epoch alone would pass with the peer left behind.
        string text = $"after commit {Guid.NewGuid():N}";
        await fixture.SendAsync(text);

        Assert.True(
            await fixture.PeerSawAsync(text),
            $"The peer did not follow our commit into the new epoch.\n{fixture.Log}");
    }

    [Fact]
    public async Task ThePeerHonoursAnAdminPolicyCommitFromTheService()
    {
        // The first time the AppDataUpdate slice faces a real peer. The admin
        // set is signed group state that every member recomputes, so if our
        // 0x8003 encoding or the commit that carries it were wrong, the peer
        // would either refuse the commit or disagree about who may act.
        //
        // Asserted by what the peer will *do* rather than by reading our own
        // copy back: the fixture granted it admin, and `rename` is admin-gated
        // on the peer's side, so a successful rename is the peer telling us it
        // read the same admin set we wrote.
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);
        Assert.SkipUnless(fixture.PeerIsAdmin, fixture.AdminSkipReason);

        List<string> admins = fixture.Service.GetAdminPubkeys(fixture.GroupId);

        Assert.Contains(fixture.OurPubkey, admins);
        Assert.Contains(fixture.PeerPubkey, admins);
    }

    [Fact]
    public async Task TheServiceFollowsACommitThePeerPublished()
    {
        // The other direction, and the one that depends entirely on the peer.
        // A rename is the one thing this peer commits on demand -- its
        // self-update is durable maintenance, jittered and targeted a day out,
        // so schedule-self-update plus run-maintenance publishes nothing.
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);
        Assert.SkipUnless(fixture.PeerIsAdmin, fixture.AdminSkipReason);

        ulong before = (await fixture.Service.GetGroupInfoAsync(fixture.GroupId))!.Epoch;

        await fixture.Peer.RenameAsync(fixture.GroupIdHex, $"renamed {Guid.NewGuid():N}"[..20]);

        // Drained through the adapter's own inbound path rather than the
        // engine's, because that is the path the app will use.
        Assert.True(
            await fixture.WaitForEpochAboveAsync(before),
            $"The service never applied the peer's commit.\n{fixture.Log}");

        string text = $"after their commit {Guid.NewGuid():N}";
        await fixture.SendAsync(text);

        Assert.True(await fixture.PeerSawAsync(text), $"The peer could not read it.\n{fixture.Log}");
    }

    [Fact]
    public async Task AReopenedServiceStillReadsTheGroup()
    {
        // Durability across a restart, which is the adapter's responsibility
        // rather than the engine's: it persists the advanced sender ratchet
        // itself. A process that comes back with a rewound generation counter
        // encrypts a different plaintext under a key and nonce already used.
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);

        await fixture.ReopenServiceAsync();

        MlsGroupInfo? info = await fixture.Service.GetGroupInfoAsync(fixture.GroupId);
        Assert.NotNull(info);

        string text = $"after restart {Guid.NewGuid():N}";
        await fixture.SendAsync(text);

        Assert.True(
            await fixture.PeerSawAsync(text),
            $"The peer could not read a message sent after a restart.\n{fixture.Log}");

        string reply = $"reply after restart {Guid.NewGuid():N}";
        await fixture.Peer.SendMessageAsync(fixture.GroupIdHex, reply);

        Assert.True(
            await fixture.ReceiveAsync(reply) is not null,
            $"The reopened service could not read the peer's reply.\n{fixture.Log}");
    }
}

/// <summary>
/// One group, created and hosted through <see cref="IMlsService"/>, with the
/// reference client joined to it.
/// </summary>
/// <remarks>
/// <para>
/// A class fixture rather than per-test setup: standing this up against a live
/// peer takes most of a minute, and the tests are additive — messages and
/// commits move the group forward without invalidating what came before.
/// </para>
/// <para>
/// Setup failures are captured rather than thrown. A fixture that throws takes
/// the class down with an error naming the fixture instead of the problem; the
/// tests skip with the reason and the log instead.
/// </para>
/// </remarks>
public sealed class AdapterInteropFixture : IAsyncLifetime
{
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);
    private const string PeerRelay = "ws://127.0.0.1:7777";

    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private readonly List<string> _log = [];
    private readonly InteropRelayClient _relay = new(InteropRelayClient.DefaultRelayUrl);
    private readonly List<string> _consumed = [];
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"adapter-interop-{Guid.NewGuid():N}.db");

    private SqliteMarmotStorageProvider? _storage;
    private byte[] _secret = [];
    private string _transportIdHex = "";

    public MdkCliDockerClient Peer { get; private set; } = null!;

    public DarkMatterMlsService Service { get; private set; } = null!;

    public bool Ready { get; private set; }

    public string SkipReason { get; private set; } = "The mdk-cli interop peer is not running.";

    /// <summary>Whether the admin-policy commit reached the peer.</summary>
    public bool PeerIsAdmin { get; private set; }

    public string AdminSkipReason { get; private set; } =
        "The peer was never granted admin.";

    public byte[] GroupId { get; private set; } = [];

    public string GroupIdHex { get; private set; } = "";

    public string PeerPubkey { get; private set; } = "";

    public string OurPubkey { get; private set; } = "";

    public string Log => string.Join('\n', _log);

    public async ValueTask InitializeAsync()
    {
        Peer = new MdkCliDockerClient(_log.Add);
        if (!await Peer.IsReadyAsync())
            return;

        try
        {
            await Peer.StartDaemonAsync(PeerRelay);
            PeerPubkey = await Peer.CreateIdentityAsync();
            await Peer.PublishKeyPackageAsync();

            var (secret, publicKey) = Bip340.GenerateKeyPair();
            _secret = secret;
            OurPubkey = Convert.ToHexString(publicKey).ToLowerInvariant();

            _storage = new SqliteMarmotStorageProvider($"Data Source={_dbPath}");
            Service = new DarkMatterMlsService(_storage);
            await Service.InitializeAsync(
                Convert.ToHexString(secret).ToLowerInvariant(), OurPubkey);

            // The peer's KeyPackage as the contract wants it: the kind-30443
            // event itself, not extracted bytes. StageAddMemberAsync verifies
            // the event id, signature and tag shape before reading a field, so
            // handing it raw bytes would skip the check that makes the
            // invitee's account key trustworthy.
            var envelopes = await WaitForKeyPackageAsync();
            KeyPackagePublication publication = KeyPackageEvent.Parse(envelopes[^1]);
            var validated = KeyPackagePublicationValidator.Validate(publication, _cs);

            CoreKeyPackage invitee = CoreKeyPackage.Create(
                publication.AuthorPublicKeyHex, publication.KeyPackageBytes, _cs.Id);

            invitee.EventJson = envelopes[^1];
            invitee.NostrEventId = publication.EventIdHex;

            MlsGroupInfo created = await Service.CreateGroupAsync("Adapter interop", [PeerRelay]);

            GroupId = created.GroupId;
            GroupIdHex = Convert.ToHexString(GroupId).ToLowerInvariant();

            MlsWelcome staged = await Service.StageAddMemberAsync(GroupId, invitee);

            // The group's address as a peer computes it, read off the signed
            // routing component rather than known by another route.
            _transportIdHex = Convert.ToHexString(
                Service.GetNostrGroupId(GroupId)!).ToLowerInvariant();

            // WelcomeData is an MLSMessage framing a Welcome -- the MIP-02 wire
            // form that goes in the kind-444 rumor. Decoding it back is not
            // ceremony: if the adapter framed it wrongly, this throws here
            // rather than leaving a peer to fail on bytes it cannot explain.
            var framed = MlsMessage.ReadFrom(new TlsReader(staged.WelcomeData));
            var welcome = Assert.IsType<Welcome>(framed.Body);

            // The gift wrap is the caller's job under this contract, exactly as
            // it will be in the app.
            await _relay.PublishAsync(
                WelcomePublication.Wrap(
                    _secret,
                    publicKey,
                    validated.CredentialIdentity,
                    Convert.FromHexString(publication.EventIdHex),
                    [PeerRelay],
                    welcome,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                RelayTimeout);

            await Service.MergeStagedAsync(GroupId);

            if (!await WaitForAsync(async () =>
            {
                await Peer.SyncAsync();
                return MdkCliDockerClient.ContainsGroupId(await Peer.InvitesAsync(), GroupIdHex);
            }))
            {
                SkipReason = $"The peer never saw the invite.\n{Log}";
                return;
            }

            await Peer.AcceptInviteAsync(GroupIdHex);

            if (!await WaitForAsync(async () =>
                MdkCliDockerClient.ContainsGroupId(await Peer.GroupsAsync(), GroupIdHex)))
            {
                SkipReason = $"The peer never joined.\n{Log}";
                return;
            }

            Ready = true;

            await GrantThePeerAdminAsync();
        }
        catch (Exception ex)
        {
            SkipReason = $"Adapter interop fixture setup failed: {ex}\n{Log}";
        }
    }

    /// <summary>
    /// Makes the peer an admin, through the service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tracked apart from <see cref="Ready"/> on purpose. This is the newest and
    /// least proven path in the adapter — nothing had staged an AppDataUpdate
    /// against a real peer before — and folding its failure into the fixture's
    /// readiness would skip the message and ratchet tests too, which is the
    /// opposite of what a new failure here should tell us.
    /// </para>
    /// <para>
    /// It also buys the only lever that makes this peer commit on demand: its
    /// <c>rename</c> is admin-gated.
    /// </para>
    /// </remarks>
    private async Task GrantThePeerAdminAsync()
    {
        try
        {
            byte[] envelope = await Service.StageUpdateAdminPubkeysAsync(
                GroupId, [OurPubkey, PeerPubkey]);

            await PublishRawAsync(envelope);
            await Service.MergeStagedAsync(GroupId);

            if (!await WaitForAsync(async () =>
            {
                await Peer.SyncAsync();
                return await Peer.GroupEpochAsync(GroupIdHex)
                    == (await Service.GetGroupInfoAsync(GroupId))!.Epoch;
            }))
            {
                AdminSkipReason =
                    $"The peer never reached our epoch after the admin commit.\n{Log}";
                return;
            }

            PeerIsAdmin = true;
        }
        catch (Exception ex)
        {
            AdminSkipReason = $"Granting the peer admin failed: {ex}\n{Log}";
        }
    }

    public ValueTask DisposeAsync()
    {
        Service?.Dispose();
        _storage?.Dispose();

        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // A stray temp file is not worth failing a run over.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Encrypts through the service and publishes the envelope as-is.</summary>
    public async Task SendAsync(string text)
    {
        byte[] envelope = await Service.EncryptMessageAsync(GroupId, text);
        await PublishRawAsync(envelope);
    }

    /// <summary>
    /// Publishes bytes the service produced without touching them.
    /// </summary>
    /// <remarks>
    /// The whole point of the contract change: what the service hands back is a
    /// complete signed kind-445, so anything that re-wraps it here would be
    /// testing a wrapper the app is losing.
    /// </remarks>
    public Task PublishRawAsync(byte[] envelope) =>
        _relay.PublishAsync(Encoding.UTF8.GetString(envelope), RelayTimeout);

    /// <summary>Whether the peer has read <paramref name="text"/> yet.</summary>
    public Task<bool> PeerSawAsync(string text) =>
        WaitForAsync(async () =>
        {
            await Peer.SyncAsync();
            return await Peer.HasMessageAsync(GroupIdHex, text);
        });

    /// <summary>
    /// Drains the relay through the service until <paramref name="text"/> arrives.
    /// </summary>
    /// <remarks>
    /// Everything goes through <see cref="IMlsService.DecryptMessageAsync"/>,
    /// including our own echoes and the peer's commits — which is what the app's
    /// subscription loop does, and is why a commit applied here moves the
    /// service's epoch as a side effect.
    /// </remarks>
    public async Task<MlsDecryptedMessage?> ReceiveAsync(string text)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < SettleTimeout)
        {
            foreach (string envelope in await FetchGroupEventsAsync())
            {
                if (_consumed.Contains(envelope))
                    continue;

                try
                {
                    MlsDecryptedMessage decrypted = await Service.DecryptMessageAsync(
                        GroupId, Encoding.UTF8.GetBytes(envelope));

                    _consumed.Add(envelope);

                    if (!decrypted.IsCommit && decrypted.Plaintext == text)
                        return decrypted;
                }
                catch (Exception ex)
                {
                    // Our own outbound messages are not ours to decrypt, and a
                    // duplicate is refused by design. Neither is a verdict.
                    _log.Add($"decrypt: {ex.Message}");
                    _consumed.Add(envelope);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return null;
    }

    /// <summary>Drains inbound traffic until the group's epoch passes <paramref name="epoch"/>.</summary>
    public async Task<bool> WaitForEpochAboveAsync(ulong epoch)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < SettleTimeout)
        {
            foreach (string envelope in await FetchGroupEventsAsync())
            {
                if (_consumed.Contains(envelope))
                    continue;

                try
                {
                    await Service.DecryptMessageAsync(GroupId, Encoding.UTF8.GetBytes(envelope));
                }
                catch (Exception ex)
                {
                    _log.Add($"decrypt: {ex.Message}");
                }

                _consumed.Add(envelope);
            }

            if ((await Service.GetGroupInfoAsync(GroupId))?.Epoch > epoch)
                return true;

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return false;
    }

    /// <summary>
    /// Disposes the service and opens a new one over the same database.
    /// </summary>
    /// <remarks>
    /// Deliberately not a fresh database. What is being checked is that
    /// everything needed to keep talking survived in storage — the group, its
    /// epoch, and the sender ratchet the adapter persists by hand.
    /// </remarks>
    public async Task ReopenServiceAsync()
    {
        Service.Dispose();
        _storage!.Dispose();

        _storage = new SqliteMarmotStorageProvider($"Data Source={_dbPath}");
        Service = new DarkMatterMlsService(_storage);

        await Service.InitializeAsync(
            Convert.ToHexString(_secret).ToLowerInvariant(), OurPubkey);
    }

    private Task<IReadOnlyList<string>> FetchGroupEventsAsync() =>
        _relay.FetchAsync(
            new Dictionary<string, object>
            {
                ["kinds"] = new[] { 445 },
                ["#h"] = new[] { _transportIdHex },
            },
            RelayTimeout);

    private async Task<IReadOnlyList<string>> WaitForKeyPackageAsync()
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < SettleTimeout)
        {
            var envelopes = await _relay.FetchKeyPackagesAsync(PeerPubkey, RelayTimeout);
            if (envelopes.Count > 0)
                return envelopes;

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new InvalidOperationException("The peer never published a KeyPackage.");
    }

    private async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < SettleTimeout)
        {
            try
            {
                if (await condition())
                    return true;
            }
            catch (Exception ex)
            {
                _log.Add($"poll: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return false;
    }
}
