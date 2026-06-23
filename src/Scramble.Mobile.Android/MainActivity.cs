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
        // On Android 15+ (targetSdk 35) edge-to-edge is enforced and
        // WindowSoftInputMode=AdjustResize silently no-ops — the activity draws under
        // the IME and we have to apply the keyboard's bottom inset as padding ourselves.
        // Without this the chat input is hidden behind the keyboard.
        //
        // We attach both:
        //   * a one-shot inset listener that sets the final padding once the keyboard
        //     has finished animating (covers Android 10 and earlier where the animation
        //     callback isn't dispatched), and
        //   * a WindowInsetsAnimationCompat callback that drives the padding frame by
        //     frame so the content tracks the keyboard during the open/close animation
        //     on Android 11+.
        InstallImeInsetHandling();
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

    /// <summary>
    /// Wires both the inset listener and the animation callback that translate the
    /// IME (soft keyboard) bottom inset into bottom padding on the Avalonia content
    /// view. Applied to <c>android.R.id.content</c> so every screen the app renders
    /// — chat, settings, dialogs — automatically picks up the keyboard inset.
    /// </summary>
    private void InstallImeInsetHandling()
    {
        var rootView = Window?.DecorView.FindViewById(global::Android.Resource.Id.Content);
        if (rootView == null) return;

        // Static inset listener: applies the final padding once the keyboard has
        // finished animating. On Android 11+ the animation callback drives this in
        // real-time during the open/close; this listener still fires for the final
        // value and for any platform that doesn't deliver the animation callbacks.
        ViewCompat.SetOnApplyWindowInsetsListener(rootView, new ImeInsetListener());

        // Animated callback (Android 11+): drives the bottom padding frame-by-frame
        // so the content slides in sync with the keyboard instead of snapping at the
        // end. Dispatch mode is "stop" so children don't also receive the animation —
        // we're the only consumer in the activity.
        ViewCompat.SetWindowInsetsAnimationCallback(rootView,
            new ImeInsetAnimationCallback(WindowInsetsAnimationCompat.Callback.DispatchModeStop));
    }

    /// <summary>
    /// Reads the IME bottom inset off the incoming WindowInsetsCompat and applies it as
    /// bottom padding on the receiving view. System bar insets (status / navigation) are
    /// preserved on the other edges so we don't trample whatever the system chrome needs.
    /// </summary>
    private sealed class ImeInsetListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(global::Android.Views.View v, WindowInsetsCompat insets)
        {
            var imeBottom = insets.GetInsets(WindowInsetsCompat.Type.Ime()).Bottom;
            var systemBars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());

            // When the IME is up its inset already includes the navigation bar height;
            // when it's down we still need to leave room for the nav bar itself.
            var bottom = System.Math.Max(imeBottom, systemBars.Bottom);

            v.SetPadding(systemBars.Left, systemBars.Top, systemBars.Right, bottom);
            return insets;
        }
    }

    /// <summary>
    /// Animation callback that interpolates the bottom padding while the IME is
    /// opening or closing. Required because <see cref="ImeInsetListener"/> only sees
    /// the final state; without this the content snaps instead of sliding.
    /// </summary>
    private sealed class ImeInsetAnimationCallback : WindowInsetsAnimationCompat.Callback
    {
        public ImeInsetAnimationCallback(int dispatchMode) : base(dispatchMode) { }

        public override WindowInsetsCompat OnProgress(
            WindowInsetsCompat insets,
            IList<WindowInsetsAnimationCompat> runningAnimations)
        {
            // Only react if at least one running animation is the IME (a system bar
            // animation could also be in flight on some devices); the inset value
            // already reflects whatever the runtime is currently displaying so we can
            // just forward it through the same calculation the static listener uses.
            var imeBottom = insets.GetInsets(WindowInsetsCompat.Type.Ime()).Bottom;
            var systemBars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            var bottom = System.Math.Max(imeBottom, systemBars.Bottom);

            var root = Current?.Window?.DecorView.FindViewById(global::Android.Resource.Id.Content);
            root?.SetPadding(systemBars.Left, systemBars.Top, systemBars.Right, bottom);
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
