using System.Diagnostics;
using System.Text.Json;
using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Types;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// Scramble creates a group and the Marmot reference client joins it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test that decides whether the engine is real.</b> Everything
/// else checks that we can read what the reference publishes, which validates
/// our decoders and nothing else. A group whose required-capabilities,
/// requirement list or component state were subtly wrong would pass every one
/// of those and be refused by the first actual peer. Only a peer trying to join
/// can tell us.
/// </para>
/// <para>
/// The peer is <c>mdk</c>'s own CLI. It matters that it is not the other two:
/// <c>whitenoise-rs</c> is archived upstream and speaks the legacy protocol, and
/// <c>wn-agent</c> is a gateway connector that never subscribes for inbound, so
/// an invite to it is never read. See
/// <c>tests/mdk-cli-docker/Dockerfile</c>.
/// </para>
/// <para>
/// Requires <c>docker compose -f docker-compose.test.yml up -d nostr-relay
/// mdk-cli</c>. Skips when the peer is absent, like the rest of the suite.
/// </para>
/// </remarks>
[Trait("Category", "DarkMatterInterop")]
public class GroupInteropTests
{
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);

    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private readonly List<string> _log = [];

    /// <summary>The relay as the containerised peer addresses it.</summary>
    private const string PeerRelay = "ws://127.0.0.1:7777";

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

        public Task<byte[]> SignAsync(NostrEventTemplate template, CancellationToken ct = default) =>
            Task.FromResult(Bip340.Sign(Secret, template.ComputeId()));
    }

    [Fact]
    public async Task TheReferenceClientJoinsAGroupScrambleCreated()
    {
        var peer = new MdkCliDockerClient(_log.Add);
        Assert.SkipUnless(await peer.IsReadyAsync(), "The mdk-cli interop peer is not running.");

        _log.Add(await peer.VersionAsync());

        // 1. Bring the peer up on our relay and give it a publishable identity.
        await peer.StartDaemonAsync(PeerRelay);
        string peerPubkey = await peer.CreateIdentityAsync();
        await peer.PublishKeyPackageAsync();

        // 2. Fetch the peer's KeyPackage the way any client would, and validate
        //    it with our own stack before trusting a byte of it.
        var relay = new InteropRelayClient(InteropRelayClient.DefaultRelayUrl);
        var envelopes = await WaitForKeyPackageAsync(relay, peerPubkey);

        KeyPackagePublication publication = KeyPackageEvent.Parse(envelopes[^1]);
        var validated = KeyPackagePublicationValidator.Validate(publication, _cs);

        Assert.Equal(peerPubkey, Convert.ToHexString(validated.CredentialIdentity).ToLowerInvariant());

        var carried = MlsMessage.ReadFrom(new TlsReader(publication.KeyPackageBytes));
        var peerKeyPackage = (KeyPackage)carried.Body;

        // 3. Create the group and add the peer. Every Marmot gate now runs
        //    against a leaf built by the reference implementation.
        var inviter = new LocalSigner();
        var group = await MarmotGroupBuilder.CreateAsync(
            _cs, inviter, "Scramble interop", "Created by Scramble", Now(), [PeerRelay]);

        StagedInvite staged = MarmotGroupInvite.Add(group.Group, _cs, [peerKeyPackage]);
        string groupIdHex = Convert.ToHexString(group.GroupId).ToLowerInvariant();
        _log.Add($"group {groupIdHex}");

        // 4. Publish the Welcome, then apply. Publish-before-apply: a commit
        //    applied and never published strands us in an epoch the peer can
        //    never reach.
        string wrapped = WelcomePublication.Wrap(
            inviter.Secret,
            inviter.AccountPublicKey.Span,
            validated.CredentialIdentity,
            Convert.FromHexString(publication.EventIdHex),
            [PeerRelay],
            staged.Welcome,
            (long)Now());

        await relay.PublishAsync(wrapped, RelayTimeout);
        staged.Applied();

        // 5. The peer syncs and must see our invite. This is the assertion that
        //    everything before it exists to reach: the reference implementation
        //    read our Welcome, decoded our GroupContext, and did not refuse it.
        Assert.True(
            await WaitForAsync(async () =>
            {
                await peer.SyncAsync();
                return MdkCliDockerClient.ContainsGroupId(await peer.InvitesAsync(), groupIdHex);
            }),
            $"The reference client never saw an invite to {groupIdHex}.\n{Log()}");

        // 6. And it can actually join, which proves the group is usable and not
        //    merely well-formed enough to list.
        await peer.AcceptInviteAsync(groupIdHex);

        Assert.True(
            await WaitForAsync(async () =>
                MdkCliDockerClient.ContainsGroupId(await peer.GroupsAsync(), groupIdHex)),
            $"The reference client accepted but never joined {groupIdHex}.\n{Log()}");

        // 7. Both accounts are members from the peer's point of view — one
        //    group, not two that happen to share an id.
        JsonElement members = await peer.MembersAsync(groupIdHex);
        var seen = MdkCliDockerClient.Strings(members).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(peerPubkey, seen);
        Assert.Contains(
            Convert.ToHexString(inviter.AccountPublicKey.ToArray()).ToLowerInvariant(), seen);
    }

    /// <summary>
    /// Waits for the peer's KeyPackage to appear on the relay.
    /// </summary>
    /// <remarks>
    /// Publishing is asynchronous inside the peer, so the first fetch can
    /// legitimately be empty. Polling distinguishes "not yet" from "never"; a
    /// fixed sleep does not.
    /// </remarks>
    private async Task<IReadOnlyList<string>> WaitForKeyPackageAsync(
        InteropRelayClient relay, string pubkey)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < SettleTimeout)
        {
            var envelopes = await relay.FetchKeyPackagesAsync(pubkey, RelayTimeout);
            if (envelopes.Count > 0)
                return envelopes;

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.Fail($"The peer never published a KeyPackage.\n{Log()}");
        return [];
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
                // The peer can legitimately fail a command while it is mid-sync.
                // Recorded rather than thrown so the failure message carries the
                // whole history instead of only the last attempt.
                _log.Add($"poll: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return false;
    }

    private string Log() => string.Join('\n', _log);

    private static ulong Now() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
