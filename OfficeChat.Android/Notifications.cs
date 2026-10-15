using Android.App;
using Android.Content;
using Android.OS;

namespace OfficeChat.Android;

/// <summary>Каналы и уведомления: сообщения, напоминания, игры и постоянный значок фоновой работы.</summary>
internal static class Notifications
{
    public const string MessagesChannel = "messages";
    public const string RemindersChannel = "reminders";
    public const string ServiceChannel = "service";

    public static void EnsureChannels(Context context)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O || context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
            return;
        manager.CreateNotificationChannel(new NotificationChannel(MessagesChannel, "Сообщения", NotificationImportance.High)
        {
            Description = "Новые сообщения от коллег",
        });
        manager.CreateNotificationChannel(new NotificationChannel(RemindersChannel, "Напоминания", NotificationImportance.High)
        {
            Description = "Сработавшие напоминания и приглашения в игру",
        });
        var service = new NotificationChannel(ServiceChannel, "Работа в фоне", NotificationImportance.Min)
        {
            Description = "Значок, пока OfficeChat на связи (без него Android закрывает программу и сообщения не приходят)",
        };
        service.SetShowBadge(false);
        manager.CreateNotificationChannel(service);
    }

    /// <summary>Нажатие на уведомление — открыть программу (и переписку, если указана).</summary>
    public static PendingIntent OpenApp(Context context, Guid? conversation, int requestCode)
    {
        var intent = new Intent(context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        if (conversation is { } key) intent.PutExtra(MainActivity.ConversationExtra, key.ToString());
        return PendingIntent.GetActivity(context, requestCode, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    public static Notification ServiceNotification(Context context)
    {
        EnsureChannels(context);
        return Builder(context, ServiceChannel)
            .SetContentTitle("OfficeChat на связи")!
            .SetContentText("Сообщения и напоминания приходят сразу")!
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifyChat)!
            .SetOngoing(true)!
            .SetContentIntent(OpenApp(context, null, 0))!
            .Build()!;
    }

    public static Notification.Builder Builder(Context context, string channel) =>
        Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(context, channel)
#pragma warning disable CA1422 // до Android 8 каналов нет
            : new Notification.Builder(context);
#pragma warning restore CA1422
}
