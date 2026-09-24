using System.Reactive.Linq;
using System.Reactive.Subjects;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// The app's inbound Welcome path, driven with the kind-444 rumors a conformant
/// peer actually sends.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these exist for.</b> Until P11's flip,
/// <c>MessageService.HandleWelcomeEventAsync</c> parsed the rumor with
/// marmot-cs's <c>WelcomeEventParser</c>, which <i>requires</i> an
/// <c>["encoding","base64"]</c> tag — a tag current peers reject, so no peer
/// emits it — and its <c>catch (FormatException)</c> returns. The app therefore
/// discarded every Welcome a conformant peer ever sent: no pending invite, no
/// error, nothing on screen.
/// </para>
/// <para>
/// <b>Why nothing caught it.</b> Two suites each covered half the seam. The
/// Whitenoise interop tests do drive this path — including
/// "Whitenoise creates the group, Scramble joins" — and they skip, because that
/// peer was retired. The live <c>DarkMatterInterop</c> suite does run against a
/// real peer, and it unwraps the gift wrap with the engine's own codec and enters
/// at <c>IMlsService.ProcessWelcomeAsync</c>, below this method. Meanwhile every
/// app-to-app test passed, because our own Welcomes carried the same
/// non-conformant tag: interop failing in a mirror.
/// </para>
/// <para>
/// So the first test below is the reproduction, and it is deliberately the
/// plainest possible rumor: the two tags MIP-02 mandates and nothing else.
/// <c>InboundWelcomeInteropTests</c> is the same claim against the reference
/// client, which is what would have caught this in the first place.
/// </para>
/// </remarks>
public class InboundWelcomeTests : IDisposable
{
    private readonly Mock<IStorageService> _storageMock = new();
    private readonly Mock<INostrService> _nostrMock = new();
    private readonly Mock<IMlsService> _mlsMock = new();
    private readonly Subject<NostrEventReceived> _events = new();
    private readonly MessageService _sut;
    private readonly List<PendingInvite> _inviteStore = new();

    /// <summary>A KeyPackage event id: 32 bytes of hex, as a relay carries it.</summary>
    private const string KeyPackageEventId =
        "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private const string InviterPubKey =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    public InboundWelcomeTests()
    {
        _nostrMock.Setup(n => n.Events).Returns(_events.AsObservable());
        _nostrMock.Setup(n => n.ConnectedRelayUrls).Returns(new List<string> { "wss://relay.example.com" });
        _nostrMock.Setup(n => n.FetchUserMetadataAsync(It.IsAny<string>())).ReturnsAsync((UserMetadata?)null);

        _storageMock.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(new User
        {
            Id = "user-1",
            PublicKeyHex = new string('a', 64),
            PrivateKeyHex = new string('b', 64),
            Npub = "npub1test",
            DisplayName = "Test User",
            CreatedAt = DateTime.UtcNow
        });
        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat>());
        _storageMock.Setup(s => s.IsWelcomeEventDismissedAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>())).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.DismissWelcomeEventAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        _mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        // Key material is present: what is under test is the rumor's shape, not
        // whether this device can open the Welcome.
        _mlsMock.Setup(m => m.CanProcessWelcomeAsync(It.IsAny<byte[]>())).ReturnsAsync(true);


        // A pending-invite store that behaves like the real table rather than a stub.
        // PendingInvites has a UNIQUE index on NostrEventId and the insert is
        // INSERT OR IGNORE, so re-saving a welcome we already hold keeps the FIRST row
        // and silently discards the newly minted Id. Code that saves and then reads
        // back needs a double that does the same; an always-empty read is what let
        // "Invite not found" reach a user's phone.
        _storageMock.Setup(s => s.GetPendingInvitesAsync())
            .ReturnsAsync(() => _inviteStore.ToList());
        _storageMock.Setup(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>()))
            .Callback<PendingInvite>(i =>
            {
                if (!_inviteStore.Any(e => e.NostrEventId == i.NostrEventId))
                    _inviteStore.Add(i);
            })
            .Returns(Task.CompletedTask);

        _sut = new MessageService(_storageMock.Object, _nostrMock.Object, _mlsMock.Object);
    }

    public void Dispose()
    {
        _events.Dispose();
        _sut.Dispose();
    }

    /// <summary>A kind-444 rumor as it arrives from a gift wrap, with the given tags.</summary>
    private static NostrEventReceived Welcome(params List<string>[] tags) => new()
    {
        Kind = 444,
        EventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        PublicKey = InviterPubKey,
        Content = Convert.ToBase64String(new byte[] { 0x01, 0x02, 0x03, 0x04 }),
        CreatedAt = DateTime.UtcNow,
        Tags = tags.ToList(),
        RelayUrl = "wss://relay.example.com"
    };

    private async Task DeliverAsync(NostrEventReceived welcome)
    {
        await _sut.InitializeAsync();
        _events.OnNext(welcome);
        await Task.Delay(250);
    }

    // ------------------------------------------------------------ the repro

    [Fact]
    public async Task AWelcomeFromAConformantPeerBecomesAnInvite()
    {
        // The reproduction. Exactly the two tags MIP-02 mandates: no "encoding"
        // (peers refuse it), no "p" (the gift wrap routes), no "h" (the group id
        // is inside the Welcome). Under the old parser this produced no invite
        // and no error.
        var welcome = Welcome(
            new() { "e", KeyPackageEventId },
            new() { "relays", "wss://peer.example.com" });

        await DeliverAsync(welcome);

        _storageMock.Verify(s => s.SavePendingInviteAsync(It.Is<PendingInvite>(i =>
                i.SenderPublicKey == InviterPubKey
                && i.KeyPackageEventId == KeyPackageEventId
                && i.NostrEventId == welcome.EventId
                && i.RelayUrls.Contains("wss://peer.example.com"))),
            Times.Once,
            "a Welcome carrying exactly the two tags the spec mandates must produce an invite");
    }

    [Fact]
    public async Task TheKeyPackageBindingAndRelayHintsSurviveIntact()
    {
        // The two fields the join actually needs afterwards: the KeyPackage event
        // id, which is what finds the private material that opens the Welcome, and
        // the group's relays, which is where the conversation lives. A parser that
        // accepted the rumor but dropped either would fail later and elsewhere.
        var welcome = Welcome(
            new() { "e", KeyPackageEventId },
            new() { "relays", "wss://one.example.com", "wss://two.example.com" });

        PendingInvite? saved = null;
        _storageMock.Setup(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>()))
            .Callback((PendingInvite i) => saved = i)
            .Returns(Task.CompletedTask);

        await DeliverAsync(welcome);

        Assert.NotNull(saved);
        Assert.Equal(KeyPackageEventId, saved!.KeyPackageEventId);
        Assert.Equal(new[] { "wss://one.example.com", "wss://two.example.com" }, saved.RelayUrls);
    }

    [Fact]
    public async Task AnUnknownTagDoesNotCostTheInvite()
    {
        // The opposite mistake, and just as bad: a peer that adds a tag we do not
        // know must still be able to invite us. The kind-444 rules constrain the
        // two tags above and say nothing about others, so rejecting an unknown one
        // invents a rule -- which is how the old parser failed, from the other
        // direction. "encoding" is the interesting case because our own engine
        // stopped emitting it: a peer still sending it is not our problem.
        var welcome = Welcome(
            new() { "e", KeyPackageEventId },
            new() { "relays", "wss://peer.example.com" },
            new() { "encoding", "base64" },
            new() { "p", new string('a', 64) },
            new() { "something-new", "1" });

        await DeliverAsync(welcome);

        _storageMock.Verify(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>()), Times.Once);
    }

    // --------------------------------------------------------- fail closed

    [Fact]
    public async Task ARepeatedKeyPackageTagIsRefused()
    {
        // Explicitly a MUST NOT. Both tags are routing-significant, so "take the
        // first" lets an attacker prepend one and steer the join -- two peers then
        // disagree about what one signed rumor says.
        await DeliverAsync(Welcome(
            new() { "e", KeyPackageEventId },
            new() { "e", "1f".PadRight(64, '0') },
            new() { "relays", "wss://peer.example.com" }));

        _storageMock.Verify(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>()), Times.Never);
    }

    [Fact]
    public async Task ARepeatedRelaysTagIsRefused()
    {
        await DeliverAsync(Welcome(
            new() { "e", KeyPackageEventId },
            new() { "relays", "wss://one.example.com" },
            new() { "relays", "wss://two.example.com" }));

        _storageMock.Verify(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>()), Times.Never);
    }

    [Fact]
    public async Task AKeyPackageIdThatIsNotAnEventIdIsRefused()
    {
        // 32 bytes of hex or nothing: an id of another shape cannot name an event,
        // so a Welcome carrying one cannot be matched to the material that opens it.
        await DeliverAsync(Welcome(
            new() { "e", "kp-event-id" },
            new() { "relays", "wss://peer.example.com" }));

        _storageMock.Verify(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>()), Times.Never);
    }

    [Fact]
    public async Task AWelcomeWithNoRelaysIsRefused()
    {
        // MIP-02 mandates the tag, and a group whose relays we do not know is a
        // conversation we cannot reach. Refusing says so at the invite rather than
        // leaving an accepted chat that never receives anything.
        await DeliverAsync(Welcome(new List<string> { "e", KeyPackageEventId }));

        _storageMock.Verify(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>()), Times.Never);
    }

    // ------------------------------------------ re-delivery keeps one identity

    /// <summary>
    /// The same welcome arriving twice must surface the invite that is actually in
    /// storage, not a second one with an id that exists nowhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported from a device on 2026-09-24: tapping Accept logged
    /// <c>ArgumentException: Invite not found</c> from
    /// <c>MessageService.AcceptInviteAsync</c>, one millisecond after the tap.
    /// </para>
    /// <para>
    /// <b>The mechanism.</b> Every inbound welcome mints a fresh
    /// <c>Guid.NewGuid()</c> for the invite. <c>PendingInvites</c> has a UNIQUE index
    /// on <c>NostrEventId</c> and the insert is <c>INSERT OR IGNORE</c>, so the second
    /// delivery of a welcome we already hold writes nothing and the row keeps its
    /// ORIGINAL id — while the code went on to publish the in-memory object, whose id
    /// had just been discarded. <c>AcceptInviteAsync</c> then looked that id up in
    /// storage and found nothing.
    /// </para>
    /// <para>
    /// Re-delivery is not exotic: the same welcome legitimately arrives from every
    /// relay the inviter published it to, which is why
    /// <c>ChatListViewModel.OnNewInvite</c> deduplicates on <c>NostrEventId</c>. That
    /// dedup only helps while the list still holds the event, so the failure surfaced
    /// after a refresh or on a cold start.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSameWelcomeTwiceSurfacesTheStoredInviteNotASecondId()
    {
        var welcome = Welcome(
            new() { "e", KeyPackageEventId },
            new() { "relays", "wss://peer.example.com" });

        // Both copies must be in flight at once. A sequential re-delivery is caught by
        // the duplicate check on NostrEventId and returns before minting anything, so
        // it cannot reach this bug -- the window is the await on the sender's profile,
        // which sits BETWEEN that check and the insert. Two relays delivering the same
        // welcome land in exactly that gap: both see no stored row, both mint an id,
        // the first insert wins and the second is silently ignored by INSERT OR IGNORE.
        var gate = new TaskCompletionSource();
        _nostrMock.Setup(n => n.FetchUserMetadataAsync(It.IsAny<string>()))
            .Returns(async () => { await gate.Task; return (UserMetadata?)null; });

        var surfaced = new List<PendingInvite>();
        using var sub = _sut.NewInvites.Subscribe(surfaced.Add);

        await _sut.InitializeAsync();
        _events.OnNext(welcome);
        _events.OnNext(welcome);
        await Task.Delay(100);       // both past the duplicate check, both waiting
        gate.SetResult();            // release them into the insert together
        await Task.Delay(400);

        Assert.NotEmpty(surfaced);

        // Whatever reached the UI must be acceptable, which means present in storage.
        var stored = (await _storageMock.Object.GetPendingInvitesAsync()).ToList();
        foreach (var invite in surfaced)
        {
            Assert.Contains(stored, i => i.Id == invite.Id);
        }

        // And re-delivery must not invent a second identity for one welcome.
        Assert.Single(stored);
        Assert.Single(surfaced.Select(i => i.Id).Distinct());
    }
}
