using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace OfficeChat.Platform;

/// <summary>
/// Значок в области уведомлений (StatusNotifierItem — Cinnamon, MATE, Xfce, KDE;
/// в GNOME нужно расширение AppIndicator). Открыть окно, выйти. Пока есть непрочитанные — показывает
/// их число и мигает, чтобы о сообщении нельзя было забыть, даже закрыв всплывающее окно.
/// </summary>
public sealed class Tray : IDisposable
{
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(600);

    private readonly TrayIcon _icon;
    private readonly DispatcherTimer _blinkTimer;
    private int _unread;
    private bool _badgeVisible;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public Tray()
    {
        var open = new NativeMenuItem("Открыть");
        open.Click += (_, _) => OpenRequested?.Invoke();
        var exit = new NativeMenuItem("Выход");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        _icon = new TrayIcon
        {
            Icon = AppIcon.Normal,
            ToolTipText = "OfficeChat",
            Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), exit } },
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => OpenRequested?.Invoke();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });

        _blinkTimer = new DispatcherTimer { Interval = BlinkInterval };
        _blinkTimer.Tick += (_, _) =>
        {
            _badgeVisible = !_badgeVisible;
            UpdateIcon();
        };
    }

    public void SetUnread(int count)
    {
        if (count == _unread) return;
        _unread = count;
        _icon.ToolTipText = count > 0 ? $"OfficeChat — непрочитанных: {count}" : "OfficeChat";
        _badgeVisible = count > 0;
        UpdateIcon();
        if (count > 0) _blinkTimer.Start();
        else _blinkTimer.Stop();
    }

    private void UpdateIcon() =>
        _icon.Icon = _badgeVisible && _unread > 0 ? AppIcon.WithBadge(_unread) : AppIcon.Normal;

    public void Dispose()
    {
        _blinkTimer.Stop();
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
