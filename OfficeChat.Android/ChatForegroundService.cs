using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using Android.Runtime;

namespace OfficeChat.Android;

/// <summary>
/// Фоновая служба: пока она работает, Android не закрывает программу — связь с сервером не рвётся, и сообщения
/// с напоминаниями приходят сразу (облачных push-уведомлений у офисного сервера нет). В строке уведомлений — значок.
/// </summary>
[Register("ru/officechat/app/ChatService")]
public class ChatForegroundService : Service
{
    private const int NotificationId = 1;
    private WifiManager.MulticastLock? _multicast;

    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(ChatForegroundService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) context.StartForegroundService(intent);
        else context.StartService(intent);
    }

    public static void Stop(Context context) => context.StopService(new Intent(context, typeof(ChatForegroundService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        AndroidSetup.Init(this);
        var notification = Notifications.ServiceNotification(this);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);

        // Широковещательные пакеты Wi-Fi (поиск сервера и коллег) телефон без этого отбрасывает.
        if (_multicast == null && GetSystemService(WifiService) is WifiManager wifi)
        {
            _multicast = wifi.CreateMulticastLock("officechat");
            _multicast.SetReferenceCounted(false);
            _multicast.Acquire();
        }
        // Систему перезапустила службу без экрана (программу выгружали) — поднимаем переписку сами.
        AndroidSetup.StartHeadless(this);
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _multicast?.Release();
        _multicast = null;
        base.OnDestroy();
    }
}
