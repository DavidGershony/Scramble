using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reflection;
using System.Reactive.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Scramble.Core;
using Scramble.Core.Logging;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.Services;

namespace Scramble.Presentation.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly INostrService _nostrService;
    private readonly IStorageService _storageService;
    private readonly IMlsService _mlsService;
    private readonly IMessageService _messageService;
    private readonly IPlatformLauncher _launcher;
    private readonly PlatformContext? _platform;
    private bool _isRestoringSettings;

    [Reactive] public partial string? PublicKeyHex { get; set; }
    [Reactive] public partial string? PrivateKeyHex { get; set; }
    [Reactive] public partial string? Npub { get; set; }
    [Reactive] public partial string? DisplayName { get; set; }
    [Reactive] public partial string? Username { get; set; }
    [Reactive] public partial string? About { get; set; }
    [Reactive] public partial string? AvatarUrl { get; set; }

    [Reactive] public partial string NewRelayUrl { get; set; } = string.Empty;

    // Log Viewer
    [Reactive] public partial bool ShowLogViewer { get; set; }
    [Reactive] public partial string LogContent { get; set; } = string.Empty;

    // Key Package
    [Reactive] public partial bool IsPublishingKeyPackage { get; set; }
    [Reactive] public partial string? KeyPackageStatus { get; set; }
    [Reactive] public partial bool KeyPackageSuccess { get; set; }

    // Key Package Audit
    [Reactive] public partial bool IsAuditingKeyPackages { get; set; }
    [Reactive] public partial string? AuditStatus { get; set; }
    [Reactive] public partial KeyPackageAuditResult? LastAuditResult { get; set; }

    // MLS Reset
    [Reactive] public partial bool IsResettingMls { get; set; }
    [Reactive] public partial string? MlsResetStatus { get; set; }
    [Reactive] public partial bool MlsResetSuccess { get; set; }
    [Reactive] public partial bool ShowMlsResetConfirmation { get; set; }

    // NIP-13 Proof of Work
    [Reactive] public partial bool ProofOfWorkEnabled { get; set; }
    [Reactive] public partial double ProofOfWorkDifficulty { get; set; } = 21;

    // Relay list publish
    [Reactive] public partial string? PublishRelayListStatus { get; set; }

    // Per-purpose relay overrides (kind 10050 DM, kind 10051 KeyPackage)
    [Reactive] public partial string NewDmRelayUrl { get; set; } = string.Empty;
    [Reactive] public partial string NewKpRelayUrl { get; set; } = string.Empty;
    [Reactive] public partial bool ShowDmRelaySection { get; set; }
    [Reactive] public partial bool ShowKpRelaySection { get; set; }
    [Reactive] public partial bool ShowGeneralRelaySection { get; set; }

    // Relay picker — add from popular list or custom input
    [Reactive] public partial string CustomRelayDomain { get; set; } = string.Empty;
    [Reactive] public partial bool AddToGeneral { get; set; } = true;
    [Reactive] public partial bool AddToDm { get; set; }
    [Reactive] public partial bool AddToKp { get; set; }

    // NIP-65 published relay list (fetched from network)
    [Reactive] public partial bool IsFetchingNip65 { get; set; }
    [Reactive] public partial string? Nip65Status { get; set; }
    public ObservableCollection<RelayViewModel> Nip65Relays { get; } = new();

    // Profile editing (hidden when using external signer — no private key to sign kind 0)
    [Reactive] public partial bool CanEditProfile { get; set; }

    // Profile publish confirmation
    [Reactive] public partial bool ShowPublishConfirmation { get; set; }
    [Reactive] public partial bool IsPublishingProfile { get; set; }
    [Reactive] public partial string? PublishProfileStatus { get; set; }

    // MIP-04 media loading
    [Reactive] public partial bool IsMip04Enabled { get; set; }
    [Reactive] public partial string? Mip04DependencyWarning { get; set; }

    // Blossom server — user edits domain only; full URL is https://{domain}
    [Reactive] public partial string BlossomServerDomain { get; set; } = "blossom.primal.net";
    [Reactive] public partial bool IsEditingBlossom { get; set; }
    [Reactive] public partial string? BlossomStatus { get; set; }
    [Reactive] public partial bool BlossomStatusIsError { get; set; }

    // Notifications — master toggle + mode selection (all default to off)
    [Reactive] public partial bool NotificationsEnabled { get; set; }
    [Reactive] public partial bool NotificationModeBackground { get; set; }

    /// <summary>Whether the app is running as a desktop (non-mobile) host.</summary>
    public bool IsDesktop => _platform is null or { IsMobile: false };

    /// <summary>Whether the app is running as a mobile host (Android, etc.).</summary>
    public bool IsMobile => _platform is { IsMobile: true };

    // My Devices
    [Reactive] public partial bool ShowDeviceSection { get; set; } = true;
    [Reactive] public partial bool IsFetchingDevices { get; set; }
    [Reactive] public partial string? DeviceStatus { get; set; }
    [Reactive] public partial string? LocalSlotId { get; set; }
    [Reactive] public partial string? LocalSlotIdShort { get; set; }
    public ObservableCollection<DeviceViewModel> MyDevices { get; } = new();

    // Device sync
    [Reactive] public partial string SyncRelayDomain { get; set; } = string.Empty;
    [Reactive] public partial bool IsEditingSyncRelay { get; set; }
    [Reactive] public partial string? SyncRelayStatus { get; set; }
    [Reactive] public partial bool ShowDeviceSyncChat { get; set; }
    [Reactive] public partial bool DummyKeyPackagesEnabled { get; set; }
    [Reactive] public partial bool NotificationModePush { get; set; }
    [Reactive] public partial string NotificationServerNpub { get; set; } = string.Empty;
    [Reactive] public partial string NotificationServerRelay { get; set; } = string.Empty;
    [Reactive] public partial string NotificationPushUrl { get; set; } = string.Empty;
    [Reactive] public partial string NotificationVerifyCode { get; set; } = string.Empty;
    [Reactive] public partial string? NotificationRegistrationStatus { get; set; }
    [Reactive] public partial bool NotificationAwaitingVerification { get; set; }

    /// <summary>
    /// Platform-specific delegate to request runtime permissions.
    /// Set by the Android host in App.axaml.cs. Null on platforms that don't need it.
    /// </summary>
    public static Func<string[], Task<bool>>? PermissionRequestFunc { get; set; }

    // Library versions
    public string MarmotCsVersion { get; } = GetPackageVersion("MarmotCs.Core");
    public string DotnetMlsVersion { get; } = GetPackageVersion("DotnetMls");

    // Theme selection
    [Reactive] public partial int SelectedThemeIndex { get; set; } = 4;
    public static string[] AvailableThemeNames { get; set; } = Array.Empty<string>();
    public static Action<int>? OnThemeChanged { get; set; }

    public ObservableCollection<RelayViewModel> Relays { get; } = new();
    public ObservableCollection<string> DmRelays { get; } = new();
    public ObservableCollection<string> KpRelays { get; } = new();
    public ObservableCollection<RelayPickerItem> PopularRelays { get; } = new();
    public ObservableCollection<UnifiedRelayViewModel> AllRelays { get; } = new();

    /// <summary>Popular relay domains shown in the relay picker.</summary>
    internal static readonly string[] PopularRelayDomains =
    {
        "relay.damus.io",
        "nos.lol",
        "relay.nostr.band",
        "relay.snort.social",
        "relay.primal.net",
        "nostr.wine",
        "relay.angor.io",
        "relay2.angor.io",
        "purplepag.es",
        "relay.nsec.app",
    };

    public ReactiveCommand<Unit, Unit> SaveProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmPublishProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelPublishProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> AddRelayCommand { get; }
    public ReactiveCommand<RelayViewModel, Unit> RemoveRelayCommand { get; }
    public ReactiveCommand<Unit, Unit> AddDmRelayCommand { get; }
    public ReactiveCommand<string, Unit> RemoveDmRelayCommand { get; }
    public ReactiveCommand<Unit, Unit> AddKpRelayCommand { get; }
    public ReactiveCommand<string, Unit> RemoveKpRelayCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDmRelaySectionCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleKpRelaySectionCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleGeneralRelaySectionCommand { get; }
    public ReactiveCommand<Unit, Unit> AddPickedRelaysCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCustomRelayCommand { get; }
    public ReactiveCommand<Unit, Unit> FetchNip65Command { get; }
    public ReactiveCommand<Unit, Unit> ViewLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseLogViewerCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLogFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> PublishKeyPackageCommand { get; }
    public ReactiveCommand<Unit, Unit> AuditKeyPackagesCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetMlsDataCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmResetMlsCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelResetMlsCommand { get; }
    public ReactiveCommand<Unit, Unit> PublishRelayListCommand { get; }
    public ReactiveCommand<RelayViewModel, Unit> CycleRelayUsageCommand { get; }
    public ReactiveCommand<Unit, Unit> RegisterNotificationsCommand { get; }
    public ReactiveCommand<Unit, Unit> VerifyNotificationsCommand { get; }
    public ReactiveCommand<Unit, Unit> GeneratePushTopicCommand { get; }
    public ReactiveCommand<Unit, Unit> SubscribeInNtfyCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveBlossomCommand { get; }
    public ReactiveCommand<Unit, Unit> EditBlossomCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditBlossomCommand { get; }
    public ReactiveCommand<Unit, Unit> ReconnectDisconnectedCommand { get; }
    public ReactiveCommand<Unit, Unit> FetchDevicesCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDeviceSectionCommand { get; }
    public ReactiveCommand<DeviceViewModel, Unit> MarkDeviceAsLostCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveSyncRelayCommand { get; }
    public ReactiveCommand<Unit, Unit> EditSyncRelayCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditSyncRelayCommand { get; }
    public ReactiveCommand<UnifiedRelayViewModel, Unit> SyncRelayMembershipCommand { get; }
    public ReactiveCommand<UnifiedRelayViewModel, Unit> RemoveUnifiedRelayCommand { get; }
    public ReactiveCommand<UnifiedRelayViewModel, Unit> CycleUnifiedRelayUsageCommand { get; }

    /// <summary>
    /// Assigned by the parent (<see cref="MainViewModel"/>) — shell-agnostic navigation
    /// back to the chat list. Works in both Window-hosted (desktop) and overlay (mobile) layouts.
    /// </summary>
    [Reactive] public partial ReactiveCommand<Unit, Unit>? BackCommand { get; set; }

    /// <summary>
    /// Assigned by the parent (<see cref="MainViewModel"/>) so SettingsView does not need
    /// a <c>$parent[Window]</c> visual-tree walk that breaks in the Avalonia mobile shell.
    /// </summary>
    [Reactive] public partial ReactiveCommand<Unit, Unit>? LogoutCommand { get; set; }

    public SettingsViewModel(INostrService nostrService, IStorageService storageService, IMlsService mlsService, IMessageService messageService, IPlatformLauncher launcher, PlatformContext? platform = null)
    {
        _logger = LoggingConfiguration.CreateLogger<SettingsViewModel>();
        _nostrService = nostrService;
        _storageService = storageService;
        _mlsService = mlsService;
        _messageService = messageService;
        _launcher = launcher;
        _platform = platform;

        SaveProfileCommand = ReactiveCommand.CreateFromTask(SaveProfileAsync);
        ConfirmPublishProfileCommand = ReactiveCommand.CreateFromTask(PublishProfileAsync);
        CancelPublishProfileCommand = ReactiveCommand.Create(() => { ShowPublishConfirmation = false; });

        // Relay add validation — user enters domain only, we prepend wss://
        var canAddRelay = this.WhenAnyValue(x => x.NewRelayUrl,
            url => !string.IsNullOrWhiteSpace(url) && IsValidDomain(url));

        AddRelayCommand = ReactiveCommand.CreateFromTask(AddRelayAsync, canAddRelay);
        RemoveRelayCommand = ReactiveCommand.Create<RelayViewModel>(RemoveRelay);

        // Per-purpose relay commands
        var canAddDmRelay = this.WhenAnyValue(x => x.NewDmRelayUrl,
            url => !string.IsNullOrWhiteSpace(url) && IsValidDomain(url));
        var canAddKpRelay = this.WhenAnyValue(x => x.NewKpRelayUrl,
            url => !string.IsNullOrWhiteSpace(url) && IsValidDomain(url));

        AddDmRelayCommand = ReactiveCommand.CreateFromTask(AddDmRelayAsync, canAddDmRelay);
        RemoveDmRelayCommand = ReactiveCommand.CreateFromTask<string>(RemoveDmRelayAsync);
        AddKpRelayCommand = ReactiveCommand.CreateFromTask(AddKpRelayAsync, canAddKpRelay);
        RemoveKpRelayCommand = ReactiveCommand.CreateFromTask<string>(RemoveKpRelayAsync);
        ToggleDmRelaySectionCommand = ReactiveCommand.Create(() => { ShowDmRelaySection = !ShowDmRelaySection; });
        ToggleKpRelaySectionCommand = ReactiveCommand.Create(() => { ShowKpRelaySection = !ShowKpRelaySection; });
        ToggleGeneralRelaySectionCommand = ReactiveCommand.Create(() => { ShowGeneralRelaySection = !ShowGeneralRelaySection; });

        // Relay picker commands
        AddPickedRelaysCommand = ReactiveCommand.CreateFromTask(AddPickedRelaysAsync);
        var canAddCustom = this.WhenAnyValue(x => x.CustomRelayDomain,
            d => !string.IsNullOrWhiteSpace(d) && IsValidDomain(d));
        AddCustomRelayCommand = ReactiveCommand.CreateFromTask(AddCustomRelayAsync, canAddCustom);

        // NIP-65 fetch
        FetchNip65Command = ReactiveCommand.CreateFromTask(FetchNip65Async);

        // Populate the popular relay picker
        foreach (var domain in PopularRelayDomains)
            PopularRelays.Add(new RelayPickerItem { Domain = domain });

        // Log viewer commands
        ViewLogsCommand = ReactiveCommand.Create(() =>
        {
            LogContent = LoggingConfiguration.GetRecentLogContents(1000);
            ShowLogViewer = true;
        });

        CloseLogViewerCommand = ReactiveCommand.Create(() =>
        {
            ShowLogViewer = false;
        });

        RefreshLogsCommand = ReactiveCommand.Create(() =>
        {
            LogContent = LoggingConfiguration.GetRecentLogContents(1000);
        });

        OpenLogFolderCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                var logDir = LoggingConfiguration.LogDirectory;
                if (Directory.Exists(logDir))
                {
                    _launcher.OpenFolder(logDir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open log folder");
            }
        });

        ExportLogsCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                var logFiles = LoggingConfiguration.GetLogFiles().ToList();
                if (logFiles.Count == 0)
                {
                    _logger.LogWarning("No log files found to export");
                    return;
                }

                var latestLogFile = logFiles.First();
                _launcher.ShareFile(latestLogFile, "text/plain", $"Scramble Logs - {DateTime.Now:yyyy-MM-dd}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export logs");
            }
        });

        // Key package commands
        PublishKeyPackageCommand = ReactiveCommand.CreateFromTask(PublishKeyPackageAsync);
        AuditKeyPackagesCommand = ReactiveCommand.CreateFromTask(AuditKeyPackagesAsync);

        // MLS reset commands (two-step: show confirmation, then execute)
        ResetMlsDataCommand = ReactiveCommand.Create(() => { ShowMlsResetConfirmation = true; MlsResetStatus = null; });
        ConfirmResetMlsCommand = ReactiveCommand.CreateFromTask(ResetMlsDataAsync);
        CancelResetMlsCommand = ReactiveCommand.Create(() => { ShowMlsResetConfirmation = false; });

        // Relay list commands
        PublishRelayListCommand = ReactiveCommand.CreateFromTask(PublishRelayListAsync);
        CycleRelayUsageCommand = ReactiveCommand.CreateFromTask<RelayViewModel>(CycleRelayUsageAsync);

        // Notification registration
        RegisterNotificationsCommand = ReactiveCommand.CreateFromTask(RegisterNotificationsAsync);
        VerifyNotificationsCommand = ReactiveCommand.CreateFromTask(VerifyNotificationsAsync);
        GeneratePushTopicCommand = ReactiveCommand.Create(GeneratePushTopic);
        SubscribeInNtfyCommand = ReactiveCommand.Create(SubscribeInNtfy);

        // Blossom server edit/save
        SaveBlossomCommand = ReactiveCommand.CreateFromTask(SaveBlossomAsync);
        EditBlossomCommand = ReactiveCommand.Create(() => { IsEditingBlossom = true; BlossomStatus = null; });
        CancelEditBlossomCommand = ReactiveCommand.Create(() => { IsEditingBlossom = false; BlossomStatus = null; });

        // Reconnect only relays that are currently offline
        ReconnectDisconnectedCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var disconnected = Relays.Where(r => !r.IsConnected).Select(r => r.Url).ToList();
            foreach (var url in disconnected)
            {
                try
                {
                    await _nostrService.ReconnectRelayAsync(url);
                    // Sync connection state to unified view
                    var unified = AllRelays.FirstOrDefault(u =>
                        string.Equals(u.Url, url, StringComparison.OrdinalIgnoreCase));
                    if (unified != null) unified.IsConnected = true;
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to reconnect to {Url}", url); }
            }
        });

        // My Devices
        FetchDevicesCommand = ReactiveCommand.CreateFromTask(FetchDevicesAsync);
        ToggleDeviceSectionCommand = ReactiveCommand.Create(() => { ShowDeviceSection = !ShowDeviceSection; });
        MarkDeviceAsLostCommand = ReactiveCommand.CreateFromTask<DeviceViewModel>(MarkDeviceAsLostAsync);
        SaveSyncRelayCommand = ReactiveCommand.CreateFromTask(SaveSyncRelayAsync);
        EditSyncRelayCommand = ReactiveCommand.Create(() => { IsEditingSyncRelay = true; });
        CancelEditSyncRelayCommand = ReactiveCommand.Create(() => { IsEditingSyncRelay = false; });
        SyncRelayMembershipCommand = ReactiveCommand.CreateFromTask<UnifiedRelayViewModel>(SyncRelayMembershipAsync);
        RemoveUnifiedRelayCommand = ReactiveCommand.CreateFromTask<UnifiedRelayViewModel>(RemoveUnifiedRelayAsync);
        CycleUnifiedRelayUsageCommand = ReactiveCommand.CreateFromTask<UnifiedRelayViewModel>(CycleUnifiedRelayUsageAsync);

        // Persist show/hide device-sync chat toggle
        this.WhenAnyValue(x => x.ShowDeviceSyncChat)
            .Skip(1)
            .Subscribe(async show =>
            {
                if (_isRestoringSettings) return;
                try
                {
                    await _storageService.SaveSettingAsync("show_device_sync_chat", show ? "true" : "false");
                    _logger.LogInformation("Device-sync chat visibility {Status}", show ? "enabled" : "disabled");
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save device-sync chat visibility"); }
            });

        // Persist dummy KeyPackage obfuscation toggle
        this.WhenAnyValue(x => x.DummyKeyPackagesEnabled)
            .Skip(1)
            .Subscribe(async enabled =>
            {
                if (_isRestoringSettings) return;
                try
                {
                    await _storageService.SaveSettingAsync("dummy_keypackages_enabled", enabled ? "true" : "false");
                    _logger.LogInformation("Dummy KeyPackage obfuscation {Status}", enabled ? "enabled" : "disabled");
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save dummy KeyPackage setting"); }
            });

        // Add default relays (will be replaced by saved relays in LoadSettingsAsync)
        foreach (var relay in NostrConstants.DefaultRelays)
            Relays.Add(new RelayViewModel { Url = relay, IsConnected = false });

        // Persist MIP-04 toggle changes and check dependencies
        this.WhenAnyValue(x => x.IsMip04Enabled)
            .Skip(1) // Skip initial default value
            .Subscribe(async enabled =>
            {
                if (_isRestoringSettings) return;
                try
                {
                    await _storageService.SaveSettingAsync("mip04_enabled", enabled ? "true" : "false");
                    _logger.LogInformation("MIP-04 media loading {Status}", enabled ? "enabled" : "disabled");

                    if (enabled)
                    {
                        // Check audio recording dependencies (platform-aware)
                        if (_platform is { HasAudioRecording: false })
                        {
                            Mip04DependencyWarning = "Voice messages are not available on this platform.";
                        }
                        else
                        {
                            var audioWarning = ChatViewModel.AudioRecordingService?.CheckDependencies();
                            Mip04DependencyWarning = audioWarning;
                            if (audioWarning != null)
                                _logger.LogWarning("MIP-04 dependency issue: {Warning}", audioWarning);
                        }
                    }
                    else
                    {
                        Mip04DependencyWarning = null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save MIP-04 setting");
                }
            });

        // Master notification toggle — when turned off, disable both modes;
        // when turned on by user action, restore the previously saved mode.
        this.WhenAnyValue(x => x.NotificationsEnabled)
            .Skip(1) // skip initial default
            .Subscribe(async enabled =>
            {
                if (_isRestoringSettings) return;
                try { await _storageService.SaveSettingAsync("notifications_enabled", enabled ? "true" : "false"); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save notifications enabled"); }

                if (!enabled)
                {
                    NotificationModeBackground = false;
                    NotificationModePush = false;
                }
                else
                {
                    // Restore previously saved mode when user explicitly turns notifications on
                    try
                    {
                        var savedMode = await _storageService.GetSettingAsync("notification_mode");
                        if (savedMode == "background")
                            NotificationModeBackground = true;
                        else if (savedMode == "push")
                            NotificationModePush = true;
                    }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to restore notification mode"); }
                }
            });

        // Persist notification mode; request POST_NOTIFICATIONS only when user explicitly
        // enables background service mode (opt-in, not on startup or settings restore).
        this.WhenAnyValue(x => x.NotificationModeBackground)
            .Skip(1) // skip initial value
            .Subscribe(async isBackground =>
            {
                if (_isRestoringSettings) return;
                if (isBackground)
                {
                    try { await _storageService.SaveSettingAsync("notification_mode", "background"); }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to save notification mode"); }

                    // Only request permission on explicit user action
                    if (PermissionRequestFunc != null)
                    {
                        // Android 13+ requires POST_NOTIFICATIONS for the foreground service notification
                        const string postNotifications = "android.permission.POST_NOTIFICATIONS";
                        var granted = await PermissionRequestFunc(new[] { postNotifications });
                        _logger.LogInformation("POST_NOTIFICATIONS permission {Result}", granted ? "granted" : "denied");
                    }
                }
            });

        this.WhenAnyValue(x => x.NotificationModePush)
            .Skip(1)
            .Where(push => push) // only persist when toggled ON
            .Subscribe(async _ =>
            {
                if (_isRestoringSettings) return;
                try { await _storageService.SaveSettingAsync("notification_mode", "push"); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save notification mode"); }
            });

        // Persist notification settings
        this.WhenAnyValue(x => x.NotificationServerNpub)
            .Skip(1)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Subscribe(async v =>
            {
                if (_isRestoringSettings) return;
                try { await _storageService.SaveSettingAsync("notification_server_npub", v.Trim()); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save notification server npub"); }
            });

        this.WhenAnyValue(x => x.NotificationServerRelay)
            .Skip(1)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Subscribe(async v =>
            {
                if (_isRestoringSettings) return;
                try { await _storageService.SaveSettingAsync("notification_server_relay", v.Trim()); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save notification server relay"); }
            });

        this.WhenAnyValue(x => x.NotificationPushUrl)
            .Skip(1)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Subscribe(async v =>
            {
                if (_isRestoringSettings) return;
                try { await _storageService.SaveSettingAsync("notification_push_url", v.Trim()); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save notification push URL"); }
            });

        // Theme change handler — persist to profile DB so it survives account switches
        this.WhenAnyValue(x => x.SelectedThemeIndex)
            .Skip(1)
            .Subscribe(async index =>
            {
                _logger.LogInformation("Theme changed to index {Index}", index);
                OnThemeChanged?.Invoke(index);
                if (_isRestoringSettings) return;
                try { await _storageService.SaveSettingAsync("theme_index", index.ToString()); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save theme index"); }
            });

        // PoW settings — persist and push to the signer service
        this.WhenAnyValue(x => x.ProofOfWorkEnabled, x => x.ProofOfWorkDifficulty)
            .Skip(1)
            .Subscribe(async tuple =>
            {
                try
                {
                    var (enabled, difficulty) = tuple;
                    var effectiveBits = enabled ? (int)difficulty : 0;
                    _nostrService.SetNip46ProofOfWorkDifficulty(effectiveBits);
                    if (!_isRestoringSettings)
                        await _storageService.SaveSettingAsync("pow_difficulty", effectiveBits.ToString());
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save PoW setting"); }
            });

        // Settings are loaded separately via LoadSettingsAsync(), called by
        // MainViewModel.InitializeAfterLoginAsync on the UI thread. This
        // ensures ObservableCollection modifications are safe for data binding
        // and avoids running on a thread-pool thread when the constructor is
        // invoked inside Task.Run (no SynchronizationContext).
    }

    /// <summary>
    /// Loads all persisted settings from the profile database. Called by
    /// <see cref="MainViewModel.InitializeAfterLoginAsync"/> on the UI thread
    /// after the view model is bound to the view (so ObservableCollection
    /// modifications are safe). Wraps the entire operation in
    /// <c>_isRestoringSettings = true</c> to suppress redundant
    /// <c>SaveSettingAsync</c> calls from reactive subscriptions.
    /// </summary>
    internal async Task LoadSettingsAsync()
    {
        // Suppress reactive subscription side-effects (SaveSettingAsync calls)
        // while we're restoring values from the database — writing back the
        // same values that were just read is a wasteful I/O round-trip.
        _isRestoringSettings = true;
        try
        {
        var user = await _storageService.GetCurrentUserAsync();
        if (user != null)
        {
            PublicKeyHex = user.PublicKeyHex;
            PrivateKeyHex = user.PrivateKeyHex;
            CanEditProfile = !user.IsRemoteSigner;
            Npub = user.Npub;
            DisplayName = user.DisplayName;
            Username = user.Username;
            About = user.About;
            AvatarUrl = user.AvatarUrl;

            // Load saved relay list
            var savedRelays = await _storageService.GetUserRelayListAsync(user.PublicKeyHex);
            if (savedRelays.Count > 0)
            {
                Relays.Clear();
                foreach (var relay in savedRelays)
                    Relays.Add(new RelayViewModel { Url = relay.Url, Usage = relay.Usage, IsConnected = false });
            }

            // Sync actual connection state from NostrService
            var connectedUrls = _nostrService.ConnectedRelayUrls;
            foreach (var relay in Relays)
            {
                relay.IsConnected = connectedUrls.Contains(relay.Url);
            }
        }

        // Load saved theme from profile DB and apply (triggers OnThemeChanged via subscription)
        var savedTheme = await _storageService.GetSettingAsync("theme_index");
        if (savedTheme != null && int.TryParse(savedTheme, out var themeIndex) &&
            themeIndex >= 0 && themeIndex < AvailableThemeNames.Length)
        {
            _logger.LogInformation("Loaded theme index {Index} from profile", themeIndex);
            SelectedThemeIndex = themeIndex;
        }

        // Load MIP-04 setting
        var mip04Setting = await _storageService.GetSettingAsync("mip04_enabled");
        IsMip04Enabled = mip04Setting == "true";
        _logger.LogInformation("Loaded MIP-04 setting: {Enabled}", IsMip04Enabled);

        // Check dependencies if enabled
        if (IsMip04Enabled)
            Mip04DependencyWarning = ChatViewModel.AudioRecordingService?.CheckDependencies();

        // Load Blossom server URL — stored as full URL, display as domain only
        var blossomUrl = await _storageService.GetSettingAsync("blossom_server_url");
        if (!string.IsNullOrEmpty(blossomUrl))
        {
            BlossomServerDomain = StripScheme(blossomUrl);
            if (ChatViewModel.MediaUploadService is BlossomUploadService blossom)
                blossom.BlossomServerUrl = blossomUrl;
        }
        _logger.LogInformation("Loaded Blossom server domain: {Domain}", BlossomServerDomain);

        // Load notification settings — _isRestoringSettings is already true
        // from the outer guard, suppressing permission prompts and redundant saves.
        {
            var notifEnabled = await _storageService.GetSettingAsync("notifications_enabled");
            var notifMode = await _storageService.GetSettingAsync("notification_mode");

            // Backward compatibility: if notifications_enabled key doesn't exist yet,
            // derive it from whether a mode was previously saved.
            if (notifEnabled == null)
                NotificationsEnabled = notifMode is "background" or "push";
            else
                NotificationsEnabled = notifEnabled == "true";

            if (NotificationsEnabled)
            {
                if (notifMode == "push")
                {
                    NotificationModePush = true;
                    NotificationModeBackground = false;
                }
                else if (notifMode == "background")
                {
                    NotificationModeBackground = true;
                    NotificationModePush = false;
                }
            }
            // else: both remain false — user hasn't opted in yet

            var notifNpub = await _storageService.GetSettingAsync("notification_server_npub");
            if (!string.IsNullOrEmpty(notifNpub))
                NotificationServerNpub = notifNpub;
            var notifRelay = await _storageService.GetSettingAsync("notification_server_relay");
            if (!string.IsNullOrEmpty(notifRelay))
                NotificationServerRelay = notifRelay;
            var notifPushUrl = await _storageService.GetSettingAsync("notification_push_url");
            if (!string.IsNullOrEmpty(notifPushUrl))
                NotificationPushUrl = notifPushUrl;
        }

        // Load per-purpose relay lists
        var dmRelayJson = await _storageService.GetSettingAsync("dm_relay_urls");
        if (!string.IsNullOrEmpty(dmRelayJson))
        {
            try
            {
                var dmUrls = JsonSerializer.Deserialize<List<string>>(dmRelayJson);
                if (dmUrls != null)
                {
                    DmRelays.Clear();
                    foreach (var url in dmUrls)
                        DmRelays.Add(url);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize DM relay list");
            }
        }

        var kpRelayJson = await _storageService.GetSettingAsync("kp_relay_urls");
        if (!string.IsNullOrEmpty(kpRelayJson))
        {
            try
            {
                var kpUrls = JsonSerializer.Deserialize<List<string>>(kpRelayJson);
                if (kpUrls != null)
                {
                    KpRelays.Clear();
                    foreach (var url in kpUrls)
                        KpRelays.Add(url);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize KeyPackage relay list");
            }
        }

        _logger.LogInformation("Loaded purpose relay lists: {DmCount} DM, {KpCount} KP", DmRelays.Count, KpRelays.Count);

        // Build unified relay view from all backing stores
        BuildAllRelays();

        // Load local device slot ID
        var slotId = _mlsService.GetLocalKeyPackageSlotId();
        LocalSlotId = slotId;
        LocalSlotIdShort = slotId != null && slotId.Length >= 8 ? slotId[..8] : slotId;
        _logger.LogInformation("Local device slot ID: {SlotId}", LocalSlotIdShort ?? "(none)");

        // Load sync relay
        var syncRelayUrl = await _storageService.GetSettingAsync("sync_relay_url");
        if (!string.IsNullOrEmpty(syncRelayUrl))
            SyncRelayDomain = StripScheme(syncRelayUrl);

        // Load device-sync chat visibility
        var showSync = await _storageService.GetSettingAsync("show_device_sync_chat");
        ShowDeviceSyncChat = showSync != "false"; // default: visible

        // Load dummy KeyPackage obfuscation setting (default: off)
        var dummyKpEnabled = await _storageService.GetSettingAsync("dummy_keypackages_enabled");
        DummyKeyPackagesEnabled = dummyKpEnabled == "true";

        // Load PoW difficulty setting (default: 0 = disabled for local keys,
        // 28 bits for NIP-46 remote signers since common relays like nos.lol require it)
        var powSetting = await _storageService.GetSettingAsync("pow_difficulty");
        if (powSetting != null && int.TryParse(powSetting, out var powBits) && powBits > 0)
        {
            ProofOfWorkEnabled = true;
            ProofOfWorkDifficulty = Math.Clamp(powBits, 21, 28);
        }
        else if (powSetting == null && user?.IsRemoteSigner == true)
        {
            // Auto-enable PoW for NIP-46 users who haven't configured it yet.
            // Most NIP-46 relay infrastructure (nos.lol) requires 28-bit PoW.
            ProofOfWorkEnabled = true;
            ProofOfWorkDifficulty = 28;
            await _storageService.SaveSettingAsync("pow_difficulty", "28");
        }
        else
        {
            ProofOfWorkEnabled = false;
            ProofOfWorkDifficulty = 21;
        }
        _nostrService.SetNip46ProofOfWorkDifficulty(ProofOfWorkEnabled ? (int)ProofOfWorkDifficulty : 0);

        // Auto-fetch devices from relays
        _ = FetchDevicesAsync();
        }
        finally
        {
            _isRestoringSettings = false;
        }
    }

    private async Task SaveProfileAsync()
    {
        var user = await _storageService.GetCurrentUserAsync();
        if (user != null)
        {
            user.DisplayName = DisplayName ?? "";
            user.Username = Username;
            user.About = About;
            user.AvatarUrl = AvatarUrl;
            user.LastUpdatedAt = DateTime.UtcNow;

            await _storageService.SaveCurrentUserAsync(user);
            _logger.LogInformation("Profile saved locally");
        }

        // Show confirmation dialog for publishing to relays
        ShowPublishConfirmation = true;
    }

    private async Task PublishProfileAsync()
    {
        ShowPublishConfirmation = false;
        IsPublishingProfile = true;
        PublishProfileStatus = null;

        try
        {
            var user = await _storageService.GetCurrentUserAsync();
            if (user == null)
            {
                PublishProfileStatus = "No user found";
                return;
            }

            var eventId = await _nostrService.PublishMetadataAsync(
                user.Username ?? user.DisplayName ?? "",
                user.DisplayName,
                user.About,
                user.AvatarUrl,
                user.PrivateKeyHex);

            PublishProfileStatus = "Profile published to relays";
            _logger.LogInformation("Published profile metadata, event ID: {EventId}", eventId);
        }
        catch (Exception ex)
        {
            PublishProfileStatus = $"Failed to publish: {ex.Message}";
            _logger.LogError(ex, "Failed to publish profile metadata");
        }
        finally
        {
            IsPublishingProfile = false;
        }
    }

    private async Task AddRelayAsync()
    {
        if (string.IsNullOrWhiteSpace(NewRelayUrl)) return;

        var url = NormalizeRelayUrl(NewRelayUrl);
        var relay = new RelayViewModel { Url = url, IsConnected = false };
        Relays.Add(relay);

        await _nostrService.ConnectAsync(url);
        NewRelayUrl = string.Empty;
        await SaveRelayListAsync();
    }

    private async void RemoveRelay(RelayViewModel relay)
    {
        Relays.Remove(relay);
        try
        {
            await _nostrService.DisconnectRelayAsync(relay.Url);
            await SaveRelayListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to disconnect relay {Url}", relay.Url);
        }
    }

    private async Task CycleRelayUsageAsync(RelayViewModel relay)
    {
        relay.Usage = relay.Usage switch
        {
            RelayUsage.Both => RelayUsage.Read,
            RelayUsage.Read => RelayUsage.Write,
            RelayUsage.Write => RelayUsage.Both,
            _ => RelayUsage.Both
        };
        await SaveRelayListAsync();
    }

    private async Task SaveRelayListAsync()
    {
        if (string.IsNullOrEmpty(PublicKeyHex)) return;

        var relayPrefs = Relays.Select(r => new RelayPreference { Url = r.Url, Usage = r.Usage }).ToList();
        await _storageService.SaveUserRelayListAsync(PublicKeyHex, relayPrefs);
        _logger.LogInformation("Saved relay list with {Count} relays", relayPrefs.Count);
    }

    // ── Per-purpose relay management ──

    private async Task AddDmRelayAsync()
    {
        if (string.IsNullOrWhiteSpace(NewDmRelayUrl)) return;
        var url = NormalizeRelayUrl(NewDmRelayUrl);
        if (!DmRelays.Contains(url))
            DmRelays.Add(url);
        NewDmRelayUrl = string.Empty;
        await SavePurposeRelaysAsync();
    }

    private async Task RemoveDmRelayAsync(string url)
    {
        DmRelays.Remove(url);
        await SavePurposeRelaysAsync();
    }

    private async Task AddKpRelayAsync()
    {
        if (string.IsNullOrWhiteSpace(NewKpRelayUrl)) return;
        var url = NormalizeRelayUrl(NewKpRelayUrl);
        if (!KpRelays.Contains(url))
            KpRelays.Add(url);
        NewKpRelayUrl = string.Empty;
        await SavePurposeRelaysAsync();
    }

    private async Task RemoveKpRelayAsync(string url)
    {
        KpRelays.Remove(url);
        await SavePurposeRelaysAsync();
    }

    private async Task SavePurposeRelaysAsync()
    {
        if (string.IsNullOrEmpty(PublicKeyHex)) return;

        var dmJson = JsonSerializer.Serialize(DmRelays.ToList());
        var kpJson = JsonSerializer.Serialize(KpRelays.ToList());
        await _storageService.SaveSettingAsync("dm_relay_urls", dmJson);
        await _storageService.SaveSettingAsync("kp_relay_urls", kpJson);
        _logger.LogInformation("Saved purpose relay lists: {DmCount} DM, {KpCount} KP", DmRelays.Count, KpRelays.Count);
    }

    // ── Relay picker ──

    private async Task AddPickedRelaysAsync()
    {
        var selected = PopularRelays.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) return;

        foreach (var item in selected)
        {
            var url = NormalizeRelayUrl(item.Domain);
            await AddRelayToListsAsync(url);
            item.IsSelected = false;
        }
    }

    private async Task AddCustomRelayAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomRelayDomain)) return;
        var url = NormalizeRelayUrl(CustomRelayDomain);
        await AddRelayToListsAsync(url);
        CustomRelayDomain = string.Empty;
    }

    private async Task AddRelayToListsAsync(string url)
    {
        // Check if already in unified list
        var existing = AllRelays.FirstOrDefault(r =>
            string.Equals(r.Url, url, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            // Already exists — update toggles based on picker checkboxes
            if (AddToGeneral) existing.IsGeneral = true;
            if (AddToDm) existing.IsDm = true;
            if (AddToKp) existing.IsKp = true;
            await SyncRelayMembershipAsync(existing);
        }
        else
        {
            // New relay — add to unified list and sync to backing stores
            var unified = new UnifiedRelayViewModel
            {
                Url = url,
                IsConnected = false,
                IsGeneral = AddToGeneral,
                IsDm = AddToDm,
                IsKp = AddToKp,
            };
            AllRelays.Add(unified);
            await SyncRelayMembershipAsync(unified);
        }
    }

    // ── Unified relay management ──

    /// <summary>Build the unified relay list from all backing stores.</summary>
    private void BuildAllRelays()
    {
        AllRelays.Clear();

        var allUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Relays) allUrls.Add(r.Url);
        foreach (var url in DmRelays) allUrls.Add(url);
        foreach (var url in KpRelays) allUrls.Add(url);

        foreach (var url in allUrls)
        {
            var generalRelay = Relays.FirstOrDefault(r =>
                string.Equals(r.Url, url, StringComparison.OrdinalIgnoreCase));

            AllRelays.Add(new UnifiedRelayViewModel
            {
                Url = url,
                IsConnected = generalRelay?.IsConnected ?? false,
                Error = generalRelay?.Error,
                IsGeneral = generalRelay != null,
                IsDm = DmRelays.Any(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase)),
                IsKp = KpRelays.Any(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase)),
                Usage = generalRelay?.Usage ?? RelayUsage.Both,
            });
        }
    }

    /// <summary>
    /// Syncs a unified relay's toggle state back to the backing stores.
    /// Called when the user toggles General/DM/KP checkboxes on a relay card.
    /// </summary>
    private async Task SyncRelayMembershipAsync(UnifiedRelayViewModel relay)
    {
        // Sync General list
        var existingGeneral = Relays.FirstOrDefault(r =>
            string.Equals(r.Url, relay.Url, StringComparison.OrdinalIgnoreCase));

        if (relay.IsGeneral && existingGeneral == null)
        {
            Relays.Add(new RelayViewModel { Url = relay.Url, IsConnected = false, Usage = relay.Usage });
            try
            {
                await _nostrService.ConnectAsync(relay.Url);
                relay.IsConnected = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to relay {Url}", relay.Url);
            }
        }
        else if (!relay.IsGeneral && existingGeneral != null)
        {
            Relays.Remove(existingGeneral);
            try { await _nostrService.DisconnectRelayAsync(relay.Url); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to disconnect relay {Url}", relay.Url); }
            relay.IsConnected = false;
        }

        await SaveRelayListAsync();

        // Sync DM list
        var hasDm = DmRelays.Any(u => string.Equals(u, relay.Url, StringComparison.OrdinalIgnoreCase));
        if (relay.IsDm && !hasDm)
            DmRelays.Add(relay.Url);
        else if (!relay.IsDm && hasDm)
            DmRelays.Remove(DmRelays.First(u => string.Equals(u, relay.Url, StringComparison.OrdinalIgnoreCase)));

        // Sync KP list
        var hasKp = KpRelays.Any(u => string.Equals(u, relay.Url, StringComparison.OrdinalIgnoreCase));
        if (relay.IsKp && !hasKp)
            KpRelays.Add(relay.Url);
        else if (!relay.IsKp && hasKp)
            KpRelays.Remove(KpRelays.First(u => string.Equals(u, relay.Url, StringComparison.OrdinalIgnoreCase)));

        await SavePurposeRelaysAsync();
    }

    /// <summary>Remove a relay from all lists.</summary>
    private async Task RemoveUnifiedRelayAsync(UnifiedRelayViewModel relay)
    {
        AllRelays.Remove(relay);

        var existingGeneral = Relays.FirstOrDefault(r =>
            string.Equals(r.Url, relay.Url, StringComparison.OrdinalIgnoreCase));
        if (existingGeneral != null)
        {
            Relays.Remove(existingGeneral);
            try { await _nostrService.DisconnectRelayAsync(relay.Url); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to disconnect relay {Url}", relay.Url); }
        }
        await SaveRelayListAsync();

        var dmMatch = DmRelays.FirstOrDefault(u => string.Equals(u, relay.Url, StringComparison.OrdinalIgnoreCase));
        if (dmMatch != null) DmRelays.Remove(dmMatch);
        var kpMatch = KpRelays.FirstOrDefault(u => string.Equals(u, relay.Url, StringComparison.OrdinalIgnoreCase));
        if (kpMatch != null) KpRelays.Remove(kpMatch);
        await SavePurposeRelaysAsync();
    }

    /// <summary>Cycle relay usage (Read &amp; Write / Read / Write).</summary>
    private async Task CycleUnifiedRelayUsageAsync(UnifiedRelayViewModel relay)
    {
        relay.Usage = relay.Usage switch
        {
            RelayUsage.Both => RelayUsage.Read,
            RelayUsage.Read => RelayUsage.Write,
            RelayUsage.Write => RelayUsage.Both,
            _ => RelayUsage.Both
        };

        // Sync to the backing RelayViewModel
        var existingGeneral = Relays.FirstOrDefault(r =>
            string.Equals(r.Url, relay.Url, StringComparison.OrdinalIgnoreCase));
        if (existingGeneral != null)
            existingGeneral.Usage = relay.Usage;

        await SaveRelayListAsync();
    }

    // ── NIP-65 fetch ──

    private async Task FetchNip65Async()
    {
        if (string.IsNullOrEmpty(PublicKeyHex)) return;

        try
        {
            IsFetchingNip65 = true;
            Nip65Status = "Fetching...";
            Nip65Relays.Clear();

            var relayList = await _nostrService.FetchRelayListAsync(PublicKeyHex);

            if (relayList.Count == 0)
            {
                Nip65Status = "No NIP-65 relay list found on network";
                return;
            }

            foreach (var rp in relayList)
                Nip65Relays.Add(new RelayViewModel { Url = rp.Url, Usage = rp.Usage, IsConnected = false });

            Nip65Status = $"Found {relayList.Count} relays published on network";
            _logger.LogInformation("Fetched NIP-65 relay list: {Count} relays", relayList.Count);
        }
        catch (Exception ex)
        {
            Nip65Status = $"Failed: {ex.Message}";
            _logger.LogError(ex, "Failed to fetch NIP-65 relay list");
        }
        finally
        {
            IsFetchingNip65 = false;
        }
    }

    private async Task PublishRelayListAsync()
    {
        if (string.IsNullOrEmpty(PublicKeyHex))
        {
            _logger.LogWarning("Cannot publish relay list: no keys loaded");
            return;
        }

        try
        {
            PublishRelayListStatus = "Publishing...";
            var relayPrefs = Relays.Select(r => new RelayPreference { Url = r.Url, Usage = r.Usage }).ToList();
            var relayUrls = Relays.Select(r => r.Url).ToList();
            await _nostrService.PublishRelayListAsync(relayPrefs, PrivateKeyHex);
            // Publish purpose-specific relay lists: use per-purpose overrides when configured,
            // otherwise fall back to the general relay list
            var dmRelayUrls = DmRelays.Count > 0 ? DmRelays.ToList() : relayUrls;
            var kpRelayUrls = KpRelays.Count > 0 ? KpRelays.ToList() : relayUrls;

            // Include sync relay in KP relay list so all devices can discover each other
            var syncRelayUrl = await _storageService.GetSettingAsync("sync_relay_url");
            if (!string.IsNullOrEmpty(syncRelayUrl) &&
                !kpRelayUrls.Any(r => string.Equals(r.TrimEnd('/'), syncRelayUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            {
                kpRelayUrls.Add(syncRelayUrl);
            }

            await _nostrService.PublishDmRelayListAsync(dmRelayUrls, PrivateKeyHex);
            await _nostrService.PublishKeyPackageRelayListAsync(kpRelayUrls, PrivateKeyHex);
            await _storageService.SaveUserRelayListAsync(PublicKeyHex, relayPrefs);
            PublishRelayListStatus = $"Published {relayPrefs.Count} relays";
            _logger.LogInformation("Published relay lists (kind 10002, 10050, 10051) and saved locally");
        }
        catch (Exception ex)
        {
            PublishRelayListStatus = "Failed to publish relay list";
            _logger.LogError(ex, "Failed to publish relay list");
        }
    }

    private async Task PublishKeyPackageAsync()
    {
        if (string.IsNullOrEmpty(PublicKeyHex))
        {
            KeyPackageStatus = "Error: No keys loaded. Please log in first.";
            KeyPackageSuccess = false;
            return;
        }

        // Pre-check: signer users (null PrivateKeyHex) need an external signer to publish.
        // Without this check the publish would fail deep in NostrService with an opaque error.
        if (string.IsNullOrEmpty(PrivateKeyHex) && !_nostrService.HasExternalSigner)
        {
            KeyPackageStatus = "Error: Signer not connected. Please restart your signer app (e.g. Amber) and try again.";
            KeyPackageSuccess = false;
            _logger.LogWarning("KeyPackage publish aborted: no private key and no external signer available");
            return;
        }

        IsPublishingKeyPackage = true;
        KeyPackageStatus = "Generating key package...";
        KeyPackageSuccess = false;

        try
        {
            _logger.LogInformation("Starting key package generation and publishing");

            // Initialize MLS service if needed
            await _mlsService.InitializeAsync(PrivateKeyHex ?? new string('0', 64), PublicKeyHex);

            // Generate key package
            KeyPackageStatus = "Generating MLS key package...";
            var keyPackage = await _mlsService.GenerateKeyPackageAsync();
            _logger.LogInformation("Generated key package with {DataLength} bytes", keyPackage.Data.Length);

            // Publish to Nostr relays with MDK-provided tags
            KeyPackageStatus = "Publishing to relays...";
            var eventId = await _nostrService.PublishKeyPackageAsync(keyPackage.Data, PrivateKeyHex, keyPackage.NostrTags);
            _logger.LogInformation("Published key package with event ID: {EventId} using {TagCount} MDK tags", eventId, keyPackage.NostrTags.Count);

            // Record which relays the key package was published to
            keyPackage.NostrEventId = eventId;
            keyPackage.RelayUrls = _nostrService.ConnectedRelayUrls.ToList();

            // Save key package locally
            await _storageService.SaveKeyPackageAsync(keyPackage);

            KeyPackageStatus = $"Key package published successfully!\nEvent ID: {eventId[..16]}...";
            KeyPackageSuccess = true;
            _logger.LogInformation("Key package published and saved successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish key package");
            KeyPackageStatus = $"Error: {ex.Message}";
            KeyPackageSuccess = false;
        }
        finally
        {
            IsPublishingKeyPackage = false;
        }
    }
    private async Task AuditKeyPackagesAsync()
    {
        IsAuditingKeyPackages = true;
        AuditStatus = "Fetching KeyPackages from relays...";

        try
        {
            var result = await _messageService.AuditKeyPackagesAsync();
            LastAuditResult = result;

            if (result.TotalOnRelays == 0)
            {
                AuditStatus = "No KeyPackages found on relays. Publish one first.";
            }
            else
            {
                var parts = new List<string>
                {
                    $"{result.TotalOnRelays} on relays",
                    $"{result.ActiveWithKeys} active (this device)"
                };
                if (result.PeerDevice > 0)
                    parts.Add($"{result.PeerDevice} peer device");
                if (result.Lost > 0)
                    parts.Add($"{result.Lost} lost");
                if (result.Expired > 0)
                    parts.Add($"{result.Expired} expired");
                AuditStatus = $"Audit complete: {string.Join(", ", parts)}";
            }

            if (result.Lost > 0)
            {
                AuditStatus += $"\n{result.Lost} KeyPackage(s) have lost keys — invites to these cannot be accepted.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to audit KeyPackages");
            AuditStatus = $"Audit failed: {ex.Message}";
        }
        finally
        {
            IsAuditingKeyPackages = false;
        }
    }

    private async Task ResetMlsDataAsync()
    {
        IsResettingMls = true;
        MlsResetStatus = null;
        MlsResetSuccess = false;
        ShowMlsResetConfirmation = false;

        try
        {
            _logger.LogWarning("User initiated MLS data reset");

            // 1. Clear in-memory MLS state (signing keys, stored key packages, groups)
            await _mlsService.ResetAsync();

            // 2. Wipe MLS-related database tables (MlsStates, KeyPackages, PendingInvites, etc.)
            await _storageService.ResetMlsDataAsync();

            // 3. Reset skipped invite counter
            await _storageService.ResetSkippedInviteCountAsync();

            _logger.LogInformation("MLS data reset completed successfully");
            MlsResetStatus = "MLS state cleared. Restart the app and publish a new KeyPackage to receive invites.";
            MlsResetSuccess = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MLS data reset failed");
            MlsResetStatus = $"Reset failed: {ex.Message}";
            MlsResetSuccess = false;
        }
        finally
        {
            IsResettingMls = false;
        }
    }

    private void GeneratePushTopic()
    {
        var random = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(random);
        var topic = $"scramble-{Convert.ToHexString(random).ToLowerInvariant()}";
        NotificationPushUrl = $"https://ntfy.sh/{topic}";
        _logger.LogInformation("Generated push topic: {Topic}", topic);
    }

    private void SubscribeInNtfy()
    {
        if (string.IsNullOrWhiteSpace(NotificationPushUrl)) return;
        // Use ntfy:// scheme so Android opens the ntfy app directly
        var url = NotificationPushUrl.Trim();
        if (url.StartsWith("https://ntfy.sh/", StringComparison.OrdinalIgnoreCase))
        {
            var topic = url["https://ntfy.sh/".Length..];
            _launcher.OpenUrl($"ntfy://ntfy.sh/{topic}");
        }
        else
        {
            _launcher.OpenUrl(url);
        }
    }

    private async Task VerifyNotificationsAsync()
    {
        if (string.IsNullOrWhiteSpace(NotificationVerifyCode))
        {
            NotificationRegistrationStatus = "Enter the verification code";
            return;
        }

        if (string.IsNullOrWhiteSpace(NotificationServerNpub) || string.IsNullOrEmpty(PublicKeyHex))
        {
            NotificationRegistrationStatus = "Missing server npub or keys";
            return;
        }

        try
        {
            NotificationRegistrationStatus = "Verifying...";

            var serverPubKeyHex = NotificationServerNpub.Trim().StartsWith("npub1")
                ? _nostrService.NpubToHex(NotificationServerNpub.Trim())
                : NotificationServerNpub.Trim();

            if (string.IsNullOrEmpty(serverPubKeyHex) || serverPubKeyHex.Length != 64)
            {
                NotificationRegistrationStatus = "Invalid server npub";
                return;
            }

            var content = $"{{\"action\": \"verify\", \"code\": \"{NotificationVerifyCode.Trim()}\"}}";
            var rumorTags = new List<List<string>> { new() { "p", serverPubKeyHex } };
            var targetRelays = new List<string> { NotificationServerRelay.Trim() };

            var eventId = await _nostrService.PublishGiftWrapAsync(
                rumorKind: 14,
                content: content,
                rumorTags: rumorTags,
                senderPrivateKeyHex: PrivateKeyHex,
                senderPublicKeyHex: PublicKeyHex,
                recipientPublicKeyHex: serverPubKeyHex,
                targetRelayUrls: targetRelays);

            NotificationRegistrationStatus = "Verification sent — check your push notifications for confirmation.";
            NotificationVerifyCode = string.Empty;
            _logger.LogInformation("Notification verification sent, event {EventId}", eventId);
        }
        catch (Exception ex)
        {
            NotificationRegistrationStatus = $"Verification failed: {ex.Message}";
            _logger.LogError(ex, "Failed to verify notifications");
        }
    }

    private async Task RegisterNotificationsAsync()
    {
        if (string.IsNullOrWhiteSpace(NotificationServerNpub) || string.IsNullOrWhiteSpace(NotificationPushUrl)
            || string.IsNullOrWhiteSpace(NotificationServerRelay))
        {
            NotificationRegistrationStatus = "Enter server npub, relay, and push URL";
            return;
        }

        if (string.IsNullOrEmpty(PublicKeyHex))
        {
            NotificationRegistrationStatus = "No keys loaded";
            return;
        }

        try
        {
            NotificationRegistrationStatus = "Registering...";

            // Decode server npub to hex
            var serverPubKeyHex = NotificationServerNpub.Trim().StartsWith("npub1")
                ? _nostrService.NpubToHex(NotificationServerNpub.Trim())
                : NotificationServerNpub.Trim();

            if (string.IsNullOrEmpty(serverPubKeyHex) || serverPubKeyHex.Length != 64)
            {
                NotificationRegistrationStatus = "Invalid server npub";
                return;
            }

            var serverRelay = NotificationServerRelay.Trim();

            // Build registration JSON — server relay is the relay to monitor
            var content = $"{{\"action\": \"register\", \"push_url\": \"{NotificationPushUrl.Trim()}\", \"relays\": [\"{serverRelay}\"]}}";

            // Send as NIP-59 gift-wrapped kind-14 DM to the server's inbox relay
            var rumorTags = new List<List<string>> { new() { "p", serverPubKeyHex } };
            var targetRelays = new List<string> { serverRelay };

            var eventId = await _nostrService.PublishGiftWrapAsync(
                rumorKind: 14,
                content: content,
                rumorTags: rumorTags,
                senderPrivateKeyHex: PrivateKeyHex,
                senderPublicKeyHex: PublicKeyHex,
                recipientPublicKeyHex: serverPubKeyHex,
                targetRelayUrls: targetRelays);

            NotificationRegistrationStatus = "Check your push notifications for a verification code";
            NotificationAwaitingVerification = true;
            _logger.LogInformation("Notification registration sent to {ServerPubKey}, event {EventId}", serverPubKeyHex[..16], eventId);
        }
        catch (Exception ex)
        {
            NotificationRegistrationStatus = $"Failed: {ex.Message}";
            _logger.LogError(ex, "Failed to register for notifications");
        }
    }

    private static string GetPackageVersion(string packageName)
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var asm = assemblies.FirstOrDefault(a =>
                a.GetName().Name?.Equals(packageName, StringComparison.OrdinalIgnoreCase) == true);
            if (asm != null)
            {
                var ver = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>();
                if (ver != null) return ver.InformationalVersion.Split('+')[0];
                return asm.GetName().Version?.ToString() ?? "unknown";
            }
            return "not loaded";
        }
        catch { return "error"; }
    }

    // ── URL helpers ──

    /// <summary>
    /// Normalizes user input to a full wss:// relay URL.
    /// Accepts bare domain ("relay.damus.io"), domain with path, or full URL.
    /// </summary>
    internal static string NormalizeRelayUrl(string input)
    {
        var trimmed = input.Trim().TrimEnd('/');
        if (trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            return trimmed.ToLowerInvariant();
        return $"wss://{trimmed}".ToLowerInvariant();
    }

    /// <summary>
    /// Validates that the input looks like a domain (or domain/path).
    /// Strips any wss:// prefix before checking so users who paste full URLs still pass.
    /// </summary>
    internal static bool IsValidDomain(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim();

        // Accept full URLs too (backward compat for paste)
        if (trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(trimmed, UriKind.Absolute, out _);

        // Bare domain: must contain a dot, no spaces, valid as URI when prefixed
        return trimmed.Contains('.') && !trimmed.Contains(' ') &&
               Uri.TryCreate($"wss://{trimmed}", UriKind.Absolute, out _);
    }

    /// <summary>
    /// Strips https:// or http:// scheme from a URL, returning the domain/path.
    /// </summary>
    internal static string StripScheme(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return trimmed[8..];
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return trimmed[7..];
        if (trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return trimmed[6..];
        if (trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            return trimmed[5..];
        return trimmed;
    }

    /// <summary>
    /// Saves the blossom server domain after validation.
    /// </summary>
    private async Task SaveBlossomAsync()
    {
        try
        {
            var domain = BlossomServerDomain.Trim().ToLowerInvariant();

            // Strip scheme if user pasted a full URL
            domain = StripScheme(domain).TrimEnd('/');

            if (string.IsNullOrWhiteSpace(domain) || !domain.Contains('.') || domain.Contains(' '))
            {
                BlossomStatus = "Invalid domain";
                BlossomStatusIsError = true;
                return;
            }

            var fullUrl = $"https://{domain}";
            if (!Uri.TryCreate(fullUrl, UriKind.Absolute, out _))
            {
                BlossomStatus = "Invalid domain";
                BlossomStatusIsError = true;
                return;
            }

            BlossomServerDomain = domain;
            await _storageService.SaveSettingAsync("blossom_server_url", fullUrl);
            if (ChatViewModel.MediaUploadService is BlossomUploadService blossom)
                blossom.BlossomServerUrl = fullUrl;

            BlossomStatus = "Saved";
            BlossomStatusIsError = false;
            IsEditingBlossom = false;
            _logger.LogInformation("Blossom server URL updated: {Url}", fullUrl);
        }
        catch (Exception ex)
        {
            BlossomStatus = $"Error: {ex.Message}";
            BlossomStatusIsError = true;
            _logger.LogError(ex, "Failed to save Blossom server URL");
        }
    }

    private async Task FetchDevicesAsync()
    {
        if (string.IsNullOrEmpty(PublicKeyHex))
        {
            DeviceStatus = "No account loaded";
            return;
        }

        try
        {
            IsFetchingDevices = true;
            DeviceStatus = "Fetching devices from relays...";
            MyDevices.Clear();

            var keyPackages = (await _nostrService.FetchKeyPackagesAsync(PublicKeyHex)).ToList();
            var localSlotId = _mlsService.GetLocalKeyPackageSlotId();

            // Load lost slot IDs to mark devices accordingly
            var lostRaw = await _storageService.GetSettingAsync("lost_slot_ids");
            var lostSlotIds = new HashSet<string>(
                lostRaw?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>());

            // Compute dummy slot IDs so we don't show our own decoys in the device list
            var dummySlotIds = MessageService.ComputeDummySlotIds(PrivateKeyHex);

            if (keyPackages.Count == 0)
            {
                DeviceStatus = "No KeyPackages found on relays. Publish a KeyPackage first.";
                return;
            }

            // Group by slot ID — each unique slot ID is a device
            // Filter out dummy slot IDs (our own decoys for privacy)
            var deviceGroups = keyPackages
                .Where(kp => !string.IsNullOrEmpty(kp.SlotId) && !dummySlotIds.Contains(kp.SlotId!))
                .GroupBy(kp => kp.SlotId!)
                .OrderByDescending(g => g.Key == localSlotId) // This device first
                .ThenByDescending(g => g.Max(kp => kp.CreatedAt))
                .ToList();

            foreach (var group in deviceGroups)
            {
                var latestKp = group.OrderByDescending(kp => kp.CreatedAt).First();
                var isThisDevice = group.Key == localSlotId;

                MyDevices.Add(new DeviceViewModel
                {
                    ShortSlotId = group.Key.Length >= 16 ? group.Key[..16] : group.Key,
                    FullSlotId = group.Key,
                    CreatedAt = latestKp.CreatedAt.ToLocalTime(),
                    IsThisDevice = isThisDevice,
                    IsLost = lostSlotIds.Contains(group.Key),
                    CipherSuite = latestKp.CipherSuiteName,
                    IsSupported = latestKp.IsCipherSuiteSupported,
                    KeyPackageCount = group.Count(),
                    NostrEventIds = group
                        .Where(kp => !string.IsNullOrEmpty(kp.NostrEventId))
                        .Select(kp => kp.NostrEventId!)
                        .Distinct()
                        .ToList(),
                });
            }

            // Also add KPs without a slot ID (legacy, pre-multi-device)
            var noSlotKps = keyPackages.Where(kp => string.IsNullOrEmpty(kp.SlotId)).ToList();
            foreach (var kp in noSlotKps)
            {
                MyDevices.Add(new DeviceViewModel
                {
                    ShortSlotId = "(legacy)",
                    FullSlotId = "",
                    CreatedAt = kp.CreatedAt.ToLocalTime(),
                    IsThisDevice = string.IsNullOrEmpty(localSlotId), // likely this device if we have no local slot
                    CipherSuite = kp.CipherSuiteName,
                    IsSupported = kp.IsCipherSuiteSupported,
                    NostrEventIds = !string.IsNullOrEmpty(kp.NostrEventId)
                        ? new List<string> { kp.NostrEventId! }
                        : new List<string>(),
                });
            }

            DeviceStatus = $"Found {MyDevices.Count} device(s) on relays";
            _logger.LogInformation("FetchDevices: found {Count} device(s), local slot={LocalSlot}",
                MyDevices.Count, localSlotId?[..Math.Min(8, localSlotId?.Length ?? 0)]);
        }
        catch (Exception ex)
        {
            DeviceStatus = $"Error: {ex.Message}";
            _logger.LogError(ex, "Failed to fetch devices");
        }
        finally
        {
            IsFetchingDevices = false;
        }
    }

    /// <summary>
    /// Mark a peer device as lost/obsolete. Persists the slot ID to a local blocklist
    /// so it is never auto-added to groups, and adds it to seen_peer_slot_ids.
    /// Attempts NIP-09 event deletion for the KeyPackage events on relays.
    /// </summary>
    private async Task MarkDeviceAsLostAsync(DeviceViewModel device)
    {
        if (device.IsThisDevice || string.IsNullOrEmpty(device.FullSlotId))
            return;

        try
        {
            device.IsLost = true;

            // Persist to lost_slot_ids
            var lostRaw = await _storageService.GetSettingAsync("lost_slot_ids");
            var lostSet = new HashSet<string>(
                lostRaw?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>());
            lostSet.Add(device.FullSlotId);
            await _storageService.SaveSettingAsync("lost_slot_ids", string.Join(",", lostSet));

            // Also add to seen_peer_slot_ids to prevent re-detection
            var seenRaw = await _storageService.GetSettingAsync("seen_peer_slot_ids");
            var seenSet = new HashSet<string>(
                seenRaw?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>());
            seenSet.Add(device.FullSlotId);
            await _storageService.SaveSettingAsync("seen_peer_slot_ids", string.Join(",", seenSet));

            // Attempt NIP-09 deletion of the KeyPackage events on relays.
            // This is best-effort — the user owns the pubkey so they can sign deletion events.
            if (device.NostrEventIds.Count > 0 && !string.IsNullOrEmpty(PrivateKeyHex))
            {
                foreach (var eventId in device.NostrEventIds)
                {
                    try
                    {
                        await _nostrService.PublishDeletionAsync(eventId, "Device marked as lost");
                        _logger.LogInformation("Published NIP-09 deletion for KP event {EventId}", eventId);
                    }
                    catch (Exception delEx)
                    {
                        _logger.LogWarning(delEx, "Failed to delete KP event {EventId} from relays", eventId);
                    }
                }
            }

            DeviceStatus = $"Device {device.ShortSlotId} marked as lost";
            _logger.LogInformation("Marked device as lost: slot={SlotId}", device.FullSlotId[..Math.Min(16, device.FullSlotId.Length)]);
        }
        catch (Exception ex)
        {
            DeviceStatus = $"Failed to mark device as lost: {ex.Message}";
            _logger.LogError(ex, "Failed to mark device {SlotId} as lost", device.FullSlotId);
        }
    }

    private async Task SaveSyncRelayAsync()
    {
        var domain = SyncRelayDomain?.Trim() ?? "";
        if (string.IsNullOrEmpty(domain))
        {
            // Clear sync relay
            await _storageService.SaveSettingAsync("sync_relay_url", "");
            SyncRelayStatus = "Cleared";
            IsEditingSyncRelay = false;
            _logger.LogInformation("Sync relay cleared");
            return;
        }

        // Strip scheme if user pasted a full URL, then build wss:// URL
        domain = StripScheme(domain);
        var fullUrl = $"wss://{domain}";

        try
        {
            // Validate it looks like a valid URL
            if (!Uri.TryCreate(fullUrl, UriKind.Absolute, out _))
            {
                SyncRelayStatus = "Invalid relay domain";
                return;
            }

            await _storageService.SaveSettingAsync("sync_relay_url", fullUrl);
            SyncRelayDomain = domain;

            // Connect to the sync relay immediately
            try
            {
                await _nostrService.ConnectAsync(fullUrl);
                SyncRelayStatus = "Saved and connected";
            }
            catch
            {
                SyncRelayStatus = "Saved (connection failed — will retry on next login)";
            }

            IsEditingSyncRelay = false;
            _logger.LogInformation("Sync relay saved: {Url}", fullUrl);
        }
        catch (Exception ex)
        {
            SyncRelayStatus = $"Error: {ex.Message}";
            _logger.LogError(ex, "Failed to save sync relay");
        }
    }
}

public partial class RelayViewModel : ViewModelBase
{
    [Reactive] public partial string Url { get; set; } = string.Empty;
    [Reactive] public partial bool IsConnected { get; set; }
    [Reactive] public partial string? Error { get; set; }
    [Reactive] public partial RelayUsage Usage { get; set; } = RelayUsage.Both;

    private readonly ObservableAsPropertyHelper<string> _usageLabel;
    public string UsageLabel => _usageLabel.Value;

    public RelayViewModel()
    {
        _usageLabel = this.WhenAnyValue(x => x.Usage)
            .Select(u => u switch
            {
                RelayUsage.Read => "Read",
                RelayUsage.Write => "Write",
                _ => "Read & Write"
            })
            .ToProperty(this, x => x.UsageLabel);
    }
}

public partial class RelayPickerItem : ViewModelBase
{
    [Reactive] public partial string Domain { get; set; } = string.Empty;
    [Reactive] public partial bool IsSelected { get; set; }
}

/// <summary>
/// Unified view model for a relay that shows its membership across all relay lists
/// (General, DM/NIP-17, KeyPackage) with toggles for each.
/// </summary>
public partial class UnifiedRelayViewModel : ViewModelBase
{
    [Reactive] public partial string Url { get; set; } = string.Empty;
    [Reactive] public partial bool IsConnected { get; set; }
    [Reactive] public partial string? Error { get; set; }
    [Reactive] public partial bool IsGeneral { get; set; }
    [Reactive] public partial bool IsDm { get; set; }
    [Reactive] public partial bool IsKp { get; set; }
    [Reactive] public partial RelayUsage Usage { get; set; } = RelayUsage.Both;

    private readonly ObservableAsPropertyHelper<string> _usageLabel;
    public string UsageLabel => _usageLabel.Value;

    public UnifiedRelayViewModel()
    {
        _usageLabel = this.WhenAnyValue(x => x.Usage)
            .Select(u => u switch
            {
                RelayUsage.Read => "Read",
                RelayUsage.Write => "Write",
                _ => "R&W"
            })
            .ToProperty(this, x => x.UsageLabel);
    }
}

public partial class DeviceViewModel : ViewModelBase
{
    /// <summary>Short display label: first 16 chars of slot ID.</summary>
    [Reactive] public partial string ShortSlotId { get; set; } = string.Empty;

    /// <summary>Full 64-char slot ID hex string.</summary>
    public string FullSlotId { get; set; } = string.Empty;

    /// <summary>When the KeyPackage was published (from Nostr event created_at).</summary>
    [Reactive] public partial DateTime CreatedAt { get; set; }

    /// <summary>True if this is the current device.</summary>
    [Reactive] public partial bool IsThisDevice { get; set; }

    /// <summary>True if the device has been marked as lost/obsolete by the user.</summary>
    [Reactive] public partial bool IsLost { get; set; }

    /// <summary>Display label: "This device", "Lost", or "Peer device".</summary>
    public string DeviceLabel => IsThisDevice ? "This device" : IsLost ? "Lost" : "Peer device";

    /// <summary>Cipher suite name from the KeyPackage.</summary>
    [Reactive] public partial string CipherSuite { get; set; } = string.Empty;

    /// <summary>Whether the KP uses a supported cipher suite.</summary>
    [Reactive] public partial bool IsSupported { get; set; } = true;

    /// <summary>Number of KeyPackage events published for this slot on relays.</summary>
    public int KeyPackageCount { get; set; }

    /// <summary>Nostr event IDs for the KeyPackage events in this slot (for NIP-09 deletion).</summary>
    public List<string> NostrEventIds { get; set; } = new();
}
