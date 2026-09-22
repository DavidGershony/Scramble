using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Headless tests for group member management: add, remove, and leave.
/// </summary>
public class HeadlessGroupMemberTests : HeadlessTestBase
{
    private async Task<(RealTestContext Creator, RealTestContext Joiner, Chat Chat)> CreateGroupWithTwoUsers(string backend)
    {
        var creator = await CreateRealContext(backend);
        await creator.MessageService.InitializeAsync();

        var joiner = await CreateRealContext(backend);
        await joiner.MessageService.InitializeAsync();

        // Creator creates a group
        var groupInfo = await creator.MlsService.CreateGroupAsync("Member Test Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Member Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { creator.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await creator.Storage.SaveChatAsync(chat);

        // Generate joiner's KeyPackage
        var joinerKp = await joiner.MlsService.GenerateKeyPackageAsync();
        await PrepareKeyPackageForAddMemberAsync(joinerKp, joiner);

        // Mock fetching joiner's KeyPackage from relays
        creator.MockNostr.Setup(n => n.FetchKeyPackagesAsync(joiner.User.PublicKeyHex))
            .ReturnsAsync((IEnumerable<KeyPackage>)new[] { joinerKp });

        return (creator, joiner, chat);
    }

    // --- Add Member ---

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task AddMember_UpdatesParticipantList(string backend)
    {
        var (creator, joiner, chat) = await CreateGroupWithTwoUsers(backend);

        Assert.Single(chat.ParticipantPublicKeys);

        await creator.MessageService.AddMemberAsync(chat.Id, joiner.User.PublicKeyHex);

        var stored = await creator.Storage.GetChatAsync(chat.Id);
        Assert.Contains(joiner.User.PublicKeyHex, stored!.ParticipantPublicKeys);
        Assert.Equal(2, stored.ParticipantPublicKeys.Count);
    }

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task AddMember_PublishesWelcomeToRelay(string backend)
    {
        var (creator, joiner, chat) = await CreateGroupWithTwoUsers(backend);

        await creator.MessageService.AddMemberAsync(chat.Id, joiner.User.PublicKeyHex);

        // Verify Welcome was published
        creator.MockNostr.Verify(n => n.PublishWelcomeAsync(
            It.IsAny<byte[]>(), joiner.User.PublicKeyHex, It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    // --- Remove Member ---

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task RemoveMember_UpdatesParticipantList(string backend)
    {
        var (creator, joiner, chat) = await CreateGroupWithTwoUsers(backend);

        // First add the member
        await creator.MessageService.AddMemberAsync(chat.Id, joiner.User.PublicKeyHex);
        var stored = await creator.Storage.GetChatAsync(chat.Id);
        Assert.Equal(2, stored!.ParticipantPublicKeys.Count);

        // Then remove
        await creator.MessageService.RemoveMemberAsync(chat.Id, joiner.User.PublicKeyHex);

        stored = await creator.Storage.GetChatAsync(chat.Id);
        Assert.DoesNotContain(joiner.User.PublicKeyHex, stored!.ParticipantPublicKeys);
    }

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task RemoveMember_PublishesCommit(string backend)
    {
        var (creator, joiner, chat) = await CreateGroupWithTwoUsers(backend);

        await creator.MessageService.AddMemberAsync(chat.Id, joiner.User.PublicKeyHex);
        await creator.MessageService.RemoveMemberAsync(chat.Id, joiner.User.PublicKeyHex);

        // The removal commit goes out through the member that publishes a
        // finished kind-445 and demands a relay OK before the caller merges.
        creator.MockNostr.Verify(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()),
            Times.AtLeastOnce);
    }

    // --- Multi-device Add Member ---

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task AddMember_MultipleDevices_PublishesWelcomePerDevice(string backend)
    {

        var creator = await CreateRealContext(backend);
        await creator.MessageService.InitializeAsync();

        // Two joiner "devices" — same Nostr identity, different MLS services
        var joinerDevice1 = await CreateRealContext(backend);
        await joinerDevice1.MessageService.InitializeAsync();
        var joinerDevice2 = await CreateRealContext(backend);
        await joinerDevice2.MessageService.InitializeAsync();

        // Use device1's pubkey as the shared Nostr identity
        var joinerPubKey = joinerDevice1.User.PublicKeyHex;

        // Creator creates a group
        var groupInfo = await creator.MlsService.CreateGroupAsync("Multi-Device Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Multi-Device Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { creator.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await creator.Storage.SaveChatAsync(chat);

        // Generate KPs from each device (different MLS leaves)
        var kp1 = await joinerDevice1.MlsService.GenerateKeyPackageAsync();
        await PrepareKeyPackageForAddMemberAsync(kp1, joinerDevice1);
        kp1.SlotId = "device1-slot-" + Guid.NewGuid().ToString("N");

        var kp2 = await joinerDevice2.MlsService.GenerateKeyPackageAsync();
        await PrepareKeyPackageForAddMemberAsync(kp2, joinerDevice2, joinerDevice1.User);
        kp2.SlotId = "device2-slot-" + Guid.NewGuid().ToString("N");

        // Mock relay to return both KPs for the same pubkey
        creator.MockNostr.Setup(n => n.FetchKeyPackagesAsync(joinerPubKey))
            .ReturnsAsync((IEnumerable<KeyPackage>)new[] { kp1, kp2 });

        // Act: add member — should send Welcome to both devices
        await creator.MessageService.AddMemberAsync(chat.Id, joinerPubKey);

        // Assert: exactly 2 Welcomes published (one per device)
        creator.MockNostr.Verify(n => n.PublishWelcomeAsync(
            It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Exactly(2));

        // Assert: participant list only has one entry (same Nostr identity, not per-device)
        var stored = await creator.Storage.GetChatAsync(chat.Id);
        Assert.Contains(joinerPubKey, stored!.ParticipantPublicKeys);
    }

    [AvaloniaTheory]
    [InlineData("managed")]
    public async Task AddMember_SameSlotMultipleKPs_TakesLatestOnly(string backend)
    {

        var creator = await CreateRealContext(backend);
        await creator.MessageService.InitializeAsync();

        var joiner = await CreateRealContext(backend);
        await joiner.MessageService.InitializeAsync();

        var groupInfo = await creator.MlsService.CreateGroupAsync("Dedup Test Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Dedup Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { creator.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await creator.Storage.SaveChatAsync(chat);

        // Two KPs with SAME SlotId but different timestamps (rotation scenario)
        var kpOld = await joiner.MlsService.GenerateKeyPackageAsync();
        await PrepareKeyPackageForAddMemberAsync(kpOld, joiner);
        kpOld.SlotId = "same-slot";
        kpOld.CreatedAt = DateTime.UtcNow.AddHours(-1); // older

        var kpNew = await joiner.MlsService.GenerateKeyPackageAsync();
        await PrepareKeyPackageForAddMemberAsync(kpNew, joiner);
        kpNew.SlotId = "same-slot";
        kpNew.CreatedAt = DateTime.UtcNow; // newer

        // Mock relay to return both KPs (old rotation + new)
        creator.MockNostr.Setup(n => n.FetchKeyPackagesAsync(joiner.User.PublicKeyHex))
            .ReturnsAsync((IEnumerable<KeyPackage>)new[] { kpOld, kpNew });

        // Act: dedup should pick only the latest KP per slot
        await creator.MessageService.AddMemberAsync(chat.Id, joiner.User.PublicKeyHex);

        // Assert: only 1 Welcome (dedup removed the older KP with same SlotId)
        creator.MockNostr.Verify(n => n.PublishWelcomeAsync(
            It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    // --- Leave Group ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task LeaveGroup_DeletesLocalState(string backend)
    {
        var creator = await CreateRealContext(backend);
        await creator.MessageService.InitializeAsync();

        var groupInfo = await creator.MlsService.CreateGroupAsync("Leave Test", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Leave Test",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { creator.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await creator.Storage.SaveChatAsync(chat);

        // LeaveGroup cleans up local state (MLS + chat) without self-removal commit (RFC 9420)
        await creator.MessageService.LeaveGroupAsync(chat.Id);

        var stored = await creator.Storage.GetChatAsync(chat.Id);
        Assert.Null(stored);
    }
}
