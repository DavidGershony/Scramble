using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Scramble.UI.Tests.TestHelpers;

namespace Scramble.UI.Tests;

/// <summary>
/// Shared infrastructure for headless integration tests using real MLS services.
/// Provides CreateRealContext(), native DLL checks, and DB cleanup.
/// </summary>
public abstract class HeadlessTestBase : IDisposable
{
    protected readonly List<string> DbPaths = new();
    protected readonly List<IDisposable> Disposables = new();

    public void Dispose()
    {
        foreach (var d in Disposables) d.Dispose();

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        foreach (var path in DbPaths) TryDeleteFile(path);
    }

    protected static bool NativeDllAvailable()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "scramble_native.dll");
        return File.Exists(dllPath);
    }

    protected static bool ShouldSkip(string backend)
        => backend == "rust" && !NativeDllAvailable();

    protected record RealTestContext(
        User User,
        StorageService Storage,
        IMlsService MlsService,
        MessageService MessageService,
        Subject<NostrEventReceived> EventsSubject,
        Mock<INostrService> MockNostr,
        Mock<IPlatformClipboard> MockClipboard,
        Mock<IQrCodeGenerator> MockQrGenerator,
        Mock<IPlatformLauncher> MockLauncher);

    protected async Task<RealTestContext> CreateRealContext(string backend, bool saveUser = true)
    {
        var nostrService = new NostrService();
        var (privKey, pubKey, nsec, npub) = nostrService.GenerateKeyPair();

        var dbPath = Path.Combine(Path.GetTempPath(), $"scramble_headless_{backend}_{Guid.NewGuid()}.db");
        DbPaths.Add(dbPath);
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = pubKey,
            PrivateKeyHex = privKey,
            Npub = npub,
            Nsec = nsec,
            DisplayName = $"Test User ({backend})",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };
        if (saveUser) await storage.SaveCurrentUserAsync(user);

        IMlsService mlsService = backend switch
        {
            "managed" => new ManagedMlsService(storage),
            "rust" => new MlsService(storage),
            _ => throw new ArgumentException($"Unknown backend '{backend}'.")
        };

        var eventsSubject = new Subject<NostrEventReceived>();
        var mockNostr = new Mock<INostrService>();
        mockNostr.Setup(n => n.Events).Returns(eventsSubject.AsObservable());
        mockNostr.Setup(n => n.WelcomeMessages).Returns(Observable.Empty<MarmotWelcomeEvent>());
        mockNostr.Setup(n => n.GroupMessages).Returns(Observable.Empty<MarmotGroupMessageEvent>());
        mockNostr.Setup(n => n.ConnectionStatus).Returns(Observable.Empty<NostrConnectionStatus>());
        mockNostr.Setup(n => n.SyncStatus).Returns(Observable.Empty<string?>());
        mockNostr.Setup(n => n.ConnectedRelayUrls).Returns(new List<string> { "wss://relay.test" });
        mockNostr.Setup(n => n.ConnectAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.ConnectAsync(It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.DisconnectAsync()).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeToWelcomesAsync(It.IsAny<string>(), It.IsAny<string?>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeToGroupMessagesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<DateTimeOffset?>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeAsync(It.IsAny<string>(), It.IsAny<NostrFilter>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.UnsubscribeAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.FetchUserMetadataAsync(It.IsAny<string>())).ReturnsAsync((UserMetadata?)null);
        mockNostr.Setup(n => n.FetchKeyPackagesAsync(It.IsAny<string>())).ReturnsAsync(Enumerable.Empty<KeyPackage>());
        mockNostr.Setup(n => n.FetchWelcomeEventsAsync(It.IsAny<string>(), It.IsAny<string?>())).ReturnsAsync(Enumerable.Empty<NostrEventReceived>());
        mockNostr.Setup(n => n.FetchRelayListAsync(It.IsAny<string>())).ReturnsAsync(new List<RelayPreference>());
        mockNostr.Setup(n => n.PublishRelayListAsync(It.IsAny<List<RelayPreference>>(), It.IsAny<string?>()))
            .ReturnsAsync(() => "fakenip65_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>()))
            .ReturnsAsync(() => "fakekp_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishWelcomeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(() => "fakewelcome_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishGroupMessageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => "fakemsg_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishRawEventJsonAsync(It.IsAny<byte[]>()))
            .ReturnsAsync(() => "fakemsg_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishCommitAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => "fakecommit_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.WaitForRelayOkAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((true, (string?)null));
        mockNostr.Setup(n => n.GenerateKeyPair())
            .Returns((privKey, pubKey, nsec, npub));
        mockNostr.Setup(n => n.ImportPrivateKey(It.IsAny<string>()))
            .Returns((privKey, pubKey, nsec, npub));

        var messageService = new MessageService(storage, mockNostr.Object, mlsService);
        Disposables.Add(messageService);

        var mockClipboard = new Mock<IPlatformClipboard>();
        var mockQrGenerator = new Mock<IQrCodeGenerator>();
        var mockLauncher = new Mock<IPlatformLauncher>();

        return new RealTestContext(
            user, storage, mlsService, messageService, eventsSubject,
            mockNostr, mockClipboard, mockQrGenerator, mockLauncher);
    }

    /// <summary>
    /// Creates a MainViewModel wired up to the given context.
    /// </summary>
    protected MainViewModel CreateMainViewModel(RealTestContext ctx)
    {
        return new MainViewModel(
            ctx.MessageService, ctx.MockNostr.Object, ctx.Storage,
            ctx.MlsService, ctx.MockClipboard.Object,
            ctx.MockQrGenerator.Object, ctx.MockLauncher.Object);
    }

    /// <summary>
    /// Builds a minimal kind-30443 Nostr event JSON for AddMemberAsync.
    /// Uses actual MDK-provided tags from the KeyPackage.
    /// </summary>
    protected static string CreateFakeKeyPackageEventJson(string ownerPubKey, byte[] keyPackageData, List<List<string>>? tags = null)
    {
        var contentBase64 = Convert.ToBase64String(keyPackageData);
        var tagsArray = tags?.Select(t => t.ToArray()).ToArray()
            ?? new[]
            {
                new[] { "encoding", "base64" },
                new[] { "mls_protocol_version", "1.0" },
                new[] { "mls_ciphersuite", "0x0001" }
            };
        var eventObj = new
        {
            id = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            pubkey = ownerPubKey,
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            kind = 30443,
            tags = tagsArray,
            content = contentBase64,
            sig = new string('a', 128)
        };
        return JsonSerializer.Serialize(eventObj);
    }

    /// <summary>
    /// Prepares a KeyPackage for AddMemberAsync by setting EventJson and NostrEventId.
    /// </summary>
    protected static void PrepareKeyPackageForAddMember(KeyPackage kp, string ownerPubKey)
    {
        kp.EventJson = CreateFakeKeyPackageEventJson(ownerPubKey, kp.Data, kp.NostrTags);
        kp.NostrEventId = "fake443_" + Guid.NewGuid().ToString("N");
    }

    protected static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
