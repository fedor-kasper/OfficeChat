// Общие сервисы (ChatService, GameService) написаны под WPF и используют
// System.Windows.Threading.DispatcherTimer. В Linux-версии этого типа нет —
// подставляем совместимую обёртку над таймером Avalonia, чтобы общие файлы не менять.
namespace System.Windows.Threading;

public sealed class DispatcherTimer
{
    private readonly Avalonia.Threading.DispatcherTimer _timer = new();

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public event EventHandler? Tick
    {
        add => _timer.Tick += value;
        remove => _timer.Tick -= value;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();
}
