using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;
namespace Scramble.Core.Tests;

/// <summary>
/// Tests for the skipped-invite notification feature.
/// When a kind-444 Welcome event arrives and we have no key material for it
/// (e.g. fresh install / new login) the event should be dismissed immediately,
/// the skipped count incremented, and the SkippedInvites observable should fire.
/// No PendingInvite should be saved.
/// </summary>
public class SkippedInviteTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<IStorageService> _storageMock;
    private readonly Mock<INostrService> _nostrMock;
    private readonly Mock<IMlsService> _mlsMock;
    private readonly Subject<NostrEventReceived> _eventsSubject;
    private readonly MessageService _sut;
    private readonly List<PendingInvite> _inviteStore = new();

    private readonly User _currentUser = new()
    {
        Id = "user-1",
        PublicKeyHex = "aa".PadLeft(64, 'a'),
        PrivateKeyHex = "bb".PadLeft(64, 'b'),
        Npub = "npub1test",
        DisplayName = "Test User",
        CreatedAt = DateTime.UtcNow
    };

    public SkippedInviteTests(ITestOutputHelper output)
    {
        _output = output;
        _storageMock = new Mock<IStorageService>();
        _nostrMock = new Mock<INostrService>();
        _mlsMock = new Mock<IMlsService>();
        _eventsSubject = new Subject<NostrEventReceived>();

        _nostrMock.Setup(n => n.Events).Returns(_eventsSubject.AsObservable());
        _nostrMock.Setup(n => n.ConnectedRelayUrls).Returns(new List<string> { "wss://relay.example.com" });

        _storageMock.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(_currentUser);
        _storageMock.Setup(s => s.GetArchivedChatsAsync()).ReturnsAsync(Enumerable.Empty<Chat>());
        _storageMock.Setup(s => s.DismissWelcomeEventAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.IncrementSkippedInviteCountAsync()).Returns(Task.CompletedTask);

        _mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _mlsMock.Setup(m => m.GetAdminPubkeys(It.IsAny<byte[]>())).Returns(new List<string>());


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
        _eventsSubject.Dispose();
        _sut.Dispose();
    }

    private async Task InitializeServiceAsync()
    {
        await _sut.InitializeAsync();
    }

    [Fact]
    public async Task HandleWelcomeEvent_NoKeyMaterial_DismissesAndIncrementsCount()
    {
        // Arrange
        await InitializeServiceAsync();

        var eventId = "no-key-welcome".PadLeft(64, '0');
        var senderPubKey = "cc".PadLeft(64, 'c');
        var welcomeData = new byte[] { 0x01, 0x02 };

        _storageMock.Setup(s => s.IsWelcomeEventDismissedAsync(eventId)).ReturnsAsync(false);
        _mlsMock.Setup(m => m.CanProcessWelcomeAsync(It.IsAny<byte[]>())).ReturnsAsync(false);

        var invites = new List<PendingInvite>();
        var skipped = new List<Unit>();
        using var inviteSub = _sut.NewInvites.Subscribe(i => invites.Add(i));
        using var skippedSub = _sut.SkippedInvites.Subscribe(u => skipped.Add(u));

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 444,
            EventId = eventId,
            PublicKey = senderPubKey,
            Content = Convert.ToBase64String(welcomeData),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", "abcdef1234" } }
        });

        await Task.Delay(200);

        // Assert: dismissed and count incremented — no invite saved
        _storageMock.Verify(s => s.DismissWelcomeEventAsync(eventId), Times.Once);
        _storageMock.Verify(s => s.IncrementSkippedInviteCountAsync(), Times.Once);
        _storageMock.Verify(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>()), Times.Never);

        Assert.Empty(invites);
        Assert.Single(skipped);

        _output.WriteLine("Skipped invite was correctly dismissed and count incremented");
    }

    [Fact]
    public async Task HandleWelcomeEvent_KeyMaterialAvailable_SavesPendingInviteNormally()
    {
        // Arrange
        await InitializeServiceAsync();

        var eventId = "has-key-welcome".PadLeft(64, '0');
        var senderPubKey = "dd".PadLeft(64, 'd');
        var welcomeData = new byte[] { 0x01, 0x02 };

        _storageMock.Setup(s => s.IsWelcomeEventDismissedAsync(eventId)).ReturnsAsync(false);
        _nostrMock.Setup(n => n.FetchUserMetadataAsync(senderPubKey)).ReturnsAsync((UserMetadata?)null);
        _mlsMock.Setup(m => m.CanProcessWelcomeAsync(It.IsAny<byte[]>())).ReturnsAsync(true);

        var invites = new List<PendingInvite>();
        var skipped = new List<Unit>();
        using var inviteSub = _sut.NewInvites.Subscribe(i => invites.Add(i));
        using var skippedSub = _sut.SkippedInvites.Subscribe(u => skipped.Add(u));

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 444,
            EventId = eventId,
            PublicKey = senderPubKey,
            Content = Convert.ToBase64String(welcomeData),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "h", "abcdef1234" },
                new() { "e", "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1" },
                new() { "relays", "wss://test.relay" }
            }
        });

        await Task.Delay(200);

        // Assert: invite saved, nothing skipped
        _storageMock.Verify(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>()), Times.Once);
        _storageMock.Verify(s => s.DismissWelcomeEventAsync(It.IsAny<string>()), Times.Never);
        _storageMock.Verify(s => s.IncrementSkippedInviteCountAsync(), Times.Never);

        Assert.Single(invites);
        Assert.Empty(skipped);

        _output.WriteLine("Welcome with key material correctly saved as pending invite");
    }

    [Fact]
    public async Task HandleWelcomeEvent_CanProcessThrows_TreatsAsProcessable()
    {
        // Arrange: CanProcessWelcomeAsync throws — should fall through and save the invite
        await InitializeServiceAsync();

        var eventId = "check-throws-welcome".PadLeft(64, '0');
        var senderPubKey = "ee".PadLeft(64, 'e');

        _storageMock.Setup(s => s.IsWelcomeEventDismissedAsync(eventId)).ReturnsAsync(false);
        _nostrMock.Setup(n => n.FetchUserMetadataAsync(senderPubKey)).ReturnsAsync((UserMetadata?)null);
        _mlsMock.Setup(m => m.CanProcessWelcomeAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(new InvalidOperationException("MLS service not initialized"));

        var invites = new List<PendingInvite>();
        var skipped = new List<Unit>();
        using var inviteSub = _sut.NewInvites.Subscribe(i => invites.Add(i));
        using var skippedSub = _sut.SkippedInvites.Subscribe(u => skipped.Add(u));

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 444,
            EventId = eventId,
            PublicKey = senderPubKey,
            Content = Convert.ToBase64String(new byte[] { 0x01 }),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "h", "abcdef1234" },
                new() { "e", "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1" },
                new() { "relays", "wss://test.relay" }
            }
        });

        await Task.Delay(200);

        // Assert: treated as processable — invite saved, nothing skipped
        _storageMock.Verify(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>()), Times.Once);
        _storageMock.Verify(s => s.IncrementSkippedInviteCountAsync(), Times.Never);

        Assert.Single(invites);
        Assert.Empty(skipped);

        _output.WriteLine("Exception in CanProcessWelcomeAsync correctly treated as processable (fail-open)");
    }

    [Fact]
    public async Task HandleWelcomeEvent_MultipleNoKeyWelcomes_IncrementsCountEachTime()
    {
        // Arrange
        await InitializeServiceAsync();

        _mlsMock.Setup(m => m.CanProcessWelcomeAsync(It.IsAny<byte[]>())).ReturnsAsync(false);

        for (var i = 1; i <= 3; i++)
        {
            var eventId = $"no-key-{i}".PadLeft(64, '0');
            _storageMock.Setup(s => s.IsWelcomeEventDismissedAsync(eventId)).ReturnsAsync(false);
            _storageMock.Setup(s => s.GetPendingInvitesAsync()).ReturnsAsync(new List<PendingInvite>());
        }

        var skipped = new List<Unit>();
        using var skippedSub = _sut.SkippedInvites.Subscribe(u => skipped.Add(u));

        // Act: fire 3 no-key welcomes
        for (var i = 1; i <= 3; i++)
        {
            _eventsSubject.OnNext(new NostrEventReceived
            {
                Kind = 444,
                EventId = $"no-key-{i}".PadLeft(64, '0'),
                PublicKey = "ff".PadLeft(64, 'f'),
                Content = Convert.ToBase64String(new byte[] { 0x01 }),
                CreatedAt = DateTime.UtcNow,
                Tags = new List<List<string>>()
            });
        }

        await Task.Delay(400);

        // Assert: count incremented 3 times, SkippedInvites fired 3 times
        _storageMock.Verify(s => s.IncrementSkippedInviteCountAsync(), Times.Exactly(3));
        Assert.Equal(3, skipped.Count);

        _output.WriteLine("Multiple no-key welcomes each increment the count");
    }
}
