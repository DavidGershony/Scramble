using System.Reactive.Linq;
using System.Reactive.Subjects;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// The app-layer admin gate on the three governance operations in
/// <see cref="MessageService"/>: add member, remove member, update the admin list.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these pin is that an unknown authority denies.</b> All three call sites used
/// to read <c>admins.Count &gt; 0 &amp;&amp; !admins.Contains(me)</c>, so an empty list
/// permitted everyone. The justification was backward compatibility with legacy groups,
/// and legacy groups stopped existing when marmot-cs was deleted in P11 step 5 — every
/// Dark Matter group carries app-component <c>0x8003</c> from creation, because
/// <c>AdminPolicy.Create</c> refuses an empty set.
/// </para>
/// <para>
/// So an empty read now means the policy could not be determined, and the answer to an
/// undetermined authority is no.
/// </para>
/// <para>
/// <b>Why this is worth a test rather than a comment.</b> Our peers already refuse an
/// unauthorised commit — <c>CommitAdmission.Require</c> runs before <c>ProcessCommit</c>
/// on every inbound handshake — but we do not run that check against our own commits. A
/// member who got past this gate would stage, publish, see the relay's OK, and merge
/// locally while every peer dropped the commit. The group forks, and the fork is ours.
/// This gate is the only thing between a bad read and that.
/// </para>
/// <para>
/// Thirteen existing tests failed when the gate was closed, every one of them mocking an
/// empty admin list and then performing a governance operation. That is the shape of the
/// bug: it was invisible precisely because the test doubles agreed with it.
/// </para>
/// </remarks>
public class AdminGateTests : IDisposable
{
    private readonly Mock<IStorageService> _storage = new();
    private readonly Mock<INostrService> _nostr = new();
    private readonly Mock<IMlsService> _mls = new();
    private readonly Subject<NostrEventReceived> _events = new();
    private readonly MessageService _sut;

    private static readonly byte[] GroupId = { 0x01, 0x02, 0x03, 0x04 };
    private const string ChatId = "chat-1";

    private static readonly string Me = new('a', 64);
    private static readonly string SomeoneElse = new('c', 64);

    public AdminGateTests()
    {
        _nostr.Setup(n => n.Events).Returns(_events.AsObservable());
        _nostr.Setup(n => n.ConnectedRelayUrls).Returns(new List<string> { "wss://relay.example.com" });

        _storage.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        _storage.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat>());
        _storage.Setup(s => s.SaveChatAsync(It.IsAny<Chat>())).Returns(Task.CompletedTask);
        _storage.Setup(s => s.SaveMessageAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);
        _storage.Setup(s => s.SaveSettingAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _storage.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(new User
        {
            Id = "user-1",
            PublicKeyHex = Me,
            PrivateKeyHex = new string('b', 64),
            Npub = "npub1test",
            DisplayName = "Test User",
            CreatedAt = DateTime.UtcNow
        });
        _storage.Setup(s => s.GetChatAsync(ChatId)).ReturnsAsync(new Chat
        {
            Id = ChatId,
            Name = "Test Group",
            Type = ChatType.Group,
            MlsGroupId = GroupId,
            ParticipantPublicKeys = new List<string> { Me, SomeoneElse }
        });

        _mls.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _mls.Setup(m => m.GetNostrGroupId(It.IsAny<byte[]>())).Returns(GroupId);

        _sut = new MessageService(_storage.Object, _nostr.Object, _mls.Object);
    }

    public void Dispose()
    {
        _events.Dispose();
        _sut.Dispose();
        GC.SuppressFinalize(this);
    }

    private void AdminsAre(params string[] admins) =>
        _mls.Setup(m => m.GetAdminPubkeys(It.IsAny<byte[]>())).Returns(admins.ToList());

    // ── an unreadable policy denies ─────────────────────────────────────────

    public static TheoryData<string> GovernanceOperations() => new()
    {
        "add", "remove", "admins",
    };

    /// <summary>
    /// Loads the current user, the way every other MessageService suite does. Without it
    /// the service refuses with "User not logged in" long before the admin gate, and the
    /// test would pass or fail for the wrong reason.
    /// </summary>
    private async Task<Exception?> RunAsync(string operation)
    {
        await _sut.InitializeAsync();
        return await Record.ExceptionAsync(() => InvokeAsync(operation));
    }

    private Task InvokeAsync(string operation) => operation switch
    {
        "add" => _sut.AddMemberAsync(ChatId, SomeoneElse),
        "remove" => _sut.RemoveMemberAsync(ChatId, SomeoneElse),
        "admins" => _sut.UpdateAdminPubkeysAsync(ChatId, new List<string> { Me }),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    [Theory]
    [MemberData(nameof(GovernanceOperations))]
    public async Task AnEmptyAdminPolicyRefusesEveryGovernanceOperation(string operation)
    {
        AdminsAre();   // the policy could not be read

        var ex = await RunAsync(operation);

        // The message has to say WHY, because "you are not an admin" would be a lie:
        // we do not know that, and the user may well be one.
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("could not be read", ex!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(GovernanceOperations))]
    public async Task ANonAdminIsRefusedEveryGovernanceOperation(string operation)
    {
        AdminsAre(SomeoneElse);   // a real policy, and we are not in it

        var ex = await RunAsync(operation);
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("Only group admins", ex!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(GovernanceOperations))]
    public async Task AListedAdminGetsPastTheGate(string operation)
    {
        AdminsAre(Me, SomeoneElse);

        // Past the gate is as far as this asserts. What happens next needs a staged
        // commit, a relay and a merge, which the rollback suite covers; the only claim
        // here is that authority is not what stopped it.
        var ex = await RunAsync(operation);

        if (ex is InvalidOperationException io)
        {
            Assert.DoesNotContain("Only group admins", io.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("could not be read", io.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// An uppercase entry does NOT match, and the gate denies rather than guessing.
    /// </summary>
    [Fact]
    public async Task AnUppercaseAdminEntryIsNotMatchedAndDenies()
    {
        AdminsAre(Me.ToUpperInvariant());

        var ex = await RunAsync("admins");
        Assert.IsType<InvalidOperationException>(ex);

        // Documents today's behaviour rather than endorsing it: GetAdminPubkeys is
        // specified to return lowercase, so this path should not arise -- but if it
        // ever does, the gate denies rather than silently mismatching into a commit
        // our peers would refuse. Denying on an unexpected shape is the safe half.
        Assert.Contains("Only group admins", ex!.Message, StringComparison.Ordinal);
    }
}
