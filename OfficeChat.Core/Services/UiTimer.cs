namespace OfficeChat.Services;

/// <summary>
/// Таймер, который срабатывает в потоке, где его создали (в UI-потоке программы), — замена
/// DispatcherTimer из WPF, чтобы общий код не зависел ни от WPF, ни от Avalonia.
/// Работает через SynchronizationContext, который есть у любого UI-фреймворка.
/// </summary>
public sealed class UiTimer : IDisposable
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private Timer? _timer;
    // Срабатывание уже поставлено в очередь UI-потока — следующее не копим (как DispatcherTimer).
    private int _pending;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    public bool IsEnabled => _timer != null;

    public event EventHandler? Tick;

    public void Start()
    {
        Stop();
        _timer = new Timer(_ => OnElapsed(), null, Interval, Interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void OnElapsed()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        if (_context == null)
        {
            Raise();
            return;
        }
        _context.Post(_ => Raise(), null);
    }

    private void Raise()
    {
        Interlocked.Exchange(ref _pending, 0);
        if (_timer != null) Tick?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();
}
