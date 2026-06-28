using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Android.App;
using Android.Content.PM;
using Android.Views;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using AndroidX.Core.View;
using Avalonia.Android;
using ReactiveUI;
using Scramble.MobileAndroid.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;

namespace Scramble.MobileAndroid;

[Activity(
    Label = "Scramble",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/Icon",
    MainLauncher = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    /// <summary>
    /// Singleton so App.axaml.cs can wire the permission request delegate.
    /// Set in OnCreate, cleared in OnDestroy.
    /// </summary>
    public static MainActivity? Current { get; private set; }

    /// <summary>
    /// Reference to the ShellViewModel so the back button can navigate.
    /// Set from App.axaml.cs after creating the view model.
    /// </summary>
    public static ShellViewModel? Shell { get; private set; }

    private int _nextRequestCode = 2000;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingPermissions = new();
    private CompositeDisposable _disposables = new();

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);

        // Create notification channels (safe to call multiple times)
        RelayForegroundService.CreateNotificationChannel(this);
        MobileAndroidNotificationService.CreateChannel(this);

        // Set platform notification service for the orchestrator
        NotificationOrchestrator.NotificationService = new MobileAndroidNotificationService(this);

        // If Shell was already set (normal cold start where OnFrameworkInitializationCompleted
        // runs synchronously inside base.OnCreate), wire the subscription immediately.
        if (Shell != null)
            SubscribeToLoginState();

        // Handle the Android back button / gesture to navigate within the app
        OnBackPressedDispatcher.AddCallback(this, new ScrambleBackCallback());

        // IME (soft keyboard) inset handling.
        //
        // Android 15+ (targetSdk 35) sets EDGE_TO_EDGE_ENFORCED on the window, which
        // silently disables WindowSoftInputMode=AdjustResize — the activity stays
        // fullscreen even when the IME opens, and the chat input is hidden behind
        // the keyboard. WindowCompat.SetDecorFitsSystemWindows(true) does NOT
        // override the enforcement (verified via dumpsys window — the flag stays on).
        //
        // The working fix is to install a WindowInsetsCompat listener on the Avalonia
        // view itself (first child of android.R.id.content) and apply the IME bottom
        // inset as padding. Applying to android.R.id.content does not work because
        // Avalonia draws into a SurfaceView that doesn't react to parent padding.
        //
        // Try to attach immediately (Avalonia.Android usually has its view mounted by
        // the time base.OnCreate returns), and also post as a backstop in case Avalonia
        // is still booting its view tree on a slow boot.
        InstallImeInsetHandling();
        Window?.DecorView.Post(InstallImeInsetHandling);
    }

    /// <summary>
    /// Called from App.axaml.cs after the ShellViewModel is created. Handles the case
    /// where OnCreate finishes before OnFrameworkInitializationCompleted (rare, but
    /// possible on process restart). Safe to call multiple times — only the first
    /// call wires the subscription.
    /// </summary>
    public static void SetShell(ShellViewModel shell)
    {
        Shell = shell;
        // Wire subscription if the Activity is already alive but missed the Shell in OnCreate
        Current?.SubscribeToLoginState();
    }

    private bool _loginStateSubscribed;

    private void SubscribeToLoginState()
    {
        if (_loginStateSubscribed || Shell == null) return;
        _loginStateSubscribed = true;

        Shell.WhenAnyValue(x => x.IsLoggedIn)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(isLoggedIn =>
            {
                if (isLoggedIn && Shell.MainViewModel != null)
                    StartRelayServiceIfEnabled(Shell.MainViewModel);
                else
                    RelayForegroundService.StopIfRunning(this);
            })
            .DisposeWith(_disposables);
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
    }

    protected override void OnDestroy()
    {
        _loginStateSubscribed = false;
        _disposables.Dispose();
        _disposables = new CompositeDisposable();
        if (Current == this) Current = null;
        base.OnDestroy();
    }

    /// <summary>
    /// Observes the NotificationModeBackground and NotificationsEnabled settings
    /// and starts/stops the relay foreground service accordingly.
    /// The service only runs when BOTH the master toggle AND background mode are enabled.
    /// </summary>
    private void StartRelayServiceIfEnabled(MainViewModel mainVm)
    {
        var settingsVm = mainVm.SettingsViewModel;
        settingsVm.WhenAnyValue(
                x => x.NotificationsEnabled,
                x => x.NotificationModeBackground,
                (enabled, background) => enabled && background)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(shouldRun =>
            {
                if (shouldRun)
                    RelayForegroundService.Start(this);
                else
                    RelayForegroundService.StopIfRunning(this);
            })
            .DisposeWith(_disposables);
    }

    /// <summary>
    /// Request runtime permissions and return true if all were granted.
    /// Skips the prompt for permissions that are already granted.
    /// </summary>
    public Task<bool> RequestPermissionsAsync(string[] permissions)
    {
        // Filter to only permissions that haven't been granted yet
        var needed = permissions
            .Where(p => ContextCompat.CheckSelfPermission(this, p) != Permission.Granted)
            .ToArray();

        if (needed.Length == 0)
            return Task.FromResult(true);

        var requestCode = Interlocked.Increment(ref _nextRequestCode);
        var tcs = new TaskCompletionSource<bool>();
        _pendingPermissions[requestCode] = tcs;

        ActivityCompat.RequestPermissions(this, needed, requestCode);

        return tcs.Task;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (_pendingPermissions.TryRemove(requestCode, out var tcs))
        {
            var allGranted = grantResults.Length > 0 && grantResults.All(r => r == Permission.Granted);
            tcs.TrySetResult(allGranted);
        }
    }

    private bool _imeListenersAttached;

    /// <summary>
    /// Resolves the Avalonia surface view (first child of <c>android.R.id.content</c>)
    /// and attaches the IME inset listener + animation callback that drive the
    /// keyboard-aware padding. Idempotent: a second call is a no-op once the
    /// listeners are bound, which lets us call this both inline from <see cref="OnCreate"/>
    /// and as a posted message — whichever observes the Avalonia view first wins.
    /// </summary>
    private void InstallImeInsetHandling()
    {
        if (_imeListenersAttached) return;

        if (Window?.DecorView.FindViewById(global::Android.Resource.Id.Content) is not Android.Views.ViewGroup contentGroup)
            return;
        if (contentGroup.ChildCount == 0)
        {
            contentGroup.Post(InstallImeInsetHandling);
            return;
        }

        var avaloniaView = contentGroup.GetChildAt(0);
        if (avaloniaView == null) return;

        ViewCompat.SetOnApplyWindowInsetsListener(avaloniaView, new ImeInsetListener());
        ViewCompat.SetWindowInsetsAnimationCallback(avaloniaView,
            new ImeInsetAnimationCallback(WindowInsetsAnimationCompat.Callback.DispatchModeStop, avaloniaView));
        ViewCompat.RequestApplyInsets(avaloniaView);
        _imeListenersAttached = true;
    }

    /// <summary>
    /// Reads the IME bottom inset off the incoming WindowInsetsCompat and applies it as
    /// bottom padding on the receiving view. System bar insets (status / navigation) are
    /// preserved on the other edges so we don't trample whatever the system chrome needs.
    /// </summary>
    private sealed class ImeInsetListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat? OnApplyWindowInsets(global::Android.Views.View? v, WindowInsetsCompat? insets)
        {
            if (v == null || insets == null) return insets;

            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime());
            var systemBars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            var bottom = System.Math.Max(ime.Bottom, systemBars.Bottom);
            v.SetPadding(systemBars.Left, systemBars.Top, systemBars.Right, bottom);
            return insets;
        }
    }

    /// <summary>
    /// Animation callback that interpolates the bottom padding while the IME is
    /// opening or closing, so the content slides in sync with the keyboard.
    /// </summary>
    private sealed class ImeInsetAnimationCallback : WindowInsetsAnimationCompat.Callback
    {
        private readonly global::Android.Views.View _target;

        public ImeInsetAnimationCallback(int dispatchMode, global::Android.Views.View target) : base(dispatchMode)
        {
            _target = target;
        }

        public override WindowInsetsCompat OnProgress(
            WindowInsetsCompat? insets,
            IList<WindowInsetsAnimationCompat>? runningAnimations)
        {
            if (insets == null) return new WindowInsetsCompat.Builder().Build();

            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime());
            var systemBars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            var bottom = System.Math.Max(ime.Bottom, systemBars.Bottom);
            _target.SetPadding(systemBars.Left, systemBars.Top, systemBars.Right, bottom);
            return insets;
        }
    }

    /// <summary>
    /// Handles the Android back button / gesture. Navigates within the app:
    /// chat -> chat list, settings -> chat list, chat list -> exit.
    /// </summary>
    private class ScrambleBackCallback : AndroidX.Activity.OnBackPressedCallback
    {
        public ScrambleBackCallback() : base(true) { }

        public override void HandleOnBackPressed()
        {
            var main = Shell?.MainViewModel;
            if (main == null)
            {
                // No active session — let Android handle it (exit/minimize)
                Enabled = false;
                Current?.OnBackPressedDispatcher.OnBackPressed();
                Enabled = true;
                return;
            }

            // If a chat is open, go back to the chat list
            if (main.ChatViewModel.HasChat && main.ChatViewModel.BackCommand != null)
            {
                main.ChatViewModel.BackCommand.Execute().Subscribe();
                return;
            }

            // If settings (or any overlay) is open, go back to chat list
            if (main.CurrentView != null)
            {
                main.ShowChatsCommand.Execute().Subscribe();
                return;
            }

            // Already at root (chat list) — let Android handle it (exit/minimize)
            Enabled = false;
            Current?.OnBackPressedDispatcher.OnBackPressed();
            Enabled = true;
        }
    }
}
