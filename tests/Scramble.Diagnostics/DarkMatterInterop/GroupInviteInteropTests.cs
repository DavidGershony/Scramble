using System.Diagnostics;
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
/// The outbound direction: a live <c>wn-agent</c> joining a group we created.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half that actually proves the GroupContext.</b> The inbound
/// suite checks that we can read what the reference implementation publishes,
/// which validates our decoders. Nothing there validates what we <i>produce</i>
/// — a group whose required-capabilities, requirement list or component state
/// were subtly wrong would pass every inbound test and be refused by the first
/// real peer. The only way to find that out is to have one try to join.
/// </para>
/// <para>
/// The agent runs with <c>--dev-allow-any-invites</c>, so it accepts an invite
/// from an account it has never heard of. That flag is why this test can exist
/// at all without an allowlist dance, and it is dev-only for obvious reasons.
/// </para>
/// <para>
/// Requires <c>docker compose -f docker-compose.test.yml up -d nostr-relay
/// wn-agent</c>; skips when the agent is absent, like the rest of the suite.
/// </para>
/// </remarks>
[Trait("Category", "DarkMatterInterop")]
public class GroupInviteInteropTests
{
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(30);

    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private readonly List<string> _log = [];

    /// <summary>The relay the agent is configured to use, as it sees it.</summary>
    private const string AgentRelay = "ws://127.0.0.1:7777";

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

    [Fact(Skip =
        "Does not pass yet, and is kept because the investigation is the value. " +
        "Established: the Welcome we publish reaches the relay, correctly p-tagged " +
        "and within the NIP-59 backwards-jitter window; the agent holds a relay " +
        "connection the relay logs as 'sent: 0 events', so nothing is subscribed " +
        "until a client holds subscribe_inbound open. Not established: whether a " +
        "held subscription actually makes it fetch, because a held subscription " +
        "starves the agent's control pool and the group query then returns nothing. " +
        "Twice the agent's data volume was left unstartable (startup failed " +
        "code=app_error) after a run — reproducible, but NOT cleanly attributable " +
        "to our Welcome: manual probing killed subscriptions mid-stream in between, " +
        "and that confound was never isolated. Next step is to read " +
        "stream_inbound_events in agent-connector rather than to keep guessing.")]
    public async Task TheReferenceAgentJoinsAGroupWeCreated()
    {
        var agent = new WnAgentDockerClient(_log.Add);
        Assert.SkipUnless(await agent.IsReadyAsync(), "The wn-agent interop peer is not running.");

        AgentBootstrap bootstrap = await agent.BootstrapAsync();
        string groupIdHex;
        var relay = new InteropRelayClient(InteropRelayClient.DefaultRelayUrl);

        // 0. Subscribe FIRST and hold it. The agent fetches nothing while nobody
        //    is subscribed — the relay log shows its connection sitting at
        //    "sent: 0 events" — so publishing before this point publishes into
        //    silence.
        //    Disposed in a finally, not a `using`: it has to be released before
        //    step 5 can query, and a test that fails in between must not leave it
        //    holding one of the agent's few control slots — that starves every
        //    later request, including bootstrap, and reads as the agent hanging.
        InboundSubscription inbound = agent.SubscribeInbound(bootstrap.AccountIdHex);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.True(inbound.IsAlive, "The inbound subscription died immediately.");

        // 1. Fetch the agent's KeyPackage and the event id that names it. The
        //    Welcome must cite the EVENT id — that is how the agent finds the
        //    private material it published this KeyPackage with.
        var envelopes = await relay.FetchKeyPackagesAsync(bootstrap.AccountIdHex, RelayTimeout);
        Assert.NotEmpty(envelopes);

        KeyPackagePublication publication = KeyPackageEvent.Parse(envelopes[^1]);
        var validated = KeyPackagePublicationValidator.Validate(publication, _cs);

        var carried = MlsMessage.ReadFrom(new TlsReader(publication.KeyPackageBytes));
        var agentKeyPackage = (KeyPackage)carried.Body;

        // 2. Create a group and add the agent. Every Marmot gate runs against a
        //    leaf we did not build, which is the part no unit test can cover.
        var inviter = new LocalSigner();
        var group = await MarmotGroupBuilder.CreateAsync(
            _cs, inviter, "Interop", "Scramble invites wn-agent", Now());

        StagedInvite staged = MarmotGroupInvite.Add(group.Group, _cs, [agentKeyPackage]);

        Assert.Equal(
            validated.CredentialIdentity, Assert.Single(staged.AddedAccounts));

        // 3. Publish the Welcome, then apply. Publish-before-apply: a commit
        //    applied and never published leaves us in an epoch the agent can
        //    never reach.
        string wrapped = WelcomePublication.Wrap(
            inviter.Secret,
            inviter.AccountPublicKey.Span,
            validated.CredentialIdentity,
            Convert.FromHexString(publication.EventIdHex),
            [AgentRelay],
            staged.Welcome,
            (long)Now());

        await relay.PublishAsync(wrapped, RelayTimeout);
        staged.Applied();

        // 4. Watch the stream, not the socket. A held subscription starves the
        //    agent's small control pool, so a concurrent group_info poll gets
        //    nothing back — the stream is the only channel that reports anything
        //    while it is open.
            groupIdHex = Convert.ToHexString(group.GroupId).ToLowerInvariant();
            await WaitForStreamActivityAsync(inbound);

            foreach (string line in inbound.Lines)
                _log.Add($"stream: {line}");
        }
        finally
        {
            // 5. Release before asking. With nothing competing for a control
            //    slot the answer is trustworthy.
            inbound.Dispose();
        }

        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.True(
            await WaitForJoinAsync(agent, bootstrap.AccountIdHex, groupIdHex),
            $"wn-agent did not join {groupIdHex}. Log:\n{string.Join('\n', _log)}");
    }

    /// <summary>
    /// Waits until the subscription stream says something beyond its ack.
    /// </summary>
    /// <remarks>
    /// The ack arrives immediately; anything after it is the agent reporting
    /// inbound work. Falling through on the deadline rather than failing here is
    /// deliberate — the group query is the verdict, and a silent stream is
    /// evidence for the failure message rather than a result of its own.
    /// </remarks>
    private static async Task WaitForStreamActivityAsync(InboundSubscription inbound)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < StreamTimeout)
        {
            if (inbound.Lines.Count > 1)
                return;

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Polls the agent until it reports the group, or the deadline passes.
    /// </summary>
    private async Task<bool> WaitForJoinAsync(
        WnAgentDockerClient agent, string accountIdHex, string groupIdHex)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < JoinTimeout)
        {
            try
            {
                if (await agent.HasGroupAsync(accountIdHex, groupIdHex))
                    return true;
            }
            catch (Exception ex)
            {
                // A control call can fail while the agent is mid-join. Recorded
                // rather than thrown, so the failure message carries the whole
                // history instead of only the last attempt.
                _log.Add($"group_info: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return false;
    }

    private static ulong Now() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
