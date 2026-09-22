using System.Reactive.Linq;
using System.Reactive.Subjects;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Marmot;
using Scramble.Marmot.Ingest;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// Which inbound refusals reach the user, and which are the client working.
/// </summary>
/// <remarks>
/// <para>
/// <b>The regression these exist for.</b> Before P11 step 3 the app recognised
/// the expected "commit from before I joined" case by substring-matching
/// marmot-cs's wording — <c>"UnprocessableResult"</c> and <c>"epoch"</c>. The
/// Dark Matter engine words its refusals differently, so after the flip that
/// predicate could never match, and every expected refusal a healthy client
/// produces — duplicates, own echoes, pre-join commits, messages held for
/// replay, a branch convergence did not select — fell through to
/// <see cref="IMessageService.DecryptionErrors"/>. What the user then sees is
/// <c>ChatListViewModel</c>'s <i>"Failed to decrypt message… Group may need
/// reset"</i>, advising a reset of a group that is entirely healthy.
/// </para>
/// <para>
/// <b>Both directions are pinned deliberately.</b> A test that only proves the
/// expected refusals are quiet would pass just as well if the service swallowed
/// everything, which is the worse bug of the two: a refusal wrongly hidden is
/// not investigable. So each quiet case is paired against a refusal that must
/// still be surfaced.
/// </para>
/// </remarks>
public class InboundRefusalClassificationTests : IDisposable
{
    private readonly Mock<IStorageService> _storageMock = new();
    private readonly Mock<INostrService> _nostrMock = new();
    private readonly Mock<IMlsService> _mlsMock = new();
    private readonly Subject<NostrEventReceived> _events = new();
    private readonly MessageService _sut;
    private readonly Chat _chat;

    private static readonly byte[] GroupIdBytes = { 0x01, 0x02, 0x03 };
    private static readonly string GroupIdHex = Convert.ToHexString(GroupIdBytes).ToLowerInvariant();
    private const string SenderPubKey = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static readonly GroupId Group = new(GroupIdBytes);
    private static readonly EpochId Epoch = new(7);

    public InboundRefusalClassificationTests()
    {
        _chat = new Chat
        {
            Id = "chat-1",
            Name = "Group",
            Type = ChatType.Group,
            MlsGroupId = GroupIdBytes,
            MlsEpoch = 7,
            ParticipantPublicKeys = new List<string> { SenderPubKey }
        };

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
        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { _chat });
        _storageMock.Setup(s => s.GetChatByGroupIdAsync(It.IsAny<string>())).ReturnsAsync(_chat);
        _storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.SaveMessageAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.SaveChatAsync(It.IsAny<Chat>())).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.GetUsersByPublicKeysAsync(It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(new Dictionary<string, User>());

        _mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _mlsMock.Setup(m => m.GetAdminPubkeys(It.IsAny<byte[]>())).Returns(new List<string>());

        _sut = new MessageService(_storageMock.Object, _nostrMock.Object, _mlsMock.Object);
    }

    public void Dispose()
    {
        _events.Dispose();
        _sut.Dispose();
    }

    /// <summary>Makes the next inbound decrypt fail the way the engine would.</summary>
    private void RefuseWith(IngestOutcome outcome) =>
        _mlsMock.Setup(m => m.DecryptMessageAsync(
                It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>()))
            .ThrowsAsync(new MlsIngestRefusedException(
                outcome, $"The message was not delivered: {outcome.GetType().Name}."));

    /// <summary>Makes the next inbound decrypt fail with untyped prose, as the legacy engines do.</summary>
    private void RefuseWithText(string message) =>
        _mlsMock.Setup(m => m.DecryptMessageAsync(
                It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>()))
            .ThrowsAsync(new InvalidOperationException(message));

    /// <summary>
    /// Pushes one kind-445 through and reports whether the user was told.
    /// </summary>
    private async Task<MlsDecryptionError?> PushAsync()
    {
        MlsDecryptionError? surfaced = null;
        using var subscription = _sut.DecryptionErrors.Subscribe(e => surfaced = e);

        await _sut.InitializeAsync();

        _events.OnNext(new NostrEventReceived
        {
            Kind = 445,
            EventId = new string('d', 64),
            PublicKey = SenderPubKey,
            Content = Convert.ToBase64String(new byte[] { 0xAA, 0xBB }),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", GroupIdHex } },
            RawJson = string.Empty
        });

        await Task.Delay(200);
        return surfaced;
    }

    // ------------------------------------------------- the refusals that are quiet

    /// <summary>
    /// The case the old predicate was written for, and the one the flip broke.
    /// </summary>
    /// <remarks>
    /// We joined by Welcome at epoch N+1 and cannot decrypt the commit at epoch N
    /// that admitted us. Nothing is wrong, and there is nothing a user could do.
    /// </remarks>
    [Fact]
    public async Task APreJoinCommitIsNotADecryptionError()
    {
        RefuseWith(new IngestOutcome.Stale(StaleReason.PreMembership));

        Assert.Null(await PushAsync());
    }

    /// <summary>
    /// A lost commit race, which on this engine is not an exception at all.
    /// </summary>
    /// <remarks>
    /// The legacy engine threw <c>RaceLostException</c> from the decrypt call and
    /// the app caught it by type. The Dark Matter engine does not decide races at
    /// ingest: a commit arriving while ours is unresolved comes back
    /// <see cref="IngestOutcome.Buffered"/>, and which branch wins is settled
    /// afterwards by <c>CommitOrdering</c> — priority class, then committer, then
    /// content digest, none of which a relay can influence. The side that loses
    /// sees <see cref="StaleReason.LosingBranch"/>. Both are the system working.
    /// </remarks>
    [Theory]
    [InlineData(StaleReason.LosingBranch)]
    [InlineData(StaleReason.AlreadySeen)]
    [InlineData(StaleReason.OwnEcho)]
    [InlineData(StaleReason.NotForThisClient)]
    [InlineData(StaleReason.BeyondAnchor)]
    [InlineData(StaleReason.BeyondAppRetention)]
    public async Task AStaleMessageAboutGroupHistoryIsNotADecryptionError(StaleReason reason)
    {
        RefuseWith(new IngestOutcome.Stale(reason));

        Assert.Null(await PushAsync());
    }

    /// <summary>Routing and dedup refusals: never ours to read.</summary>
    [Theory]
    [InlineData(InputRejectionCategory.Duplicate)]
    [InlineData(InputRejectionCategory.OwnEcho)]
    [InlineData(InputRejectionCategory.WrongRecipient)]
    [InlineData(InputRejectionCategory.UnknownGroup)]
    public async Task AnIgnoredInputIsNotADecryptionError(InputRejectionCategory category)
    {
        RefuseWith(new IngestOutcome.Ignored(category));

        Assert.Null(await PushAsync());
    }

    /// <summary>
    /// Held, not lost — so "failed to decrypt" would be false.
    /// </summary>
    /// <remarks>
    /// A message refused while a commit of ours is unresolved is kept durably, and
    /// the engine replays it once the group can read it. <b>Nothing in the app
    /// triggers that replay yet</b> — see the KNOWN GAP note on
    /// <c>MessageService.IsExpectedOutcome</c> — but that is an argument for
    /// building the trigger, not for telling the user their group needs a reset
    /// over a message that is safely on disk.
    /// </remarks>
    [Fact]
    public async Task AMessageHeldForReplayIsNotADecryptionError()
    {
        RefuseWith(new IngestOutcome.Buffered(Group, Epoch));

        Assert.Null(await PushAsync());
    }

    /// <summary>A transport-deferred message is retryable, not failed.</summary>
    [Fact]
    public async Task ATransportDeferredMessageIsNotADecryptionError()
    {
        RefuseWith(new IngestOutcome.TransportDeferred(Group));

        Assert.Null(await PushAsync());
    }

    // -------------------------------------------- the refusals that must be seen

    /// <summary>
    /// A signature that does not verify is not housekeeping.
    /// </summary>
    /// <remarks>
    /// The control for every quiet case above. Broadening the expected set until
    /// this goes quiet is the failure mode the old comment warned about: verify_id
    /// mismatches and unhandled GroupContextExtensions commits used to arrive as
    /// "Unprocessable" and had to stay visible.
    /// </remarks>
    [Theory]
    [InlineData(InputRejectionCategory.InvalidSignature)]
    [InlineData(InputRejectionCategory.InvalidEncoding)]
    [InlineData(InputRejectionCategory.AuthorizationFailed)]
    [InlineData(InputRejectionCategory.UnsupportedRequiredFeature)]
    public async Task AMalformedOrUnauthorizedInputIsSurfaced(InputRejectionCategory category)
    {
        RefuseWith(new IngestOutcome.Ignored(category));

        MlsDecryptionError? surfaced = await PushAsync();

        Assert.NotNull(surfaced);
        Assert.Equal("chat-1", surfaced!.ChatId);
    }

    /// <summary>
    /// This device's standing in the group is the user's problem to know about.
    /// </summary>
    /// <remarks>
    /// <see cref="IngestOutcome.LocalState"/>'s own documentation says to surface
    /// it, because no amount of retrying changes it.
    /// </remarks>
    [Theory]
    [InlineData(LocalIngestState.Removed)]
    [InlineData(LocalIngestState.RejoinConfirmationRequired)]
    [InlineData(LocalIngestState.Quarantined)]
    public async Task ALocalStateBlockIsSurfaced(LocalIngestState state)
    {
        RefuseWith(new IngestOutcome.LocalState(state));

        Assert.NotNull(await PushAsync());
    }

    /// <summary>A proposal the group refused is a real event, not noise.</summary>
    [Fact]
    public async Task ARejectedProposalIsSurfaced()
    {
        RefuseWith(new IngestOutcome.Rejected(ProposalRejectionCategory.AuthorizationFailed));

        Assert.NotNull(await PushAsync());
    }

    /// <summary>
    /// A local bound that stopped us keeping the message is retryable, not failed.
    /// </summary>
    /// <remarks>
    /// Explicitly not a protocol rejection — the same bytes arriving again must be
    /// processed rather than dismissed as a duplicate of something never kept.
    /// </remarks>
    [Fact]
    public async Task AResourceRefusalIsNotADecryptionError()
    {
        RefuseWith(new IngestOutcome.ResourceRefused(Group));

        Assert.Null(await PushAsync());
    }

    /// <summary>
    /// A stale reason outside the expected set reaches the user.
    /// </summary>
    /// <remarks>
    /// The default arm's direction, pinned. The expected set is enumerated rather
    /// than defaulted-to-quiet precisely so a reason the engine adds later — or one
    /// like <see cref="StaleReason.InvalidAgainstCanonicalState"/> that means the
    /// device has genuinely diverged — is not swallowed by a predicate written
    /// before it mattered.
    /// </remarks>
    [Theory]
    [InlineData(StaleReason.InvalidAgainstCanonicalState)]
    [InlineData(StaleReason.SelfEvicted)]
    [InlineData(StaleReason.Quarantined)]
    public async Task AStaleReasonOutsideTheExpectedSetIsSurfaced(StaleReason reason)
    {
        RefuseWith(new IngestOutcome.Stale(reason));

        Assert.NotNull(await PushAsync());
    }

    // ------------------------------------------------------- the resync banner

    /// <summary>
    /// The out-of-sync mark had the same prose-matching bug, one arm along.
    /// </summary>
    /// <remarks>
    /// It tested <c>ex.Message.Contains("epoch")</c>. The engine names this
    /// condition <see cref="StaleReason.InvalidAgainstCanonicalState"/> — "does
    /// not apply to the history this device actually holds" — and its text says
    /// nothing about epochs, so the resync banner had stopped being raised.
    /// </remarks>
    [Fact]
    public async Task AMessageAgainstHistoryWeDoNotHoldMarksTheChatOutOfSync()
    {
        RefuseWith(new IngestOutcome.Stale(StaleReason.InvalidAgainstCanonicalState));

        await PushAsync();

        Assert.True(_chat.IsOutOfSync);
        _storageMock.Verify(s => s.SaveChatAsync(It.Is<Chat>(c => c.IsOutOfSync)), Times.AtLeastOnce);
    }

    /// <summary>
    /// A surfaced refusal that is not about our history leaves the chat alone.
    /// </summary>
    /// <remarks>
    /// The control for the mark: a bad signature is worth showing and is not a
    /// reason to tell the user their device has fallen behind.
    /// </remarks>
    [Fact]
    public async Task ABadSignatureDoesNotMarkTheChatOutOfSync()
    {
        RefuseWith(new IngestOutcome.Ignored(InputRejectionCategory.InvalidSignature));

        Assert.NotNull(await PushAsync());
        Assert.False(_chat.IsOutOfSync);
    }

    // --------------------------------------------------- the legacy engines

    /// <summary>
    /// marmot-cs's prose still has to be understood, until marmot-cs goes.
    /// </summary>
    /// <remarks>
    /// <c>ManagedMlsService</c> and <c>MlsService</c> throw untyped text and are
    /// still constructed by tests until step 5 removes the library. The substring
    /// arm is scoped to exceptions that are not
    /// <see cref="MlsIngestRefusedException"/>, so it cannot override an outcome.
    /// </remarks>
    [Fact]
    public async Task ALegacyEnginesPreJoinProseIsStillUnderstood()
    {
        RefuseWithText(
            "Expected ApplicationMessageResult or CommitResult but got "
            + "UnprocessableResult: stale epoch 6 for group");

        Assert.Null(await PushAsync());
    }

    /// <summary>A legacy failure that is not the pre-join case still surfaces.</summary>
    [Fact]
    public async Task ALegacyEnginesOtherFailuresAreStillSurfaced()
    {
        RefuseWithText(
            "Expected ApplicationMessageResult or CommitResult but got "
            + "UnprocessableResult: verify_id mismatch");

        Assert.NotNull(await PushAsync());
    }
}
