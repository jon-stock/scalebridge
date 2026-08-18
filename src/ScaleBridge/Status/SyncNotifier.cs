using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace ScaleBridge.Status;

/// <summary>
/// Posts the "minimal status visibility" notification required by Prompt.md Section 4,
/// requirement 7, and the foreground-service notification required while the GATT connection
/// in <see cref="Ble.ScaleConnectionService"/> is open.
/// </summary>
public static class SyncNotifier
{
    public const string ChannelId = "scale_bridge_status";
    public const int ForegroundNotificationId = 1;
    public const int StatusNotificationId = 2;

    public static void EnsureChannel(Context context)
    {
        if ((int)Build.VERSION.SdkInt < 26)
            return;

        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        var channel = new NotificationChannel(ChannelId, "ScaleBridge sync status", NotificationImportance.Low)
        {
            Description = "Shows the outcome of automatic weight syncs to Health Connect.",
        };
        manager.CreateNotificationChannel(channel);
    }

    public static Notification BuildForegroundNotification(Context context, string text)
    {
        EnsureChannel(context);
        return new NotificationCompat.Builder(context, ChannelId)
            .SetContentTitle("ScaleBridge")
            .SetContentText(text)
            .SetSmallIcon(Resource.Drawable.appicon)
            .SetOngoing(true)
            .SetPriority(NotificationCompat.PriorityLow)
            .Build();
    }

    public static void PostSuccess(Context context, double weightKg, DateTimeOffset whenLocal)
    {
        EnsureChannel(context);
        var notification = new NotificationCompat.Builder(context, ChannelId)
            .SetContentTitle("ScaleBridge: weight synced")
            .SetContentText($"{weightKg:0.0} kg written to Health Connect at {whenLocal:t}")
            .SetSmallIcon(Resource.Drawable.appicon)
            .SetAutoCancel(true)
            .Build();

        NotificationManagerCompat.From(context).Notify(StatusNotificationId, notification);
    }

    public static void PostError(Context context, string message)
    {
        EnsureChannel(context);
        var notification = new NotificationCompat.Builder(context, ChannelId)
            .SetContentTitle("ScaleBridge: sync failed")
            .SetContentText(message)
            .SetSmallIcon(Resource.Drawable.appicon)
            .SetAutoCancel(true)
            .Build();

        NotificationManagerCompat.From(context).Notify(StatusNotificationId, notification);
    }
}
