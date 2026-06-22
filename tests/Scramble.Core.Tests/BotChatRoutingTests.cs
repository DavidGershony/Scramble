using System.Reactive.Linq;
using System.Reactive.Subjects;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// Adversarial tests for kind 14 NIP-17 DM routing.  Backstop the rule that
/// strangers must NOT land in the user's most recent active chat unless that
/// chat explicitly opts in via <see cref="Chat.AcceptsCrossKeyResponses"/>.
///
/// The original bug (lock-in via <c>ParticipantPublicKeys.Add</c> after a
/// fail-open fallback) routinely surfaced strangers' DMs in real bot chats and
/// then permanently bolted them on.  See feedback memory "routing-fail-closed".
/// </summary>
public class BotChatRoutingTests : IDisposable
{
    private readonly Mock<IStorageService> _storageMock;
    private readonly Mock<INostrService> _nostrMock;
    private readonly Mock<IMlsService> _mlsMock;
    private readonly Subject<NostrEventReceived> _eventsSubject;
    private readonly MessageService _sut;
    private readonly List<Chat> _chatStore = new();
    private readonly List<Message> _messageStore = new();

    private readonly User _currentUser = new()
    {
        Id = "user-1",
        PublicKeyHex = new string('a', 64),
        PrivateKeyHex = new string('b', 64),
        Npub = "npub1current",
        DisplayName = "Current User",
        CreatedAt = DateTime.UtcNow
    };

    public BotChatRoutingTests()
    {
        _storageMock = new Mock<IStorageService>();
        _nostrMock = new Mock<INostrService>();
        _mlsMock = new Mock<IMlsService>();
        _eventsSubject = new Subject<NostrEventReceived>();

        _nostrMock.Setup(n => n.Events).Returns(_eventsSubject.AsObservable());
        _nostrMock.Setup(n => n.ConnectedRelayUrls).Returns(new List<string> { "wss://relay.example.com" });

        _storageMock.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(_currentUser);

        // In-memory chat store backed by the mock so the same Chat instances are observed
        // across routing, enrollment, and audit calls.
        _storageMock.Setup(s => s.GetAllChatsAsync())
            .ReturnsAsync(() => _chatStore.ToList());
        _storageMock.Setup(s => s.SaveChatAsync(It.IsAny<Chat>()))
            .Returns<Chat>(c =>
            {
                var existing = _chatStore.FirstOrDefault(x => x.Id == c.Id);
                if (existing != null) _chatStore.Remove(existing);
                _chatStore.Add(c);
                return Task.CompletedTask;
            });

        _storageMock.Setup(s => s.SaveMessageAsync(It.IsAny<Message>()))
            .Returns<Message>(m =>
            {
                _messageStore.Add(m);
                return Task.CompletedTask;
            });
        _storageMock.Setup(s => s.UpdateMessageChatIdAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((id, newChatId) =>
            {
                var msg = _messageStore.FirstOrDefault(m => m.Id == id);
                if (msg != null) msg.ChatId = newChatId;
                return Task.CompletedTask;
            });
        _storageMock.Setup(s => s.GetMessagesForChatAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns<string, int, int>((chatId, limit, offset) =>
                Task.FromResult(_messageStore.Where(m => m.ChatId == chatId).Take(limit).AsEnumerable()));
        _storageMock.Setup(s => s.GetLastMessagePerChatAsync())
            .ReturnsAsync(() => _messageStore
                .GroupBy(m => m.ChatId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Timestamp).First()));

        _storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.GetUserByPublicKeyAsync(It.IsAny<string>())).ReturnsAsync((string _) => null);
        _storageMock.Setup(s => s.GetChatByGroupIdAsync(It.IsAny<string>())).ReturnsAsync((string _) => null);
        _storageMock.Setup(s => s.GetArchivedChatsAsync()).ReturnsAsync(Enumerable.Empty<Chat>());
        _storageMock.Setup(s => s.GetUsersByPublicKeysAsync(It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(new Dictionary<string, User>());
        _storageMock.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string _) => null);
        _storageMock.Setup(s => s.SaveSettingAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        _mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _mlsMock.Setup(m => m.CanProcessWelcomeAsync(It.IsAny<byte[]>())).ReturnsAsync(true);

        _sut = new MessageService(_storageMock.Object, _nostrMock.Object, _mlsMock.Object);
    }

    public void Dispose()
    {
        _eventsSubject.Dispose();
        _sut.Dispose();
    }

    private NostrEventReceived MakeKind14(string sender, string relayUrl, DateTime createdAt, string? content = null)
        => new()
        {
            Kind = 14,
            EventId = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            PublicKey = sender,
            Content = content ?? $"hello from {sender[..8]}",
            CreatedAt = createdAt,
            RelayUrl = relayUrl,
            Tags = new List<List<string>>()
        };

    // ─── Default behavior: fail closed ─────────────────────────────────

    [Fact]
    public async Task StrangerKind14_WithRecentOutgoingInBotChat_DoesNotLandInThatChat()
    {
        // Existing bot chat with sender S; user just messaged them.
        var senderS = new string('5', 64);
        var stranger = new string('7', 64);
        var existing = new Chat
        {
            Id = "chat-bot-vidu",
            Name = "post-bot-vidu",
            Type = ChatType.Bot,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, senderS },
            RelayUrls = new List<string> { "wss://relay.example.com" },
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            LastActivityAt = DateTime.UtcNow.AddMinutes(-5),
            AcceptsCrossKeyResponses = false
        };
        _chatStore.Add(existing);
        _messageStore.Add(new Message
        {
            Id = "msg-out-1",
            ChatId = existing.Id,
            SenderPublicKey = _currentUser.PublicKeyHex,
            IsFromCurrentUser = true,
            Content = "hi vidu",
            Timestamp = DateTime.UtcNow.AddMinutes(-5),
        });

        await _sut.InitializeAsync();

        // A kind 14 arrives from a never-seen stranger within the 30-min window.
        _eventsSubject.OnNext(MakeKind14(stranger, "wss://relay.example.com", DateTime.UtcNow));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // The stranger's message must NOT be in the existing chat.
        var existingAfter = _chatStore.Single(c => c.Id == existing.Id);
        Assert.DoesNotContain(stranger, existingAfter.ParticipantPublicKeys);

        var strangerMessage = _messageStore.SingleOrDefault(m => m.SenderPublicKey == stranger);
        Assert.NotNull(strangerMessage);
        Assert.NotEqual(existing.Id, strangerMessage!.ChatId);
    }

    // ─── Audit: heals existing misroutes ─────────────────────────────────

    [Fact]
    public async Task Audit_MovesMisroutedMessagesAndResetsParticipants()
    {
        // Pre-existing chat with the lock-in symptom: the true sender plus two strangers
        // already in ParticipantPublicKeys, and messages from both strangers persisted here.
        var trueSender = new string('5', 64);
        var stranger1 = new string('7', 64);
        var stranger2 = new string('9', 64);
        var contaminated = new Chat
        {
            Id = "chat-contaminated",
            Name = "post-bot-vidu",
            Type = ChatType.Bot,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, trueSender, stranger1, stranger2 },
            RelayUrls = new List<string> { "wss://relay.example.com" },
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            LastActivityAt = DateTime.UtcNow.AddMinutes(-10),
            AcceptsCrossKeyResponses = false
        };
        _chatStore.Add(contaminated);

        // History: earliest non-self message is from the true sender, then strangers later.
        _messageStore.Add(new Message
        {
            Id = "msg-true-1",
            ChatId = contaminated.Id,
            SenderPublicKey = trueSender,
            IsFromCurrentUser = false,
            Content = "legit reply",
            Timestamp = DateTime.UtcNow.AddDays(-4),
        });
        _messageStore.Add(new Message
        {
            Id = "msg-stranger1-a",
            ChatId = contaminated.Id,
            SenderPublicKey = stranger1,
            IsFromCurrentUser = false,
            Content = "misrouted from stranger1",
            Timestamp = DateTime.UtcNow.AddDays(-2),
        });
        _messageStore.Add(new Message
        {
            Id = "msg-stranger1-b",
            ChatId = contaminated.Id,
            SenderPublicKey = stranger1,
            IsFromCurrentUser = false,
            Content = "another stranger1 message",
            Timestamp = DateTime.UtcNow.AddDays(-2).AddHours(1),
        });
        _messageStore.Add(new Message
        {
            Id = "msg-stranger2-a",
            ChatId = contaminated.Id,
            SenderPublicKey = stranger2,
            IsFromCurrentUser = false,
            Content = "misrouted from stranger2",
            Timestamp = DateTime.UtcNow.AddDays(-1),
        });

        await _sut.InitializeAsync();
        await _sut.RunBotChatRoutingAuditAsync();

        // Contaminated chat: participants reset to [currentUser, trueSender]; only the legit reply remains.
        var healed = _chatStore.Single(c => c.Id == contaminated.Id);
        Assert.Equal(new HashSet<string> { _currentUser.PublicKeyHex, trueSender }, healed.ParticipantPublicKeys.ToHashSet());
        var msgsRemaining = _messageStore.Where(m => m.ChatId == contaminated.Id).ToList();
        Assert.Single(msgsRemaining);
        Assert.Equal(trueSender, msgsRemaining[0].SenderPublicKey);

        // The stranger messages are gone from the contaminated chat — moved to new chats.
        var stranger1Msgs = _messageStore.Where(m => m.SenderPublicKey == stranger1).ToList();
        var stranger2Msgs = _messageStore.Where(m => m.SenderPublicKey == stranger2).ToList();
        Assert.All(stranger1Msgs, m => Assert.NotEqual(contaminated.Id, m.ChatId));
        Assert.All(stranger2Msgs, m => Assert.NotEqual(contaminated.Id, m.ChatId));
        Assert.Single(stranger1Msgs.Select(m => m.ChatId).Distinct());
        Assert.Single(stranger2Msgs.Select(m => m.ChatId).Distinct());
    }

    [Fact]
    public async Task Audit_SkipsWhenRunWithin24Hours()
    {
        _storageMock.Setup(s => s.GetSettingAsync("bot_chat_audit_last_run_at"))
            .ReturnsAsync(DateTime.UtcNow.AddHours(-1).ToString("O"));

        var trueSender = new string('5', 64);
        var stranger = new string('7', 64);
        var contaminated = new Chat
        {
            Id = "chat-contaminated",
            Name = "post-bot-vidu",
            Type = ChatType.Bot,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, trueSender, stranger },
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            LastActivityAt = DateTime.UtcNow.AddMinutes(-10),
            AcceptsCrossKeyResponses = false
        };
        _chatStore.Add(contaminated);
        _messageStore.Add(new Message
        {
            Id = "msg-true-1",
            ChatId = contaminated.Id,
            SenderPublicKey = trueSender,
            IsFromCurrentUser = false,
            Timestamp = DateTime.UtcNow.AddDays(-4),
            Content = "x"
        });
        _messageStore.Add(new Message
        {
            Id = "msg-stranger-a",
            ChatId = contaminated.Id,
            SenderPublicKey = stranger,
            IsFromCurrentUser = false,
            Timestamp = DateTime.UtcNow.AddDays(-2),
            Content = "y"
        });

        await _sut.InitializeAsync();
        await _sut.RunBotChatRoutingAuditAsync();

        // Audit ran within the last 24h, so the stranger is still in participants.
        var after = _chatStore.Single(c => c.Id == contaminated.Id);
        Assert.Contains(stranger, after.ParticipantPublicKeys);
    }
}
