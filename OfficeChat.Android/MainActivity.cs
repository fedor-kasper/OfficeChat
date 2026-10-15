using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using OfficeChat.Mobile;

namespace OfficeChat.Android;

/// <summary>Экран приложения. Сама переписка живёт в фоновой службе — экран можно закрыть, сообщения продолжат приходить.</summary>
[Activity(Label = "OfficeChat", Theme = "@style/OfficeChatTheme", Icon = "@drawable/icon", MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask, WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : AvaloniaMainActivity<MobileApp>
{
    /// <summary>В Intent уведомления: какую переписку открыть.</summary>
    public const string ConversationExtra = "conversation";

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        AndroidSetup.Init(this);
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }

    /// <summary>Экран, открытый сейчас (для камеры: снимок возвращается в экран).</summary>
    public static MainActivity? Current { get; private set; }

    private const int PhotoRequest = 4242;
    private TaskCompletionSource<bool>? _photo;

    /// <summary>Открыть камеру; снимок запишется в target. true — снимок сделан.</summary>
    public Task<bool> CapturePhotoAsync(Java.IO.File target)
    {
        _photo?.TrySetResult(false);
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(this, AndroidPlatform.FilesAuthority, target);
        var intent = new Intent(global::Android.Provider.MediaStore.ActionImageCapture);
        intent.PutExtra(global::Android.Provider.MediaStore.ExtraOutput, uri);
        intent.AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);
        _photo = new TaskCompletionSource<bool>();
        StartActivityForResult(intent, PhotoRequest);
        return _photo.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == PhotoRequest) _photo?.TrySetResult(resultCode == Result.Ok);
    }

    protected override void OnDestroy()
    {
        if (Current == this) Current = null;
        _photo?.TrySetResult(false);
        base.OnDestroy();
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
        AndroidPlatform.AskForNotifications(this);
        OpenFromIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        OpenFromIntent(intent);
    }

    protected override void OnResume()
    {
        Current = this;
        base.OnResume();
        MobileSession.IsForeground = true;
        // Вернулись на открытую переписку — новые в ней прочитаны.
        if (MobileSession.VisibleConversation is { } contact) MobileSession.Opened(contact);
    }

    protected override void OnPause()
    {
        MobileSession.IsForeground = false;
        base.OnPause();
    }

    private static void OpenFromIntent(Intent? intent)
    {
        if (intent?.GetStringExtra(ConversationExtra) is { } key && Guid.TryParse(key, out var id))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ShellView.Instance?.OpenConversation(id));
    }
}
