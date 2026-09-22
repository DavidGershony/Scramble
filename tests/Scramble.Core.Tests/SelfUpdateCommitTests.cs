using System.Reactive.Linq;
using System.Reactive.Subjects;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// MIP-02's self-update, which is the one commit path whose meaning changed at
/// P11's flip rather than only its plumbing.
/// </summary>
/// <remarks>
/// <para>
/// <c>UpdateKeysAsync</c> used to apply the rotation itself, so this path
/// published and stopped. The Dark Matter engine stages it like every other
/// commit — so publishing without merging leaves a staged commit behind, and the
/// engine refuses to stage a second while one is pending. The first key rotation
/// would then wedge the group: no add, remove or admin change could be staged
/// again, for a reason nothing in the UI could explain or clear.
/// </para>
/// <para>
/// The scheduler in front of this waits five minutes and swallows what it
/// catches, so these drive <c>PerformSelfUpdateAsync</c> directly.
/// </para>
/// </remarks>
public class SelfUpdateCommitTests : IDisposable
{
    private readonly Mock<IStorageService> _storageMock = new();
    private readonly Mock<INostrService> _nostrMock = new();
    private readonly Mock<IMlsService> _mlsMock = new();
    private readonly Subject<NostrEventReceived> _events = new();
    private readonly MessageService _sut;

    private static readonly byte[] GroupId = { 0x01, 0x02, 0x03, 0x04 };

    /// <summary>
    /// Whether a commit is staged, tracked the way the engine tracks it: staging
    /// sets it, merging or clearing resets it. A mock that always answered "yes"
    /// would let the post-merge assertion pass for the wrong reason.
    /// </summary>
    private bool _commitPending;

    public SelfUpdateCommitTests()
    {
        _nostrMock.Setup(n => n.Events).Returns(_events.AsObservable());
        _nostrMock.Setup(n => n.ConnectedRelayUrls).Returns(new List<string> { "wss://relay.example.com" });

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

        _mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _mlsMock.Setup(m => m.HasPendingCommit(It.IsAny<byte[]>())).Returns(() => _commitPending);
        _mlsMock.Setup(m => m.UpdateKeysAsync(It.IsAny<byte[]>()))
            .Callback(() => _commitPending = true)
            .ReturnsAsync(new byte[] { 0xAA, 0xBB });
        _mlsMock.Setup(m => m.MergeStagedAsync(It.IsAny<byte[]>()))
            .Callback(() => _commitPending = false)
            .Returns(Task.CompletedTask);
        _mlsMock.Setup(m => m.ClearStagedAsync(It.IsAny<byte[]>()))
            .Callback(() => _commitPending = false)
            .Returns(Task.CompletedTask);

        _sut = new MessageService(_storageMock.Object, _nostrMock.Object, _mlsMock.Object);
    }

    public void Dispose()
    {
        _events.Dispose();
        _sut.Dispose();
    }

    [Fact]
    public async Task TheRotationIsMergedOnceTheRelayHasIt()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ReturnsAsync("commit-event-1");

        await _sut.PerformSelfUpdateAsync(GroupId);

        _mlsMock.Verify(m => m.MergeStagedAsync(GroupId), Times.Once,
            "an unmerged rotation leaves a staged commit that blocks every later commit");
        _mlsMock.Verify(m => m.ClearStagedAsync(It.IsAny<byte[]>()), Times.Never);
        Assert.False(_commitPending);
    }

    [Fact]
    public async Task TheRotationIsPublishedBeforeItIsApplied()
    {
        await _sut.InitializeAsync();
        var mergedBeforePublishReturned = false;
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .Callback(() => mergedBeforePublishReturned = !_commitPending)
            .ReturnsAsync("commit-event-1");

        await _sut.PerformSelfUpdateAsync(GroupId);

        // Whichever step is unrecoverable goes second. A rotation applied before
        // its publish puts this member in an epoch nobody else can reach, and MLS
        // will not let a member re-process a commit it authored.
        Assert.False(mergedBeforePublishReturned);
    }

    [Fact]
    public async Task AnUnconfirmedRotationIsRolledBackAndRethrown()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(new PublishUnconfirmedException("event-1", 445, "no relay accepted"));

        await Assert.ThrowsAsync<PublishUnconfirmedException>(
            () => _sut.PerformSelfUpdateAsync(GroupId));

        _mlsMock.Verify(m => m.ClearStagedAsync(GroupId), Times.Once,
            "the scheduler retries this, and a retry cannot stage while the last attempt is pending");
        _mlsMock.Verify(m => m.MergeStagedAsync(It.IsAny<byte[]>()), Times.Never);
        Assert.False(_commitPending);
    }

    [Fact]
    public async Task AFailureThatIsNotAMissingRelayOkIsRolledBackToo()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(new InvalidOperationException("No connected relays available."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.PerformSelfUpdateAsync(GroupId));

        _mlsMock.Verify(m => m.ClearStagedAsync(GroupId), Times.Once);
    }

    [Fact]
    public async Task AFailureAfterTheMergeDoesNotClearTheMergedCommit()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ReturnsAsync("commit-event-1");
        _mlsMock.Setup(m => m.MergeStagedAsync(It.IsAny<byte[]>()))
            .Callback(() => _commitPending = false)
            .ThrowsAsync(new IOException("database is locked"));

        await Assert.ThrowsAsync<IOException>(() => _sut.PerformSelfUpdateAsync(GroupId));

        // The rollback is gated on a commit still being pending, so a failure
        // after the merge succeeded clears nothing: the epoch change is real and
        // the group has the commit.
        _mlsMock.Verify(m => m.ClearStagedAsync(It.IsAny<byte[]>()), Times.Never);
    }
}
