using System.Reactive.Linq;
using System.Reactive.Subjects;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// Tests for the dummy KeyPackage obfuscation system:
/// - ComputeDummySlotIds: deterministic slot ID generation
/// - PublishDummyKeyPackagesAsync: relay publishing behaviour (private relay only, feature-flagged)
/// - Filtering: dummy slots excluded from peer device detection and settings UI
/// </summary>
public class DummyKeyPackageTests : IDisposable
{
    private readonly Mock<IStorageService> _storageMock;
    private readonly Mock<INostrService> _nostrMock;
    private readonly Mock<IMlsService> _mlsMock;
    private readonly Subject<NostrEventReceived> _eventsSubject;
    private readonly MessageService _sut;

    // A realistic 64-char hex private key for testing
    private const string TestPrivateKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string TestPrivateKey2 = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string TestPrivateRelayUrl = "wss://private.relay.example.com";

    private readonly User _currentUser = new()
    {
        Id = "user-1",
        PublicKeyHex = "aa".PadLeft(64, 'a'),
        PrivateKeyHex = TestPrivateKey,
        Npub = "npub1test",
        DisplayName = "Test User",
        CreatedAt = DateTime.UtcNow
    };

    public DummyKeyPackageTests()
    {
        _storageMock = new Mock<IStorageService>();
        _nostrMock = new Mock<INostrService>();
        _mlsMock = new Mock<IMlsService>();
        _eventsSubject = new Subject<NostrEventReceived>();

        _nostrMock.Setup(n => n.Events).Returns(_eventsSubject.AsObservable());
        _nostrMock.Setup(n => n.ConnectedRelayUrls).Returns(new List<string> { "wss://relay.example.com" });

        _storageMock.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(_currentUser);
        _storageMock.Setup(s => s.SaveMessageAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.SaveChatAsync(It.IsAny<Chat>())).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.GetChatByGroupIdAsync(It.IsAny<string>())).ReturnsAsync((string _) => null as Chat);
        _storageMock.Setup(s => s.GetArchivedChatsAsync()).ReturnsAsync(Enumerable.Empty<Chat>());

        _mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _mlsMock.Setup(m => m.GetAdminPubkeys(It.IsAny<byte[]>())).Returns(new List<string>());
        _mlsMock.Setup(m => m.CanProcessWelcomeAsync(It.IsAny<byte[]>())).ReturnsAsync(true);

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

    // ─── ComputeDummySlotIds: determinism ─────────────────────────────

    [Fact]
    public void ComputeDummySlotIds_SameKey_ProducesSameSlotIds()
    {
        // The core invariant: two calls with the same key must produce identical sets.
        // This is what allows multiple devices to independently compute the same dummies.
        var set1 = MessageService.ComputeDummySlotIds(TestPrivateKey);
        var set2 = MessageService.ComputeDummySlotIds(TestPrivateKey);

        Assert.Equal(set1, set2);
    }

    [Fact]
    public void ComputeDummySlotIds_ReturnsExpectedCount()
    {
        var slots = MessageService.ComputeDummySlotIds(TestPrivateKey);

        Assert.Equal(MessageService.DummyKeyPackageCount, slots.Count);
    }

    [Fact]
    public void ComputeDummySlotIds_AllSlotsAre64CharHex()
    {
        // Slot IDs must be 32-byte hex (64 chars) to match real slot ID format
        var slots = MessageService.ComputeDummySlotIds(TestPrivateKey);

        foreach (var slot in slots)
        {
            Assert.Equal(64, slot.Length);
            Assert.True(slot.All(c => "0123456789abcdef".Contains(c)),
                $"Slot ID contains non-hex chars: {slot}");
        }
    }

    [Fact]
    public void ComputeDummySlotIds_AllSlotsAreUnique()
    {
        // Each dummy must have a distinct slot ID (no collisions)
        var slots = MessageService.ComputeDummySlotIds(TestPrivateKey);

        Assert.Equal(slots.Count, slots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ComputeDummySlotIds_DifferentKeys_ProduceDifferentSlotIds()
    {
        // Different private keys must produce completely disjoint dummy sets
        var slots1 = MessageService.ComputeDummySlotIds(TestPrivateKey);
        var slots2 = MessageService.ComputeDummySlotIds(TestPrivateKey2);

        Assert.Empty(slots1.Intersect(slots2, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComputeDummySlotIds_NullPrivateKey_ReturnsEmptySet()
    {
        // External signer users have no private key — should get an empty set
        var slots = MessageService.ComputeDummySlotIds(null);

        Assert.Empty(slots);
    }

    [Fact]
    public void ComputeDummySlotIds_EmptyPrivateKey_ReturnsEmptySet()
    {
        var slots = MessageService.ComputeDummySlotIds("");

        Assert.Empty(slots);
    }

    [Fact]
    public void ComputeDummySlotIds_CustomCount_ReturnsRequestedCount()
    {
        var slots = MessageService.ComputeDummySlotIds(TestPrivateKey, count: 10);

        Assert.Equal(10, slots.Count);
    }

    [Fact]
    public void ComputeDummySlotIds_CaseInsensitiveMatch()
    {
        // Verify that the set is case-insensitive, matching real slot ID behaviour
        var slots = MessageService.ComputeDummySlotIds(TestPrivateKey);
        var firstSlot = slots.First();

        // The set should recognise the upper-case version as already present
        Assert.Contains(firstSlot.ToUpperInvariant(), slots);
    }

    // ─── PublishDummyKeyPackagesAsync: relay publishing ────────────────

    [Fact]
    public async Task PublishDummyKeyPackages_PublishesExactlyDummyKeyPackageCount()
    {
        // Arrange
        await InitializeServiceAsync();

        _nostrMock.Setup(n => n.PublishKeyPackageAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>(), It.IsAny<Func<string, Task>?>()))
            .ReturnsAsync("event-id-dummy");

        // Act
        await _sut.PublishDummyKeyPackagesAsync(TestPrivateRelayUrl);

        // Assert: exactly DummyKeyPackageCount calls
        _nostrMock.Verify(
            n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>(), It.IsAny<Func<string, Task>?>()),
            Times.Exactly(MessageService.DummyKeyPackageCount));
    }

    [Fact]
    public async Task PublishDummyKeyPackages_UsesDeterministicSlotIdsAsDTags()
    {
        // Arrange
        await InitializeServiceAsync();

        var publishedDTags = new List<string>();
        _nostrMock.Setup(n => n.PublishKeyPackageAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>(), It.IsAny<Func<string, Task>?>()))
            .Callback<byte[], string, List<List<string>>?, Func<string, Task>?>((_, _, tags, _) =>
            {
                var dTag = tags?.FirstOrDefault(t => t.Count >= 2 && t[0] == "d");
                if (dTag != null) publishedDTags.Add(dTag[1]);
            })
            .ReturnsAsync("event-id-dummy");

        // Act
        await _sut.PublishDummyKeyPackagesAsync(TestPrivateRelayUrl);

        // Assert: published d-tags must match the deterministic dummy slot IDs
        var expectedSlotIds = MessageService.ComputeDummySlotIds(TestPrivateKey);
        Assert.Equal(expectedSlotIds.Count, publishedDTags.Count);
        foreach (var dTag in publishedDTags)
        {
            Assert.Contains(dTag, expectedSlotIds);
        }
    }

    [Fact]
    public async Task PublishDummyKeyPackages_IncludesRealisticMip00Tags()
    {
        // Arrange
        await InitializeServiceAsync();

        var publishedTagSets = new List<List<List<string>>>();
        _nostrMock.Setup(n => n.PublishKeyPackageAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>(), It.IsAny<Func<string, Task>?>()))
            .Callback<byte[], string, List<List<string>>?, Func<string, Task>?>((_, _, tags, _) =>
            {
                if (tags != null) publishedTagSets.Add(tags);
            })
            .ReturnsAsync("event-id-dummy");

        // Act
        await _sut.PublishDummyKeyPackagesAsync(TestPrivateRelayUrl);

        // Assert: each published set must have d, ciphersuite, mls_protocol_version, relays tags
        foreach (var tags in publishedTagSets)
        {
            Assert.Contains(tags, t => t[0] == "d");
            Assert.Contains(tags, t => t[0] == "ciphersuite" && t[1] == "0x0001");
            Assert.Contains(tags, t => t[0] == "mls_protocol_version" && t[1] == "mls10");
            Assert.Contains(tags, t => t[0] == "relays");
        }
    }

    [Fact]
    public async Task PublishDummyKeyPackages_RelaysTagContainsOnlyPrivateRelay()
    {
        // Arrange
        await InitializeServiceAsync();

        var publishedRelayTags = new List<List<string>>();
        _nostrMock.Setup(n => n.PublishKeyPackageAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>(), It.IsAny<Func<string, Task>?>()))
            .Callback<byte[], string, List<List<string>>?, Func<string, Task>?>((_, _, tags, _) =>
            {
                var relayTag = tags?.FirstOrDefault(t => t.Count >= 1 && t[0] == "relays");
                if (relayTag != null) publishedRelayTags.Add(relayTag);
            })
            .ReturnsAsync("event-id-dummy");

        // Act
        await _sut.PublishDummyKeyPackagesAsync(TestPrivateRelayUrl);

        // Assert: each relay tag contains exactly the private relay URL (not broadcast relays)
        Assert.Equal(MessageService.DummyKeyPackageCount, publishedRelayTags.Count);
        foreach (var relayTag in publishedRelayTags)
        {
            Assert.Equal(2, relayTag.Count); // ["relays", "wss://private.relay.example.com"]
            Assert.Equal("relays", relayTag[0]);
            Assert.Equal(TestPrivateRelayUrl, relayTag[1]);
        }
    }

    [Fact]
    public async Task PublishDummyKeyPackages_DataIsNonEmptyAndPlausibleSize()
    {
        // Arrange
        await InitializeServiceAsync();

        var publishedData = new List<byte[]>();
        _nostrMock.Setup(n => n.PublishKeyPackageAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>(), It.IsAny<Func<string, Task>?>()))
            .Callback<byte[], string, List<List<string>>?, Func<string, Task>?>((data, _, _, _) =>
            {
                publishedData.Add(data.ToArray());
            })
            .ReturnsAsync("event-id-dummy");

        // Act
        await _sut.PublishDummyKeyPackagesAsync(TestPrivateRelayUrl);

        // Assert: data blobs are 200-350 bytes (plausible KeyPackage size range)
        Assert.Equal(MessageService.DummyKeyPackageCount, publishedData.Count);
        foreach (var data in publishedData)
        {
            Assert.InRange(data.Length, 200, 350);
            // Ensure it's not all zeros (actually random)
            Assert.True(data.Any(b => b != 0), "Dummy data should contain non-zero bytes");
        }
    }

    [Fact]
    public async Task PublishDummyKeyPackages_PassesCorrectPrivateKey()
    {
        // Arrange
        await InitializeServiceAsync();

        var passedKeys = new List<string>();
        _nostrMock.Setup(n => n.PublishKeyPackageAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>(), It.IsAny<Func<string, Task>?>()))
            .Callback<byte[], string, List<List<string>>?, Func<string, Task>?>((_, key, _, _) =>
            {
                passedKeys.Add(key);
            })
            .ReturnsAsync("event-id-dummy");

        // Act
        await _sut.PublishDummyKeyPackagesAsync(TestPrivateRelayUrl);

        // Assert: all calls use the current user's private key (so the event is signed by the real identity)
        Assert.All(passedKeys, key => Assert.Equal(TestPrivateKey, key));
    }

    // ─── PublishDummyKeyPackagesAsync: skip/guard conditions ──────────

    [Fact]
    public async Task PublishDummyKeyPackages_ExternalSigner_SkipsPublishing()
    {
        // Arrange: user with no private key (external signer)
        var externalUser = new User
        {
            Id = "user-ext",
            PublicKeyHex = "cc".PadLeft(64, 'c'),
            PrivateKeyHex = null,
            Npub = "npub1ext",
            DisplayName = "External Signer User",
            CreatedAt = DateTime.UtcNow
        };
        _storageMock.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(externalUser);

        await InitializeServiceAsync();

        // Act
        await _sut.PublishDummyKeyPackagesAsync(TestPrivateRelayUrl);

        // Assert: no KeyPackages published
        _nostrMock.Verify(
            n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>(), It.IsAny<Func<string, Task>?>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishDummyKeyPackages_NoPrivateRelay_SkipsPublishing()
    {
        // Arrange
        await InitializeServiceAsync();

        // Act: pass null/empty private relay URL
        await _sut.PublishDummyKeyPackagesAsync("");

        // Assert: no KeyPackages published
        _nostrMock.Verify(
            n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>(), It.IsAny<Func<string, Task>?>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishDummyKeyPackages_NotLoggedIn_Throws()
    {
        // Arrange: no current user
        _storageMock.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync((User?)null);

        var sut2 = new MessageService(_storageMock.Object, _nostrMock.Object, _mlsMock.Object);
        // Don't call InitializeAsync — _currentUser stays null

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut2.PublishDummyKeyPackagesAsync(TestPrivateRelayUrl));

        sut2.Dispose();
    }

    [Fact]
    public async Task PublishDummyKeyPackages_PartialFailure_ContinuesPublishingRest()
    {
        // Arrange: first call throws, rest succeed
        await InitializeServiceAsync();

        int callCount = 0;
        _nostrMock.Setup(n => n.PublishKeyPackageAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>(), It.IsAny<Func<string, Task>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) throw new Exception("Relay error");
                return "event-id-ok";
            });

        // Act — should not throw despite one failure
        await _sut.PublishDummyKeyPackagesAsync(TestPrivateRelayUrl);

        // Assert: all DummyKeyPackageCount attempts were made (not short-circuited)
        _nostrMock.Verify(
            n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>(), It.IsAny<Func<string, Task>?>()),
            Times.Exactly(MessageService.DummyKeyPackageCount));
    }

    // ─── Filtering: dummy slots excluded from peer detection ──────────

    [Fact]
    public void DummySlotIds_NeverOverlapWithRealSlotId()
    {
        // A real slot ID is generated randomly by MLS. While a collision with HMAC output
        // is astronomically unlikely, this test documents the separation of concerns.
        var dummySlots = MessageService.ComputeDummySlotIds(TestPrivateKey);

        // A real slot ID would be random hex — simulate a few
        var realSlots = Enumerable.Range(0, 100)
            .Select(_ => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))
            .ToList();

        foreach (var realSlot in realSlots)
        {
            Assert.DoesNotContain(realSlot, dummySlots);
        }
    }

    [Fact]
    public void PeerDeviceFiltering_ExcludesDummySlotIds()
    {
        // Simulates the filtering logic from MainViewModel:
        // peerKps.Where(kp => !dummySlotIds.Contains(kp.SlotId!))
        var localSlotId = "local-slot-id-real-device-00000000000000000000000000000000";
        var dummySlotIds = MessageService.ComputeDummySlotIds(TestPrivateKey);
        var lostSlotIds = new HashSet<string>();
        var seenSlotIds = new HashSet<string>();

        // Construct a mixed list: 1 real peer + 4 dummies
        var allKeyPackages = new List<KeyPackage>();

        var realPeerSlot = "peer-device-slot-real-peer-00000000000000000000000000000000";
        allKeyPackages.Add(new KeyPackage
        {
            SlotId = realPeerSlot,
            OwnerPublicKey = _currentUser.PublicKeyHex,
            CiphersuiteId = 0x0001,
            Data = new byte[] { 1, 2, 3 }
        });

        foreach (var dummySlot in dummySlotIds)
        {
            allKeyPackages.Add(new KeyPackage
            {
                SlotId = dummySlot,
                OwnerPublicKey = _currentUser.PublicKeyHex,
                CiphersuiteId = 0x0001,
                Data = new byte[] { 4, 5, 6 }
            });
        }

        // Apply the same filter as MainViewModel
        var peerKps = allKeyPackages
            .Where(kp => kp.IsCipherSuiteSupported
                && !string.IsNullOrEmpty(kp.SlotId)
                && kp.SlotId != localSlotId
                && !seenSlotIds.Contains(kp.SlotId!)
                && !lostSlotIds.Contains(kp.SlotId!)
                && !dummySlotIds.Contains(kp.SlotId!))
            .ToList();

        // Only the real peer device should remain
        Assert.Single(peerKps);
        Assert.Equal(realPeerSlot, peerKps[0].SlotId);
    }

    [Fact]
    public void SettingsDeviceList_ExcludesDummySlotIds()
    {
        // Simulates the filtering logic from SettingsViewModel:
        // keyPackages.Where(kp => !string.IsNullOrEmpty(kp.SlotId) && !dummySlotIds.Contains(kp.SlotId!))
        var dummySlotIds = MessageService.ComputeDummySlotIds(TestPrivateKey);

        var keyPackages = new List<KeyPackage>();

        // This device
        keyPackages.Add(new KeyPackage
        {
            SlotId = "this-device-slot-0000000000000000000000000000000000000000",
            OwnerPublicKey = _currentUser.PublicKeyHex,
            Data = new byte[] { 1 }
        });

        // A real second device
        keyPackages.Add(new KeyPackage
        {
            SlotId = "second-device-slot-000000000000000000000000000000000000000",
            OwnerPublicKey = _currentUser.PublicKeyHex,
            Data = new byte[] { 2 }
        });

        // Dummy decoys
        foreach (var dummySlot in dummySlotIds)
        {
            keyPackages.Add(new KeyPackage
            {
                SlotId = dummySlot,
                OwnerPublicKey = _currentUser.PublicKeyHex,
                Data = new byte[] { 3 }
            });
        }

        // Apply the same filter as SettingsViewModel
        var deviceGroups = keyPackages
            .Where(kp => !string.IsNullOrEmpty(kp.SlotId) && !dummySlotIds.Contains(kp.SlotId!))
            .GroupBy(kp => kp.SlotId!)
            .ToList();

        // Should see exactly 2 real devices, no dummies
        Assert.Equal(2, deviceGroups.Count);
        Assert.DoesNotContain(deviceGroups, g => dummySlotIds.Contains(g.Key));
    }

    [Fact]
    public void ExternalSignerUser_HasNoDummySlots_AllKpsArePeers()
    {
        // External signer: ComputeDummySlotIds returns empty, so no filtering happens
        var dummySlotIds = MessageService.ComputeDummySlotIds(null);
        Assert.Empty(dummySlotIds);

        var localSlotId = "local-slot-id-0000000000000000000000000000000000000000000";

        var keyPackages = new List<KeyPackage>
        {
            new() { SlotId = localSlotId, CiphersuiteId = 0x0001, Data = new byte[] { 1 } },
            new() { SlotId = "peer-slot-000000000000000000000000000000000000000000000", CiphersuiteId = 0x0001, Data = new byte[] { 2 } },
        };

        // With empty dummy set, the filter passes everything through (except local)
        var peerKps = keyPackages
            .Where(kp => kp.IsCipherSuiteSupported
                && !string.IsNullOrEmpty(kp.SlotId)
                && kp.SlotId != localSlotId
                && !dummySlotIds.Contains(kp.SlotId!))
            .ToList();

        Assert.Single(peerKps);
        Assert.Equal("peer-slot-000000000000000000000000000000000000000000000", peerKps[0].SlotId);
    }
}
