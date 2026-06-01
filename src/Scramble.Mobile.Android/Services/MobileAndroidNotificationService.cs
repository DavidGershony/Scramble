using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Scramble.Presentation.Services;
using _Res = global::Scramble.Mobile.Android.Resource;

namespace Scramble.MobileAndroid.Services;

public class MobileAndroidNotificationService : INotificationService
{
    public const string ChannelId = "scramble_messages";
    private readonly Context _context;
    private readonly NotificationManager? _notificationManager;

    public MobileAndroidNotificationService(Context context)
    {
        _context = context;
        _notificationManager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
    }

    public void ShowMessageNotification(string chatId, string chatName, string senderName, string messagePreview)
    {
        var intent = new Intent(_context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        intent.PutExtra("chatId", chatId);

        var pendingIntent = PendingIntent.GetActivity(
            _context, chatId.GetHashCode(), intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var title = string.IsNullOrEmpty(chatName) ? senderName : chatName;
        var text = chatName == senderName ? messagePreview : $"{senderName}: {messagePreview}";

        var notification = new NotificationCompat.Builder(_context, ChannelId)
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetSmallIcon(_Res.Drawable.ic_notification)!
            .SetContentIntent(pendingIntent)!
            .SetAutoCancel(true)!
            .SetCategory(Notification.CategoryMessage)!
            .SetPriority(NotificationCompat.PriorityHigh)
            .Build()!;

        _notificationManager?.Notify(chatId.GetHashCode(), notification);
    }

    public void ClearNotificationsForChat(string chatId)
    {
        _notificationManager?.Cancel(chatId.GetHashCode());
    }

    /// <summary>
    /// Creates the notification channel for chat messages.
    /// Call once during app startup (e.g. in MainActivity.OnCreate).
    /// </summary>
    public static void CreateChannel(Context context)
    {
        var channel = new NotificationChannel(
            ChannelId,
            "Messages",
            NotificationImportance.High)
        {
            Description = "Notifications for incoming chat messages"
        };

        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        manager?.CreateNotificationChannel(channel);
    }
}
