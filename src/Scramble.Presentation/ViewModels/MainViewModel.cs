using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Microsoft.Extensions.Logging;
using Scramble.Core;
using Scramble.Core.Crypto;
using Scramble.Core.Logging;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.Services;

namespace Scramble.Presentation.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ILogger<MainViewModel> _logger = LoggingConfiguration.CreateLogger<MainViewModel>();
    private readonly IMessageService _messageService;
    private readonly INostrService _nostrService;
    private readonly IStorageService _storageService;
    private readonly IMlsService _mlsService;
    private readonly IPlatformClipboard _clipboard;
    private readonly IQrCodeGenerator _qrCodeGenerator;
    private NotificationOrchestrator? _notificationOrchestrator;
    private bool _connectionGracePeriodActive;
    private CancellationTokenSource? _noInternetDebounceCts;

    /// <summary>
    /// Signals when the external signer (Amber/NIP-46) has been wired into NostrService/MLS.
    /// Completed immediately for local-key users. Background sync tasks await this before
    /// attempting operations that require event signing (e.g., invites, announcements).
    /// </summary>
    private readonly TaskCompletionSource _signerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Reactive] public partial User? CurrentUser { get; set; }
    [Reactive] public partial bool IsLoggedIn { get; set; }
    [Reactive] public partial bool IsConnected { get; set; }

    [Reactive] public partial ViewModelBase? CurrentView { get; set; }
    [Reactive] public partial string HeaderDisplayName { get; set; } = "Scramble";
    [Reactive] public partial bool IsHeaderLoading { get; set; }

    // Relay statuses
    public ObservableCollection<RelayStatusViewModel> RelayStatuses { get; } = new();
    [Reactive] public partial bool IsRelayListExpanded { get; set; }
    [Reactive] public partial int ConnectedRelayCount { get; set; }
    [Reactive] public partial int TotalRelayCount { get; set; }
    [Reactive] public partial string RelayCountText { get; set; } = "Relays: 0/0";

    /// <summary>
    /// True when relay statuses have been populated (TotalRelayCount > 0) but none are
    /// connected. Used internally for debounce logic; the "No internet connection"
    /// message now appears via ChatListViewModel.StatusMessage instead of a separate banner.
    /// </summary>
    private bool _showNoInternet;
    private const string NoInternetStatusMessage = "No internet connection";

    /// <summary>
    /// True when this device was just linked to an existing identity (detected peer
    /// device KPs). Shown once to inform the user that old messages are not available
    /// due to MLS forward secrecy.
    /// </summary>
    [Reactive] public partial bool ShowForwardSecrecyBanner { get; set; }

    /// <summary>
    /// Names of non-admin groups that the peer device was not auto-added to.
    /// Shown alongside the forward-secrecy banner so the user knows to ask the admin.
    /// </summary>
    [Reactive] public partial string? SkippedNonAdminGroupsText { get; set; }

    /// <summary>True while sending device-add request DMs to group admins.</summary>
    [Reactive] public partial bool IsRequestingDeviceAdd { get; set; }

    /// <summary>
    /// Detailed info for each skipped non-admin group (admin pubkeys, chat IDs).
    /// Populated during peer device detection and consumed by RequestDeviceAddCommand.
    /// </summary>
    private List<SkippedGroupInfo> _skippedGroupDetails = new();

    // My Profile Dialog
    [Reactive] public partial bool ShowMyProfileDialog { get; set; }
    [Reactive] public partial string? MyNpub { get; set; }
    [Reactive] public partial string? MyNsec { get; set; }
    [Reactive] public partial string? CopyStatusMessage { get; set; }
    [Reactive] public partial string? MyDisplayName { get; set; }
    [Reactive] public partial string? MyName { get; set; }
    [Reactive] public partial string? MyPictureUrl { get; set; }
    [Reactive] public partial string? MyAbout { get; set; }
    [Reactive] public partial bool IsLoadingProfile { get; set; }
    [Reactive] public partial byte[]? MyNpubQrPngBytes { get; set; }

    private readonly Action? _onLogoutRequested;

    /// <summary>
    /// External signer instance, set by ShellViewModel when the user logged in via Amber/NIP-46.
    /// </summary>
    public IExternalSigner? ExternalSigner { get; set; }

    public ChatListViewModel ChatListViewModel { get; }
    public ChatViewModel ChatViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }

    public ReactiveCommand<Unit, Unit> ShowSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowChatsCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowMyProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseMyProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyNpubCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyNsecCommand { get; }
    public ReactiveCommand<Unit, Unit> ReconnectCommand { get; }
    public ReactiveCommand<RelayStatusViewModel, Unit> ReconnectRelayCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRelayListCommand { get; }
    public ReactiveCommand<Unit, Unit> DismissForwardSecrecyBannerCommand { get; }
    public ReactiveCommand<Unit, Unit> RequestDeviceAddCommand { get; }

    public MainViewModel(IMessageService messageService, INostrService nostrService, IStorageService storageService, IMlsService mlsService,
        IPlatformClipboard clipboard, IQrCodeGenerator qrCodeGenerator, IPlatformLauncher launcher, PlatformContext? platform = null,
        Action? onLogoutRequested = null)
    {
        _messageService = messageService;
        _nostrService = nostrService;
        _storageService = storageService;
        _mlsService = mlsService;
        _clipboard = clipboard;
        _qrCodeGenerator = qrCodeGenerator;
        _onLogoutRequested = onLogoutRequested;

        // Initialize child view models
        ChatListViewModel = new ChatListViewModel(messageService, storageService, mlsService, nostrService);
        ChatViewModel = new ChatViewModel(messageService, storageService, nostrService, mlsService, clipboard, platform);
        SettingsViewModel = new SettingsViewModel(nostrService, storageService, mlsService, messageService, launcher, platform);

        // Set up commands
        ShowSettingsCommand = ReactiveCommand.Create(() =>
        {
            CurrentView = SettingsViewModel;
        });

        ShowChatsCommand = ReactiveCommand.Create(() =>
        {
            CurrentView = null; // Main chat view

            // Sync relay statuses with any changes made in settings
            RefreshRelayStatusesFromSettings();

            // Update header name from settings if changed
            var name = SettingsViewModel.DisplayName ?? SettingsViewModel.Username;
            if (!string.IsNullOrEmpty(name))
            {
                HeaderDisplayName = name;
            }
        });

        // Wire SettingsView's on-screen back arrow to navigate back to chats.
        // SettingsView is a UserControl reused across desktop and mobile shells,
        // so the back command must live on the view model rather than relying on
        // a $parent[Window] visual-tree walk (which only works on desktop).
        SettingsViewModel.BackCommand = ShowChatsCommand;

        // When the user toggles "Show Private Notes in chat list", update the chat list immediately
        SettingsViewModel.WhenAnyValue(x => x.ShowDeviceSyncChat)
            .Skip(1) // skip initial value — LoadChatsAsync handles that
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(show => ChatListViewModel.SetDeviceSyncChatVisibility(show));

        // Wire ChatView's back arrow to clear the active chat and return to the list.
        // Only on mobile — on desktop the chat list is always visible side-by-side.
        if (platform is { IsMobile: true })
        {
            ChatViewModel.BackCommand = ReactiveCommand.Create(() =>
            {
                ChatViewModel.ClearChat();
                ChatListViewModel.SelectedChat = null;
            });
        }

        LogoutCommand = ReactiveCommand.CreateFromTask(LogoutAsync);
        SettingsViewModel.LogoutCommand = LogoutCommand;

        ShowMyProfileCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            _logger.LogInformation("ShowMyProfileCommand executed - opening profile dialog");
            MyNpub = CurrentUser?.Npub ?? "No key available";
            MyNsec = CurrentUser?.Nsec; // null for external signer users
            CopyStatusMessage = null;
            MyDisplayName = null;
            MyName = null;
            MyPictureUrl = null;
            MyAbout = null;
            MyNpubQrPngBytes = _qrCodeGenerator.GeneratePng(BuildShareableProfilePayload(), 10);
            ShowMyProfileDialog = true;

            // Show cached profile immediately, then refresh in background
            if (!string.IsNullOrEmpty(CurrentUser?.PublicKeyHex))
            {
                IsLoadingProfile = true;
                try
                {
                    var metadata = await _messageService.GetCachedOrFetchProfileAsync(CurrentUser.PublicKeyHex);
                    if (metadata != null)
                    {
                        MyDisplayName = metadata.DisplayName;
                        MyName = metadata.Name;
                        MyPictureUrl = metadata.Picture;
                        MyAbout = metadata.About;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch profile metadata");
                }
                finally
                {
                    IsLoadingProfile = false;
                }
            }
        });

        CloseMyProfileCommand = ReactiveCommand.Create(() =>
        {
            ShowMyProfileDialog = false;
            CopyStatusMessage = null;
            MyNpubQrPngBytes = null;
        });

        CopyNpubCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (string.IsNullOrEmpty(MyNpub)) return;

            try
            {
                await _clipboard.SetTextAsync(MyNpub);
                CopyStatusMessage = "Copied npub to clipboard!";

                await Task.Delay(2000);
                CopyStatusMessage = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to copy npub to clipboard");
                CopyStatusMessage = "Failed to copy";
            }
        });

        CopyNsecCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (string.IsNullOrEmpty(MyNsec)) return;

            try
            {
                await _clipboard.SetTextAsync(MyNsec);
                CopyStatusMessage = "Copied nsec to clipboard! Clear your clipboard soon.";
                _logger.LogInformation("User copied nsec to clipboard");

                await Task.Delay(3000);
                CopyStatusMessage = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to copy nsec to clipboard");
                CopyStatusMessage = "Failed to copy";
            }
        });

        ReconnectCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            _logger.LogInformation("Reconnecting to all relays...");
            var relays = NostrConstants.DefaultRelays.ToList();

            // Try to use saved relay list
            if (CurrentUser != null)
            {
                var saved = await _storageService.GetUserRelayListAsync(CurrentUser.PublicKeyHex);
                if (saved.Count > 0)
                    relays = saved.Select(r => r.Url).Distinct().ToList();
            }

            await _nostrService.DisconnectAsync();
            await _nostrService.ConnectAsync(relays);

            // Subscriptions are now automatically re-sent by NostrService.ConnectToRelayAsync
            // via ResendSubscriptionsToRelayAsync, so no manual re-subscribe needed here.
        });

        ReconnectRelayCommand = ReactiveCommand.CreateFromTask<RelayStatusViewModel>(async relay =>
        {
            _logger.LogInformation("Reconnecting to relay: {RelayUrl}", relay.Url);
            relay.IsReconnecting = true;
            try
            {
                await _nostrService.ReconnectRelayAsync(relay.Url);
            }
            finally
            {
                relay.IsReconnecting = false;
            }
        });

        ToggleRelayListCommand = ReactiveCommand.Create(() =>
        {
            IsRelayListExpanded = !IsRelayListExpanded;
        });

        DismissForwardSecrecyBannerCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            ShowForwardSecrecyBanner = false;
            SkippedNonAdminGroupsText = null;
            _skippedGroupDetails.Clear();
            try
            {
                await _storageService.SaveSettingAsync("forward_secrecy_banner_dismissed", "true");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist forward-secrecy banner dismissal");
            }
        });

        RequestDeviceAddCommand = ReactiveCommand.CreateFromTask(RequestDeviceAddAsync);

        // Subscribe to chat selection
        ChatListViewModel.WhenAnyValue(x => x.SelectedChat)
            .Where(chat => chat != null)
            .Subscribe(chat =>
            {
                ChatViewModel.LoadChat(chat!.Chat);
            });

        // Subscribe to connection status — track per-relay
        _nostrService.ConnectionStatus
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(status =>
            {
                var existing = RelayStatuses.FirstOrDefault(r => r.Url == status.RelayUrl);
                if (status.Removed)
                {
                    if (existing != null)
                        RelayStatuses.Remove(existing);
                }
                else if (existing != null)
                {
                    existing.IsConnected = status.IsConnected;
                    existing.Error = status.Error;
                }
                else if (!string.IsNullOrEmpty(status.RelayUrl))
                {
                    RelayStatuses.Add(new RelayStatusViewModel
                    {
                        Url = status.RelayUrl,
                        IsConnected = status.IsConnected,
                        Error = status.Error
                    });
                }
                IsConnected = RelayStatuses.Any(r => r.IsConnected);
                UpdateRelayCounts();

                // Sync to SettingsViewModel relays
                var settingsRelay = SettingsViewModel.Relays.FirstOrDefault(r => r.Url == status.RelayUrl);
                if (settingsRelay != null)
                {
                    settingsRelay.IsConnected = status.IsConnected;
                    settingsRelay.Error = status.Error;
                }
            });

        // Subscribe to background metadata updates — refresh profile dialog and header if data changed
        _messageService.MetadataUpdated
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(metadata =>
            {
                // Update profile dialog if it's showing for this user
                if (ShowMyProfileDialog && CurrentUser?.PublicKeyHex == metadata.PublicKeyHex)
                {
                    MyDisplayName = metadata.DisplayName;
                    MyName = metadata.Name;
                    MyPictureUrl = metadata.Picture;
                    MyAbout = metadata.About;
                }

                // Update header display name if it's our own profile
                if (CurrentUser?.PublicKeyHex == metadata.PublicKeyHex)
                {
                    var name = metadata.GetDisplayName();
                    if (!string.IsNullOrEmpty(metadata.DisplayName) ||
                        !string.IsNullOrEmpty(metadata.Name) ||
                        !string.IsNullOrEmpty(metadata.Username))
                    {
                        HeaderDisplayName = name;
                    }
                }
            });

    }

    public async Task InitializeAfterLoginAsync()
    {
        if (CurrentUser == null) return;

        _logger.LogInformation("InitializeAfterLoginAsync starting for {Npub}...", CurrentUser.Npub?[..Math.Min(12, CurrentUser.Npub.Length)]);

        try
        {
        HeaderDisplayName = !string.IsNullOrEmpty(CurrentUser.DisplayName)
            ? CurrentUser.DisplayName
            : "Scramble";

        // Populate profile fields from cached CurrentUser immediately so the header
        // avatar and name render without waiting for the network metadata fetch.
        MyDisplayName = CurrentUser.DisplayName;
        MyName = CurrentUser.Username;
        MyPictureUrl = CurrentUser.AvatarUrl;
        MyAbout = CurrentUser.About;

        // Show chat list spinner while chats load from DB.
        // Only show the header loading indicator if we have NO cached display name —
        // when cached data is available the header already shows the real name/picture,
        // and the background network refresh will silently update it if anything changed.
        ChatListViewModel.IsLoading = true;
        IsHeaderLoading = string.IsNullOrEmpty(CurrentUser.DisplayName);

        _logger.LogDebug("Initializing message service");
        // Run heavy service initialization (SQLite, MLS crypto) on a thread pool
        // thread to avoid blocking the Android UI thread (ANR). The await resumes
        // on the UI thread via the captured SynchronizationContext, keeping
        // subsequent reactive property sets safe.
        await Task.Run(() => _messageService.InitializeAsync());

        // Start notification orchestrator (delegates to platform-specific INotificationService)
        _notificationOrchestrator = new NotificationOrchestrator(_messageService);
        _notificationOrchestrator.Initialize();

        // Set user context on ChatViewModel for signing events and contact resolution.
        // For signer users, PrivateKeyHex is null but PublicKeyHex is still needed.
        ChatViewModel.SetUserContext(CurrentUser.PrivateKeyHex, CurrentUser.PublicKeyHex);

        // Initialize MLS service with user's keys (non-fatal if native library unavailable)
        // ManagedMlsService only needs the public key — pass empty private key for signer users
        if (!string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
        {
            var mlsPrivateKey = CurrentUser.PrivateKeyHex ?? new string('0', 64);
            try
            {
                _logger.LogDebug("Initializing MLS service");
                await Task.Run(() => _mlsService.InitializeAsync(mlsPrivateKey, CurrentUser.PublicKeyHex));

                // NOTE: InitializeAsync already restores service state (KeyPackage private keys,
                // signing keys) from persistence internally via SaveServiceStateAsync/LoadState.
                // A redundant ImportServiceStateAsync call was removed here because it starts with
                // _storedKeyPackages.Clear() — if a KP were generated between the two calls, it
                // would be silently lost. See KeyPackageAuditPersistenceTests.DoubleImport test.

                // Wire up the Nostr event signer for kind 445 MLS group messages.
                // Local key users sign immediately; signer users wire after background restore.
                if (!string.IsNullOrEmpty(CurrentUser.PrivateKeyHex))
                {
                    _mlsService.SetNostrEventSigner(new LocalNostrEventSigner(CurrentUser.PrivateKeyHex));
                    _logger.LogInformation("MLS event signer set to LocalNostrEventSigner");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MLS initialization failed (native library may be unavailable). Continuing without MLS support.");
            }
        }

        // Update SettingsViewModel with user data
        SettingsViewModel.PublicKeyHex = CurrentUser.PublicKeyHex;
        SettingsViewModel.PrivateKeyHex = CurrentUser.PrivateKeyHex;
        SettingsViewModel.Npub = CurrentUser.Npub;
        SettingsViewModel.DisplayName = CurrentUser.DisplayName;
        SettingsViewModel.Username = CurrentUser.Username;
        SettingsViewModel.About = CurrentUser.About;
        SettingsViewModel.AvatarUrl = CurrentUser.AvatarUrl;

        // === FAST PATH: Show chats from DB immediately ===
        _logger.LogDebug("Loading chats from local database");
        await ChatListViewModel.LoadChatsAsync();
        _logger.LogInformation("Chat list loaded from DB — UI is now responsive");

        // === BACKGROUND: All relay/network operations run without blocking the UI ===
        // Suppress the "no internet" banner for 15 s while initial connections + retries complete.
        _connectionGracePeriodActive = true;
        _ = ClearGracePeriodAfterDelayAsync();
        _ = InitializeNetworkAsync();

        // === BACKGROUND: Signer restore runs without blocking chats or relays ===
        // The 60 s get_public_key timeout was blocking the entire init path.
        // Signer is only needed for signing outgoing events (kind 445, NIP-42 AUTH);
        // receiving messages, loading chats, and connecting relays all work without it.
        if (string.IsNullOrEmpty(CurrentUser.PrivateKeyHex) && ExternalSigner?.IsConnected != true)
        {
            _ = RestoreSignerInBackgroundAsync();
        }
        else if (string.IsNullOrEmpty(CurrentUser.PrivateKeyHex) && ExternalSigner?.IsConnected == true)
        {
            WireExternalSigner();
            _signerReady.TrySetResult();
        }
        else
        {
            // Local-key user — no signer needed, mark ready immediately
            _signerReady.TrySetResult();
        }

        _logger.LogInformation("InitializeAfterLoginAsync completed (network init continuing in background)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InitializeAfterLoginAsync failed");
        }
    }

    /// <summary>
    /// Restores the external signer session in the background so it doesn't block
    /// chat loading or relay connections. Retries multiple times with delays because
    /// mobile signers (e.g. Amber) are often offline when Scramble starts — the phone
    /// may be locked, the app backgrounded, or the WebSocket not yet re-established.
    /// When the signer connects, it's wired into NostrService, MediaUploadService,
    /// and MLS on the main thread.
    /// </summary>
    private async Task RestoreSignerInBackgroundAsync()
    {
        if (CurrentUser == null || ExternalSigner == null) return;

        if (string.IsNullOrEmpty(CurrentUser.SignerRelayUrl) ||
            string.IsNullOrEmpty(CurrentUser.SignerRemotePubKey) ||
            string.IsNullOrEmpty(CurrentUser.SignerLocalPrivateKeyHex) ||
            string.IsNullOrEmpty(CurrentUser.SignerLocalPublicKeyHex))
        {
            _logger.LogWarning("Signer user has no persisted session details — cannot auto-reconnect");
            return;
        }

        const int maxAttempts = 5;
        const int delayBetweenAttemptsSeconds = 15;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            _logger.LogInformation("Restoring signer session in background (attempt {Attempt}/{Max})", attempt, maxAttempts);

            if (attempt > 1)
            {
                Observable.Return(System.Reactive.Unit.Default)
                    .ObserveOn(RxSchedulers.MainThreadScheduler)
                    .Subscribe(_ => ChatListViewModel.StatusMessage = $"Reconnecting to signer (attempt {attempt}/{maxAttempts})...");
            }

            try
            {
                var connected = await ExternalSigner.RestoreSessionAsync(
                    CurrentUser.GetSignerRelayUrls(),
                    CurrentUser.SignerRemotePubKey,
                    CurrentUser.SignerLocalPrivateKeyHex,
                    CurrentUser.SignerLocalPublicKeyHex,
                    CurrentUser.SignerSecret);

                if (connected)
                {
                    _logger.LogInformation("Signer session restored successfully on attempt {Attempt}/{Max}", attempt, maxAttempts);

                    if (ExternalSigner.PublicKeyHex != null &&
                        !string.Equals(ExternalSigner.PublicKeyHex, CurrentUser.PublicKeyHex, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Signer reported pubkey ({SignerKey}) does not match stored user pubkey ({UserKey}) — keeping stored identity.",
                            ExternalSigner.PublicKeyHex[..Math.Min(16, ExternalSigner.PublicKeyHex.Length)] + "...",
                            CurrentUser.PublicKeyHex[..Math.Min(16, CurrentUser.PublicKeyHex.Length)] + "...");
                    }

                    // Wire signer and clear status on main thread
                    Observable.Return(System.Reactive.Unit.Default)
                        .ObserveOn(RxSchedulers.MainThreadScheduler)
                        .Subscribe(_ =>
                        {
                            ChatListViewModel.StatusMessage = null;
                            WireExternalSigner();
                            _signerReady.TrySetResult();
                        });
                    return;
                }

                _logger.LogWarning("Signer restore attempt {Attempt}/{Max} failed — signer may be offline", attempt, maxAttempts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Signer restore attempt {Attempt}/{Max} threw an exception", attempt, maxAttempts);
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(delayBetweenAttemptsSeconds));
            }
        }

        // All attempts exhausted
        _logger.LogWarning("Signer session restore failed after {Max} attempts — giving up", maxAttempts);
        _signerReady.TrySetResult(); // Unblock waiters even though signer failed — sync will fail gracefully
        Observable.Return(System.Reactive.Unit.Default)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => ChatListViewModel.StatusMessage = "Signer disconnected. Restart your signer app.");
    }

    /// <summary>
    /// Wires the connected external signer into NostrService, MediaUploadService, and MLS.
    /// Must be called on the main thread.
    /// </summary>
    private void WireExternalSigner()
    {
        if (ExternalSigner?.IsConnected != true) return;

        _nostrService.SetExternalSigner(ExternalSigner);
        ChatViewModel.MediaUploadService?.SetExternalSigner(ExternalSigner);
        _logger.LogInformation("External signer wired to NostrService and MediaUploadService");

        _mlsService.SetNostrEventSigner(new ExternalNostrEventSigner(ExternalSigner));
        _logger.LogInformation("MLS event signer set to ExternalNostrEventSigner (background restore)");
    }

    private void RefreshRelayStatusesFromSettings()
    {
        var settingsRelays = SettingsViewModel.Relays;

        // Remove relay statuses that no longer exist in settings
        var toRemove = RelayStatuses.Where(rs => !settingsRelays.Any(sr => sr.Url == rs.Url)).ToList();
        foreach (var item in toRemove)
        {
            RelayStatuses.Remove(item);
            _logger.LogInformation("Removed relay status for {Url} (removed in settings)", item.Url);
        }

        // Add relay statuses for new relays added in settings
        foreach (var sr in settingsRelays)
        {
            if (!RelayStatuses.Any(rs => rs.Url == sr.Url))
            {
                RelayStatuses.Add(new RelayStatusViewModel
                {
                    Url = sr.Url,
                    IsConnected = sr.IsConnected,
                    Error = sr.Error
                });
                _logger.LogInformation("Added relay status for {Url} (added in settings)", sr.Url);
            }
        }

        IsConnected = RelayStatuses.Any(r => r.IsConnected);
        UpdateRelayCounts();
    }

    private void UpdateRelayCounts()
    {
        ConnectedRelayCount = RelayStatuses.Count(r => r.IsConnected);
        TotalRelayCount = RelayStatuses.Count;
        RelayCountText = $"Relays: {ConnectedRelayCount}/{TotalRelayCount}";

        if (ConnectedRelayCount > 0)
        {
            // At least one relay is connected — clear the no-internet status.
            _connectionGracePeriodActive = false;
            CancelNoInternetDebounce();
            ClearNoInternetStatus();
            return;
        }

        // All relays disconnected (or none configured).
        if (_connectionGracePeriodActive || TotalRelayCount == 0)
        {
            ClearNoInternetStatus();
            return;
        }

        // All configured relays are disconnected, grace period is over.
        // Debounce: wait before showing the status so the auto-reconnect
        // (1 s → 2 s → 4 s → 8 s exponential backoff) can recover from
        // transient drops without aggressively flashing "No internet".
        if (!_showNoInternet && _noInternetDebounceCts == null)
            ScheduleNoInternetBanner();
    }

    private void CancelNoInternetDebounce()
    {
        _noInternetDebounceCts?.Cancel();
        _noInternetDebounceCts = null;
    }

    /// <summary>
    /// Clears the "No internet connection" status message, but only if it was
    /// set by the no-internet logic — avoids stomping signer or operational messages.
    /// </summary>
    private void ClearNoInternetStatus()
    {
        _showNoInternet = false;
        if (ChatListViewModel.StatusMessage == NoInternetStatusMessage)
            ChatListViewModel.StatusMessage = null;
    }

    /// <summary>
    /// Waits 15 seconds before showing the "No internet" status. If any relay
    /// reconnects in the meantime, the timer is cancelled via
    /// <see cref="CancelNoInternetDebounce"/>. The 15 s window covers the first
    /// four auto-reconnect attempts (1 + 2 + 4 + 8 = 15 s).
    /// </summary>
    private void ScheduleNoInternetBanner()
    {
        var cts = new CancellationTokenSource();
        _noInternetDebounceCts = cts;
        _ = ShowNoInternetAfterDelayAsync(cts);
    }

    private async Task ShowNoInternetAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);

            Observable.Return(Unit.Default)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    _noInternetDebounceCts = null;
                    if (ConnectedRelayCount == 0 && TotalRelayCount > 0 && !_connectionGracePeriodActive)
                    {
                        _showNoInternet = true;
                        ChatListViewModel.StatusMessage = NoInternetStatusMessage;
                    }
                });
        }
        catch (OperationCanceledException) { /* debounce cancelled — relay reconnected */ }
        catch (Exception ex) { _logger.LogWarning(ex, "ShowNoInternetAfterDelayAsync failed"); }
    }

    private async Task ClearGracePeriodAfterDelayAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            _connectionGracePeriodActive = false;
            Observable.Return(System.Reactive.Unit.Default)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => UpdateRelayCounts());
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ClearGracePeriodAfterDelayAsync failed"); }
    }

    private async Task AutoDismissForwardSecrecyBannerAsync()
    {
        try
        {
            await Task.Delay(5000);
            ShowForwardSecrecyBanner = false;
            SkippedNonAdminGroupsText = null;
            await _storageService.SaveSettingAsync("forward_secrecy_banner_dismissed", "true");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "AutoDismissForwardSecrecyBannerAsync failed"); }
    }

    /// <summary>
    /// Builds the payload encoded into the user's "share me" QR. Prefers a NIP-21
    /// <c>nostr:nprofile1...</c> URI carrying the pubkey plus up to 3 connected relay
    /// hints — this lets a scanning client locate the user's keypackages and metadata
    /// immediately, instead of guessing relays from a bare npub. Falls back to the bare
    /// npub if the hex pubkey isn't available (e.g. early in startup).
    ///
    /// The <c>nostr:</c> prefix lets generic QR scanners deep-link into any registered
    /// Nostr handler; Scramble's own scanner strips it on the way in.
    /// </summary>
    private string BuildShareableProfilePayload()
    {
        var hex = CurrentUser?.PublicKeyHex;
        if (string.IsNullOrEmpty(hex))
            return MyNpub ?? string.Empty;

        // Cap at 3 relays to keep the QR scannable. Prefer connected relays so the
        // hint actually works for the recipient; if none are connected (offline at
        // the moment of opening the dialog) fall back to all configured relays.
        var relays = RelayStatuses
            .Where(r => r.IsConnected)
            .Select(r => r.Url)
            .Take(3)
            .ToList();
        if (relays.Count == 0)
            relays = RelayStatuses.Select(r => r.Url).Take(3).ToList();

        try
        {
            return "nostr:" + Nip19.EncodeNprofile(hex, relays);
        }
        catch
        {
            // EncodeNprofile only throws on a malformed hex pubkey; the bare npub is
            // still useful even without relay hints.
            return MyNpub ?? string.Empty;
        }
    }

    /// <summary>
    /// Runs all relay/network operations in the background after the UI is already showing cached chats.
    /// </summary>
    private async Task InitializeNetworkAsync()
    {
        try
        {
            // 1. Determine relays to connect to (from cache, then network)
            var relaysToConnect = NostrConstants.DefaultRelays.ToList();
            var shouldPublishNip65 = false;
            try
            {
                var savedRelays = await _storageService.GetUserRelayListAsync(CurrentUser!.PublicKeyHex);
                if (savedRelays.Count > 0)
                {
                    _logger.LogInformation("Using {Count} saved relays from local storage", savedRelays.Count);
                    relaysToConnect = savedRelays.Select(r => r.Url).Distinct().ToList();
                }
                else
                {
                    _logger.LogInformation("No saved relays, fetching NIP-65 relay list from discovery relays");
                    var discoveredRelays = await _nostrService.FetchRelayListAsync(CurrentUser.PublicKeyHex);
                    if (discoveredRelays.Count > 0)
                    {
                        _logger.LogInformation("Discovered {Count} relays via NIP-65", discoveredRelays.Count);
                        await _storageService.SaveUserRelayListAsync(CurrentUser.PublicKeyHex, discoveredRelays);
                        relaysToConnect = discoveredRelays.Select(r => r.Url).Distinct().ToList();
                    }
                    else
                    {
                        // New key with no relay list anywhere — publish defaults after connecting
                        shouldPublishNip65 = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch relay list, using defaults");
            }

            // 2. Set auth credentials before connecting so NIP-42 challenges can be answered
            _nostrService.SetAuthCredentials(CurrentUser.PrivateKeyHex);

            _logger.LogInformation("Connecting to {Count} relays", relaysToConnect.Count);
            await _nostrService.ConnectAsync(relaysToConnect);

            // 2b. Connect to the dedicated sync relay (if configured)
            try
            {
                var syncRelayUrl = await _storageService.GetSettingAsync("sync_relay_url");
                if (!string.IsNullOrEmpty(syncRelayUrl))
                {
                    _logger.LogInformation("Connecting to sync relay: {Url}", syncRelayUrl);
                    await _nostrService.ConnectAsync(syncRelayUrl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to sync relay");
            }

            // 3. Publish default NIP-65 relay list for new keys (must happen after connect)
            if (shouldPublishNip65)
            {
                var defaultPrefs = NostrConstants.DefaultRelays
                    .Select(url => new RelayPreference { Url = url, Usage = RelayUsage.Both })
                    .ToList();
                var defaultUrls = NostrConstants.DefaultRelays.ToList();
                try
                {
                    _logger.LogInformation("Publishing default relay lists (kind 10002, 10050, 10051) for new key");
                    await _nostrService.PublishRelayListAsync(defaultPrefs, CurrentUser!.PrivateKeyHex);
                    await _nostrService.PublishDmRelayListAsync(defaultUrls, CurrentUser.PrivateKeyHex);
                    await _nostrService.PublishKeyPackageRelayListAsync(defaultUrls, CurrentUser.PrivateKeyHex);
                    await _storageService.SaveUserRelayListAsync(CurrentUser.PublicKeyHex, defaultPrefs);
                    _logger.LogInformation("Default relay lists published successfully");
                }
                catch (Exception pubEx)
                {
                    _logger.LogWarning(pubEx, "Failed to publish default relay lists");
                }
            }

            // 4. Connect additional relays + group subscriptions in parallel
            var subscriptionTasks = new List<Task>();

            var chats = await _messageService.GetChatsAsync();

            // Include archived chats in relay subscriptions so messages are still received
            var archivedChats = await _storageService.GetArchivedChatsAsync();
            var allGroupChats = chats.Concat(archivedChats);

            var groupIds = allGroupChats
                .Where(c => (c.Type == ChatType.Group || c.Type == ChatType.DeviceSync)
                         && c.MlsGroupId != null && c.MlsGroupId.Length > 0)
                .Select(c => c.NostrGroupId != null && c.NostrGroupId.Length > 0
                    ? Convert.ToHexString(c.NostrGroupId).ToLowerInvariant()
                    : Convert.ToHexString(c.MlsGroupId!).ToLowerInvariant())
                .Distinct()
                .ToList();

            if (groupIds.Count > 0)
            {
                var latestActivity = chats
                    .Where(c => (c.Type == ChatType.Group || c.Type == ChatType.DeviceSync)
                             && c.MlsGroupId != null)
                    .Select(c => c.LastActivityAt)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();
                DateTimeOffset? since = latestActivity > DateTime.MinValue
                    ? new DateTimeOffset(latestActivity, TimeSpan.Zero).AddMinutes(-5)
                    : null;
                subscriptionTasks.Add(_nostrService.SubscribeToGroupMessagesAsync(groupIds, since));
            }

            // 4b. Ensure group chat relays are connected (normal relays, not bot-only)
            var groupRelayUrls = chats
                .Where(c => c.Type == ChatType.Group && c.RelayUrls.Count > 0)
                .SelectMany(c => c.RelayUrls)
                .Distinct()
                .Where(url => !_nostrService.ConnectedRelayUrls.Any(c =>
                    string.Equals(c.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (groupRelayUrls.Count > 0)
            {
                _logger.LogInformation("Connecting to {Count} missing group chat relay(s): {Relays}",
                    groupRelayUrls.Count, string.Join(", ", groupRelayUrls));
                subscriptionTasks.Add(_nostrService.ConnectAsync(groupRelayUrls));
            }

            // 4c. Connect bot-specific relays (receive-only, excluded from group broadcasts)
            var botRelayUrls = chats
                .Where(c => c.Type == ChatType.Bot && c.RelayUrls.Count > 0)
                .SelectMany(c => c.RelayUrls)
                .Distinct()
                .ToList();

            if (botRelayUrls.Count > 0)
            {
                _logger.LogInformation("Connecting to {Count} bot-specific relays", botRelayUrls.Count);
                subscriptionTasks.Add(_nostrService.ConnectBotRelaysAsync(botRelayUrls));
            }

            // 4d. Connect outbox relays (contacts' preferred relays) for gift wrap delivery/reception
            try
            {
                var contacts = await _storageService.GetContactsAsync(CurrentUser.PublicKeyHex);
                if (contacts.Count > 0)
                {
                    // Collect cached relay URLs for each contact, aggregate by frequency
                    var relayFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var contact in contacts)
                    {
                        // Check cached DM relays (kind 10050) first, then NIP-65
                        var dmCached = await _storageService.GetContactRelayListAsync(contact.PublicKeyHex, 10050);
                        if (dmCached.HasValue && dmCached.Value.Urls.Count > 0)
                        {
                            foreach (var url in dmCached.Value.Urls)
                            {
                                var normalized = url.TrimEnd('/');
                                relayFrequency[normalized] = relayFrequency.GetValueOrDefault(normalized) + 1;
                            }
                        }
                        else
                        {
                            var nip65Cached = await _storageService.GetContactRelayPreferencesAsync(contact.PublicKeyHex);
                            if (nip65Cached.HasValue)
                            {
                                foreach (var pref in nip65Cached.Value.Relays.Where(r =>
                                    r.Usage == RelayUsage.Read || r.Usage == RelayUsage.Both))
                                {
                                    var normalized = pref.Url.TrimEnd('/');
                                    relayFrequency[normalized] = relayFrequency.GetValueOrDefault(normalized) + 1;
                                }
                            }
                        }
                    }

                    if (relayFrequency.Count > 0)
                    {
                        // Take top 12 most common relays, excluding ones we're already connected to
                        var topOutboxRelays = relayFrequency
                            .OrderByDescending(kv => kv.Value)
                            .Select(kv => kv.Key)
                            .Where(url => !_nostrService.ConnectedRelayUrls.Any(c =>
                                string.Equals(c.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                            .Take(12)
                            .ToList();

                        if (topOutboxRelays.Count > 0)
                        {
                            _logger.LogInformation(
                                "Connecting to {Count} outbox relays from {ContactCount} contacts' cached relay lists",
                                topOutboxRelays.Count, contacts.Count);
                            subscriptionTasks.Add(_nostrService.ConnectOutboxRelaysAsync(topOutboxRelays));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect outbox relays for contacts");
            }

            await Task.WhenAll(subscriptionTasks);

            // 4e. Subscribe to welcomes/gift wraps on ALL connected relays (including outbox)
            if (!string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
            {
                await _nostrService.SubscribeToWelcomesAsync(
                    CurrentUser.PublicKeyHex, CurrentUser.PrivateKeyHex);
            }

            // 5. KeyPackage check + profile metadata in parallel (non-blocking)
            var backgroundTasks = new List<Task>();

            if (!string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
            {
                backgroundTasks.Add(CheckKeyPackagesAndMultiDeviceAsync());
                async Task CheckKeyPackagesAndMultiDeviceAsync()
                {
                    try
                    {
                        var myKeyPackages = await _nostrService.FetchKeyPackagesAsync(CurrentUser.PublicKeyHex);

                        // Always ensure this device has a published KeyPackage, regardless of
                        // whether other KPs exist on the relay (they may belong to other devices).
                        // Idempotent: no-ops if a local unused KP already exists.
                        await _messageService.AutoPublishKeyPackageIfNeededAsync();

                        // Publish dummy KeyPackages to mask real device count.
                        // Only when feature is opted in AND a private relay is configured.
                        // Best-effort, non-blocking — failures are logged and ignored.
                        try
                        {
                            var dummyKpEnabled = await _storageService.GetSettingAsync("dummy_keypackages_enabled");
                            var syncRelayUrl = await _storageService.GetSettingAsync("sync_relay_url");
                            if (dummyKpEnabled == "true" && !string.IsNullOrEmpty(syncRelayUrl))
                            {
                                await _messageService.PublishDummyKeyPackagesAsync(syncRelayUrl);
                            }
                        }
                        catch (Exception dummyEx)
                        {
                            _logger.LogDebug(dummyEx, "Dummy KP publishing failed (non-fatal)");
                        }

                        // If we don't have a local slot ID yet (v3 state migration or pre-multi-device),
                        // try to adopt the d-tag from a relay KP that matches our local key material.
                        // This prevents the device from seeing its own KP as a "peer device".
                        _mlsService.TryReconcileSlotId(myKeyPackages);

                        // Multi-device: detect peer device KeyPackages (same pubkey, different slot ID).
                        // Only process genuinely new peer devices — skip slot IDs we've already seen.
                        var localSlotId = _mlsService.GetLocalKeyPackageSlotId();
                        var peerKps = new List<KeyPackage>();
                        if (!string.IsNullOrEmpty(localSlotId))
                        {
                            // Load the set of peer slot IDs we've already processed
                            var seenRaw = await _storageService.GetSettingAsync("seen_peer_slot_ids");
                            var seenSlotIds = new HashSet<string>(
                                seenRaw?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>());

                            // Also skip devices the user has explicitly marked as lost
                            var lostRaw = await _storageService.GetSettingAsync("lost_slot_ids");
                            var lostSlotIds = new HashSet<string>(
                                lostRaw?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>());

                            // Compute dummy slot IDs so we never treat our own decoys as peer devices
                            var dummySlotIds = MessageService.ComputeDummySlotIds(CurrentUser.PrivateKeyHex);

                            peerKps = myKeyPackages
                                .Where(kp => kp.IsCipherSuiteSupported
                                    && !string.IsNullOrEmpty(kp.SlotId)
                                    && kp.SlotId != localSlotId
                                    && !seenSlotIds.Contains(kp.SlotId!)
                                    && !lostSlotIds.Contains(kp.SlotId!)
                                    && !dummySlotIds.Contains(kp.SlotId!))
                                .ToList();

                            if (peerKps.Count > 0)
                            {
                                _logger.LogInformation("Detected {Count} NEW peer device KeyPackage(s), adding to groups",
                                    peerKps.Count);

                                // For Amber/NIP-46 users, the signer may still be connecting in the
                                // background. Wait for it before attempting operations that require
                                // event signing (AddPeerDeviceToGroups, announce, invite, Welcome).
                                if (string.IsNullOrEmpty(CurrentUser.PrivateKeyHex))
                                {
                                    _logger.LogInformation("Peer devices detected — waiting for external signer before adding to groups...");
                                    var signerTimeout = Task.Delay(TimeSpan.FromSeconds(90));
                                    await Task.WhenAny(_signerReady.Task, signerTimeout);
                                    if (!_nostrService.HasExternalSigner)
                                    {
                                        _logger.LogWarning("External signer not available — skipping peer device group additions");
                                        // Still mark as seen so we don't retry every login
                                        foreach (var kp in peerKps)
                                            seenSlotIds.Add(kp.SlotId!);
                                        await _storageService.SaveSettingAsync("seen_peer_slot_ids",
                                            string.Join(",", seenSlotIds));
                                        peerKps.Clear();
                                    }
                                }

                                var allSkippedGroups = new List<string>();
                                var allSkippedDetails = new List<SkippedGroupInfo>();

                                foreach (var peerKp in peerKps)
                                {
                                    try
                                    {
                                        var addResult = await _messageService.AddPeerDeviceToGroupsAsync(peerKp);
                                        _logger.LogInformation(
                                            "Peer device (slot={SlotId}): added to {Added} group(s), {Skipped} non-admin skipped",
                                            peerKp.SlotId?[..Math.Min(16, peerKp.SlotId?.Length ?? 0)],
                                            addResult.AddedCount, addResult.SkippedNonAdminGroups.Count);

                                        allSkippedGroups.AddRange(addResult.SkippedNonAdminGroups);
                                        allSkippedDetails.AddRange(addResult.SkippedGroupDetails);
                                    }
                                    catch (Exception peerEx)
                                    {
                                        _logger.LogWarning(peerEx, "Failed to add peer device (slot={SlotId}) to groups",
                                            peerKp.SlotId?[..Math.Min(16, peerKp.SlotId?.Length ?? 0)]);
                                    }

                                    // Mark this slot ID as seen regardless of success/failure,
                                    // so we don't repeatedly attempt to add dead/orphan devices.
                                    seenSlotIds.Add(peerKp.SlotId!);
                                }

                                // Persist updated seen set
                                await _storageService.SaveSettingAsync("seen_peer_slot_ids",
                                    string.Join(",", seenSlotIds));

                                // Show forward-secrecy banner if not previously dismissed
                                var dismissed = await _storageService.GetSettingAsync("forward_secrecy_banner_dismissed");
                                if (dismissed != "true")
                                {
                                    if (allSkippedGroups.Count > 0)
                                    {
                                        SkippedNonAdminGroupsText = string.Join(", ", allSkippedGroups.Distinct());
                                        _skippedGroupDetails = allSkippedDetails;
                                    }
                                    ShowForwardSecrecyBanner = true;
                                }
                            }
                        }

                        // ── Device-sync group: create/load and invite new peer devices ──
                        // This runs regardless of whether a local slot ID exists, so that
                        // the sync group (Private Notes) is always created on login.
                        //
                        // For Amber/NIP-46 users, the signer may still be connecting in the
                        // background. Wait for it before attempting operations that require
                        // event signing (announce, invite, Welcome).
                        if (string.IsNullOrEmpty(CurrentUser.PrivateKeyHex) && !_nostrService.HasExternalSigner)
                        {
                            _logger.LogInformation("DeviceSync: waiting for external signer before sync operations...");
                            var signerTimeout = Task.Delay(TimeSpan.FromSeconds(90));
                            var completed = await Task.WhenAny(_signerReady.Task, signerTimeout);
                            if (completed == signerTimeout)
                            {
                                _logger.LogWarning("DeviceSync: signer not ready after 90 s — skipping sync operations this session");
                            }
                            else
                            {
                                _logger.LogInformation("DeviceSync: signer ready, proceeding with sync operations");
                            }

                            // Even if signer timed out, still create the group (local-only).
                            // But skip announce/invite if signer never connected.
                            if (!_nostrService.HasExternalSigner)
                            {
                                _logger.LogWarning("DeviceSync: no external signer available — creating group only, skipping announce/invite");
                                try
                                {
                                    await _messageService.GetOrCreateDeviceSyncGroupAsync();
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to create device-sync group");
                                }
                                // Skip the rest of the sync flow — no signer
                                return;
                            }
                        }

                        try
                        {
                            var syncChat = await _messageService.GetOrCreateDeviceSyncGroupAsync();
                            _logger.LogInformation("Device-sync group ready: {ChatId}", syncChat.Id);

                            // Subscribe to kind 445 messages for the sync group so we receive
                            // messages in this session (the bulk subscription at login may have
                            // run before the sync group existed).
                            if (syncChat.MlsGroupId != null && syncChat.MlsGroupId.Length > 0)
                            {
                                var subGroupId = syncChat.NostrGroupId != null && syncChat.NostrGroupId.Length > 0
                                    ? Convert.ToHexString(syncChat.NostrGroupId).ToLowerInvariant()
                                    : Convert.ToHexString(syncChat.MlsGroupId).ToLowerInvariant();
                                var since = new DateTimeOffset(syncChat.LastActivityAt, TimeSpan.Zero).AddMinutes(-5);
                                await _nostrService.SubscribeToGroupMessagesAsync(new[] { subGroupId }, since);
                                _logger.LogInformation("Subscribed to kind 445 for sync group {GroupId}",
                                    subGroupId[..Math.Min(16, subGroupId.Length)]);
                            }

                            // Announce this device's OS + slot ID so the user can see which
                            // devices are alive and what they're running from Private Notes.
                            try
                            {
                                await _messageService.AnnounceDeviceToSyncGroupAsync(GetCurrentOsName());
                            }
                            catch (Exception announceEx)
                            {
                                _logger.LogWarning(announceEx, "Failed to post device announcement");
                            }

                            // Invite peer devices to the sync group using a deterministic
                            // inviter protocol: only the device with the lexicographically
                            // SMALLEST slot ID sends invites. This prevents the race where
                            // both devices invite each other simultaneously and end up in
                            // different groups. The non-inviter waits to receive a Welcome.
                            if (!string.IsNullOrEmpty(localSlotId))
                            {
                                var lostRaw2 = await _storageService.GetSettingAsync("lost_slot_ids");
                                var lostSlotIds2 = new HashSet<string>(
                                    lostRaw2?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>());
                                var dummySlotIds2 = MessageService.ComputeDummySlotIds(CurrentUser.PrivateKeyHex);

                                var syncPeerKps = myKeyPackages
                                    .Where(kp => kp.IsCipherSuiteSupported
                                        && !string.IsNullOrEmpty(kp.SlotId)
                                        && kp.SlotId != localSlotId
                                        && !lostSlotIds2.Contains(kp.SlotId!)
                                        && !dummySlotIds2.Contains(kp.SlotId!)
                                        && kp.ExpiresAt > DateTime.UtcNow) // skip expired KPs
                                    // Deduplicate by slot ID — keep only the newest KP per device
                                    .GroupBy(kp => kp.SlotId)
                                    .Select(g => g.OrderByDescending(kp => kp.CreatedAt).First())
                                    .ToList();

                                // Deterministic inviter: only smallest slot ID invites.
                                var isDesignatedInviter = syncPeerKps.Count > 0 &&
                                    syncPeerKps.All(kp => string.Compare(localSlotId, kp.SlotId, StringComparison.Ordinal) < 0);

                                if (isDesignatedInviter)
                                {
                                    // Check if this group has already converged (peer joined
                                    // and we've exchanged messages). Don't reset a working group.
                                    var converged = await _storageService.GetSettingAsync("sync_group_converged");

                                    foreach (var peerKp in syncPeerKps)
                                    {
                                        try
                                        {
                                            await _messageService.InvitePeerToSyncGroupAsync(peerKp, syncChat.Id);
                                            await _storageService.SaveSettingAsync("sync_group_converged", "true");
                                            _logger.LogInformation("Invited peer device (slot={SlotId}) to sync group",
                                                peerKp.SlotId?[..Math.Min(16, peerKp.SlotId?.Length ?? 0)]);
                                        }
                                        catch (Exception syncEx)
                                        {
                                            // Invite failed — likely "already a member" from a
                                            // diverged state (both devices ended up in different
                                            // groups after a race). Only reset if the group hasn't
                                            // previously converged (prevents reset loop on working groups).
                                            if (converged == "true")
                                            {
                                                _logger.LogDebug(syncEx,
                                                    "Sync invite for slot={SlotId} failed but group previously converged — skipping reset",
                                                    peerKp.SlotId?[..Math.Min(16, peerKp.SlotId?.Length ?? 0)]);
                                                continue;
                                            }

                                            _logger.LogWarning(syncEx, "Sync invite failed for slot={SlotId}, attempting group reset",
                                                peerKp.SlotId?[..Math.Min(16, peerKp.SlotId?.Length ?? 0)]);

                                            try
                                            {
                                                await _messageService.ResetDeviceSyncGroupAsync();
                                                syncChat = await _messageService.GetOrCreateDeviceSyncGroupAsync();
                                                await _messageService.InvitePeerToSyncGroupAsync(peerKp, syncChat.Id);
                                                await _storageService.SaveSettingAsync("sync_group_converged", "true");
                                                _logger.LogInformation(
                                                    "Sync group reset succeeded — invited peer (slot={SlotId}) to fresh group",
                                                    peerKp.SlotId?[..Math.Min(16, peerKp.SlotId?.Length ?? 0)]);
                                            }
                                            catch (Exception retryEx)
                                            {
                                                _logger.LogWarning(retryEx,
                                                    "Sync group reset+retry also failed for slot={SlotId}",
                                                    peerKp.SlotId?[..Math.Min(16, peerKp.SlotId?.Length ?? 0)]);
                                            }
                                        }
                                    }
                                }
                                else if (syncPeerKps.Count > 0)
                                {
                                    _logger.LogInformation(
                                        "Not the designated sync inviter (localSlot={LocalSlot}); waiting for Welcome from peer",
                                        localSlotId[..Math.Min(8, localSlotId.Length)]);

                                    // If we're not the inviter and our sync group was created
                                    // locally (not received via Welcome) and is older than 2 min,
                                    // it's likely a stale orphan from before the fix. Delete it
                                    // so the Welcome handler has a clean slate when it arrives.
                                    if (string.IsNullOrEmpty(syncChat.WelcomeNostrEventId) &&
                                        syncChat.CreatedAt < DateTime.UtcNow.AddMinutes(-2))
                                    {
                                        _logger.LogInformation("Deleting stale locally-created sync group (not the inviter)");
                                        await _messageService.ResetDeviceSyncGroupAsync();
                                    }
                                }
                            }
                        }
                        catch (Exception syncEx)
                        {
                            _logger.LogWarning(syncEx, "Failed to create/load device-sync group");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to check or auto-publish KeyPackage");
                    }
                }

                // Restore NIP-17 bot/agent chats from relay history. The live kind 1059
                // subscription should also pick these up, but explicit fetch ensures we
                // don't miss anything on a fresh device or after a profile reset, and
                // chats land in the Agents (DVM) tab without the user re-adding devices.
                backgroundTasks.Add(RescanNip17ChatsInBackgroundAsync());
                async Task RescanNip17ChatsInBackgroundAsync()
                {
                    try
                    {
                        await _messageService.RescanNip17ChatsAsync();
                        // Refresh the chat list so newly-created bot chats appear in the UI
                        await ChatListViewModel.LoadChatsAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to rescan NIP-17 chats");
                    }
                }

                backgroundTasks.Add(FetchOwnProfileMetadataAsync());
                async Task FetchOwnProfileMetadataAsync()
                {
                    try
                    {
                        // Show cached profile immediately; background relay refresh
                        // will emit MetadataUpdated if anything changed (handled by
                        // the subscription in the constructor).
                        var metadata = await _messageService.GetCachedOrFetchProfileAsync(CurrentUser.PublicKeyHex);
                        if (metadata != null)
                        {
                            MyDisplayName = metadata.DisplayName;
                            MyName = metadata.Name;
                            MyPictureUrl = metadata.Picture;
                            MyAbout = metadata.About;
                            // Only update header if metadata has a real name — don't downgrade
                            // from a cached display name to a truncated npub
                            var fetchedName = metadata.GetDisplayName();
                            if (!string.IsNullOrEmpty(metadata.DisplayName) ||
                                !string.IsNullOrEmpty(metadata.Name) ||
                                !string.IsNullOrEmpty(metadata.Username))
                            {
                                HeaderDisplayName = fetchedName;
                                // Persist real name back to CurrentUser and DB so restarts don't
                                // show the placeholder until metadata is fetched again
                                CurrentUser.DisplayName = fetchedName;
                                SettingsViewModel.DisplayName = fetchedName;
                                try
                                {
                                    await _storageService.SaveCurrentUserAsync(CurrentUser);
                                    _logger.LogInformation("Persisted fetched display name '{Name}' for current user", fetchedName);
                                }
                                catch (Exception saveEx)
                                {
                                    _logger.LogWarning(saveEx, "Failed to persist fetched display name");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch own profile metadata");
                    }
                    finally
                    {
                        IsHeaderLoading = false;
                    }
                }
            }
            else
            {
                IsHeaderLoading = false;
            }

            await Task.WhenAll(backgroundTasks);
            _logger.LogInformation("Background network initialization completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background network initialization failed");
            IsHeaderLoading = false;
        }
    }

    private Task LogoutAsync()
    {
        _logger.LogInformation("Logout requested — delegating to ShellViewModel");
        _notificationOrchestrator?.Dispose();
        _notificationOrchestrator = null;
        RelayStatuses.Clear();
        ChatListViewModel.ClearChats();
        ChatViewModel.ClearChat();
        CurrentUser = null;
        IsLoggedIn = false;
        HeaderDisplayName = "Scramble";

        _onLogoutRequested?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Send NIP-17 DMs to the admins of each skipped non-admin group, asking them
    /// to add this device. Deduplicates messages so each admin receives at most one
    /// DM listing all groups they administer.
    /// </summary>
    private async Task RequestDeviceAddAsync()
    {
        if (_skippedGroupDetails.Count == 0 || CurrentUser == null)
            return;

        IsRequestingDeviceAdd = true;
        try
        {
            // Build a map: admin pubkey -> list of group names they administer
            var adminToGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in _skippedGroupDetails)
            {
                foreach (var adminPubkey in group.AdminPubkeys)
                {
                    // Don't send a message to ourselves
                    if (string.Equals(adminPubkey, CurrentUser.PublicKeyHex, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!adminToGroups.TryGetValue(adminPubkey, out var groups))
                    {
                        groups = new List<string>();
                        adminToGroups[adminPubkey] = groups;
                    }
                    if (!groups.Contains(group.GroupName))
                        groups.Add(group.GroupName);
                }
            }

            if (adminToGroups.Count == 0)
            {
                _logger.LogWarning("RequestDeviceAdd: no admin pubkeys to message (all are self)");
                // Still dismiss since there's nothing actionable
                await DismissForwardSecrecyBannerCommand.Execute();
                return;
            }

            var sentCount = 0;
            foreach (var (adminPubkey, groupNames) in adminToGroups)
            {
                try
                {
                    var groupList = string.Join(", ", groupNames);
                    var content = groupNames.Count == 1
                        ? $"Hi, I've linked a new device to my account. Could you re-add me to \"{groupNames[0]}\" so my new device can receive messages? Thanks!"
                        : $"Hi, I've linked a new device to my account. Could you re-add me to these groups so my new device can receive messages: {groupList}. Thanks!";

                    var rumorTags = new List<List<string>> { new() { "p", adminPubkey } };

                    await _nostrService.PublishGiftWrapAsync(
                        rumorKind: 14,
                        content: content,
                        rumorTags: rumorTags,
                        senderPrivateKeyHex: CurrentUser.PrivateKeyHex,
                        senderPublicKeyHex: CurrentUser.PublicKeyHex,
                        recipientPublicKeyHex: adminPubkey);

                    sentCount++;
                    _logger.LogInformation("Sent device-add request DM to admin {Admin} for {Count} group(s)",
                        adminPubkey[..Math.Min(16, adminPubkey.Length)], groupNames.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send device-add request to admin {Admin}",
                        adminPubkey[..Math.Min(16, adminPubkey.Length)]);
                }
            }

            _logger.LogInformation("Device-add requests sent to {Count} admin(s)", sentCount);

            // Update banner text to confirm requests were sent, then auto-dismiss
            SkippedNonAdminGroupsText = $"Requests sent to {sentCount} admin(s). They will re-add you when they see the message.";
            _skippedGroupDetails.Clear();

            // Auto-dismiss after a short delay so user sees the confirmation
            _ = AutoDismissForwardSecrecyBannerAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send device-add requests");
        }
        finally
        {
            IsRequestingDeviceAdd = false;
        }
    }

    private static string GetCurrentOsName() =>
        OperatingSystem.IsAndroid() ? "Android" :
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsMacOS() ? "macOS" :
        OperatingSystem.IsLinux() ? "Linux" :
        RuntimeInformation.OSDescription;
}

public partial class RelayStatusViewModel : ViewModelBase
{
    [Reactive] public partial string Url { get; set; } = string.Empty;
    [Reactive] public partial bool IsConnected { get; set; }
    [Reactive] public partial string? Error { get; set; }
    [Reactive] public partial bool IsReconnecting { get; set; }

    /// <summary>Short display name: strips wss:// and trailing /</summary>
    public string DisplayName => Url.Replace("wss://", "").Replace("ws://", "").TrimEnd('/');
}
