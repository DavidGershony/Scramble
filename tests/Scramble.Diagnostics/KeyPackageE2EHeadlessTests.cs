using System.Reactive;
using System.Reactive.Linq;
using Moq;
using Scramble.Core;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Xunit;
namespace Scramble.Diagnostics;

/// <summary>
/// End-to-end headless test: publish a KeyPackage via SettingsViewModel UI command,
/// then audit via the same UI — using a real NostrService against real relays.
/// Verifies the full round-trip: generate → publish → fetch → validate.
/// </summary>
[Trait("Category", "Integration")]
public class KeyPackageE2EHeadlessTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();
    private readonly List<IDisposable> _disposables = new();

    public KeyPackageE2EHeadlessTests(ITestOutputHelper output)
    {
        _output = output;
        ProfileConfiguration.SetAllowLocalRelays(true);
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var path in _dbPaths) try { File.Delete(path); } catch { }
    }

    [Fact]
    public async Task PublishAndAudit_RealRelay_KeyPackageRoundTrip()
    {
        // === Setup: real NostrService + real MLS, fresh keypair ===
        var realNostr = new NostrService();
        _disposables.Add(realNostr);

        var (privKey, pubKey, _, npub) = realNostr.GenerateKeyPair();
        _output.WriteLine($"Test user: {npub}");
        _output.WriteLine($"Pubkey: {pubKey}");

        var dbPath = Path.Combine(Path.GetTempPath(), $"kp_e2e_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = pubKey,
            PrivateKeyHex = privKey,
            Npub = npub,
            DisplayName = "E2E Test User",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };
        await storage.SaveCurrentUserAsync(user);

        var mlsService = new ManagedMlsService(storage);
        var messageService = new MessageService(storage, realNostr, mlsService);
        _disposables.Add(messageService);
        await messageService.InitializeAsync();

        // === Step 1: Connect to real relays ===
        var relays = TestRelayConfig.RelayUrls;
        _output.WriteLine("\n--- Connecting to relays ---");
        foreach (var relay in relays)
        {
            try
            {
                await realNostr.ConnectAsync(relay);
                _output.WriteLine($"  Connected: {relay}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  Failed: {relay} — {ex.Message}");
            }
        }
        _output.WriteLine($"Connected to {realNostr.ConnectedRelayUrls.Count} relays");
        Assert.True(realNostr.ConnectedRelayUrls.Count > 0, "Must connect to at least one relay");
        await Task.Delay(1000);

        // === Step 2: Create ViewModels (same wiring as the real app) ===
        var mockClipboard = new Mock<IPlatformClipboard>();
        var mockQr = new Mock<IQrCodeGenerator>();
        var mockLauncher = new Mock<IPlatformLauncher>();

        var mainVm = new MainViewModel(
            messageService, realNostr, storage, mlsService,
            mockClipboard.Object, mockQr.Object, mockLauncher.Object);

        mainVm.CurrentUser = user;
        mainVm.IsLoggedIn = true;

        var settingsVm = mainVm.SettingsViewModel;
        _output.WriteLine($"\nSettingsVM pubkey: {settingsVm.PublicKeyHex}");
        Assert.Equal(pubKey, settingsVm.PublicKeyHex);

        // === Step 3: Publish KeyPackage via UI command ===
        _output.WriteLine("\n--- Publishing KeyPackage via SettingsViewModel ---");
        var publishDone = new TaskCompletionSource();
        settingsVm.PublishKeyPackageCommand.Execute()
            .Subscribe(_ => publishDone.TrySetResult(), ex => publishDone.TrySetException(ex));
        await Task.WhenAny(publishDone.Task, Task.Delay(15000));

        _output.WriteLine($"  KeyPackageSuccess: {settingsVm.KeyPackageSuccess}");
        _output.WriteLine($"  KeyPackageStatus: {settingsVm.KeyPackageStatus}");
        Assert.True(settingsVm.KeyPackageSuccess, $"Publish failed: {settingsVm.KeyPackageStatus}");

        // Small delay for relay indexing
        await Task.Delay(1000);

        // === Step 4: Audit KeyPackages via UI command ===
        _output.WriteLine("\n--- Auditing KeyPackages via SettingsViewModel ---");
        var auditDone = new TaskCompletionSource();
        settingsVm.AuditKeyPackagesCommand.Execute()
            .Subscribe(_ => auditDone.TrySetResult(), ex => auditDone.TrySetException(ex));
        await Task.WhenAny(auditDone.Task, Task.Delay(15000));

        _output.WriteLine($"  AuditStatus: {settingsVm.AuditStatus}");
        _output.WriteLine($"  LastAuditResult: {settingsVm.LastAuditResult?.TotalOnRelays} on relays, " +
            $"{settingsVm.LastAuditResult?.ActiveWithKeys} active, " +
            $"{settingsVm.LastAuditResult?.Lost} lost");

        Assert.NotNull(settingsVm.LastAuditResult);
        Assert.True(settingsVm.LastAuditResult!.TotalOnRelays > 0,
            $"Audit found 0 KeyPackages on relays after successful publish. " +
            $"AuditStatus: {settingsVm.AuditStatus}");
        Assert.True(settingsVm.LastAuditResult.ActiveWithKeys > 0,
            "Audit found KeyPackages but none with active local keys");

        _output.WriteLine("\n--- SUCCESS: Published KeyPackage found on relays via audit ---");

        // Cleanup
        await realNostr.DisconnectAsync();
    }
}
