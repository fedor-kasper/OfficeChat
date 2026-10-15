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
    /// <summary>Через этот FileProvider другие приложения получают наши файлы (открыть файл, снимок камерой).</summary>
    public const string FilesAuthority = "ru.officechat.app.files";

    /// <summary>Снимок для переписки — не больше этого по длинной стороне (камера даёт 12+ Мп, это лишнее).</summary>
    private const int MaxPhotoSide = 2048;

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
        var uri = FileProvider.GetUriForFile(context, FilesAuthority, file);
        var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var type = MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension) ?? "*/*";
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, type);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        context.StartActivity(Intent.CreateChooser(intent, "Открыть файл")!.AddFlags(ActivityFlags.NewTask));
        return Task.CompletedTask;
    }

    public bool CanTakePhoto => context.PackageManager?.HasSystemFeature(PackageManager.FeatureCameraAny) == true;

    public async Task<byte[]?> TakePhotoAsync()
    {
        if (MainActivity.Current is not { } activity) return null;
        var folder = new Java.IO.File(context.CacheDir, "photos");
        folder.Mkdirs();
        var file = new Java.IO.File(folder, Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            if (!await activity.CapturePhotoAsync(file) || !file.Exists() || file.Length() == 0) return null;
            return await Task.Run(() => Shrink(file.AbsolutePath));
        }
        catch (ActivityNotFoundException)
        {
            throw new InvalidOperationException("На телефоне нет приложения камеры");
        }
        finally
        {
            file.Delete();
        }
    }

    /// <summary>Уменьшить снимок и повернуть, как держали телефон (поворот камера пишет в EXIF), — в JPEG.</summary>
    private static byte[] Shrink(string path)
    {
        var bounds = new global::Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
        global::Android.Graphics.BitmapFactory.DecodeFile(path, bounds);
        var sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= MaxPhotoSide) sample *= 2;
        using var decoded = global::Android.Graphics.BitmapFactory.DecodeFile(path,
            new global::Android.Graphics.BitmapFactory.Options { InSampleSize = sample })
            ?? throw new InvalidOperationException("Снимок не читается");

        var matrix = new global::Android.Graphics.Matrix();
        var scale = Math.Min(1f, (float)MaxPhotoSide / Math.Max(decoded.Width, decoded.Height));
        matrix.PostScale(scale, scale);
        var orientation = new global::Android.Media.ExifInterface(path)
            .GetAttributeInt(global::Android.Media.ExifInterface.TagOrientation, 1);
        var degrees = orientation switch { 6 => 90, 3 => 180, 8 => 270, _ => 0 };
        if (degrees != 0) matrix.PostRotate(degrees);
        using var result = global::Android.Graphics.Bitmap.CreateBitmap(decoded, 0, 0, decoded.Width, decoded.Height, matrix, true);
        using var stream = new MemoryStream();
        result.Compress(global::Android.Graphics.Bitmap.CompressFormat.Jpeg!, 85, stream);
        return stream.ToArray();
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
