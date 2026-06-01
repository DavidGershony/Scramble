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

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "STOP")
        {
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

        // NotSticky: we manage the lifecycle ourselves from MainActivity;
        // the system should not restart this service after process death.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
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
        _wakeLock?.Acquire();
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

    /// <summary>Stop the foreground service.</summary>
    public static void Stop(Context context)
    {
        var intent = new Intent(context, typeof(RelayForegroundService));
        intent.SetAction("STOP");
        context.StartService(intent);
    }
}
