using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
using AndroidX.Core.Content;
using OfficeChat.Mobile;
using OfficeChat.Models;

namespace OfficeChat.Android;

/// <summary>Android: фоновая служба, уведомления, открытие файлов в других приложениях.</summary>
public sealed class AndroidPlatform(Context context) : IMobilePlatform
{
    private NotificationManager? Manager => context.GetSystemService(Context.NotificationService) as NotificationManager;

    // Номера уведомлений: у переписки и напоминания — свои, постоянные (новое заменяет прежнее).
    private static int IdOf(Guid key, int salt) => (key.GetHashCode() & 0x3FFFFFFF) ^ salt;

    public void StartBackground() => ChatForegroundService.Start(context);

    public void StopBackground() => ChatForegroundService.Stop(context);

    public void NotifyMessage(Contact conversation, ChatMessage message, int unread)
    {
        Notifications.EnsureChannels(context);
        var text = conversation.IsGroup && message.SenderName.Length > 0 ? $"{message.SenderName}: {message.PreviewText}" : message.PreviewText;
        var builder = Notifications.Builder(context, Notifications.MessagesChannel)
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifyChat)!
            .SetContentTitle(unread > 1 ? $"{conversation.Title} ({unread})" : conversation.Title)!
            .SetContentText(text)!
            .SetStyle(new Notification.BigTextStyle().BigText(text))!
            .SetAutoCancel(true)!
            .SetCategory(Notification.CategoryMessage)!
            .SetContentIntent(Notifications.OpenApp(context, conversation.Key, IdOf(conversation.Key, 1)))!;
        if (message.IsQuiet) builder.SetOnlyAlertOnce(true);
        Manager?.Notify(IdOf(conversation.Key, 1), builder.Build());
    }

    public void ClearMessageNotification(Guid conversationKey) => Manager?.Cancel(IdOf(conversationKey, 1));

    public void NotifyReminder(Guid id, string title, string text, bool urgent)
    {
        Notifications.EnsureChannels(context);
        var builder = Notifications.Builder(context, Notifications.RemindersChannel)
            .SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleAlarm)!
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetStyle(new Notification.BigTextStyle().BigText(text))!
            .SetCategory(Notification.CategoryReminder)!
            .SetAutoCancel(!urgent)!
            .SetOngoing(urgent)!
            .SetContentIntent(Notifications.OpenApp(context, null, IdOf(id, 2)))!;
        Manager?.Notify(IdOf(id, 2), builder.Build());
    }

    public void ClearReminderNotification(Guid id) => Manager?.Cancel(IdOf(id, 2));

    public void NotifyGame(Contact opponent, string text)
    {
        Notifications.EnsureChannels(context);
        var builder = Notifications.Builder(context, Notifications.RemindersChannel)
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifyChat)!
            .SetContentTitle(opponent.Title)!
            .SetContentText(text)!
            .SetAutoCancel(true)!
            .SetContentIntent(Notifications.OpenApp(context, opponent.Key, IdOf(opponent.Key, 3)))!;
        Manager?.Notify(IdOf(opponent.Key, 3), builder.Build());
    }

    public Task OpenFileAsync(Avalonia.Controls.TopLevel top, string path)
    {
        var file = new Java.IO.File(path);
        var uri = FileProvider.GetUriForFile(context, "ru.officechat.app.files", file);
        var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var type = MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension) ?? "*/*";
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, type);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        context.StartActivity(Intent.CreateChooser(intent, "Открыть файл")!.AddFlags(ActivityFlags.NewTask));
        return Task.CompletedTask;
    }

    public string DeviceName => $"{Build.Manufacturer} {Build.Model}".Trim();

    public string PlatformName => "android";

    /// <summary>Android 13+: уведомления нужно разрешить (один раз спрашиваем).</summary>
    public static void AskForNotifications(Activity activity)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            activity.CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
            activity.RequestPermissions(new[] { Manifest.Permission.PostNotifications }, 1);
    }
}
