using System.Diagnostics;
using DotnetMls.Crypto;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Marmot;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Storage.Sqlite;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// The inbound direction: the reference client creates a group and invites us.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of <see cref="GroupInteropTests"/>, and it proves a different
/// thing. That one shows a peer accepts what we produce; this one shows we
/// accept what a peer produces — its GroupContext, its component state, its
/// Welcome, its commits. Neither implies the other, and a client that could
/// only do one would be useless in exactly half of every conversation.
/// </para>
/// <para>
/// It also exercises the parts of being <i>discoverable</i>: the peer will not
/// invite an account it cannot look up, so this publishes relay lists and a
/// KeyPackage first and confirms the peer can see them.
/// </para>
/// </remarks>
[Trait("Category", "DarkMatterInterop")]
[Collection(DarkMatterInteropCollection.Name)]
public class InboundJoinInteropTests : IDisposable
{
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(90);
    private const string PeerRelay = "ws://127.0.0.1:7777";

    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private readonly List<string> _log = [];
    private readonly InteropRelayClient _relay = new(InteropRelayClient.DefaultRelayUrl);
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"marmot-inbound-{Guid.NewGuid():N}.db");

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

    /// <summary>Publishes an event signed by one of our test accounts.</summary>
    private async Task PublishAsync(LocalSigner signer, NostrEventTemplate template)
    {
        byte[] id = template.ComputeId();
        await _relay.PublishAsync(
            NostrEnvelope.Write(template, id, Bip340.Sign(signer.Secret, id)), RelayTimeout);
    }

    [Fact(Skip =
        "One requirement left, and it is a product decision rather than a bug. " +
        "Proposals and app components are now both satisfied. The peer's " +
        "groups create enables the QUIC agent-text-stream component, whose " +
        "default policy requires the RECEIVE role - MLS extension 0xf2d1 - of " +
        "every member. We advertise the component (we can read and honour the " +
        "policy) but not the role, because we have no QUIC transport. " +
        "Advertising 0xf2d1 would claim we can be sent stream previews we " +
        "cannot receive: a bounded degradation rather than a fork, but still a " +
        "claim we cannot back. Decide whether to claim receive-only or build " +
        "the transport.")]
    public async Task WeJoinAGroupTheReferenceClientCreated()
    {
        var peer = new MdkCliDockerClient(_log.Add);
        Assert.SkipUnless(await peer.IsReadyAsync(), "The mdk-cli interop peer is not running.");

        await peer.StartDaemonAsync(PeerRelay);
        await peer.CreateIdentityAsync();

        var us = new LocalSigner();
        _storage = new SqliteMarmotStorageProvider($"Data Source={_dbPath}");

        // 1. Become discoverable. A KeyPackage alone is not enough — the peer
        //    refuses to invite an account whose relay lists it cannot find, so
        //    publishing one without the other makes us uninvitable.
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await PublishAsync(us, RelayListEvent.BuildNip65(us.Hex, [PeerRelay], now));
        await PublishAsync(us, RelayListEvent.BuildMessageRelays(us.Hex, [PeerRelay], now));

        var publisher = new KeyPackagePublisher(_cs, us, _storage, new RelayPublisher(_relay));
        PublishedKeyPackage published = await publisher.PublishAsync((ulong)now);
        _log.Add($"our key package event {published.EventIdHex}");

        // 2. The peer must be able to see all of it. Checking here means a
        //    failure names discoverability rather than surfacing later as a
        //    group that never arrives.
        Assert.True(
            await WaitForAsync(async () =>
            {
                await peer.SyncAsync();
                return await peer.CanInviteAsync(us.Hex);
            }),
            $"The reference client could not resolve our account.\n{Log()}");

        // 3. The peer creates a group with us in it.
        string groupName = $"peer group {Guid.NewGuid():N}"[..24];
        await peer.CreateGroupAsync(groupName, us.Hex);

        // 4. Find the Welcome addressed to us and join from it.
        JoinedGroup? joined = await WaitForWelcomeAsync(us);

        Assert.True(joined is not null, $"No Welcome we could join from arrived.\n{Log()}");

        // The peer created the group, so its GroupContext, its component state
        // and its required set are all theirs — and we accepted them.
        Assert.NotEmpty(joined!.Required);
        Assert.Equal(
            joined.Required, MarmotGroupBuilder.ValidateCreated(joined.Group, "joined group"));

        // The inviter is read off the verified seal.
        Assert.Equal(peer.SelectedAccount, Convert.ToHexString(joined.InviterIdentity).ToLowerInvariant());

        // 5. And the group works: we can read a message the peer sends into it.
        string groupIdHex = Convert.ToHexString(joined.GroupId).ToLowerInvariant();
        string text = $"from the creator {Guid.NewGuid():N}";
        await peer.SendMessageAsync(groupIdHex, text);

        Assert.True(
            await WaitForMessageAsync(joined, text),
            $"We never read '{text}' in the group the peer created.\n{Log()}");
    }

    /// <summary>Polls for a gift wrap addressed to us that we can join from.</summary>
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
                    // Wraps for other accounts and stale ones are expected here;
                    // recorded so a real failure is visible in the message.
                    _log.Add($"join attempt: {ex.Message}");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return null;
    }

    /// <summary>Polls the group's transport address for a message.</summary>
    private async Task<bool> WaitForMessageAsync(JoinedGroup joined, string text)
    {
        var peeler = new NostrGroupPeeler();
        string transportIdHex = Convert.ToHexString(
            GroupMessages.TransportGroupId(joined.Group)).ToLowerInvariant();

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < SettleTimeout)
        {
            var envelopes = await _relay.FetchAsync(
                new Dictionary<string, object>
                {
                    ["kinds"] = new[] { 445 },
                    ["#h"] = new[] { transportIdHex },
                },
                RelayTimeout);

            foreach (string envelope in envelopes)
            {
                try
                {
                    PeeledMessage peeled = peeler.Peel(
                        envelope, _ => GroupMessages.ExporterSecret(joined.Group));

                    if (GroupMessages.Receive(joined.Group, peeled.MlsBytes).Event.Content == text)
                        return true;
                }
                catch (Exception ex)
                {
                    _log.Add($"peel: {ex.Message}");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return false;
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

    private string Log() => string.Join('\n', _log);

    /// <summary>Adapts the interop relay client to the publisher's seam.</summary>
    private sealed class RelayPublisher(InteropRelayClient relay) : IKeyPackageRelay
    {
        public async Task<KeyPackagePublishOutcome> PublishAsync(
            string envelope, CancellationToken ct = default)
        {
            await relay.PublishAsync(envelope, RelayTimeout, ct);
            return KeyPackagePublishOutcome.Accepted;
        }
    }
}
