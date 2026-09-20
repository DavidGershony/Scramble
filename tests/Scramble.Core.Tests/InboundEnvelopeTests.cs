using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// What an inbound kind-445 hands to <see cref="IMlsService.DecryptMessageAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Dark Matter engine ingests the <b>event</b>, not the content of one. Its
/// peeler is the only thing that verifies an event's id and signature, so a
/// caller that base64-decodes the content and passes the ciphertext alone is
/// handing the engine routing nobody checked — and the engine refuses bare
/// ciphertext rather than guessing at the envelope it came from. Before P11's
/// flip this path did exactly that, because the legacy engines took ciphertext
/// and nothing else.
/// </para>
/// <para>
/// <b>Both shapes still have to work</b>, which is why these tests pin the
/// choice rather than the new behaviour alone: an event that arrived from a relay
/// carries its own text, and one that never had an envelope — a gift-wrap rumor —
/// carries only content.
/// </para>
/// </remarks>
public class InboundEnvelopeTests : IDisposable
{
    private readonly Mock<IStorageService> _storageMock = new();
    private readonly Mock<INostrService> _nostrMock = new();
    private readonly Mock<IMlsService> _mlsMock = new();
    private readonly Subject<NostrEventReceived> _events = new();
    private readonly MessageService _sut;

    private static readonly byte[] GroupId = { 0x01, 0x02, 0x03 };
    private static readonly string GroupIdHex = Convert.ToHexString(GroupId).ToLowerInvariant();
    private const string SenderPubKey = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    /// <summary>What the service was handed, captured rather than asserted inline.</summary>
    private byte[]? _ingested;

    public InboundEnvelopeTests()
    {
        var chat = new Chat
        {
            Id = "chat-1",
            Name = "Group",
            Type = ChatType.Group,
            MlsGroupId = GroupId,
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
        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { chat });
        _storageMock.Setup(s => s.GetChatByGroupIdAsync(It.IsAny<string>())).ReturnsAsync(chat);
        _storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.SaveMessageAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.SaveChatAsync(It.IsAny<Chat>())).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.GetUsersByPublicKeysAsync(It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(new Dictionary<string, User>());

        _mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _mlsMock.Setup(m => m.GetAdminPubkeys(It.IsAny<byte[]>())).Returns(new List<string>());
        _mlsMock.Setup(m => m.DecryptMessageAsync(
                It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>()))
            .Callback((byte[] _, byte[] payload, string? _, DateTimeOffset? _) => _ingested = payload)
            .ReturnsAsync(new MlsDecryptedMessage
            {
                SenderPublicKey = SenderPubKey,
                Plaintext = "hello",
                Epoch = 1,
                RumorKind = 9,
                RumorEventId = "rumor-1"
            });

        _sut = new MessageService(_storageMock.Object, _nostrMock.Object, _mlsMock.Object);
    }

    public void Dispose()
    {
        _events.Dispose();
        _sut.Dispose();
    }

    /// <summary>A signed kind-445 as a relay sends one. Only its shape matters here.</summary>
    private static string Envelope(string contentBase64) =>
        $"{{\"id\":\"{new string('d', 64)}\",\"pubkey\":\"{SenderPubKey}\",\"created_at\":1757000000,"
        + $"\"kind\":445,\"tags\":[[\"h\",\"{GroupIdHex}\"]],\"content\":\"{contentBase64}\","
        + $"\"sig\":\"{new string('e', 128)}\"}}";

    private async Task PushAsync(NostrEventReceived e)
    {
        await _sut.InitializeAsync();
        _events.OnNext(e);
        await Task.Delay(200);
    }

    [Fact]
    public async Task AnEventFromARelayIsIngestedWhole()
    {
        var content = Convert.ToBase64String(new byte[] { 0xAA, 0xBB });
        var envelope = Envelope(content);

        await PushAsync(new NostrEventReceived
        {
            Kind = 445,
            EventId = new string('d', 64),
            PublicKey = SenderPubKey,
            Content = content,
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", GroupIdHex } },
            RawJson = envelope
        });

        Assert.NotNull(_ingested);
        Assert.Equal(envelope, Encoding.UTF8.GetString(_ingested!));
    }

    [Fact]
    public async Task TheEnvelopeIsPassedThroughByteForByte()
    {
        // Not a re-serialisation of the parsed fields: an event id is a hash of a
        // canonical form, and a round trip that escapes one character differently
        // produces a different id — which the engine then refuses, on an event
        // that was valid when it arrived. The awkward spacing is the point.
        var content = Convert.ToBase64String(new byte[] { 0x01 });
        var envelope = "{ \"kind\" : 445 ,\"content\":\"" + content + "\",\"id\":\"" + new string('d', 64)
            + "\",\"sig\":\"" + new string('e', 128) + "\",\"pubkey\":\"" + SenderPubKey
            + "\",\"created_at\":1757000000,\"tags\":[[\"h\",\"" + GroupIdHex + "\"]]}";

        await PushAsync(new NostrEventReceived
        {
            Kind = 445,
            EventId = new string('d', 64),
            PublicKey = SenderPubKey,
            Content = content,
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", GroupIdHex } },
            RawJson = envelope
        });

        Assert.Equal(envelope, Encoding.UTF8.GetString(_ingested!));
    }

    [Fact]
    public async Task AnEventWithNoEnvelopeFallsBackToItsContent()
    {
        // A gift-wrap rumor has no envelope of its own, and the base64 content is
        // then the only thing there is. Losing this arm would break every inbound
        // path that does not come straight off a relay.
        byte[] ciphertext = { 0xAA, 0xBB, 0xCC };

        await PushAsync(new NostrEventReceived
        {
            Kind = 445,
            EventId = new string('d', 64),
            PublicKey = SenderPubKey,
            Content = Convert.ToBase64String(ciphertext),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", GroupIdHex } },
            RawJson = string.Empty
        });

        Assert.Equal(ciphertext, _ingested);
    }
}
