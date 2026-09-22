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
using Scramble.Nostr.Crypto;

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

        // One engine now: the backend parameter selected between two marmot-cs
        // backends and there is nothing left to select. It stays on the test
        // signatures for the moment because removing it touches every [InlineData]
        // in the suite; the value is ignored.
        //
        // The explicit InitializeAsync is not optional here. ManagedMlsService
        // initialised itself lazily on first use; this engine refuses every member
        // until it has an identity, because a session host is built with one and
        // never re-identified.
        IMlsService mlsService = DarkMatterMlsServiceFactory.Create(storage);
        await mlsService.InitializeAsync(privKey, pubKey);

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
            .ReturnsAsync(() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishWelcomeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(() => "fakewelcome_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishGroupMessageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => "fakemsg_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishRawEventJsonAsync(It.IsAny<byte[]>()))
            .ReturnsAsync(() => "fakemsg_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishCommitAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => "fakecommit_" + Guid.NewGuid().ToString("N"));
        // Where every commit goes now: a staged commit is already a signed
        // kind-445, so MessageService publishes it as-is and requires a relay OK.
        mockNostr.Setup(n => n.PublishCommitEventAsync(It.IsAny<byte[]>()))
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
    /// <summary>
    /// The kind-30443 event a KeyPackage was published under, signed for real.
    /// </summary>
    /// <remarks>
    /// <b>It used to be a fake: a random id and 128 'a' characters for a
    /// signature.</b> That passed against the legacy engine, which read the fields
    /// without checking them. The Dark Matter engine verifies the event id and the
    /// signature before reading a single field — deliberately, because an invitee's
    /// account key is only trustworthy if the event carrying it verifies — so an
    /// unsigned fixture now fails with "The event id does not match its content",
    /// which is the engine being right and the fixture being wrong.
    /// </remarks>
    protected static string SignedKeyPackageEventJson(
        User owner, byte[] keyPackageData, List<List<string>>? tags = null)
    {
        var template = new NostrEventTemplate(
            owner.PublicKeyHex,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            30443,
            (tags ?? []).Select(t => (IReadOnlyList<string>)t).ToList(),
            Convert.ToBase64String(keyPackageData));

        byte[] id = template.ComputeId();
        byte[] signature = Bip340.Sign(Convert.FromHexString(owner.PrivateKeyHex!), id);

        return NostrEnvelope.Write(template, id, signature);
    }

    /// <summary>
    /// Prepares a KeyPackage for AddMemberAsync by setting EventJson and NostrEventId.
    /// </summary>
    /// <summary>
    /// A KeyPackage ready to be invited with: signed, and bound to the event id it
    /// went out under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Publishing is two steps and the second one is not optional.</b> A Welcome
    /// names the KeyPackage it consumed by its kind-30443 event id, and
    /// <see cref="IMlsService.MarkKeyPackagePublishedAsync"/> binds that id to the
    /// private material this device kept. Without the binding the join fails closed
    /// — correctly: a Welcome naming a KeyPackage this device never published has not
    /// been admitted by us. It reads as "the private key is no longer available",
    /// because that is how <c>AcceptInviteAsync</c> reports any refusal naming a
    /// KeyPackage.
    /// </para>
    /// <para>
    /// <paramref name="signer"/> is separate from <paramref name="holder"/> for the
    /// multi-device case: two devices of one account publish two leaves signed by the
    /// same account key, and each binds only its own.
    /// </para>
    /// </remarks>
    protected static async Task PrepareKeyPackageForAddMemberAsync(
        KeyPackage kp, RealTestContext holder, User? signer = null)
    {
        User owner = signer ?? holder.User;

        kp.EventJson = SignedKeyPackageEventJson(owner, kp.Data, kp.NostrTags);
        kp.OwnerPublicKey = owner.PublicKeyHex;

        // The id the envelope above actually carries, not a fresh guid: the engine
        // reads it back out of the event it verified.
        using var doc = JsonDocument.Parse(kp.EventJson!);
        kp.NostrEventId = doc.RootElement.GetProperty("id").GetString();

        await holder.MlsService.MarkKeyPackagePublishedAsync(kp, kp.NostrEventId!);
    }

    protected static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
