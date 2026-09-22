using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;
namespace Scramble.Core.Tests;

/// <summary>
/// Tests for the stuck-invite fix: AcceptInviteAsync auto-dismisses when the targeted
/// KeyPackage has already been consumed (single-use per RFC 9420) instead of leaving
/// the invite stuck in pending forever.
/// </summary>
public class WelcomeInviteFixTests
{
    private readonly ITestOutputHelper _output;

    public WelcomeInviteFixTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AcceptInvite_KeyPackageMismatch_AutoDismissesInvite()
    {
        // Arrange: create a pending invite that targets a KeyPackage we don't have
        var inviteId = Guid.NewGuid().ToString();
        var nostrEventId = "aabbccdd11223344aabbccdd11223344aabbccdd11223344aabbccdd11223344";
        var invite = new PendingInvite
        {
            Id = inviteId,
            SenderPublicKey = "deadbeef" + new string('0', 56),
            NostrEventId = nostrEventId,
            WelcomeData = new byte[] { 0x00, 0x01 }, // dummy
            ReceivedAt = DateTime.UtcNow
        };

        var storageMock = new Mock<IStorageService>();
        storageMock.Setup(s => s.GetPendingInvitesAsync())
            .ReturnsAsync(new List<PendingInvite> { invite });
        storageMock.Setup(s => s.DismissWelcomeEventAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        storageMock.Setup(s => s.DeletePendingInviteAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var mlsMock = new Mock<IMlsService>();
        mlsMock.Setup(m => m.ProcessWelcomeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException(
                "None of the 1 stored KeyPackages match this Welcome. The private key material for the targeted KeyPackage may have been lost."));

        var nostrMock = new Mock<INostrService>();

        var messageService = new MessageService(storageMock.Object, nostrMock.Object, mlsMock.Object);

        // Act & Assert: should throw but also auto-dismiss
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => messageService.AcceptInviteAsync(inviteId));

        _output.WriteLine($"Exception: {ex.Message}");
        Assert.Contains("dismissed", ex.Message);

        // Verify the invite was dismissed and deleted
        storageMock.Verify(s => s.DismissWelcomeEventAsync(nostrEventId), Times.Once);
        storageMock.Verify(s => s.DeletePendingInviteAsync(inviteId), Times.Once);
    }

    [Fact]
    public async Task AcceptInvite_NonKeyPackageError_DoesNotAutoDismiss()
    {
        // Arrange: create a pending invite that fails for a different reason
        var inviteId = Guid.NewGuid().ToString();
        var nostrEventId = "1122334455667788112233445566778811223344556677881122334455667788";
        var invite = new PendingInvite
        {
            Id = inviteId,
            SenderPublicKey = "deadbeef" + new string('0', 56),
            NostrEventId = nostrEventId,
            WelcomeData = new byte[] { 0x00, 0x01 },
            ReceivedAt = DateTime.UtcNow
        };

        var storageMock = new Mock<IStorageService>();
        storageMock.Setup(s => s.GetPendingInvitesAsync())
            .ReturnsAsync(new List<PendingInvite> { invite });

        var mlsMock = new Mock<IMlsService>();
        mlsMock.Setup(m => m.ProcessWelcomeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("Some other MLS error"));

        var nostrMock = new Mock<INostrService>();

        var messageService = new MessageService(storageMock.Object, nostrMock.Object, mlsMock.Object);

        // Act & Assert: should throw the original error without auto-dismissing
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => messageService.AcceptInviteAsync(inviteId));

        Assert.Equal("Some other MLS error", ex.Message);

        // Verify the invite was NOT dismissed
        storageMock.Verify(s => s.DismissWelcomeEventAsync(It.IsAny<string>()), Times.Never);
        storageMock.Verify(s => s.DeletePendingInviteAsync(It.IsAny<string>()), Times.Never);
    }

}
