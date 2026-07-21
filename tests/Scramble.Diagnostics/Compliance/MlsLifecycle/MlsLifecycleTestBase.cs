using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Diagnostics.RelayHarness;
using Scramble.Diagnostics.TestHelpers;
using Xunit;

namespace Scramble.Diagnostics.Compliance.MlsLifecycle;

/// <summary>
/// Shared harness for MLS lifecycle regression tests (S2 in ANALYSIS.md).
///
/// Each fixture spins up an in-process <see cref="FaultyRelay"/> on a loopback
/// port and lets tests create N party instances that share that relay. All
/// state (SQLite DB, MLS group state, subscriptions) is per-party and cleaned
/// up on disposal so tests are hermetic.
///
/// Why hermetic: <see cref="RelayHarness.FaultyRelay"/> gives us deterministic
/// timing (no external relay flake) and lets a single test flip fault knobs
/// mid-run. That is the missing ingredient the pre-existing MLS test surface
/// lacked — see ANALYSIS.md §"HeadlessRealMlsIntegrationTests.cs 67% fix-density".
/// </summary>
public abstract class MlsLifecycleTestBase : IAsyncLifetime
{
    protected FaultyRelay Relay { get; private set; } = null!;
    protected ITestOutputHelper Output { get; }

    private readonly List<string> _dbPaths = new();
    private readonly List<NostrService> _nostrServices = new();
    private readonly List<MessageService> _messageServices = new();

    protected MlsLifecycleTestBase(ITestOutputHelper output)
    {
        Output = output;
    }

    public virtual async ValueTask InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        Relay = new FaultyRelay();
        await Relay.StartAsync();
        Output.WriteLine($"[harness] FaultyRelay listening at {Relay.WsUrl}");
    }

    public virtual async ValueTask DisposeAsync()
    {
        foreach (var m in _messageServices)
        {
            try { m.Dispose(); } catch { }
        }
        foreach (var n in _nostrServices)
        {
            try { await n.DisconnectAsync(); } catch { }
            try { (n as IDisposable)?.Dispose(); } catch { }
        }
        if (Relay != null) await Relay.DisposeAsync();

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        foreach (var p in _dbPaths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }

    /// <summary>
    /// A single MLS participant: its keys, Nostr connection, storage, MLS
    /// service and MessageService. All tests operate on collections of these.
    /// </summary>
    protected sealed record Party(
        string Name,
        string PubKeyHex,
        string PrivKeyHex,
        NostrService NostrService,
        StorageService Storage,
        IMlsService MlsService,
        MessageService MessageService);

    /// <summary>
    /// Create a fresh party with its own identity, DB and MLS state, connected
    /// to the shared <see cref="Relay"/>. Registered for cleanup on dispose.
    /// </summary>
    protected async Task<Party> CreatePartyAsync(string name)
    {
        var nostr = new NostrService();
        _nostrServices.Add(nostr);

        var keys = nostr.GenerateKeyPair();

        var dbPath = Path.Combine(Path.GetTempPath(),
            $"scramble_mls_{name}_{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);

        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();
        await storage.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = keys.publicKeyHex,
            PrivateKeyHex = keys.privateKeyHex,
            Npub = keys.npub,
            Nsec = keys.nsec,
            DisplayName = name,
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        IMlsService mls = new ManagedMlsService(storage);
        var messages = new MessageService(storage, nostr, mls);
        _messageServices.Add(messages);
        await messages.InitializeAsync();

        await nostr.ConnectAsync(Relay.WsUrl);
        await Task.Delay(300); // let subscriptions settle

        Output.WriteLine(
            $"[{name}] pubkey={keys.publicKeyHex[..12]}… slot={mls.GetLocalKeyPackageSlotId() ?? "(none)"}");

        return new Party(name, keys.publicKeyHex, keys.privateKeyHex,
            nostr, storage, mls, messages);
    }

    /// <summary>Publish a KeyPackage for each party and wait for the relay to index it.</summary>
    protected async Task PublishKeyPackagesAsync(params Party[] parties)
    {
        foreach (var p in parties)
        {
            var kp = await p.MlsService.GenerateKeyPackageAsync();
            await p.NostrService.PublishKeyPackageAsync(kp.Data, p.PrivKeyHex, kp.NostrTags);
            Output.WriteLine($"[{p.Name}] published KP slot={p.MlsService.GetLocalKeyPackageSlotId()?[..12] ?? "(none)"}");
        }
        await Task.Delay(750);
    }

    /// <summary>Subscribe each party to its inbox for gift-wrapped Welcomes.</summary>
    protected async Task SubscribeToWelcomesAsync(params Party[] parties)
    {
        foreach (var p in parties)
            await p.NostrService.SubscribeToWelcomesAsync(p.PubKeyHex, p.PrivKeyHex);
        await Task.Delay(500);
    }

    /// <summary>
    /// Have <paramref name="inviter"/> create a group and add each of
    /// <paramref name="invitees"/>. Returns the local <see cref="Chat"/> the
    /// inviter created; each invitee will have its own copy after
    /// <see cref="AcceptWelcomesAsync"/>.
    /// </summary>
    protected async Task<Chat> CreateGroupAndInviteAsync(
        Party inviter, string groupName, params Party[] invitees)
    {
        var chat = await inviter.MessageService.CreateGroupAsync(
            groupName,
            invitees.Select(i => i.PubKeyHex));
        Output.WriteLine(
            $"[{inviter.Name}] created group '{groupName}' mlsGroupId={Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant()[..12]}… epoch=0");

        var nostrGroupId = chat.NostrGroupId != null
            ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();
        await inviter.NostrService.SubscribeToGroupMessagesAsync(new[] { nostrGroupId });
        await Task.Delay(500);

        return chat;
    }

    /// <summary>
    /// For each party, rescan its pending invites, accept the invite whose
    /// Welcome decrypts into <paramref name="mlsGroupIdHex"/>, and subscribe
    /// to that group's kind-445 stream. Returns the accepted <see cref="Chat"/>
    /// per party in the same order as <paramref name="invitees"/>.
    ///
    /// Implementation note: <see cref="PendingInvite.GroupId"/> holds the
    /// NostrGroupId (the "h" tag on the wrapper event), NOT the MLS group id
    /// — see <c>MessageService.HandleWelcome</c>. Rather than filter on a
    /// value whose meaning has historically flipped (cluster C3 in
    /// ANALYSIS.md), we accept the invite and then assert the resulting
    /// Chat's MlsGroupId matches. Multi-group harness callers should invoke
    /// this once per group in the order the groups were created.
    /// </summary>
    protected async Task<IReadOnlyList<Chat>> AcceptWelcomesAsync(
        string mlsGroupIdHex, params Party[] invitees)
    {
        var accepted = new List<Chat>();
        foreach (var p in invitees)
        {
            await p.MessageService.RescanInvitesAsync();
            var pending = (await p.Storage.GetPendingInvitesAsync()).ToList();
            Assert.NotEmpty(pending);

            // Walk pending invites oldest-first (SavePendingInvite orders by
            // ReceivedAt DESC on read; reverse so a same-test multi-group
            // sequence lines up with the group-creation order) and pick the
            // one whose Welcome, when accepted, gives us the expected MLS id.
            Chat? matched = null;
            var errors = new List<string>();
            foreach (var invite in pending.OrderBy(i => i.ReceivedAt))
            {
                Chat chat;
                try
                {
                    chat = await p.MessageService.AcceptInviteAsync(invite.Id);
                }
                catch (Exception ex)
                {
                    errors.Add($"invite {invite.Id[..8]}…: {ex.GetType().Name} {ex.Message}");
                    continue;
                }

                var acceptedMlsHex = chat.MlsGroupId is null
                    ? "(null)"
                    : Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
                if (string.Equals(acceptedMlsHex, mlsGroupIdHex, StringComparison.OrdinalIgnoreCase))
                {
                    matched = chat;
                    break;
                }
                // Wrong group — the party joined it, but it isn't the one this
                // AcceptWelcomesAsync call is asking about. Leave it in place;
                // a subsequent call for that MLS id will find it via storage.
                Output.WriteLine(
                    $"[{p.Name}] accepted invite {invite.Id[..8]}… but Chat.MlsGroupId={acceptedMlsHex[..12]}… ≠ expected {mlsGroupIdHex[..12]}…");
            }

            Assert.True(matched != null,
                $"[{p.Name}] no pending invite yielded a Chat with MlsGroupId={mlsGroupIdHex[..12]}…; " +
                $"errors=[{string.Join("; ", errors)}]");

            accepted.Add(matched!);

            var nostrGid = matched!.NostrGroupId != null
                ? Convert.ToHexString(matched.NostrGroupId).ToLowerInvariant()
                : mlsGroupIdHex;
            await p.NostrService.SubscribeToGroupMessagesAsync(new[] { nostrGid });
            Output.WriteLine(
                $"[{p.Name}] accepted invite → chat={matched.Id[..8]}… epoch={await CurrentEpochAsync(p, matched.MlsGroupId!)}");
        }
        await Task.Delay(500);
        return accepted;
    }

    /// <summary>
    /// Poll every party's MLS epoch for <paramref name="mlsGroupId"/> until they all
    /// match, or fail on timeout. This is the mandatory "warm-up" step after a
    /// multi-add group creation: <c>MessageService.CreateGroupAsync</c> issues one
    /// AddMember commit per invitee, each advancing the epoch by one. Late-arriving
    /// invitees must process the commits that landed after their Welcome before
    /// they can send messages the rest of the group can decrypt. Without this
    /// gate, tests race the pipeline and fail with "never received message" —
    /// which is a harness bug, not an MLS bug.
    /// </summary>
    protected async Task WaitForEpochParityAsync(
        byte[] mlsGroupId, IEnumerable<Party> parties, TimeSpan? timeout = null)
    {
        var partyList = parties.ToList();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        List<ulong> lastEpochs = new();
        while (DateTime.UtcNow < deadline)
        {
            var epochs = new List<ulong>();
            foreach (var p in partyList)
                epochs.Add(await CurrentEpochAsync(p, mlsGroupId));
            lastEpochs = epochs;
            if (epochs.All(e => e == epochs[0]))
            {
                Output.WriteLine($"[epoch-parity] all parties at epoch={epochs[0]}");
                return;
            }
            await Task.Delay(500);
        }
        Assert.Fail(
            $"parties never reached epoch parity within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s: " +
            $"[{string.Join(",", partyList.Zip(lastEpochs).Select(z => $"{z.First.Name}={z.Second}"))}]");
    }

    /// <summary>Poll <paramref name="party"/>'s storage until a matching message arrives, or fail.</summary>
    protected async Task<Message> WaitForMessageAsync(
        Party party, string chatId, string expectedContent,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var msgs = await party.Storage.GetMessagesForChatAsync(chatId);
            var hit = msgs.FirstOrDefault(m =>
                m.Content == expectedContent && m.Type == MessageType.Text);
            if (hit != null) return hit;
            await Task.Delay(200);
        }
        Assert.Fail(
            $"[{party.Name}] never received message '{expectedContent}' " +
            $"in chat {chatId[..8]}… within {(timeout ?? TimeSpan.FromSeconds(10)).TotalSeconds}s");
        return null!; // unreachable
    }

    protected static async Task<ulong> CurrentEpochAsync(Party p, byte[] mlsGroupId)
    {
        var info = await p.MlsService.GetGroupInfoAsync(mlsGroupId);
        return info?.Epoch ?? ulong.MaxValue;
    }

    /// <summary>
    /// True when the process is running on a hosted CI runner
    /// (GITHUB_ACTIONS=true or the more generic CI=true). Used to scale
    /// timeouts — the shared runners are measurably slower than local dev
    /// machines for the in-process FaultyRelay pipeline
    /// (see runs 29498185991, 29817840255).
    /// </summary>
    protected static bool IsCi =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Scale a locally-tuned timeout for CI. Local: identity. CI: x3.
    /// Rule of thumb: whatever passes on a fast dev workstation, CI needs
    /// at least 3× before the same test lands reliably.
    /// </summary>
    protected static TimeSpan CiScale(TimeSpan local) =>
        IsCi ? TimeSpan.FromMilliseconds(local.TotalMilliseconds * 3) : local;
}
