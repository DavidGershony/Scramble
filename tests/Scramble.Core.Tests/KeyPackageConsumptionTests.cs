using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;
namespace Scramble.Core.Tests;

/// <summary>
/// Tests for Fix 2 (mark KeyPackage as used after accepting invite) and
/// Fix 3 (auto-publish new KeyPackage when last one is consumed).
/// </summary>
public class KeyPackageConsumptionTests
{
    private readonly ITestOutputHelper _output;

    public KeyPackageConsumptionTests(ITestOutputHelper output) => _output = output;

    private static PendingInvite CreateInvite(string kpEventId = "kp_event_aabb0011")
    {
        return new PendingInvite
        {
            Id = Guid.NewGuid().ToString(),
            SenderPublicKey = "deadbeef" + new string('0', 56),
            NostrEventId = "welcome_" + Guid.NewGuid().ToString("N"),
            WelcomeData = new byte[] { 0x01, 0x02 },
            KeyPackageEventId = kpEventId,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private (Mock<IStorageService> storage, Mock<INostrService> nostr, Mock<IMlsService> mls) CreateMocks(
        PendingInvite invite, List<KeyPackage>? ownKeyPackages = null)
    {
        var storageMock = new Mock<IStorageService>();
        storageMock.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        storageMock.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(new User
        {
            Id = "user1",
            PublicKeyHex = "e9b03d7d" + new string('0', 56),
            PrivateKeyHex = new string('0', 64),
            IsCurrentUser = true
        });
        storageMock.Setup(s => s.GetPendingInvitesAsync())
            .ReturnsAsync(new List<PendingInvite> { invite });
        storageMock.Setup(s => s.SaveChatAsync(It.IsAny<Chat>())).Returns(Task.CompletedTask);
        storageMock.Setup(s => s.DismissWelcomeEventAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        storageMock.Setup(s => s.DeletePendingInviteAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat>());

        // Return own KeyPackages for the auto-publish check
        var kps = ownKeyPackages ?? new List<KeyPackage>();
        storageMock.Setup(s => s.GetUnusedKeyPackagesAsync(It.IsAny<string>()))
            .ReturnsAsync(kps);
        storageMock.Setup(s => s.MarkKeyPackageUsedAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        storageMock.Setup(s => s.GetKeyPackageByNostrEventIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string eventId) => kps.FirstOrDefault(k => k.NostrEventId == eventId));
        storageMock.Setup(s => s.SaveKeyPackageAsync(It.IsAny<KeyPackage>())).Returns(Task.CompletedTask);

        var nostrMock = new Mock<INostrService>();
        nostrMock.Setup(n => n.Events).Returns(new System.Reactive.Subjects.Subject<NostrEventReceived>());
        nostrMock.Setup(n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>>()))
            .ReturnsAsync("new_kp_event_id_" + Guid.NewGuid().ToString("N")[..16]);

        var groupId = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44 };
        var mlsMock = new Mock<IMlsService>();
        mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mlsMock.Setup(m => m.ProcessWelcomeAsync(It.IsAny<byte[]>(), It.IsAny<string>()))
            .ReturnsAsync(new MlsGroupInfo
            {
                GroupId = groupId,
                GroupName = "Test Group",
                Epoch = 1,
                MemberPublicKeys = new List<string> { "e9b03d7d" + new string('0', 56) }
            });
        mlsMock.Setup(m => m.GetNostrGroupId(It.IsAny<byte[]>()))
            .Returns(new byte[] { 0xDD, 0xEE, 0xFF, 0x00, 0x11, 0x22, 0x33, 0x44 });
        mlsMock.Setup(m => m.GetAdminPubkeys(It.IsAny<byte[]>()))
            .Returns(new List<string>());
        mlsMock.Setup(m => m.GenerateKeyPackageAsync())
            .ReturnsAsync(KeyPackage.Create("e9b03d7d" + new string('0', 56), new byte[] { 0x01 }));

        return (storageMock, nostrMock, mlsMock);
    }

    /// <summary>
    /// Fix 2: After accepting an invite, the consumed KeyPackage should be marked as used
    /// in the database so future senders don't reuse it.
    /// </summary>
    [Fact]
    public async Task AcceptInvite_MarksConsumedKeyPackageAsUsed()
    {
        // Arrange
        var kpEventId = "kp_event_" + new string('a', 55);
        var invite = CreateInvite(kpEventId);
        var existingKp = new KeyPackage
        {
            Id = "kp_local_1",
            OwnerPublicKey = "e9b03d7d" + new string('0', 56),
            NostrEventId = kpEventId,
            Data = new byte[] { 0x01 },
            IsUsed = false,
            Status = KeyPackageStatus.Active,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };

        var (storageMock, nostrMock, mlsMock) = CreateMocks(invite, new List<KeyPackage> { existingKp });

        var messageService = new MessageService(storageMock.Object, nostrMock.Object, mlsMock.Object);
        await messageService.InitializeAsync();

        // Act
        await messageService.AcceptInviteAsync(invite.Id);

        // Assert: the consumed KP should be marked as used
        storageMock.Verify(s => s.MarkKeyPackageUsedAsync("kp_local_1"), Times.Once,
            "Fix 2: AcceptInviteAsync should mark the consumed KeyPackage as used in the DB");
    }

    /// <summary>
    /// Fix 2: If the invite has no KeyPackageEventId, marking should be skipped gracefully.
    /// </summary>
    [Fact]
    public async Task AcceptInvite_NoKeyPackageEventId_SkipsMarking()
    {
        var invite = CreateInvite();
        invite.KeyPackageEventId = null;

        var (storageMock, nostrMock, mlsMock) = CreateMocks(invite);

        var messageService = new MessageService(storageMock.Object, nostrMock.Object, mlsMock.Object);
        await messageService.InitializeAsync();

        // Act — should not throw
        await messageService.AcceptInviteAsync(invite.Id);

        // Assert: no attempt to mark KP as used
        storageMock.Verify(s => s.MarkKeyPackageUsedAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Fix 3: When the last unused KeyPackage is consumed, auto-publish a new one.
    /// </summary>
    [Fact]
    public async Task AcceptInvite_LastKeyPackageConsumed_AutoPublishesNewOne()
    {
        var kpEventId = "kp_event_" + new string('b', 55);
        var invite = CreateInvite(kpEventId);
        var lastKp = new KeyPackage
        {
            Id = "kp_local_last",
            OwnerPublicKey = "e9b03d7d" + new string('0', 56),
            NostrEventId = kpEventId,
            Data = new byte[] { 0x01 },
            IsUsed = false,
            Status = KeyPackageStatus.Active,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };

        var (storageMock, nostrMock, mlsMock) = CreateMocks(invite, new List<KeyPackage> { lastKp });

        // After marking used, simulate 0 unused KPs remaining
        var markCalled = false;
        storageMock.Setup(s => s.MarkKeyPackageUsedAsync(It.IsAny<string>()))
            .Callback(() => markCalled = true)
            .Returns(Task.CompletedTask);
        storageMock.Setup(s => s.GetUnusedKeyPackagesAsync(It.IsAny<string>()))
            .ReturnsAsync(() => markCalled ? new List<KeyPackage>() : new List<KeyPackage> { lastKp });

        var messageService = new MessageService(storageMock.Object, nostrMock.Object, mlsMock.Object);
        await messageService.InitializeAsync();

        // Act
        await messageService.AcceptInviteAsync(invite.Id);

        // Allow background auto-publish to complete
        await Task.Delay(500);

        // Assert: a new KeyPackage should be generated and published
        mlsMock.Verify(m => m.GenerateKeyPackageAsync(), Times.Once,
            "Fix 3: Should auto-generate a new KeyPackage when the last one is consumed");
        nostrMock.Verify(n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>>()), Times.Once,
            "Fix 3: Should auto-publish the new KeyPackage to relays");
    }

    /// <summary>
    /// MIP-00 §"Rotating KeyPackages": proactive rotation triggered by the hourly timer.
    /// When no valid (unexpired, unused) KeyPackages remain, AutoPublishKeyPackageIfNeededAsync
    /// must generate and publish a fresh one — even with no Welcome received.
    /// This covers devices that have not received a Welcome in >30 days (KP expired by time).
    /// </summary>
    [Fact]
    public async Task AutoPublishKeyPackageIfNeeded_AllKpsExpired_PublishesNewOne()
    {
        var storageMock = new Mock<IStorageService>();
        storageMock.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        storageMock.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(new User
        {
            Id = "user_rotation",
            PublicKeyHex = "e9b03d7d" + new string('0', 56),
            PrivateKeyHex = new string('0', 64),
            IsCurrentUser = true
        });
        storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat>());
        storageMock.Setup(s => s.GetPendingInvitesAsync()).ReturnsAsync(new List<PendingInvite>());
        // Empty: all KPs are past ExpiresAt so GetUnusedKeyPackagesAsync returns nothing
        storageMock.Setup(s => s.GetUnusedKeyPackagesAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<KeyPackage>());
        storageMock.Setup(s => s.SaveKeyPackageAsync(It.IsAny<KeyPackage>())).Returns(Task.CompletedTask);

        var nostrMock = new Mock<INostrService>();
        nostrMock.Setup(n => n.Events)
            .Returns(new System.Reactive.Subjects.Subject<NostrEventReceived>());
        nostrMock.Setup(n => n.ConnectedRelayUrls).Returns(new List<string>());
        nostrMock.Setup(n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>>()))
            .ReturnsAsync("rotated_kp_" + Guid.NewGuid().ToString("N")[..16]);

        var mlsMock = new Mock<IMlsService>();
        mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        mlsMock.Setup(m => m.GenerateKeyPackageAsync())
            .ReturnsAsync(KeyPackage.Create("e9b03d7d" + new string('0', 56), new byte[] { 0x01 }));

        var messageService = new MessageService(storageMock.Object, nostrMock.Object, mlsMock.Object);
        await messageService.InitializeAsync();

        // Act: same code path as the hourly _kpRotationTimer
        await messageService.AutoPublishKeyPackageIfNeededAsync();

        mlsMock.Verify(m => m.GenerateKeyPackageAsync(), Times.Once,
            "Proactive rotation: should generate a new KP when all existing ones are expired");
        nostrMock.Verify(
            n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>>()), Times.Once,
            "Proactive rotation: should publish the new KP to relays");
    }

    /// <summary>
    /// Fix 3: When there are still unused KeyPackages remaining, do NOT auto-publish.
    /// </summary>
    [Fact]
    public async Task AcceptInvite_MoreKeyPackagesRemaining_DoesNotAutoPublish()
    {
        var kpEventId = "kp_event_" + new string('c', 55);
        var invite = CreateInvite(kpEventId);
        var consumedKp = new KeyPackage
        {
            Id = "kp_local_1",
            OwnerPublicKey = "e9b03d7d" + new string('0', 56),
            NostrEventId = kpEventId,
            Data = new byte[] { 0x01 },
            IsUsed = false,
            Status = KeyPackageStatus.Active,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        var remainingKp = new KeyPackage
        {
            Id = "kp_local_2",
            OwnerPublicKey = "e9b03d7d" + new string('0', 56),
            NostrEventId = "other_kp_event",
            Data = new byte[] { 0x02 },
            IsUsed = false,
            Status = KeyPackageStatus.Active,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };

        var (storageMock, nostrMock, mlsMock) = CreateMocks(invite, new List<KeyPackage> { consumedKp, remainingKp });

        // After marking used, still 1 remaining
        storageMock.Setup(s => s.GetUnusedKeyPackagesAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<KeyPackage> { remainingKp });

        var messageService = new MessageService(storageMock.Object, nostrMock.Object, mlsMock.Object);
        await messageService.InitializeAsync();

        // Act
        await messageService.AcceptInviteAsync(invite.Id);
        await Task.Delay(200);

        // Assert: should NOT auto-publish since we still have a KP
        mlsMock.Verify(m => m.GenerateKeyPackageAsync(), Times.Never);
        nostrMock.Verify(n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>>()), Times.Never);
    }
}
