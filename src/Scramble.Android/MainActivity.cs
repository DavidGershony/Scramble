using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.AppCompat.App;
using Fragment = AndroidX.Fragment.App.Fragment;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Dialog;
using Scramble.Android.Fragments;
using Scramble.Android.Services;
using ReactiveUI.Builder;
using Scramble.Core.Configuration;
using Scramble.Core.Logging;
using Scramble.Core.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using ReactiveUI;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace Scramble.Android;

[Activity(Label = "Scramble", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, Theme = "@style/AppTheme",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden)]
public class MainActivity : AppCompatActivity, IActivatableView
{
    // Static so services survive Activity.Recreate() (theme change)
    private static ShellViewModel? _shellViewModel;
    private static bool _servicesInitialized;
    private CompositeDisposable _disposables = new();

    public ViewModelActivator Activator { get; } = new ViewModelActivator();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Apply saved theme before calling base.OnCreate and SetContentView
        SetTheme(Services.ThemeService.GetSavedStyleResource(this));

        base.OnCreate(savedInstanceState);

        // Initialize logging (safe to call multiple times — guarded internally)
        var logDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Scramble", "logs");
        var packageInfo = PackageManager?.GetPackageInfo(PackageName ?? "", 0);
        var appVersion = packageInfo?.VersionName ?? "unknown";
        LoggingConfiguration.Initialize(logDirectory: logDir, perSession: true, appVersion: appVersion);

        SetContentView(Resource.Layout.activity_main);

        // Create notification channels (safe to call multiple times)
        Services.RelayForegroundService.CreateNotificationChannel(this);
        Services.AndroidNotificationService.CreateChannel(this);

        // Set platform notification service for the orchestrator
        NotificationOrchestrator.NotificationService = new Services.AndroidNotificationService(this);

        if (!_servicesInitialized)
        {
            // Create services only once — they survive Activity.Recreate()
            var secureStorage = new AndroidSecureStorage();
            var nostrService = new NostrService();

            // Create platform services
            var clipboard = new AndroidClipboardService(this);
            var qrCodeGenerator = new AndroidQrCodeGenerator();
            var launcher = new AndroidLauncher(this);

            // Audio and upload services for voice messages
            var audioRecording = new Scramble.Android.Services.AndroidAudioRecordingService(this);
            var audioPlayback = new Scramble.Android.Services.AndroidAudioPlaybackService(this);
            var blossomUpload = new BlossomUploadService();
            ChatViewModel.AudioRecordingService = audioRecording;
            ChatViewModel.AudioPlaybackService = audioPlayback;
            ChatViewModel.MediaUploadService = blossomUpload;

            // ReactiveUI needs its platform registrations before ANY ViewModel is
            // constructed. Without this the static initialiser of
            // ReactiveNotifyPropertyChangedMixin throws the moment LoginViewModel's
            // constructor calls WhenAnyValue, and the app dies on launch with a
            // TypeInitializationException.
            //
            // It is needed now and was not before: ReactiveUI 23.x moved from
            // assembly-scanning auto-registration to an explicit builder, and this
            // head was frozen before that migration. The Avalonia heads get the
            // equivalent for free from Avalonia's UseReactiveUI().
            RxAppBuilder.CreateReactiveUIBuilder()
                .WithAndroidX()
                .BuildApp();

            // Create ShellViewModel (manages login → service creation → MainViewModel lifecycle)
            _shellViewModel = new ShellViewModel(nostrService, secureStorage, clipboard, qrCodeGenerator, launcher);
            // The Dark Matter engine, wired exactly as Scramble.Mobile.Android and
            // Scramble.Desktop wire it. ManagedMlsService was one of the two legacy
            // marmot-cs backends deleted in P11 step 5; the factory is now the single
            // place that decides how the store is built, and it fails closed without
            // an ISecureStorage rather than silently leaving MLS state unencrypted.
            _shellViewModel.MlsServiceFactory = DarkMatterMlsServiceFactory.Create;
            _servicesInitialized = true;
        }

        // Observe login state and switch fragments
        _shellViewModel.WhenAnyValue(x => x.IsLoggedIn)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(isLoggedIn =>
            {
                if (isLoggedIn && _shellViewModel.MainViewModel != null)
                {
                    ShowFragment(new ChatListFragment(_shellViewModel.MainViewModel, _shellViewModel), "chatlist");

                    // Auto-start relay foreground service if background mode is enabled
                    StartRelayServiceIfEnabled(_shellViewModel.MainViewModel);

                    // If a share intent is pending (e.g. from a cross-account share that
                    // forced a SwitchAccountAsync), navigate to the target chat as soon as
                    // the chat list finishes loading. Without this the user lands on the
                    // chat list and the URL/file is silently dropped.
                    TryStartPendingShareNavigation();
                }
                else
                {
                    // Stop the foreground service on logout
                    Services.RelayForegroundService.Stop(this);
                    ShowFragment(new LoginFragment(_shellViewModel), "login");
                }
            })
            .DisposeWith(_disposables);

        // Show the account switcher dialog when the ShellViewModel requests it
        // (e.g. after logout when other known accounts exist). Suppressed while a
        // share intent is pending — the share flow already targets a specific
        // account, so popping the picker would just be in the way.
        _shellViewModel.WhenAnyValue(x => x.ShowAccountSwitcher)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Where(show => show)
            .Subscribe(_ =>
            {
                if (PendingShareExtras != null && PendingShareExtras.GetBoolean("shareAction", false))
                {
                    _shellViewModel.ShowAccountSwitcher = false;
                    return;
                }
                ShowAccountSwitcherDialog();
            })
            .DisposeWith(_disposables);

        // Show correct initial fragment based on current state
        // (handles Activity.Recreate() after theme change when already logged in)
        if (_shellViewModel.IsLoggedIn && _shellViewModel.MainViewModel != null)
            ShowFragment(new ChatListFragment(_shellViewModel.MainViewModel, _shellViewModel), "chatlist");
        else
            ShowFragment(new LoginFragment(_shellViewModel), "login");

        // Handle share intent on cold start
        HandleShareIntent(Intent);
    }

    private void ShowFragment(Fragment fragment, string tag)
    {
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, tag)
            .Commit();
    }

    /// <summary>
    /// Shown after logout (or any other path that flips ShellViewModel.ShowAccountSwitcher
    /// while no fragment-owned switcher is on screen). Lists known accounts and lets the
    /// user switch into one without re-entering credentials.
    /// </summary>
    private void ShowAccountSwitcherDialog()
    {
        if (_shellViewModel == null) return;

        var accounts = AccountRegistryService.GetAccounts();
        var activePubKey = _shellViewModel.ActiveAccountEntry?.PublicKeyHex;
        var items = new List<string>();

        foreach (var account in accounts)
        {
            var name = !string.IsNullOrEmpty(account.DisplayName)
                ? account.DisplayName
                : account.Npub?[..Math.Min(16, account.Npub.Length)] + "...";
            var isActive = string.Equals(account.PublicKeyHex, activePubKey, StringComparison.OrdinalIgnoreCase);
            items.Add(isActive ? $"{name} (active)" : name);
        }
        items.Add("Add Account");

        var dialog = new MaterialAlertDialogBuilder(this)
            .SetTitle("Accounts")!
            .SetItems(items.ToArray(), (s, e) =>
            {
                if (e.Which < accounts.Count)
                    _ = _shellViewModel.SwitchAccountAsync(accounts[e.Which].PublicKeyHex);
                else
                    _ = _shellViewModel.AddAccountAsync();
            })!
            .Create()!;

        // Reset the property whenever the dialog goes away (item picked, back button, tap outside)
        // so a future flip from false→true re-opens it.
        dialog.DismissEvent += (s, e) => _shellViewModel.ShowAccountSwitcher = false;
        dialog.Show();
    }

    public void NavigateToChat()
    {
        if (_shellViewModel?.MainViewModel == null) return;
        var fragment = new ChatFragment(_shellViewModel.MainViewModel);
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, "chat")
            .AddToBackStack("chat")
            .Commit();
    }

    public void NavigateToSettings()
    {
        if (_shellViewModel?.MainViewModel == null) return;
        var fragment = new SettingsFragment(_shellViewModel.MainViewModel);
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, "settings")
            .AddToBackStack("settings")
            .Commit();
    }

    public void NavigateToNewChat()
    {
        if (_shellViewModel?.MainViewModel == null) return;
        // Execute command to clear state and populate relay selection list
        _shellViewModel.MainViewModel.ChatListViewModel.NewChatCommand.Execute().Subscribe();
        var fragment = new NewChatFragment(_shellViewModel.MainViewModel);
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, "newchat")
            .AddToBackStack("newchat")
            .Commit();
    }

    public void NavigateToAddBot()
    {
        if (_shellViewModel?.MainViewModel == null) return;
        _shellViewModel.MainViewModel.ChatListViewModel.AddBotCommand.Execute().Subscribe();
        var fragment = new AddBotFragment(_shellViewModel.MainViewModel);
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, "addbot")
            .AddToBackStack("addbot")
            .Commit();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        HandleShareIntent(intent);
    }

    /// <summary>
    /// Pending share intent extras, consumed by ChatFragment after navigation.
    /// </summary>
    internal static Bundle? PendingShareExtras { get; set; }

    // Guards against duplicate share-navigation watchers running concurrently
    // (e.g. one started from cold-start HandleShareIntent + one from the
    // IsLoggedIn=true observer firing once auto-login completes).
    private static int _shareWatcherActive;

    private void HandleShareIntent(Intent? intent)
    {
        if (intent == null || !intent.GetBooleanExtra("shareAction", false))
            return;

        var chatId = intent.GetStringExtra("shareChatId");
        var accountPubKey = intent.GetStringExtra("shareAccountPubKey");
        if (string.IsNullOrEmpty(chatId)) return;

        // Store share extras for ChatFragment to pick up
        PendingShareExtras = intent.Extras;

        // If the share targets a different account — or no account is active at all
        // (cold start after logout) — kick off SwitchAccountAsync. Navigation to the
        // share chat is then picked up by the IsLoggedIn=true observer once the new
        // session is fully active.
        if (!string.IsNullOrEmpty(accountPubKey) && _shellViewModel != null)
        {
            var currentAccount = AccountRegistryService.GetActiveAccount();
            var needsSwitch = currentAccount == null ||
                !string.Equals(currentAccount.PublicKeyHex, accountPubKey, StringComparison.OrdinalIgnoreCase);
            if (needsSwitch)
            {
                _ = _shellViewModel.SwitchAccountAsync(accountPubKey);
                return;
            }
        }

        // Already on the right account — try to navigate immediately. If the chat
        // list isn't populated yet (e.g. cold start before InitializeAfterLoginAsync
        // has finished), fall back to the polling watcher.
        if (!TryNavigateToShareChat(chatId))
            TryStartPendingShareNavigation();
    }

    /// <summary>
    /// Attempts to find <paramref name="chatId"/> in the current chat list and open it.
    /// Returns false if the chat list isn't available yet or doesn't contain the chat.
    /// </summary>
    private bool TryNavigateToShareChat(string chatId)
    {
        if (_shellViewModel?.MainViewModel == null) return false;

        var chatListVm = _shellViewModel.MainViewModel.ChatListViewModel;
        var chatItem = chatListVm.Chats.FirstOrDefault(c => c.Id == chatId)
                    ?? chatListVm.AgentChats.FirstOrDefault(c => c.Id == chatId);

        if (chatItem == null) return false;

        chatListVm.SelectedChat = chatItem;
        NavigateToChat();
        return true;
    }

    /// <summary>
    /// Polls the chat list for the pending share's target chat and navigates to it
    /// once it appears. Bridges the gap between IsLoggedIn=true (when the
    /// ChatListFragment is mounted) and the chat list actually being populated by
    /// LoadChatsAsync. Times out after ~15 seconds. Safe to call multiple times —
    /// re-checks PendingShareExtras on each tick so a consumed/replaced share is
    /// handled correctly.
    /// </summary>
    private void TryStartPendingShareNavigation()
    {
        var extras = PendingShareExtras;
        if (extras == null || !extras.GetBoolean("shareAction", false)) return;
        var chatId = extras.GetString("shareChatId");
        if (string.IsNullOrEmpty(chatId)) return;

        // Don't start a second watcher for the same pending share.
        if (System.Threading.Interlocked.CompareExchange(ref _shareWatcherActive, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    // Bail if a newer share replaced ours or it was consumed
                    var current = PendingShareExtras;
                    if (current == null || current.GetString("shareChatId") != chatId)
                        return;

                    if (_shellViewModel?.MainViewModel != null)
                    {
                        var chatListVm = _shellViewModel.MainViewModel.ChatListViewModel;
                        if (chatListVm.Chats.Any(c => c.Id == chatId) ||
                            chatListVm.AgentChats.Any(c => c.Id == chatId))
                        {
                            RunOnUiThread(() => TryNavigateToShareChat(chatId));
                            return;
                        }
                    }
                    await Task.Delay(200);
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _shareWatcherActive, 0);
            }
        });
    }

    protected override void OnPause()
    {
        base.OnPause();
        NotificationOrchestrator.IsAppInForeground = false;
    }

    protected override void OnResume()
    {
        base.OnResume();
        NotificationOrchestrator.IsAppInForeground = true;

        // Reconnect relay WebSockets after returning from background
        // (Android suspends network connections when app is backgrounded)
        if (_shellViewModel?.MainViewModel != null && _shellViewModel.IsLoggedIn)
        {
            var mainVm = _shellViewModel.MainViewModel;
            mainVm.ChatListViewModel.StatusMessage = "Reconnecting to relays...";

            _ = Task.Run(async () =>
            {
                try
                {
                    await mainVm.ReconnectCommand.Execute();
                    RunOnUiThread(() =>
                    {
                        var connected = mainVm.RelayStatuses.Count(r => r.IsConnected);
                        var total = mainVm.RelayStatuses.Count;
                        mainVm.ChatListViewModel.StatusMessage = $"Connected to {connected}/{total} relays";
                    });
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("Scramble", $"Relay reconnect failed: {ex.Message}");
                    RunOnUiThread(() =>
                    {
                        mainVm.ChatListViewModel.StatusMessage = "Relay reconnect failed";
                    });
                }
            });
        }

        // Reconnect external signer WebSocket after returning from background
        // (e.g. user switched to Amber to approve, then came back)
        var signer = _shellViewModel?.LoginViewModel?.ExternalSigner;
        if (signer != null && !signer.IsConnected)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await signer.ReconnectAsync();
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("Scramble", $"Signer reconnect failed: {ex.Message}");
                }
            });
        }
    }

    protected override void OnDestroy()
    {
        _disposables.Dispose();
        base.OnDestroy();
    }

    private void StartRelayServiceIfEnabled(MainViewModel mainVm)
    {
        // React to notification mode changes (including the initial load from storage).
        // Service starts when user opts in to background mode, stops when they opt out.
        var settingsVm = mainVm.SettingsViewModel;
        settingsVm.WhenAnyValue(x => x.NotificationModeBackground)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(isBackground =>
            {
                if (isBackground)
                    Services.RelayForegroundService.Start(this);
                else
                    Services.RelayForegroundService.Stop(this);
            })
            .DisposeWith(_disposables);
    }
}
