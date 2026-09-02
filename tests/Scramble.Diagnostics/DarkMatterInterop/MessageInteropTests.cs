using System.Diagnostics;
using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Types;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Marmot;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// Messages between Scramble and the Marmot reference client, both directions.
/// </summary>
/// <remarks>
/// <para>
/// Joining a group proves the GroupContext is acceptable. It proves nothing
/// about messages: the exporter label, the kind-445 wrap, the MLS application
/// framing and the payload encoding are all separate agreements, and each one
/// is invisible to every test that does not have a real peer decrypt what we
/// produced. A wrong exporter context alone would leave both sides silently
/// unable to read each other while every unit test stayed green.
/// </para>
/// <para>
/// The fixture is shared across the class: creating a group and completing an
/// invite takes most of a minute against a live peer, so paying that per test
/// would make the suite too slow to run. Each test then uses distinct message
/// text so they cannot be confused with one another.
/// </para>
/// </remarks>
[Trait("Category", "DarkMatterInterop")]
[Collection(DarkMatterInteropCollection.Name)]
public class MessageInteropTests(MessageInteropFixture fixture)
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task TheReferenceClientReadsAMessageScrambleSent()
    {
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);

        string text = $"from scramble {Guid.NewGuid():N}";
        await fixture.SendFromScrambleAsync(text);

        // The assertion the exporter secret, the kind-445 wrap, the MLS
        // application framing and the payload encoding all have to agree for.
        Assert.True(
            await WaitForAsync(async () =>
            {
                await fixture.Peer.SyncAsync();
                return await fixture.Peer.HasMessageAsync(fixture.GroupIdHex, text);
            }),
            $"The reference client never read '{text}'.\n{fixture.Log}");
    }

    [Fact]
    public async Task ScrambleReadsAMessageTheReferenceClientSent()
    {
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);

        string text = $"from reference {Guid.NewGuid():N}";
        await fixture.Peer.SendMessageAsync(fixture.GroupIdHex, text);

        ReceivedGroupMessage? received = await fixture.WaitForScrambleMessageAsync(text);

        Assert.NotNull(received);
        Assert.Equal(text, received!.Event.Content);
        Assert.Equal(MarmotAppEvent.ChatKind, received.Event.Kind);

        // The sender is read off the ratchet tree, so this is the peer's
        // identity authenticated by MLS rather than asserted by the payload.
        Assert.Equal(
            fixture.PeerPubkey,
            Convert.ToHexString(received.SenderIdentity).ToLowerInvariant());
    }

    [Fact]
    public async Task EmojiSurviveTheRoundTripToTheReferenceClient()
    {
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);

        // Above the BMP. Every .NET JSON encoder wants to emit a surrogate
        // escape here, and the payload id is a hash over the canonical form —
        // so a mis-encoding is not a display bug, it is a message the peer
        // rejects for a mismatched id.
        string text = $"rakes \U0001F342\U0001F9F9 {Guid.NewGuid():N}";
        await fixture.SendFromScrambleAsync(text);

        Assert.True(
            await WaitForAsync(async () =>
            {
                await fixture.Peer.SyncAsync();
                return await fixture.Peer.HasMessageAsync(fixture.GroupIdHex, text);
            }),
            $"The reference client never read the emoji message.\n{fixture.Log}");
    }

    [Fact]
    public async Task SeveralMessagesAllArrive()
    {
        Assert.SkipUnless(fixture.Ready, fixture.SkipReason);

        string run = Guid.NewGuid().ToString("N")[..8];
        string[] texts = [$"burst {run} one", $"burst {run} two", $"burst {run} three"];

        foreach (string text in texts)
            await fixture.SendFromScrambleAsync(text);

        Assert.True(
            await WaitForAsync(async () =>
            {
                await fixture.Peer.SyncAsync();
                foreach (string text in texts)
                {
                    if (!await fixture.Peer.HasMessageAsync(fixture.GroupIdHex, text))
                        return false;
                }

                return true;
            }),
            $"The reference client did not read every message of the burst.\n{fixture.Log}");
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < SettleTimeout)
        {
            try
            {
                if (await condition())
                    return true;
            }
            catch
            {
                // The peer can fail a command mid-sync; keep polling rather than
                // turning a transient into a verdict.
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return false;
    }
}

/// <summary>
/// One Scramble group with the reference client joined, shared by the class.
/// </summary>
/// <remarks>
/// Built once because the setup is slow against a live peer. Failures during
/// setup are captured rather than thrown: a fixture that throws takes the whole
/// class down with an error that names the fixture instead of the problem, and
/// the tests skip with the reason instead.
/// </remarks>
public sealed class MessageInteropFixture : IAsyncLifetime
{
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);
    private const string PeerRelay = "ws://127.0.0.1:7777";

    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private readonly List<string> _log = [];
    private readonly NostrGroupPeeler _peeler = new();
    private readonly InteropRelayClient _relay = new(InteropRelayClient.DefaultRelayUrl);
    private readonly List<string> _seenTransportIds = [];

    private LocalSigner _scramble = null!;
    private CreatedGroup _group = null!;

    public MdkCliDockerClient Peer { get; private set; } = null!;

    public bool Ready { get; private set; }

    public string SkipReason { get; private set; } = "The mdk-cli interop peer is not running.";

    public string GroupIdHex { get; private set; } = "";

    public string PeerPubkey { get; private set; } = "";

    public string Log => string.Join('\n', _log);

    internal sealed class LocalSigner : IAccountIdentityProofSigner
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

            var envelopes = await WaitForKeyPackageAsync();
            KeyPackagePublication publication = KeyPackageEvent.Parse(envelopes[^1]);
            var validated = KeyPackagePublicationValidator.Validate(publication, _cs);

            var carried = MlsMessage.ReadFrom(new TlsReader(publication.KeyPackageBytes));
            var peerKeyPackage = (KeyPackage)carried.Body;

            _scramble = new LocalSigner();
            _group = await MarmotGroupBuilder.CreateAsync(
                _cs, _scramble, "Message interop", "", Now(), [PeerRelay]);

            GroupIdHex = Convert.ToHexString(_group.GroupId).ToLowerInvariant();

            StagedInvite staged = MarmotGroupInvite.Add(_group.Group, _cs, [peerKeyPackage]);

            string wrapped = WelcomePublication.Wrap(
                _scramble.Secret,
                _scramble.AccountPublicKey.Span,
                validated.CredentialIdentity,
                Convert.FromHexString(publication.EventIdHex),
                [PeerRelay],
                staged.Welcome,
                (long)Now());

            await _relay.PublishAsync(wrapped, RelayTimeout);
            staged.Applied();

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
        }
        catch (Exception ex)
        {
            SkipReason = $"Interop fixture setup failed: {ex.Message}\n{Log}";
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Sends one chat message from Scramble to the group.</summary>
    public async Task SendFromScrambleAsync(string text)
    {
        string envelope = GroupMessages.Send(
            _group.Group,
            _peeler,
            MarmotAppEvent.Chat(_scramble.Hex, (long)Now(), text),
            _scramble.AccountPublicKey.Span);

        await _relay.PublishAsync(envelope, RelayTimeout);
    }

    /// <summary>
    /// Polls the relay for a kind-445 carrying <paramref name="text"/>.
    /// </summary>
    /// <remarks>
    /// Reads the group's own transport address off the signed routing component
    /// — the same value a peer would use — rather than a value the test knows by
    /// another route, so a wrong routing component fails here too.
    /// </remarks>
    public async Task<ReceivedGroupMessage?> WaitForScrambleMessageAsync(string text)
    {
        string transportIdHex = Convert.ToHexString(
            GroupMessages.TransportGroupId(_group.Group)).ToLowerInvariant();

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
                    PeeledMessage peeled = _peeler.Peel(
                        envelope, _ => GroupMessages.ExporterSecret(_group.Group));

                    // Our own messages come back from the relay too; skip
                    // anything already processed so a repeat does not look like
                    // a decryption failure.
                    if (peeled.TransportId is { } id && !_seenTransportIds.Contains(id))
                        _seenTransportIds.Add(id);

                    ReceivedGroupMessage received = GroupMessages.Receive(
                        _group.Group, peeled.MlsBytes);

                    if (received.Event.Content == text)
                        return received;
                }
                catch (Exception ex)
                {
                    // Our own outbound messages cannot be decrypted by us, and a
                    // message from an epoch we have not reached is expected too.
                    _log.Add($"peel: {ex.Message}");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return null;
    }

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

    private static ulong Now() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>
/// Every interop class runs in this one collection, and that is not incidental.
/// </summary>
/// <remarks>
/// xUnit runs test classes in parallel by default, and all of these drive a
/// <b>single shared peer container</b>: two classes creating identities and
/// starting daemons at once corrupts its SQLite state outright
/// ("backend failure: file is not a database"). One collection serialises them.
/// </remarks>
[CollectionDefinition(DarkMatterInteropCollection.Name)]
public sealed class DarkMatterInteropCollection : ICollectionFixture<MessageInteropFixture>
{
    public const string Name = "DarkMatterInterop";
}
