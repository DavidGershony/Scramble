using System.Reactive.Linq;
using System.Reactive.Subjects;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// The stage-publish-merge paths in <see cref="MessageService"/> used to roll back only on
/// <see cref="PublishUnconfirmedException"/>. Any other publish failure left the commit staged,
/// and the engine refuses to stage a second commit while one is pending — so the group took no
/// further membership or admin change, with no way for the user to clear it.
///
/// These tests pin the rollback to "any failure between staging and a confirmed merge", and pin
/// the two boundaries that matter: it must not fire after the merge succeeded, and it must not
/// bury the original failure when the rollback itself fails.
///
/// Every commit here is published by <see cref="INostrService.PublishCommitEventAsync"/>: what a
/// staging call returns is a finished kind-445 event, so the members that build one around it
/// refuse these bytes outright. The exception carried out of a failed publish is what each of
/// these tests supplies.
/// </summary>
public class StagedCommitRollbackTests : IDisposable
{
    private readonly Mock<IStorageService> _storageMock = new();
    private readonly Mock<INostrService> _nostrMock = new();
    private readonly Mock<IMlsService> _mlsMock = new();
    private readonly Subject<NostrEventReceived> _events = new();
    private readonly MessageService _sut;

    private static readonly byte[] GroupId = { 0x01, 0x02, 0x03, 0x04 };
    private const string ChatId = "chat-1";
    private const string MemberPubKey = "cc";

    private readonly User _currentUser = new()
    {
        Id = "user-1",
        PublicKeyHex = new string('a', 64),
        PrivateKeyHex = new string('b', 64),
        Npub = "npub1test",
        DisplayName = "Test User",
        CreatedAt = DateTime.UtcNow
    };

    private readonly Chat _chat = new()
    {
        Id = ChatId,
        Name = "Test Group",
        Type = ChatType.Group,
        MlsGroupId = GroupId,
        ParticipantPublicKeys = new List<string> { new string('a', 64), new string('c', 64) }
    };

    /// <summary>
    /// Tracks whether a commit is staged, the way the engine does: staging sets it, merging
    /// clears it. A mock that always answered "pending" would let the post-merge assertions
    /// pass for the wrong reason.
    /// </summary>
    private bool _commitPending;

    public StagedCommitRollbackTests()
    {
        _nostrMock.Setup(n => n.Events).Returns(_events.AsObservable());
        _nostrMock.Setup(n => n.ConnectedRelayUrls).Returns(new List<string> { "wss://relay.example.com" });

        _storageMock.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(_currentUser);
        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat>());
        _storageMock.Setup(s => s.GetChatAsync(ChatId)).ReturnsAsync(_chat);
        _storageMock.Setup(s => s.SaveChatAsync(It.IsAny<Chat>())).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.SaveMessageAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.SaveSettingAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        _mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        // This user IS an admin. The subject of these tests is rollback/replay
        // behaviour, not authority, and an empty list now denies rather than permits
        // -- MessageService.RequireLocalAdmin fails closed, because an empty read
        // means "policy unreadable", not "no policy". Mocking empty here made these
        // tests exercise a governance operation no peer would have accepted.
        _mlsMock.Setup(m => m.GetAdminPubkeys(It.IsAny<byte[]>()))
            .Returns(new List<string> { new string('a', 64) });
        _mlsMock.Setup(m => m.GetNostrGroupId(It.IsAny<byte[]>())).Returns(GroupId);

        _mlsMock.Setup(m => m.HasPendingCommit(It.IsAny<byte[]>())).Returns(() => _commitPending);
        _mlsMock.Setup(m => m.MergeStagedAsync(It.IsAny<byte[]>()))
            .Callback(() => _commitPending = false)
            .Returns(Task.CompletedTask);
        _mlsMock.Setup(m => m.ClearStagedAsync(It.IsAny<byte[]>()))
            .Callback(() => _commitPending = false)
            .Returns(Task.CompletedTask);

        _mlsMock.Setup(m => m.StageRemoveMemberAsync(It.IsAny<byte[]>(), It.IsAny<string>()))
            .Callback(() => _commitPending = true)
            .ReturnsAsync(new byte[] { 0xAA, 0xBB });
        _mlsMock.Setup(m => m.StageUpdateAdminPubkeysAsync(It.IsAny<byte[]>(), It.IsAny<List<string>>()))
            .Callback(() => _commitPending = true)
            .ReturnsAsync(new byte[] { 0xAA, 0xBB });
        _mlsMock.Setup(m => m.StageAddMemberAsync(It.IsAny<byte[]>(), It.IsAny<KeyPackage>()))
            .Callback(() => _commitPending = true)
            .ReturnsAsync(new MlsWelcome
            {
                WelcomeData = new byte[] { 0x11, 0x22 },
                CommitData = new byte[] { 0xAA, 0xBB },
                RecipientPublicKey = new string('c', 64),
                KeyPackageEventId = "kp-event-1"
            });

        _sut = new MessageService(_storageMock.Object, _nostrMock.Object, _mlsMock.Object);
    }

    public void Dispose()
    {
        _events.Dispose();
        _sut.Dispose();
    }

    private static PublishUnconfirmedException Unconfirmed() =>
        new("event-1", 445, "no relay accepted");

    private static KeyPackage PeerKeyPackage()
    {
        var kp = KeyPackage.Create(new string('c', 64), new byte[] { 0x01, 0x02 });
        kp.SlotId = new string('d', 64);
        kp.NostrEventId = "kp-event-1";
        return kp;
    }

    // ─── RemoveMember ────────────────────────────────────────────────

    [Fact]
    public async Task RemoveMember_PublishThrowsNonPublishException_ClearsStagedCommit()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(new ArgumentException("commitData is already a signed Nostr event"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.RemoveMemberAsync(ChatId, MemberPubKey));

        _mlsMock.Verify(m => m.ClearStagedAsync(GroupId), Times.Once,
            "a publish failure that is not PublishUnconfirmedException must still roll the staged commit back");
        _mlsMock.Verify(m => m.MergeStagedAsync(It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task RemoveMember_MergeThrows_ClearsStagedCommit()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ReturnsAsync("event-1");
        _mlsMock.Setup(m => m.MergeStagedAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(new InvalidOperationException("merge failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.RemoveMemberAsync(ChatId, MemberPubKey));

        _mlsMock.Verify(m => m.ClearStagedAsync(GroupId), Times.Once,
            "a merge that fails leaves the commit staged just as a failed publish does");
    }

    [Fact]
    public async Task RemoveMember_PublishUnconfirmed_StillClearsAndRethrowsUnconfirmed()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(Unconfirmed());

        await Assert.ThrowsAsync<PublishUnconfirmedException>(
            () => _sut.RemoveMemberAsync(ChatId, MemberPubKey));

        _mlsMock.Verify(m => m.ClearStagedAsync(GroupId), Times.Once);
    }

    // ─── UpdateAdminPubkeys ──────────────────────────────────────────

    [Fact]
    public async Task UpdateAdminPubkeys_PublishThrowsNonPublishException_ClearsStagedCommit()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(new ArgumentException("commitData is already a signed Nostr event"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpdateAdminPubkeysAsync(ChatId, new List<string> { new string('a', 64) }));

        _mlsMock.Verify(m => m.ClearStagedAsync(GroupId), Times.Once,
            "an admin-policy commit left staged locks the admin list permanently");
        _mlsMock.Verify(m => m.MergeStagedAsync(It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAdminPubkeys_PublishUnconfirmed_StillClearsAndRethrowsUnconfirmed()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(Unconfirmed());

        var thrown = await Assert.ThrowsAsync<PublishUnconfirmedException>(
            () => _sut.UpdateAdminPubkeysAsync(ChatId, new List<string> { new string('a', 64) }));

        Assert.Equal(445, thrown.Kind);
        _mlsMock.Verify(m => m.ClearStagedAsync(GroupId), Times.Once);
    }

    [Fact]
    public async Task UpdateAdminPubkeys_RollbackThrows_OriginalFailureStillSurfaces()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(new ArgumentException("commitData is already a signed Nostr event"));
        _mlsMock.Setup(m => m.ClearStagedAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(new NotSupportedException("staged commit API unavailable on this backend"));

        // The caller must see why the publish failed, not why the cleanup after it failed.
        var thrown = await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpdateAdminPubkeysAsync(ChatId, new List<string> { new string('a', 64) }));

        Assert.Contains("already a signed Nostr event", thrown.Message);
    }

    [Fact]
    public async Task UpdateAdminPubkeys_FailureAfterMerge_DoesNotClearTheMergedCommit()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ReturnsAsync("event-1");
        _storageMock.Setup(s => s.SaveChatAsync(It.IsAny<Chat>()))
            .ThrowsAsync(new IOException("database is locked"));

        await Assert.ThrowsAsync<IOException>(
            () => _sut.UpdateAdminPubkeysAsync(ChatId, new List<string> { new string('a', 64) }));

        _mlsMock.Verify(m => m.MergeStagedAsync(GroupId), Times.Once);
        _mlsMock.Verify(m => m.ClearStagedAsync(It.IsAny<byte[]>()), Times.Never,
            "the commit was merged and published — clearing here would misreport a succeeded epoch change");
    }

    // ─── InvitePeerToSyncGroup ───────────────────────────────────────

    [Fact]
    public async Task InvitePeerToSyncGroup_CommitPublishThrows_ClearsStagedCommit()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ThrowsAsync(new ArgumentException("commitData is already a signed Nostr event"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.InvitePeerToSyncGroupAsync(PeerKeyPackage(), ChatId));

        _mlsMock.Verify(m => m.ClearStagedAsync(GroupId), Times.Once,
            "the sync group had no rollback at all — a failed invite wedged every later device add");
    }

    [Fact]
    public async Task InvitePeerToSyncGroup_WelcomeFailsAfterMerge_DoesNotClearTheMergedCommit()
    {
        await _sut.InitializeAsync();
        _nostrMock.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
            .ReturnsAsync("commit-event-1");
        _nostrMock.Setup(n => n.PublishWelcomeAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()))
            .ThrowsAsync(Unconfirmed());

        await Assert.ThrowsAsync<PublishUnconfirmedException>(
            () => _sut.InvitePeerToSyncGroupAsync(PeerKeyPackage(), ChatId));

        _mlsMock.Verify(m => m.MergeStagedAsync(GroupId), Times.Once);
        _mlsMock.Verify(m => m.ClearStagedAsync(It.IsAny<byte[]>()), Times.Never,
            "the peer is already in the group at the new epoch; the Welcome can be resent");
    }
}
