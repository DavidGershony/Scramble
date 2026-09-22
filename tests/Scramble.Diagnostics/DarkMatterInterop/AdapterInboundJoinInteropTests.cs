using System.Diagnostics;
using DotnetMls.Crypto;
using Scramble.Core.Services;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Storage.Sqlite;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;
using CoreKeyPackage = Scramble.Core.Models.KeyPackage;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// Joining a group the reference client created, through
/// <see cref="IMlsService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The other seat.</b> <see cref="AdapterInteropTests"/> is always the
/// inviter: it creates the group, holds state it built itself, and sits at leaf
/// index 0. Everything here comes off a Welcome the peer produced instead — the
/// GroupContext, the component state, the required set, our leaf index and the
/// exporter secret — so a fault in any of them surfaces only from this seat.
/// </para>
/// <para>
/// It is also the only place <see cref="IMlsService.ProcessWelcomeAsync"/> and
/// <see cref="IMlsService.MarkKeyPackagePublishedAsync"/> meet a real peer. Both
/// are new, and the binding between them — a Welcome naming the kind-30443 event
/// whose private material opens it — is the kind of agreement that holds
/// perfectly between two copies of our own code and still fails against someone
/// else's.
/// </para>
/// </remarks>
[Trait("Category", "DarkMatterInterop")]
[Collection(DarkMatterInteropCollection.Name)]
public class AdapterInboundJoinInteropTests : IDisposable
{
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(90);
    private const string PeerRelay = "ws://127.0.0.1:7777";

    private readonly List<string> _log = [];
    private readonly InteropRelayClient _relay = new(InteropRelayClient.DefaultRelayUrl);
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"adapter-inbound-{Guid.NewGuid():N}.db");

    private SqliteMarmotStorageProvider? _storage;
    private DarkMatterMlsService? _service;

    public void Dispose()
    {
        _service?.Dispose();
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

    private string Log() => string.Join('\n', _log);

    [Fact]
    public async Task TheServiceJoinsAGroupTheReferenceClientCreated()
    {
        var peer = new MdkCliDockerClient(_log.Add);
        Assert.SkipUnless(await peer.IsReadyAsync(), "The mdk-cli interop peer is not running.");

        var us = await BecomeInvitableAsync(peer);

        string groupName = $"peer group {Guid.NewGuid():N}"[..24];
        await peer.CreateGroupAsync(groupName, us.PublicKeyHex);

        (WelcomeRumor rumor, string wrapperEventId) = await WaitForWelcomeAsync(us);

        // The binding, carried the way the app carries it: NostrService parses
        // this id off the rumor's e tag, and it is what makes the join refuse a
        // Welcome naming a KeyPackage we never published.
        string keyPackageEventId =
            Convert.ToHexString(rumor.KeyPackageEventId).ToLowerInvariant();

        MlsGroupInfo joined = await _service!.ProcessWelcomeAsync(
            rumor.WelcomeBytes, wrapperEventId, keyPackageEventId);

        string groupIdHex = Convert.ToHexString(joined.GroupId).ToLowerInvariant();

        Assert.True(
            MdkCliDockerClient.ContainsGroupId(await peer.GroupsAsync(), groupIdHex),
            $"We joined a group the peer does not have.\n{Log()}");

        Assert.Contains(us.PublicKeyHex, joined.MemberPublicKeys);
        Assert.Contains(peer.SelectedAccount, joined.MemberPublicKeys);

        // Reading from this seat. Every input came off the Welcome, so a wrong
        // exporter secret or transport id shows up only here.
        string text = $"from the creator {Guid.NewGuid():N}";
        await peer.SendMessageAsync(groupIdHex, text);

        Assert.True(
            await ReceivedAsync(joined.GroupId, text),
            $"The service never read the peer's message.\n{Log()}");

        // And writing from it. Our leaf index came off the Welcome too, and a
        // wrong one produces bytes that decrypt to nothing on their side.
        string reply = $"from the invitee {Guid.NewGuid():N}";
        byte[] envelope = await _service.EncryptMessageAsync(joined.GroupId, reply);

        await _relay.PublishAsync(
            System.Text.Encoding.UTF8.GetString(envelope), RelayTimeout);

        Assert.True(
            await WaitForAsync(async () =>
            {
                await peer.SyncAsync();
                return await peer.HasMessageAsync(groupIdHex, reply);
            }),
            $"The peer never read the reply we sent into its own group.\n{Log()}");
    }

    [Fact]
    public async Task AWelcomeNamingAKeyPackageWeNeverPublishedIsRefused()
    {
        // The fail-closed half, against a Welcome a real peer built. The bytes
        // are genuine and openable -- the group secrets really are sealed to our
        // KeyPackage -- so what is refused here is provenance, not capability.
        // Without the binding this Welcome would join happily.
        var peer = new MdkCliDockerClient(_log.Add);
        Assert.SkipUnless(await peer.IsReadyAsync(), "The mdk-cli interop peer is not running.");

        var us = await BecomeInvitableAsync(peer);

        await peer.CreateGroupAsync($"peer group {Guid.NewGuid():N}"[..24], us.PublicKeyHex);

        (WelcomeRumor rumor, string wrapperEventId) = await WaitForWelcomeAsync(us);

        string stranger = new('f', 64);

        InvalidOperationException ex =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _service!.ProcessWelcomeAsync(
                    rumor.WelcomeBytes, wrapperEventId, stranger));

        Assert.Contains("never published", ex.Message);

        // And the same bytes join once the Welcome is allowed to name the
        // KeyPackage it actually consumed. Without this the test above would
        // pass just as well if the Welcome were simply unopenable.
        MlsGroupInfo joined = await _service!.ProcessWelcomeAsync(
            rumor.WelcomeBytes,
            wrapperEventId,
            Convert.ToHexString(rumor.KeyPackageEventId).ToLowerInvariant());

        Assert.NotEmpty(joined.GroupId);
    }

    private sealed record Us(byte[] Secret, string PublicKeyHex);

    /// <summary>
    /// Publishes everything the peer needs before it will invite us.
    /// </summary>
    /// <remarks>
    /// A KeyPackage alone is not enough — the peer refuses to invite an account
    /// whose relay lists it cannot find, so publishing one without the others
    /// makes us uninvitable in a way that surfaces much later as a group that
    /// never arrives.
    /// </remarks>
    private async Task<Us> BecomeInvitableAsync(MdkCliDockerClient peer)
    {
        await peer.StartDaemonAsync(PeerRelay);
        await peer.CreateIdentityAsync();

        var (secret, publicKey) = Bip340.GenerateKeyPair();
        string publicKeyHex = Convert.ToHexString(publicKey).ToLowerInvariant();
        var us = new Us(secret, publicKeyHex);

        _storage = new SqliteMarmotStorageProvider($"Data Source={_dbPath}");
        _service = new DarkMatterMlsService(_storage);
        await _service.InitializeAsync(
            Convert.ToHexString(secret).ToLowerInvariant(), publicKeyHex);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await PublishSignedAsync(us, RelayListEvent.BuildNip65(publicKeyHex, [PeerRelay], now));
        await PublishSignedAsync(
            us, RelayListEvent.BuildMessageRelays(publicKeyHex, [PeerRelay], now));

        // Through the contract, the way the app does it: the service generates
        // and stores, the caller signs and publishes, and then tells the service
        // what event id it went out under. That last step is what lets the join
        // below find this KeyPackage's private material from the Welcome.
        CoreKeyPackage keyPackage = await _service.GenerateKeyPackageAsync();

        var template = new NostrEventTemplate(
            publicKeyHex,
            now,
            30443,
            [.. keyPackage.NostrTags.Select(t => (IReadOnlyList<string>)t)],
            Convert.ToBase64String(keyPackage.Data));

        byte[] id = template.ComputeId();
        await _relay.PublishAsync(
            NostrEnvelope.Write(template, id, Bip340.Sign(secret, id)), RelayTimeout);

        string eventIdHex = Convert.ToHexString(id).ToLowerInvariant();
        await _service.MarkKeyPackagePublishedAsync(keyPackage, eventIdHex);
        _log.Add($"our key package event {eventIdHex}");

        Assert.True(
            await WaitForAsync(async () =>
            {
                await peer.SyncAsync();
                return await peer.CanInviteAsync(publicKeyHex);
            }),
            $"The reference client could not resolve our account.\n{Log()}");

        return us;
    }

    private async Task PublishSignedAsync(Us us, NostrEventTemplate template)
    {
        byte[] id = template.ComputeId();
        await _relay.PublishAsync(
            NostrEnvelope.Write(template, id, Bip340.Sign(us.Secret, id)), RelayTimeout);
    }

    /// <summary>Polls for a gift wrap addressed to us carrying a Welcome.</summary>
    /// <remarks>
    /// Read rather than joined: the adapter is what must do the joining, and
    /// handing it a group the engine already built would test nothing.
    /// </remarks>
    private async Task<(WelcomeRumor Rumor, string WrapperEventId)> WaitForWelcomeAsync(Us us)
    {
        var clock = Stopwatch.StartNew();
        var tried = new HashSet<string>();

        while (clock.Elapsed < SettleTimeout)
        {
            var envelopes = await _relay.FetchAsync(
                new Dictionary<string, object>
                {
                    ["kinds"] = new[] { 1059 },
                    ["#p"] = new[] { us.PublicKeyHex },
                },
                RelayTimeout);

            foreach (string envelope in envelopes)
            {
                if (!tried.Add(envelope))
                    continue;

                try
                {
                    WelcomeRumor rumor = GroupJoin.Read(envelope, us.Secret);
                    return (rumor, WrapperEventIdOf(envelope));
                }
                catch (Exception ex)
                {
                    _log.Add($"wrap: {ex.Message}");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.Fail($"No Welcome addressed to us arrived.\n{Log()}");
        return default;
    }

    private static string WrapperEventIdOf(string envelope)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(envelope);
        return doc.RootElement.GetProperty("id").GetString() ?? "";
    }

    /// <summary>Drains the group's traffic through the service until text arrives.</summary>
    private async Task<bool> ReceivedAsync(byte[] groupId, string text)
    {
        string transportIdHex =
            Convert.ToHexString(_service!.GetNostrGroupId(groupId)!).ToLowerInvariant();

        var clock = Stopwatch.StartNew();
        var seen = new HashSet<string>();

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
                if (!seen.Add(envelope))
                    continue;

                try
                {
                    MlsDecryptedMessage decrypted = await _service.DecryptMessageAsync(
                        groupId, System.Text.Encoding.UTF8.GetBytes(envelope));

                    if (!decrypted.IsCommit && decrypted.Plaintext == text)
                        return true;
                }
                catch (Exception ex)
                {
                    // Our own outbound messages are not ours to decrypt.
                    _log.Add($"decrypt: {ex.Message}");
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
}
