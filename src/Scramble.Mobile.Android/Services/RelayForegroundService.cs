using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using _Res = global::Scramble.Mobile.Android.Resource;

namespace Scramble.MobileAndroid.Services;

[Service(ForegroundServiceType = ForegroundService.TypeDataSync)]
public class RelayForegroundService : global::Android.App.Service
{
    public const string ChannelId = "scramble_relay_service";
    public const int NotificationId = 9001;
    private PowerManager.WakeLock? _wakeLock;

    /// <summary>
    /// Track whether the service is currently running so we can avoid
    /// starting a new instance just to immediately stop it (which crashes
    /// on Android 12+ because StartForeground is never called).
    /// </summary>
    private static volatile bool _isRunning;
    public static bool IsRunning => _isRunning;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "STOP")
        {
            _isRunning = false;
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // Ensure the notification channel exists (the service may be started
        // before MainActivity.OnCreate runs, e.g. after a process restart).
        CreateNotificationChannel(this);

        var notification = BuildNotification();
        StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);

        AcquireWakeLock();
        _isRunning = true;

        // Sticky: if Android kills the process for memory pressure, restart the
        // service (and thus the process) so relay connections can be re-established.
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _isRunning = false;
        ReleaseWakeLock();
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Scramble")!
            .SetContentText("Keeping relay connections alive")!
            .SetSmallIcon(_Res.Drawable.ic_notification)!
            .SetOngoing(true)!
            .SetSilent(true)!
            .SetCategory(Notification.CategoryService)!
            .SetPriority(NotificationCompat.PriorityLow)
            .Build()!;
    }

    private void AcquireWakeLock()
    {
        if (_wakeLock != null) return;
        var pm = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "Scramble::RelayService");
        // Use a 30-minute timeout to avoid indefinite wake locks draining battery.
        // The service will re-acquire on reconnect if needed.
        _wakeLock?.Acquire(30 * 60 * 1000L);
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock is { IsHeld: true })
            _wakeLock.Release();
        _wakeLock = null;
    }

    /// <summary>
    /// Creates the notification channel required for the foreground service.
    /// Call once during app startup (e.g. in MainActivity.OnCreate).
    /// </summary>
    public static void CreateNotificationChannel(Context context)
    {
        var channel = new NotificationChannel(
            ChannelId,
            "Relay Connection Service",
            NotificationImportance.Low)
        {
            Description = "Keeps relay WebSocket connections alive in the background"
        };
        channel.SetShowBadge(false);

        var manager = (NotificationManager?)context.GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(channel);
    }

    /// <summary>Start the foreground service.</summary>
    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(RelayForegroundService));
        ContextCompat.StartForegroundService(context, intent);
    }

    /// <summary>
    /// Stop the foreground service. Only sends the stop command if the service
    /// is actually running — avoids starting a new instance just to stop it,
    /// which would crash on Android 12+ (ForegroundServiceDidNotStartInTimeException).
    /// </summary>
    public static void Stop(Context context)
    {
        if (!_isRunning) return;
        var intent = new Intent(context, typeof(RelayForegroundService));
        intent.SetAction("STOP");
        context.StartService(intent);
    }

    /// <summary>
    /// Safe variant of Stop — only stops if the service is currently running.
    /// Use this from reactive subscriptions where Stop may be called spuriously
    /// (e.g. when IsLoggedIn fires false on initial subscription).
    /// </summary>
    public static void StopIfRunning(Context context)
    {
        if (!_isRunning) return;
        Stop(context);
    }
}
