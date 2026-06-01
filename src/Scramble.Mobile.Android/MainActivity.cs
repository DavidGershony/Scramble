using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Android.App;
using Android.Content.PM;
using Android.Views;
using AndroidX.Core.App;
using AndroidX.Core.Content;
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
    public static ShellViewModel? Shell { get; set; }

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

        // Observe login state to start/stop relay foreground service
        if (Shell != null)
        {
            Shell.WhenAnyValue(x => x.IsLoggedIn)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(isLoggedIn =>
                {
                    if (isLoggedIn && Shell.MainViewModel != null)
                        StartRelayServiceIfEnabled(Shell.MainViewModel);
                    else
                        RelayForegroundService.Stop(this);
                })
                .DisposeWith(_disposables);
        }

        // Handle the Android back button / gesture to navigate within the app
        OnBackPressedDispatcher.AddCallback(this, new ScrambleBackCallback());
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
        _disposables.Dispose();
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
                    RelayForegroundService.Stop(this);
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
