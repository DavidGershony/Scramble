using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
using Xunit;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// The app inviting the reference client: <see cref="MessageService"/> stages and
/// publishes the commit, <see cref="NostrService"/> gift-wraps the Welcome, and the
/// peer has to be able to open it and join.
/// </summary>
/// <remarks>
/// <para>
/// <b>The half of the interop story that has never existed.</b>
/// <see cref="InboundWelcomeInteropTests"/> proves the app can read a Welcome the
/// peer built. Nothing proved the reverse through the app's own code: the adapter
/// suites publish Welcomes with the engine's <c>WelcomePublication.Wrap</c>, which
/// is not what the product uses. The product uses
/// <c>NostrService.PublishWelcomeAsync</c> — its own kind-444 rumor, its own
/// kind-13 seal, its own NIP-44, its own kind-1059 wrap — and no peer had ever
/// been asked to open one.
/// </para>
/// <para>
/// <b>It exists now because step 4 swaps that NIP-44 implementation.</b>
/// <c>NostrService</c> seals and unwraps with marmot-cs's <c>Nip44Encryption</c>,
/// which goes when <c>marmot-cs</c> does at step 5; the replacement is
/// <c>Scramble.Nostr.Crypto.Nip44</c>. The two APIs are drop-in compatible, which is
/// exactly the kind of change that looks safe and is only actually safe if a peer
/// still reads the output. So this test was written and confirmed green <b>before</b>
/// the swap, to prove it exercises the path, and is the harness the swap is judged
/// by afterwards.
/// </para>
/// <para>
/// <b>What it deliberately does not do</b> is assert on our own bytes. The oracle is
/// the peer: it lists the invite, accepts it, and ends up in the group with us. Any
/// assertion we could make about our own gift wrap would be us agreeing with
/// ourselves, which is the failure this suite exists to break.
/// </para>
/// </remarks>
[Trait("Category", "DarkMatterInterop")]
[Collection(DarkMatterInteropCollection.Name)]
public sealed class OutboundWelcomeInteropTests : IAsyncDisposable
{
    private const string PeerRelay = "ws://127.0.0.1:7777";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(90);

    private readonly List<string> _log = [];
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"outbound-welcome-{Guid.NewGuid():N}.db");

    private NostrService? _nostr;
    private MessageService? _messages;
    private IMlsService? _mls;

    private string Log() => string.Join('\n', _log);

    public async ValueTask DisposeAsync()
    {
        _messages?.Dispose();
        if (_nostr is not null)
        {
            try { await _nostr.DisconnectAsync(); } catch { /* teardown */ }
            _nostr.Dispose();
        }
        (_mls as IDisposable)?.Dispose();

        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* stray temp file */ }
    }

    [Fact]
    public async Task TheReferenceClientOpensAWelcomeTheAppSent()
    {
        var peer = new MdkCliDockerClient(_log.Add);
        Assert.SkipUnless(await peer.IsReadyAsync(), "The mdk-cli interop peer is not running.");

        ProfileConfiguration.SetAllowLocalRelays(true);
        await peer.StartDaemonAsync(PeerRelay);
        string peerPubkey = await peer.CreateIdentityAsync();

        // The peer has to be invitable by us, which means a KeyPackage of its own
        // on the relay for us to fetch.
        await peer.PublishKeyPackageAsync();

        // ── The app, wired as a head wires it ──
        _nostr = new NostrService();
        var keys = _nostr.GenerateKeyPair();

        var storage = new StorageService(_dbPath, new MockSecureStorage());
        await storage.InitializeAsync();
        await storage.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = keys.publicKeyHex,
            PrivateKeyHex = keys.privateKeyHex,
            Npub = keys.npub,
            Nsec = keys.nsec,
            DisplayName = "Inviter",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow,
        });

        _mls = DarkMatterMlsServiceFactory.Create(storage);
        await _mls.InitializeAsync(keys.privateKeyHex, keys.publicKeyHex);
        _messages = new MessageService(storage, _nostr, _mls);
        await _messages.InitializeAsync();

        _nostr.SetStorageService(storage);
        await _nostr.ConnectAsync(PeerRelay);
        await Task.Delay(500);

        // ── We create a group and invite the peer through the app ──
        // CreateGroupAsync then AddMemberAsync, which is the path both heads drive:
        // fetch the invitee's KeyPackage, stage a commit per device, publish it as a
        // finished kind-445, merge on the relay's confirmation, then the Welcome.
        Chat chat = await _messages.CreateGroupAsync(
            $"outbound {Guid.NewGuid():N}"[..20], [], [PeerRelay]);

        Assert.True(
            await WaitForAsync(async () =>
            {
                var fetched = (await _nostr!.FetchKeyPackagesAsync(peerPubkey)).ToList();
                _log.Add($"fetched {fetched.Count} key package(s) for the peer");
                return fetched.Count > 0;
            }),
            $"We never found the peer's KeyPackage, so the invite could not be built.\n{Log()}");

        await _messages.AddMemberAsync(chat.Id, peerPubkey);

        string groupIdHex = Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();
        _log.Add($"invited the peer to {groupIdHex}");

        // ── The oracle: the peer opens what we sent ──
        // Listing the invite already proves the whole outbound envelope — the wrap
        // decrypted with its key, the seal verified, the rumor parsed, and the
        // KeyPackage named in it was one it published.
        Assert.True(
            await WaitForAsync(async () =>
            {
                await peer.SyncAsync();
                return MdkCliDockerClient.ContainsGroupId(await peer.InvitesAsync(), groupIdHex);
            }),
            $"The reference client never saw the Welcome the app sent.\n{Log()}");

        await peer.AcceptInviteAsync(groupIdHex);

        Assert.True(
            await WaitForAsync(async () =>
            {
                await peer.SyncAsync();
                return MdkCliDockerClient.ContainsGroupId(await peer.GroupsAsync(), groupIdHex);
            }),
            $"The peer listed our invite but could not join the group.\n{Log()}");

        // ── And the group works in the direction only this test creates ──
        // A message we send as the group's creator, read by a member who joined
        // from our Welcome. If anything in that Welcome were subtly wrong the join
        // could still appear to succeed and the ratchet be unusable.
        string text = $"from the inviter {Guid.NewGuid():N}"[..32];
        await _messages.SendMessageAsync(chat.Id, text);

        Assert.True(
            await WaitForAsync(async () =>
            {
                await peer.SyncAsync();
                return await peer.HasMessageAsync(groupIdHex, text);
            }),
            $"The peer joined but could not read our traffic.\n{Log()}");
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Settle)
        {
            try
            {
                if (await condition())
                    return true;
            }
            catch (Exception)
            {
                // A poll that throws has not succeeded yet: the peer may still be
                // starting, or a relay query may have raced.
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return false;
    }
}
