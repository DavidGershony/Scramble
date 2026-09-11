using System.Diagnostics;
using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using Scramble.Marmot;
using Scramble.Marmot.AppComponents;
using Scramble.Marmot.Engine.Convergence;
using Scramble.Marmot.Engine.Groups;
using Scramble.Marmot.Engine.KeyPackages;
using Scramble.Marmot.Engine.Messages;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage.Sqlite;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;
using Xunit;
using MarmotDictionary = Scramble.Marmot.AppComponents.AppDataDictionary;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// A same-epoch commit race against the live reference client.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first time convergence has been exercised on anything but our own
/// bytes.</b> The scenario harness has run forks since 2026-09-06, but every
/// client in it is our own engine — so it shows that our rules are
/// self-consistent and cannot show that they handle what another
/// implementation actually emits. A fork is the one situation where agreeing
/// with yourself is worth nothing.
/// </para>
/// <para>
/// The race is built rather than waited for: the peer is made to commit, and we
/// commit from the same epoch before ingesting its commit. Both then fork from
/// the same point, which is exactly the shape two members produce when a relay
/// delivers to them in different orders.
/// </para>
/// <para>
/// <b>The peer hosts the group, and that is not incidental.</b> The only thing
/// it will commit on demand is an admin action — its own leaf rotation is
/// durable maintenance, jittered and targeted a day out, so scheduling one and
/// running a maintenance pass publishes nothing at all. Being the creator makes
/// it an admin, and a rename then commits at once. (Our engine refuses to name
/// an admin who is not yet a member, correctly, so the peer cannot be made one
/// in a group we create until commit-time promotion exists.)
/// </para>
/// <para>
/// <b>What this stops short of, and why.</b> It does not assert that the peer
/// ends up where we do. It does not, and three constructions found three
/// different reasons rather than one bug:
/// </para>
/// <list type="bullet">
/// <item>
/// Traffic on our branch alone does make the peer adopt it — but
/// app-witness score outranks every tie-break, so that version is decided by
/// our own noise. Reversing our committer tie-break survived it, which is the
/// definition of a test that covers nothing.
/// </item>
/// <item>
/// Traffic from the shared ancestor epoch is unbiased and inert: a member keeps
/// the last few epochs' keys, so it reads that cleanly and never reconsiders.
/// </item>
/// <item>
/// Both members carrying on down their own branches — unbiased, and what
/// actually happens after a fork — makes the peer <i>rewind</i> to the fork
/// epoch, abandoning its own commit, and then stop. It never folds ours. Repeated
/// sync and maintenance passes do not move it.
/// </item>
/// </list>
/// <para>
/// The third is the honest construction and it does not converge, so the
/// question is upstream's rather than ours: see the open questions in
/// <c>ai-tasks/HANDOFF-dark-matter.md</c>. What is asserted here is everything
/// that does hold — a real upstream commit becomes a branch, scores
/// deterministically, and can be adopted — and nothing that does not.
/// </para>
/// </remarks>
[Trait("Category", "DarkMatterInterop")]
[Collection(DarkMatterInteropCollection.Name)]
public class ConvergenceInteropTests : IDisposable
{
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(90);
    private const string PeerRelay = "ws://127.0.0.1:7777";


    private readonly ICipherSuite _cs = new CipherSuite0x0001();
    private readonly List<string> _log = [];
    private readonly NostrGroupPeeler _peeler = new();
    private readonly InteropRelayClient _relay = new(InteropRelayClient.DefaultRelayUrl);
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"marmot-race-{Guid.NewGuid():N}.db");

    /// <summary>Exported group states, keyed by the epoch they were taken at.</summary>
    private readonly Dictionary<ulong, byte[]> _archive = [];

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

    [Fact]
    public async Task AnUpstreamCommitFromARacedEpochIsScoredAndCanBeAdopted()
    {
        var peer = new MdkCliDockerClient(_log.Add);
        Assert.SkipUnless(await peer.IsReadyAsync(), "The mdk-cli interop peer is not running.");

        await peer.StartDaemonAsync(PeerRelay);
        string peerPubkey = await peer.CreateIdentityAsync();

        var us = new LocalSigner();
        JoinedGroup joined = await JoinAGroupThePeerHostsAsync(peer, us);
        MlsGroup group = joined.Group;
        string groupIdHex = Convert.ToHexString(joined.GroupId).ToLowerInvariant();

        Archive(group);
        ulong raceEpoch = group.Epoch;
        _log.Add($"racing from epoch {raceEpoch}");

        // 1. The peer commits first, from the shared epoch. What the commit
        //    contains does not matter — only that it forks from the epoch we
        //    are about to commit from too.
        await peer.RenameAsync(groupIdHex, "raced");

        // 2. We commit from that same epoch, before ingesting theirs.
        //    Deliberate: this is what a relay produces when two members are
        //    handed each other's commits after having already sent their own.
        using StagedCommit ours = MarmotSelfUpdate.Stage(group);
        MessageId ourCommitId = MessageId.FromMlsBytes(
            TlsCodec.Serialize(
                new MlsMessage(WireFormat.MlsPublicMessage, ours.Commit).WriteTo));
        // Read before publishing, which is the only moment it can be read: the
        // commit's class comes off the proposal cache, and applying it clears
        // that. An engine that keeps this has to record it at apply time.
        CommitOrderingPriority ourPriority = CommitOrdering.PriorityOf(group, ours.Commit)
            ?? CommitOrderingPriority.Ordinary;

        string ourWire = GroupHandshake.Wrap(group, _peeler, ours.Commit);
        ours.Publishing();
        await _relay.PublishAsync(ourWire, RelayTimeout);
        ours.Applied();
        Archive(group);

        _log.Add($"we applied our own commit, now at epoch {group.Epoch}");

        // 3. Collect the peer's competing commit off the relay.
        StoredCommit? theirs = await WaitForCompetingCommitAsync(raceEpoch, ourCommitId);
        Assert.True(
            theirs is not null,
            $"The peer never published a commit from epoch {raceEpoch}.\n{Log()}");

        // 4. Score both branches and adopt the winner.
        var materializer = new CandidateMaterializer(ConvergencePolicy.V1, Restore);

        MaterializationResult materialized = materializer.Materialize(
            group,
            OurBranchId(ourWire),
            ourPriority,
            [theirs!],
            _ => []);

        Assert.True(
            materialized.Candidates.Count == 2,
            $"Expected our branch and theirs, got {materialized.Candidates.Count}. "
            + $"Refused: [{string.Join(", ", materialized.Refused.Select(r => $"{r.Reason}"))}]."
            + "\n" + Log());

        BranchSelectionTrace trace = BranchSelectionAudit.SelectCanonicalTraced(
            group.Epoch, materialized.Candidates, ConvergencePolicy.V1);

        BranchCandidate winner = materialized.Candidates.Single(
            c => c.Id == trace.SelectedBranchId);

        _log.Add(
            $"we selected {winner.Id[..8]} (decisive "
            + $"{trace.RuleTrace.FirstOrDefault(r => r.Decisive)?.RuleName ?? "none"})");

        MlsGroup settled = winner.Id == OurBranchId(ourWire)
            ? group
            : materializer.Reorg(winner, [theirs!]);

        string settledName = NameOf(settled);
        _log.Add($"we settled at epoch {settled.Epoch}, named '{settledName}'");

        // 5. The branch we did not take is adoptable too, and that is the half
        //    of a reorg nothing else here exercises. Selecting is cheap; what
        //    costs is rewinding to the fork epoch and applying somebody else's
        //    commit to it — real upstream bytes, produced by a client that
        //    shares none of our code.
        BranchCandidate theirBranch = materialized.Candidates.Single(
            c => c.Id != OurBranchId(ourWire));

        MlsGroup adopted = materializer.Reorg(theirBranch, [theirs!]);

        Assert.Equal(raceEpoch + 1, adopted.Epoch);
        Assert.Equal("raced", NameOf(adopted));

        Assert.Contains(
            adopted.GetMembers(),
            m => Convert.ToHexString(m.identity).Equals(peerPubkey, StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            adopted.GetMembers(),
            m => Convert.ToHexString(m.identity).Equals(us.Hex, StringComparison.OrdinalIgnoreCase));

        // 6. And the choice is a function of the candidates, not of when it was
        //    asked. A selector that answered differently on a second look would
        //    diverge from itself, never mind from a peer.
        BranchSelectionTrace again = BranchSelectionAudit.SelectCanonicalTraced(
            group.Epoch, materialized.Candidates, ConvergencePolicy.V1);

        Assert.Equal(trace.SelectedBranchId, again.SelectedBranchId);
        Assert.Contains(trace.RuleTrace, r => r.Decisive);

        // 7. Whichever branch won, ours still works from where it left us.
        await SendAsync(settled, us, $"after the race {Guid.NewGuid():N}");
    }

    /// <summary>Sends a chat message into a group from our seat.</summary>
    private Task SendAsync(MlsGroup group, LocalSigner us, string text) =>
        _relay.PublishAsync(
            GroupMessages.Send(
                group,
                _peeler,
                MarmotAppEvent.Chat(us.Hex, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), text),
                us.AccountPublicKey.Span),
            RelayTimeout);

    /// <summary>The group name in a group's own app-data dictionary.</summary>
    /// <remarks>
    /// Read from the GroupContext rather than remembered from the Welcome: the
    /// point of reading it is to see which branch a reorg landed us on, and a
    /// remembered value cannot change when the history does.
    /// </remarks>
    private static string NameOf(MlsGroup group)
    {
        foreach (Extension extension in group.GroupContext.Extensions)
        {
            if (extension.ExtensionType != MarmotDictionary.ExtensionType)
                continue;

            byte[]? profile = MarmotDictionary.Decode(extension.ExtensionData)
                .Get(AppComponent.GroupProfile);

            if (profile is not null)
                return GroupProfile.Decode(profile).Name;
        }

        throw new InvalidOperationException("The group carries no profile.");
    }

    // ---- Plumbing ----

    private void Archive(MlsGroup group) => _archive[group.Epoch] = group.Export();

    private MlsGroup? Restore(EpochId epoch) =>
        _archive.TryGetValue(epoch.Value, out byte[]? blob) ? MlsGroup.Import(blob, _cs) : null;

    private static string OurBranchId(string envelope) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(envelope))).ToLowerInvariant();

    /// <summary>Polls for a commit from the race epoch that is not our own.</summary>
    /// <remarks>
    /// Ours is recognised by the digest of its MLS bytes, not by the envelope
    /// string we published. A relay re-serialises an event before handing it
    /// back, so the JSON need not be byte-identical to what went out — and
    /// mistaking our own commit for a competitor is silent: it materialises as
    /// a branch that cannot apply, and the race then has one candidate.
    /// </remarks>
    private async Task<StoredCommit?> WaitForCompetingCommitAsync(
        ulong raceEpoch, MessageId ourCommitId)
    {
        var clock = Stopwatch.StartNew();
        var tried = new HashSet<string>();

        // Restored rather than live: peeling consumes nothing here, but reading
        // the race epoch's exporter secret needs the group as it was then.
        MlsGroup atRaceEpoch = Restore(new EpochId(raceEpoch))!;
        string transportIdHex = Convert.ToHexString(
            GroupMessages.TransportGroupId(atRaceEpoch)).ToLowerInvariant();

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
                if (!tried.Add(envelope))
                    continue;

                try
                {
                    // A fresh copy per attempt: peeling and decoding must not
                    // advance the state we are about to materialise from.
                    MlsGroup probe = Restore(new EpochId(raceEpoch))!;
                    byte[] mlsBytes = _peeler
                        .Peel(envelope, _ => GroupMessages.ExporterSecret(probe))
                        .MlsBytes;

                    var message = MlsMessage.ReadFrom(new TlsReader(mlsBytes));
                    if (message.WireFormat != WireFormat.MlsPublicMessage
                        || message.Body is not PublicMessage handshake
                        || handshake.Content.ContentType != ContentType.Commit)
                    {
                        continue;
                    }

                    if (handshake.Content.Epoch != raceEpoch)
                        continue;

                    var id = MessageId.FromMlsBytes(mlsBytes);
                    if (id == ourCommitId)
                    {
                        _log.Add("saw our own commit come back off the relay");
                        continue;
                    }

                    _log.Add(
                        $"found a competing commit {id.ToString()[..8]} from epoch "
                        + $"{handshake.Content.Epoch}, leaf {handshake.Content.Sender.LeafIndex}");

                    return new StoredCommit(id, new EpochId(raceEpoch), mlsBytes, IsOurs: false);
                }
                catch (Exception ex)
                {
                    _log.Add($"peel: {ex.Message}");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return null;
    }

    /// <summary>
    /// Becomes discoverable, has the peer create a group with us in it, and
    /// joins from the Welcome.
    /// </summary>
    private async Task<JoinedGroup> JoinAGroupThePeerHostsAsync(
        MdkCliDockerClient peer, LocalSigner us)
    {
        _storage = new SqliteMarmotStorageProvider($"Data Source={_dbPath}");

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await PublishAsync(us, RelayListEvent.BuildNip65(us.Hex, [PeerRelay], now));
        await PublishAsync(us, RelayListEvent.BuildMessageRelays(us.Hex, [PeerRelay], now));

        var publisher = new KeyPackagePublisher(
            _cs, us, _storage, new RelayPublisher(_relay, RelayTimeout));

        await publisher.PublishAsync((ulong)now);

        Assert.True(
            await WaitForAsync(async () =>
            {
                await peer.SyncAsync();
                return await peer.CanInviteAsync(us.Hex);
            }),
            $"The reference client could not resolve our account.\n{Log()}");

        await peer.CreateGroupAsync($"race {Guid.NewGuid():N}"[..20], us.Hex);

        JoinedGroup? joined = await WaitForWelcomeAsync(us);
        Assert.True(joined is not null, $"No Welcome we could join from arrived.\n{Log()}");

        return joined!;
    }

    private async Task PublishAsync(LocalSigner signer, NostrEventTemplate template)
    {
        byte[] id = template.ComputeId();
        await _relay.PublishAsync(
            NostrEnvelope.Write(template, id, Bip340.Sign(signer.Secret, id)), RelayTimeout);
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
}
