using System.Diagnostics;
using System.Reactive.Linq;
using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
using Xunit;

namespace Scramble.Diagnostics.DarkMatterInterop;

/// <summary>
/// Accepting an invite from the reference client through the <b>app's own</b>
/// inbound path: <see cref="NostrService"/> unwraps the gift wrap,
/// <see cref="MessageService"/> raises the pending invite, and
/// <c>AcceptInviteAsync</c> joins.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the seam no suite covered, and a real bug lived in it.</b>
/// <see cref="AdapterInboundJoinInteropTests"/> joins a group the same peer
/// creates, but it unwraps the peer's gift wrap with the engine's own codec and
/// enters at <see cref="IMlsService.ProcessWelcomeAsync"/> — below
/// <c>MessageService.HandleWelcomeEventAsync</c>, which is where the app decides
/// whether a Welcome is an invite at all. That method parsed the kind-444 rumor
/// with marmot-cs's <c>WelcomeEventParser</c>, which <i>requires</i> an
/// <c>["encoding","base64"]</c> tag no conformant peer emits, and returned
/// silently when it threw. So the app could not accept an invite from anybody but
/// itself, and every app-to-app test passed because both sides emitted the same
/// bad tag.
/// </para>
/// <para>
/// The Whitenoise suite does drive this path — <c>WhitenoiseCreatesGroup_ScrambleJoins</c>
/// is exactly this test — and it skips, because that peer was retired in favour
/// of <c>mdk-cli</c>. One suite covered the path and never ran; the other ran and
/// stepped over the path. This test is the intersection.
/// </para>
/// <para>
/// <b>It deliberately asserts through the app's observables</b>
/// (<c>NewInvites</c>, then a real chat with messages) rather than by inspecting
/// the engine. What broke was not the engine: it was the app's reading of a
/// rumor, and only a test that lets the app do the reading can see that.
/// </para>
/// </remarks>
[Trait("Category", "DarkMatterInterop")]
[Collection(DarkMatterInteropCollection.Name)]
public sealed class InboundWelcomeInteropTests : IAsyncDisposable
{
    private const string PeerRelay = "ws://127.0.0.1:7777";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(90);

    private readonly List<string> _log = [];
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"inbound-welcome-{Guid.NewGuid():N}.db");

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
        try { File.Delete(_dbPath); } catch (IOException) { /* a stray temp file is not a failure */ }
    }

    [Fact]
    public async Task AnInviteFromTheReferenceClientBecomesAChatTheAppCanRead()
    {
        var peer = new MdkCliDockerClient(_log.Add);
        Assert.SkipUnless(await peer.IsReadyAsync(), "The mdk-cli interop peer is not running.");

        ProfileConfiguration.SetAllowLocalRelays(true);
        await peer.StartDaemonAsync(PeerRelay);
        await peer.CreateIdentityAsync();

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
            DisplayName = "Invitee",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        _mls = DarkMatterMlsServiceFactory.Create(storage);
        await _mls.InitializeAsync(keys.privateKeyHex, keys.publicKeyHex);
        _messages = new MessageService(storage, _nostr, _mls);
        await _messages.InitializeAsync();

        _nostr.SetStorageService(storage);
        await _nostr.ConnectAsync(PeerRelay);
        await Task.Delay(500);

        // ── Become invitable, through the app's own publishers ──
        // The peer resolves an account from its relay lists as well as its
        // KeyPackage, and refuses one it cannot look up.
        await _nostr.PublishRelayListAsync(
            [new RelayPreference { Url = PeerRelay, Usage = RelayUsage.Both }],
            keys.privateKeyHex);
        await _nostr.PublishDmRelayListAsync([PeerRelay], keys.privateKeyHex);

        KeyPackage keyPackage = await _mls.GenerateKeyPackageAsync();
        string keyPackageEventId = await KeyPackagePublishing.PublishAndBindAsync(
            _nostr, _mls, keyPackage, keys.privateKeyHex);
        _log.Add($"our key package event {keyPackageEventId}");

        // The inbox subscription is what carries the gift wrap in. Without it the
        // app is invitable and deaf.
        await _nostr.SubscribeToWelcomesAsync(keys.publicKeyHex, keys.privateKeyHex);

        Assert.True(
            await WaitForAsync(async () =>
            {
                await peer.SyncAsync();
                return await peer.CanInviteAsync(keys.publicKeyHex);
            }),
            $"The reference client could not resolve our account.\n{Log()}");

        // ── The peer invites us ──
        var invites = new List<PendingInvite>();
        using var inviteSub = _messages.NewInvites.Subscribe(invites.Add);

        string groupName = $"peer invite {Guid.NewGuid():N}"[..24];
        await peer.CreateGroupAsync(groupName, keys.publicKeyHex);
        _log.Add($"peer created '{groupName}'");

        // The claim: a Welcome the peer built, read by the app. Rescan alongside
        // the live subscription because a gift wrap can land before the
        // subscription settles, and the app is expected to survive either order.
        Assert.True(
            await WaitForAsync(async () =>
            {
                await _messages.RescanInvitesAsync();
                return (await storage.GetPendingInvitesAsync()).Any();
            }),
            $"The app never turned the peer's Welcome into an invite.\n{Log()}");

        PendingInvite invite = (await storage.GetPendingInvitesAsync()).First();
        Assert.Equal(keyPackageEventId, invite.KeyPackageEventId);

        // ── Accepting it produces a real chat ──
        Chat chat = await _messages.AcceptInviteAsync(invite.Id);
        Assert.NotNull(chat.MlsGroupId);

        string groupIdHex = Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();
        _log.Add($"joined {groupIdHex}");

        // Subscribing to the group's traffic is the head's job, not
        // AcceptInviteAsync's -- ChatListViewModel does it right after accepting --
        // so the test does what a head does. The address is the transport group id
        // the engine read off the group's signed routing component, never the MLS
        // group id.
        string transportIdHex = chat.NostrGroupId is { Length: > 0 }
            ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
            : groupIdHex;
        await _nostr.SubscribeToGroupMessagesAsync([transportIdHex]);

        var peerGroups = await peer.GroupsAsync();
        Assert.True(
            MdkCliDockerClient.ContainsGroupId(peerGroups, groupIdHex),
            $"We joined a group the peer does not have.\n{Log()}");

        // ── And the chat carries the peer's traffic ──
        // The join is only worth anything if what follows is readable: the
        // exporter secret, our leaf index and the epoch all came off the peer's
        // Welcome, and a fault in any of them surfaces here rather than above.
        string text = $"hello from the reference client {Guid.NewGuid():N}"[..40];
        var received = new List<Message>();
        using var messageSub = _messages.NewMessages.Subscribe(received.Add);

        await peer.SendMessageAsync(groupIdHex, text);

        Assert.True(
            await WaitForAsync(async () =>
                received.Any(m => m.Content == text)
                || (await storage.GetMessagesForChatAsync(chat.Id)).Any(m => m.Content == text)),
            $"The peer's message never arrived in the joined chat.\n{Log()}");
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
                // A poll that throws is a poll that has not succeeded yet: the
                // peer may still be starting, or a relay query may have raced.
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return false;
    }
}
