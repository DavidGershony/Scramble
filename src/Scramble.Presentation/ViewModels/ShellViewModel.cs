using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Scramble.Core.Configuration;
using Scramble.Core.Logging;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.Services;

namespace Scramble.Presentation.ViewModels;

/// <summary>
/// Top-level ViewModel that owns the login flow and service lifecycle.
/// Login runs BEFORE any user-scoped services exist. After login, the correct
/// profile is set and services are created with the right DB/MLS paths.
/// </summary>
public partial class ShellViewModel : ViewModelBase
{
    private readonly ILogger<ShellViewModel> _logger = LoggingConfiguration.CreateLogger<ShellViewModel>();

    // Profile-independent services (created at startup, survive across logins)
    private readonly INostrService _nostrService;
    private readonly IPlatformClipboard _clipboard;
    private readonly IQrCodeGenerator _qrCodeGenerator;
    private readonly IPlatformLauncher _launcher;
    private readonly ISecureStorage _secureStorage;
    private readonly PlatformContext _platform;

    // Profile-dependent services (created after login, disposed on logout)
    private IStorageService? _storageService;
    private IMlsService? _mlsService;
    private IMessageService? _messageService;
    private bool _sessionActivated;

    // Multi-account state
    private string? _previousActiveAccountPubKey;

    // Set to the logged-out account on logout, then cleared on the next session
    // activation. While set, the switcher hides this entry — picking your own
    // freshly-logged-out account in the post-logout overlay is rarely intentional
    // and was previously broken (we used to wipe the user row from the DB).
    private string? _recentlyLoggedOutPubKey;

    [Reactive] public partial bool IsLoggedIn { get; set; }
    [Reactive] public partial MainViewModel? MainViewModel { get; set; }

    /// <summary>
    /// All known accounts from the registry, ordered by last active.
    /// Used by the account switcher UI.
    /// </summary>
    public ObservableCollection<AccountEntry> KnownAccounts { get; } = new();

    /// <summary>
    /// The currently active account entry (for highlighting in the switcher).
    /// </summary>
    [Reactive] public partial AccountEntry? ActiveAccountEntry { get; set; }

    /// <summary>
    /// True when the login screen was triggered by "Add Account" (shows Cancel button).
    /// </summary>
    [Reactive] public partial bool IsAddingAccount { get; set; }

    /// <summary>
    /// True when the account switcher overlay is visible.
    /// </summary>
    [Reactive] public partial bool ShowAccountSwitcher { get; set; }

    public LoginViewModel LoginViewModel { get; }

    /// <summary>
    /// Factory for creating the MLS service. Set by the platform layer (Desktop/Android)
    /// since the implementation depends on the backend (Managed vs Rust).
    /// </summary>
    public Func<IStorageService, IMlsService>? MlsServiceFactory { get; set; }

    public ReactiveCommand<string, Unit> SwitchAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> AddAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAddAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAccountSwitcherCommand { get; }

    public ShellViewModel(
        INostrService nostrService,
        ISecureStorage secureStorage,
        IPlatformClipboard clipboard,
        IQrCodeGenerator qrCodeGenerator,
        IPlatformLauncher launcher,
        PlatformContext? platform = null)
    {
        _nostrService = nostrService;
        _secureStorage = secureStorage;
        _clipboard = clipboard;
        _qrCodeGenerator = qrCodeGenerator;
        _launcher = launcher;
        _platform = platform ?? new PlatformContext();

        // LoginViewModel uses NostrService for crypto and ExternalSignerService for Amber.
        // It does NOT get StorageService — saving happens here after profile is set.
        LoginViewModel = new LoginViewModel(nostrService, qrCodeGenerator, launcher, clipboard: clipboard);

        // When LoginViewModel produces a user, complete the login flow
        LoginViewModel.WhenAnyValue(x => x.LoggedInUser)
            .Where(user => user != null)
            .Subscribe(user => OnLoginCompleted(user!));

        // Multi-account commands
        SwitchAccountCommand = ReactiveCommand.CreateFromTask<string>(SwitchAccountAsync);
        AddAccountCommand = ReactiveCommand.CreateFromTask(AddAccountAsync);
        CancelAddAccountCommand = ReactiveCommand.CreateFromTask(CancelAddAccountAsync);
        ToggleAccountSwitcherCommand = ReactiveCommand.Create(() => { ShowAccountSwitcher = !ShowAccountSwitcher; });

        // Load the account registry
        AccountRegistryService.Load();
        RefreshKnownAccounts();

        // Try auto-login on startup
        _ = TryAutoLoginAsync();
    }

    private async Task TryAutoLoginAsync()
    {
        try
        {
            // If no profile was set yet (e.g. Android, or desktop without --profile),
            // try to auto-derive from account registry (or legacy last_user.json).
            if (!ProfileConfiguration.IsCustomProfile)
            {
                var activeAccount = AccountRegistryService.GetActiveAccount();
                var lastPubKey = activeAccount?.PublicKeyHex;

                if (lastPubKey != null)
                {
                    ProfileConfiguration.SetProfileForAccount(lastPubKey);
                    _logger.LogInformation("Auto-derived profile {Profile} from account registry", ProfileConfiguration.ProfileName);
                }
            }

            // If profile was set (from registry, auto-derived above, or --profile), check for saved user
            if (ProfileConfiguration.IsCustomProfile)
            {
                _logger.LogInformation("Checking for saved user in profile {Profile}", ProfileConfiguration.ProfileName);
                var storageService = CreateStorageService();

                // Run heavy SQLite initialization and DB query off the UI thread
                // to avoid ANR on Android. The continuation resumes on the UI thread
                // via the captured SynchronizationContext, keeping property sets safe.
                await Task.Run(async () =>
                {
                    await storageService.InitializeAsync();
                });
                var savedUser = await Task.Run(() => storageService.GetCurrentUserAsync());

                if (savedUser != null)
                {
                    _logger.LogInformation("Auto-login: found saved user {Npub}...", savedUser.Npub?[..Math.Min(12, savedUser.Npub.Length)]);
                    // Suppress signer auto-login during session restore to prevent
                    // RestoreSessionAsync's Connected event from racing with this path.
                    LoginViewModel.SuppressSignerAutoLogin = true;
                    await ActivateSession(savedUser, storageService);
                    return;
                }

                _logger.LogInformation("No saved user in profile {Profile}, showing login screen", ProfileConfiguration.ProfileName);
            }

            // No saved user or default profile — show login screen
            ShowLoginView();
            OfferKnownAccountsIfAny();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-login failed, showing login screen");
            ShowLoginView();
            OfferKnownAccountsIfAny();
        }
    }

    /// <summary>
    /// On app startup with no active account, but with previously-used accounts in the
    /// registry, surface the switcher so the user can pick one without re-entering
    /// credentials. Otherwise the login screen would appear with no path back to them.
    /// </summary>
    private void OfferKnownAccountsIfAny()
    {
        if (KnownAccounts.Count > 0)
            ShowAccountSwitcher = true;
    }

    private async void OnLoginCompleted(User user)
    {
        try
        {
            _logger.LogInformation("Login completed for {Npub}..., setting up profile", user.Npub?[..Math.Min(12, user.Npub.Length)]);

            // Sanity check: detect the wrong-npub bug at the latest possible moment.
            // For NIP-46 logins the user's signing pubkey (PublicKeyHex) MUST be
            // resolved via get_public_key and is NOT supposed to equal the bunker's
            // transport pubkey (SignerRemotePubKey). If they're equal, an earlier
            // step silently fell back to the transport key — refuse to persist.
            // Note: a few signers do legitimately reuse the same key for both
            // roles; if you hit this in practice we can downgrade to a warning.
            if (user.IsRemoteSigner
                && !string.IsNullOrEmpty(user.SignerRemotePubKey)
                && string.Equals(user.PublicKeyHex, user.SignerRemotePubKey, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Login rejected: user PublicKeyHex equals signer transport pubkey ({Pub}). This indicates get_public_key did not resolve the user's signing key.",
                    user.PublicKeyHex[..Math.Min(16, user.PublicKeyHex.Length)]);
                LoginViewModel.ErrorMessage = "Could not determine your public key from the signer. Please reconnect.";
                ShowLoginView();
                return;
            }

            // Derive profile from the user's public key (unless --profile was explicit)
            if (!ProfileConfiguration.WasExplicitlySet)
            {
                ProfileConfiguration.SetProfileForAccount(user.PublicKeyHex);
                _logger.LogInformation("Profile set to {Profile} (derived from npub)", ProfileConfiguration.ProfileName);
            }

            // Create profile-dependent services with the correct DB path
            var storageService = CreateStorageService();
            await storageService.InitializeAsync();
            await storageService.SaveCurrentUserAsync(user);

            // Update the account registry
            AccountRegistryService.AddOrUpdateAccount(new AccountEntry
            {
                PublicKeyHex = user.PublicKeyHex,
                Npub = user.Npub ?? string.Empty,
                DisplayName = user.DisplayName,
                AvatarUrl = user.AvatarUrl,
                IsRemoteSigner = user.IsRemoteSigner,
                AddedAt = DateTime.UtcNow,
                LastActiveAt = DateTime.UtcNow
            });
            AccountRegistryService.SetActiveAccount(user.PublicKeyHex);

            IsAddingAccount = false;
            RefreshKnownAccounts();

            await ActivateSession(user, storageService);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete login");
            LoginViewModel.ErrorMessage = $"Login failed: {ex.Message}";
            ShowLoginView();
        }
    }

    private async Task ActivateSession(User user, IStorageService storageService)
    {
        if (_sessionActivated)
        {
            _logger.LogWarning("ActivateSession called but session already active — ignoring duplicate (signer auto-login race)");
            return;
        }

        // Belt-and-suspenders guard against profile-DB identity corruption: the
        // active profile name must derive from the user about to be activated.
        // If they disagree, the User row in this profile's DB belongs to a
        // different account (the historical bug fixed in 07066ca). Refuse to
        // activate and surface an error rather than load a foreign identity.
        // See MainViewModelInitializeAfterLoginTests for the historical mechanism.
        if (string.IsNullOrEmpty(user.PublicKeyHex))
        {
            _logger.LogError("Refusing to activate session: user has no PublicKeyHex");
            ShowLoginView();
            // ShowLoginView calls LoginViewModel.Reset() which clears ErrorMessage,
            // so set it AFTER the reset so the user actually sees the failure.
            LoginViewModel.ErrorMessage = "Account record is missing a public key — cannot activate.";
            return;
        }
        var expectedProfile = ProfileConfiguration.DeriveProfileName(user.PublicKeyHex);
        if (!string.Equals(expectedProfile, ProfileConfiguration.ProfileName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Refusing to activate session: profile mismatch. Profile={Profile} but saved user pubkey derives {Expected} ({UserKey}…). The profile DB contains a foreign user record — run the reconcile tool.",
                ProfileConfiguration.ProfileName, expectedProfile,
                user.PublicKeyHex[..Math.Min(16, user.PublicKeyHex.Length)]);
            // Walk back the half-completed switch so the UI doesn't end up half-active.
            ProfileConfiguration.SetProfile(null);
            AccountRegistryService.ClearActiveAccount();
            ShowLoginView();
            // ShowLoginView resets the LoginViewModel — set ErrorMessage AFTER so the
            // user actually sees what went wrong instead of a blank login screen.
            LoginViewModel.ErrorMessage =
                $"This profile's data is inconsistent (saved user does not match profile path). " +
                $"Run the reconcile tool to repair, or remove and re-add the account.";
            return;
        }

        _sessionActivated = true;

        _storageService = storageService;

        // Provide storage to NostrService for contact relay list caching (outbox model)
        _nostrService.SetStorageService(storageService);

        // Create MLS service via platform factory
        _mlsService = MlsServiceFactory?.Invoke(storageService)
            ?? new ManagedMlsService(storageService);

        // Create MessageService
        _messageService = new MessageService(storageService, _nostrService, _mlsService);

        // Create MainViewModel with all services
        var mainVm = new MainViewModel(
            _messageService, _nostrService, storageService, _mlsService,
            _clipboard, _qrCodeGenerator, _launcher, _platform,
            onLogoutRequested: OnLogoutRequested);

        // Pass the external signer if the user logged in via Amber
        mainVm.ExternalSigner = LoginViewModel.ExternalSigner;

        // Set the user
        mainVm.CurrentUser = user;
        mainVm.IsLoggedIn = true;

        // Set MainViewModel on the UI thread — triggers XAML visibility switch
        // [Reactive] properties must be set on the UI thread for Avalonia bindings to update reliably
        var tcs = new TaskCompletionSource();
        RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, __) =>
        {
            MainViewModel = mainVm;
            IsLoggedIn = true;
            tcs.SetResult();
            return System.Reactive.Disposables.Disposable.Empty;
        });
        await tcs.Task;

        _logger.LogInformation("Session activated for {Npub}... in profile {Profile}",
            user.Npub?[..Math.Min(12, user.Npub.Length)], ProfileConfiguration.ProfileName);

        ActiveAccountEntry = AccountRegistryService.GetActiveAccount();
        // Once any session is active, the post-logout filter no longer applies —
        // every account in the registry is a valid switch target again.
        _recentlyLoggedOutPubKey = null;
        RefreshKnownAccounts();

        // Initialize in the background (relay connections, MLS, etc.)
        await MainViewModel.InitializeAfterLoginAsync();
    }

    /// <summary>
    /// Tears down the current session without touching the account registry.
    /// Used as the common path for logout, account switch, and add-account.
    /// </summary>
    private async Task TeardownSessionAsync()
    {
        _logger.LogInformation("Tearing down current session");

        try
        {
            // Update account metadata in registry before teardown (display name/avatar may have changed)
            if (MainViewModel?.CurrentUser is { } currentUser)
            {
                AccountRegistryService.AddOrUpdateAccount(new AccountEntry
                {
                    PublicKeyHex = currentUser.PublicKeyHex,
                    Npub = currentUser.Npub ?? string.Empty,
                    DisplayName = currentUser.DisplayName,
                    AvatarUrl = currentUser.AvatarUrl,
                    IsRemoteSigner = currentUser.IsRemoteSigner,
                    AddedAt = AccountRegistryService.GetActiveAccount()?.AddedAt ?? DateTime.UtcNow,
                    LastActiveAt = DateTime.UtcNow
                });
            }

            // Clear external signer from NostrService
            _nostrService.SetExternalSigner(null);
            ChatViewModel.MediaUploadService?.SetExternalSigner(null);

            // Reset MLS state
            if (_mlsService != null)
                await _mlsService.ResetAsync();

            // Disconnect relays
            await _nostrService.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during session teardown");
        }

        // Dispose session-scoped services
        (_messageService as IDisposable)?.Dispose();
        _storageService = null;
        _mlsService = null;
        _messageService = null;
        _sessionActivated = false;

        // Update UI on main thread
        var tcs = new TaskCompletionSource();
        RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, __) =>
        {
            MainViewModel = null;
            IsLoggedIn = false;
            ActiveAccountEntry = null;
            tcs.SetResult();
            return System.Reactive.Disposables.Disposable.Empty;
        });
        await tcs.Task;
    }

    /// <summary>
    /// Full logout: tears down session, clears active marker (account stays in registry).
    /// Shows login screen, plus the account switcher overlay if other accounts exist
    /// so the user can switch without re-entering credentials.
    ///
    /// Note: we deliberately do NOT clear IsCurrentUser on the per-profile DB row.
    /// Keys/data stay so the user can switch back into this account later. (Earlier
    /// versions cleared it, which made SwitchAccountAsync think the profile was
    /// corrupt and remove the account from the registry — silent data loss.)
    /// </summary>
    private async void OnLogoutRequested()
    {
        _logger.LogInformation("Logout requested");

        var loggedOutPubKey = ActiveAccountEntry?.PublicKeyHex;

        await TeardownSessionAsync();

        // Clear active account but keep in registry for easy re-login
        AccountRegistryService.ClearActiveAccount();
        ProfileConfiguration.SetProfile(null);

        // Hide the just-logged-out entry from the switcher until the user
        // re-activates any session. In-memory only — gone after restart.
        _recentlyLoggedOutPubKey = loggedOutPubKey;
        RefreshKnownAccounts();
        ShowLoginView();

        // If other known accounts exist, surface the switcher so the user can
        // hop into one of them without re-entering credentials.
        if (KnownAccounts.Any())
            ShowAccountSwitcher = true;
    }

    /// <summary>
    /// Switches to a different known account. Tears down current session,
    /// sets new profile, and activates the target account.
    /// </summary>
    public async Task SwitchAccountAsync(string targetPubKeyHex)
    {
        if (ActiveAccountEntry != null &&
            string.Equals(ActiveAccountEntry.PublicKeyHex, targetPubKeyHex, StringComparison.OrdinalIgnoreCase))
        {
            ShowAccountSwitcher = false;
            return;
        }

        _logger.LogInformation("Switching account to {PubKey}...", targetPubKeyHex[..Math.Min(16, targetPubKeyHex.Length)]);
        ShowAccountSwitcher = false;

        if (IsLoggedIn)
            await TeardownSessionAsync();

        // Set profile for the target account
        ProfileConfiguration.SetProfileForAccount(targetPubKeyHex);
        AccountRegistryService.SetActiveAccount(targetPubKeyHex);

        try
        {
            var storageService = CreateStorageService();
            await storageService.InitializeAsync();
            var savedUser = await storageService.GetCurrentUserAsync();

            if (savedUser != null)
            {
                await ActivateSession(savedUser, storageService);
                return;
            }

            // User not found in DB — profile may be corrupted
            _logger.LogWarning("No saved user found for account {PubKey}..., removing from registry",
                targetPubKeyHex[..Math.Min(16, targetPubKeyHex.Length)]);
            AccountRegistryService.RemoveAccount(targetPubKeyHex);
            RefreshKnownAccounts();
            ProfileConfiguration.SetProfile(null);
            ShowLoginView();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch to account {PubKey}...", targetPubKeyHex[..Math.Min(16, targetPubKeyHex.Length)]);
            AccountRegistryService.RemoveAccount(targetPubKeyHex);
            RefreshKnownAccounts();
            ProfileConfiguration.SetProfile(null);
            ShowLoginView();
            // ShowLoginView resets LoginViewModel — set ErrorMessage AFTER so the user sees what happened.
            LoginViewModel.ErrorMessage = $"Failed to switch account: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens the login screen to add a new account. Stores the current account
    /// so it can be restored if the user cancels.
    /// </summary>
    public async Task AddAccountAsync()
    {
        _logger.LogInformation("Add account requested");
        ShowAccountSwitcher = false;

        _previousActiveAccountPubKey = ActiveAccountEntry?.PublicKeyHex;
        IsAddingAccount = true;

        if (IsLoggedIn)
            await TeardownSessionAsync();

        ProfileConfiguration.SetProfile(null);
        LoginViewModel.IsAddAccountMode = true;
        ShowLoginView();
    }

    /// <summary>
    /// Cancels "Add Account" and returns to the previously active account.
    /// </summary>
    public async Task CancelAddAccountAsync()
    {
        _logger.LogInformation("Add account cancelled, returning to previous account");
        IsAddingAccount = false;

        if (_previousActiveAccountPubKey != null)
        {
            await SwitchAccountAsync(_previousActiveAccountPubKey);
            _previousActiveAccountPubKey = null;
        }
        else
        {
            ShowLoginView();
        }
    }

    /// <summary>
    /// Removes an account from the registry. If it's the active account,
    /// tears down the session first and switches to another account or shows login.
    /// </summary>
    public async Task RemoveAccountAsync(string publicKeyHex, bool deleteData = false)
    {
        _logger.LogInformation("Removing account {PubKey}...", publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);

        var isActive = ActiveAccountEntry != null &&
            string.Equals(ActiveAccountEntry.PublicKeyHex, publicKeyHex, StringComparison.OrdinalIgnoreCase);

        if (isActive)
        {
            try
            {
                if (_storageService != null)
                    await _storageService.ClearCurrentUserAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear current user from DB during account removal");
            }
            await TeardownSessionAsync();
            ProfileConfiguration.SetProfile(null);
        }

        AccountRegistryService.RemoveAccount(publicKeyHex, deleteData);
        RefreshKnownAccounts();

        if (isActive)
        {
            // Switch to the most recently active remaining account, or show login
            var accounts = AccountRegistryService.GetAccounts();
            if (accounts.Count > 0)
            {
                await SwitchAccountAsync(accounts[0].PublicKeyHex);
            }
            else
            {
                ShowLoginView();
            }
        }
    }

    private void RefreshKnownAccounts()
    {
        var accounts = AccountRegistryService.GetAccounts();
        KnownAccounts.Clear();
        foreach (var account in accounts)
        {
            if (_recentlyLoggedOutPubKey != null &&
                string.Equals(account.PublicKeyHex, _recentlyLoggedOutPubKey, StringComparison.OrdinalIgnoreCase))
                continue;
            KnownAccounts.Add(account);
        }
    }

    private void ShowLoginView()
    {
        LoginViewModel.Reset();
    }

    private IStorageService CreateStorageService()
    {
        _logger.LogInformation("Creating StorageService for profile {Profile} (DB: {Path})",
            ProfileConfiguration.ProfileName, ProfileConfiguration.DatabasePath);
        return new StorageService(ProfileConfiguration.DatabasePath, _secureStorage);
    }
}
