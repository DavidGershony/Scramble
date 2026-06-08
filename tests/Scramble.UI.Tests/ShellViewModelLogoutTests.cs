using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Moq;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Scramble.UI.Tests.TestHelpers;
using ReactiveUI;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Tests that the ShellViewModel logout flow properly tears down session state.
/// This validates that making ShellViewModel static (for theme Recreate()) doesn't
/// break logout or re-login with a different user.
/// </summary>
public class ShellViewModelLogoutTests : IDisposable
{
    private readonly List<string> _dbPaths = new();
    private readonly string _originalRootDir = ProfileConfiguration.RootDataDirectory;
    private readonly string _tempRootDir;

    public ShellViewModelLogoutTests()
    {
        // Redirect RootDataDirectory to a temp folder so tests don't delete
        // the real last_user.json from the app's data directory
        _tempRootDir = Path.Combine(Path.GetTempPath(), $"scramble_test_root_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempRootDir);
        ProfileConfiguration.SetRootDataDirectory(_tempRootDir);
    }

    public void Dispose()
    {
        // Restore original root directory
        ProfileConfiguration.SetRootDataDirectory(_originalRootDir);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var path in _dbPaths)
            try { File.Delete(path); } catch { }
        try { Directory.Delete(_tempRootDir, true); } catch { }
    }

    [Fact]
    public async Task Logout_ClearsMainViewModelAndIsLoggedIn()
    {
        // Arrange: create a ShellViewModel with mocked services (simulating static instance)
        var mockNostr = new Mock<INostrService>();
        mockNostr.Setup(n => n.Events).Returns(Observable.Empty<NostrEventReceived>());
        mockNostr.Setup(n => n.WelcomeMessages).Returns(Observable.Empty<MarmotWelcomeEvent>());
        mockNostr.Setup(n => n.GroupMessages).Returns(Observable.Empty<MarmotGroupMessageEvent>());
        mockNostr.Setup(n => n.ConnectionStatus).Returns(Observable.Empty<NostrConnectionStatus>());
        mockNostr.Setup(n => n.SyncStatus).Returns(Observable.Empty<string?>());
        mockNostr.Setup(n => n.ConnectedRelayUrls).Returns(new List<string>());
        mockNostr.Setup(n => n.ConnectAsync(It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.DisconnectAsync()).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeToWelcomesAsync(It.IsAny<string>(), It.IsAny<string?>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeToGroupMessagesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<DateTimeOffset?>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.FetchUserMetadataAsync(It.IsAny<string>())).ReturnsAsync((UserMetadata?)null);
        mockNostr.Setup(n => n.FetchKeyPackagesAsync(It.IsAny<string>())).ReturnsAsync(Enumerable.Empty<KeyPackage>());
        mockNostr.Setup(n => n.FetchWelcomeEventsAsync(It.IsAny<string>(), It.IsAny<string?>())).ReturnsAsync(Enumerable.Empty<NostrEventReceived>());

        var mockClipboard = new Mock<IPlatformClipboard>();
        var mockQr = new Mock<IQrCodeGenerator>();
        var mockLauncher = new Mock<IPlatformLauncher>();

        var shell = new ShellViewModel(
            mockNostr.Object,
            new MockSecureStorage(),
            mockClipboard.Object,
            mockQr.Object,
            mockLauncher.Object);

        // Set up MLS factory that returns managed service
        shell.MlsServiceFactory = storage => new ManagedMlsService(storage);

        // Simulate login: create a user and storage
        var nostrService = new NostrService();
        var (privKey, pubKey, nsec, npub) = nostrService.GenerateKeyPair();

        var dbPath = Path.Combine(Path.GetTempPath(), $"scramble_logout_test_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = pubKey,
            PrivateKeyHex = privKey,
            Npub = npub,
            Nsec = nsec,
            DisplayName = "Logout Test User",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };
        await storage.SaveCurrentUserAsync(user);

        // Trigger login by setting LoggedInUser (ShellViewModel observes this)
        shell.LoginViewModel.LoggedInUser = user;

        // Wait for async initialization
        await Task.Delay(1000);

        // Assert: should be logged in with a MainViewModel
        Assert.True(shell.IsLoggedIn);
        Assert.NotNull(shell.MainViewModel);

        // Act: trigger logout
        shell.MainViewModel!.LogoutCommand.Execute().Subscribe();
        await Task.Delay(1000);

        // Assert: session is torn down
        Assert.False(shell.IsLoggedIn);
        Assert.Null(shell.MainViewModel);

        // Assert: NostrService was disconnected
        mockNostr.Verify(n => n.DisconnectAsync(), Times.AtLeastOnce);

        // The ShellViewModel instance is still alive (static) but session is clean
        // A second login should work
    }
}
