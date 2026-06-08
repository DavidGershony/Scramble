using System.Reactive.Linq;
using Moq;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Scramble.UI.Tests.TestHelpers;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Belt-and-suspenders guard: <see cref="ShellViewModel.ActivateSession"/> must
/// refuse to activate when the saved User row's PublicKeyHex doesn't derive
/// the active profile name. This catches the corruption scenario fixed in
/// 07066ca where a foreign account's User record had been written into the
/// active profile's DB — without the guard, the session would activate with
/// a wrong identity (chats from one account, header from another).
/// </summary>
public class ShellViewModelProfileGuardTests : IDisposable
{
    private readonly List<string> _dbPaths = new();
    private readonly string _originalRootDir = ProfileConfiguration.RootDataDirectory;
    private readonly string _tempRootDir;

    public ShellViewModelProfileGuardTests()
    {
        _tempRootDir = Path.Combine(Path.GetTempPath(), $"scramble_test_root_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempRootDir);
        ProfileConfiguration.SetRootDataDirectory(_tempRootDir);
    }

    public void Dispose()
    {
        ProfileConfiguration.SetRootDataDirectory(_originalRootDir);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var path in _dbPaths) try { File.Delete(path); } catch { }
        try { Directory.Delete(_tempRootDir, true); } catch { }
    }

    private static Mock<INostrService> CreateMockNostr()
    {
        var m = new Mock<INostrService>();
        m.Setup(n => n.Events).Returns(Observable.Empty<NostrEventReceived>());
        m.Setup(n => n.WelcomeMessages).Returns(Observable.Empty<MarmotWelcomeEvent>());
        m.Setup(n => n.GroupMessages).Returns(Observable.Empty<MarmotGroupMessageEvent>());
        m.Setup(n => n.ConnectionStatus).Returns(Observable.Empty<NostrConnectionStatus>());
        m.Setup(n => n.SyncStatus).Returns(Observable.Empty<string?>());
        m.Setup(n => n.ConnectedRelayUrls).Returns(new List<string>());
        m.Setup(n => n.ConnectAsync(It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
        m.Setup(n => n.DisconnectAsync()).Returns(Task.CompletedTask);
        m.Setup(n => n.SubscribeToWelcomesAsync(It.IsAny<string>(), It.IsAny<string?>())).Returns(Task.CompletedTask);
        m.Setup(n => n.SubscribeToGroupMessagesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<DateTimeOffset?>())).Returns(Task.CompletedTask);
        m.Setup(n => n.FetchUserMetadataAsync(It.IsAny<string>())).ReturnsAsync((UserMetadata?)null);
        m.Setup(n => n.FetchKeyPackagesAsync(It.IsAny<string>())).ReturnsAsync(Enumerable.Empty<KeyPackage>());
        m.Setup(n => n.FetchWelcomeEventsAsync(It.IsAny<string>(), It.IsAny<string?>())).ReturnsAsync(Enumerable.Empty<NostrEventReceived>());
        return m;
    }

    [Fact]
    public async Task SwitchAccount_ProfileDirHasForeignUserRecord_RefusesToActivate()
    {
        // Arrange: simulate a corrupted profile DB on disk.
        // - Profile dir name = first 16 hex chars of pubkey A.
        // - But the User row inside has PublicKeyHex = pubkey B (foreign).
        //
        // This is exactly the production state observed in the user's logs:
        // profile 9ce249b56250ea4f's DB had a User row for npub1axcr6lf...
        var nostrService = new NostrService();
        var (privA, pubA, nsecA, npubA) = nostrService.GenerateKeyPair();
        var (privB, pubB, nsecB, npubB) = nostrService.GenerateKeyPair();
        Assert.NotEqual(pubA[..16], pubB[..16]);

        // Set up the profile dir as profile A's, but seed the DB inside with user B.
        ProfileConfiguration.SetProfileForAccount(pubA);
        var profileADbPath = ProfileConfiguration.DatabasePath;
        _dbPaths.Add(profileADbPath);

        Directory.CreateDirectory(Path.GetDirectoryName(profileADbPath)!);
        var seedStorage = new StorageService(profileADbPath, new MockSecureStorage());
        await seedStorage.InitializeAsync();
        var foreignUser = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = pubB,           // <-- mismatch with profile dir A
            PrivateKeyHex = privB,
            Npub = npubB,
            Nsec = nsecB,
            DisplayName = "Foreign user (Bob)",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };
        await seedStorage.SaveCurrentUserAsync(foreignUser);

        // Register profile A in the registry (so SwitchAccountAsync can find it).
        AccountRegistryService.Reload();
        AccountRegistryService.AddOrUpdateAccount(new AccountEntry
        {
            PublicKeyHex = pubA,
            Npub = npubA,
            DisplayName = "Profile A (Alice)",
            AddedAt = DateTime.UtcNow,
            LastActiveAt = DateTime.UtcNow
        });

        var shell = new ShellViewModel(
            CreateMockNostr().Object, new MockSecureStorage(),
            new Mock<IPlatformClipboard>().Object,
            new Mock<IQrCodeGenerator>().Object,
            new Mock<IPlatformLauncher>().Object);
        shell.MlsServiceFactory = storage => new ManagedMlsService(storage);

        // Act: try to switch into profile A.
        await shell.SwitchAccountAsync(pubA);
        await Task.Delay(300);

        // Assert: the guard MUST refuse to activate. Without the guard, MainViewModel
        // would have been set with CurrentUser.PublicKeyHex == pubB (the foreign user).
        Assert.False(shell.IsLoggedIn);
        Assert.Null(shell.MainViewModel);
        Assert.NotNull(shell.LoginViewModel.ErrorMessage);
        Assert.Contains("inconsistent", shell.LoginViewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwitchAccount_ProfileDirMatchesUser_ActivatesNormally()
    {
        // Sanity-check the negative case: when the profile dir matches the saved
        // user's pubkey, activation proceeds normally. Guards must not break the
        // healthy path.
        var nostrService = new NostrService();
        var (priv, pub, nsec, npub) = nostrService.GenerateKeyPair();

        ProfileConfiguration.SetProfileForAccount(pub);
        var dbPath = ProfileConfiguration.DatabasePath;
        _dbPaths.Add(dbPath);

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var seedStorage = new StorageService(dbPath, new MockSecureStorage());
        await seedStorage.InitializeAsync();
        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = pub,            // <-- matches profile dir
            PrivateKeyHex = priv,
            Npub = npub,
            Nsec = nsec,
            DisplayName = "Healthy user",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };
        await seedStorage.SaveCurrentUserAsync(user);

        AccountRegistryService.Reload();
        AccountRegistryService.AddOrUpdateAccount(new AccountEntry
        {
            PublicKeyHex = pub,
            Npub = npub,
            DisplayName = "Healthy account",
            AddedAt = DateTime.UtcNow,
            LastActiveAt = DateTime.UtcNow
        });

        var shell = new ShellViewModel(
            CreateMockNostr().Object, new MockSecureStorage(),
            new Mock<IPlatformClipboard>().Object,
            new Mock<IQrCodeGenerator>().Object,
            new Mock<IPlatformLauncher>().Object);
        shell.MlsServiceFactory = storage => new ManagedMlsService(storage);

        await shell.SwitchAccountAsync(pub);
        await Task.Delay(500);

        Assert.True(shell.IsLoggedIn);
        Assert.NotNull(shell.MainViewModel);
        Assert.Equal(pub, shell.MainViewModel!.CurrentUser?.PublicKeyHex);
        Assert.Null(shell.LoginViewModel.ErrorMessage);
    }
}
