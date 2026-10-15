using Android.Content;
using Android.OS;
using OfficeChat.Client;
using OfficeChat.Mobile;
using OfficeChat.Protocol;

namespace OfficeChat.Android;

/// <summary>Общая подготовка — и для экрана, и для службы, запущенной системой без экрана.</summary>
public static class AndroidSetup
{
    private static bool _done;

    public static void Init(Context context)
    {
        if (_done) return;
        _done = true;
        MobileSession.Platform = new AndroidPlatform(context.ApplicationContext ?? context);
        AccountLauncher.DeviceOverride = new DeviceInfo($"{Build.Manufacturer} {Build.Model}".Trim(), "android");
    }

    /// <summary>
    /// Запустить переписку без экрана (службу перезапустила система). Всё работает на главном потоке Android —
    /// том же, где потом будет интерфейс.
    /// </summary>
    public static void StartHeadless(Context context)
    {
        Init(context);
        if (MobileSession.IsRunning || MobileSession.SavedAccount() is not { } account) return;
        if (SynchronizationContext.Current == null)
            SynchronizationContext.SetSynchronizationContext(new LooperSynchronizationContext());
        MobileSession.Start(account);
    }
}

/// <summary>Выполнение на главном потоке Android (для службы, когда интерфейса ещё нет).</summary>
internal sealed class LooperSynchronizationContext : SynchronizationContext
{
    private readonly Handler _handler = new(Looper.MainLooper!);

    public override void Post(SendOrPostCallback d, object? state) => _handler.Post(() => d(state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Looper.MyLooper() == Looper.MainLooper)
        {
            d(state);
            return;
        }
        using var done = new ManualResetEventSlim();
        _handler.Post(() =>
        {
            try { d(state); }
            finally { done.Set(); }
        });
        done.Wait();
    }

    public override SynchronizationContext CreateCopy() => this;
}
