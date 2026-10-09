using Avalonia;
using Avalonia.Controls;

namespace OfficeChat.Platform;

/// <summary>
/// Значок в области уведомлений (StatusNotifierItem — Cinnamon, MATE, Xfce, KDE;
/// в GNOME нужно расширение AppIndicator). Открыть окно, выйти, точка при непрочитанных.
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly TrayIcon _icon;

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
    }

    public void SetUnread(int count)
    {
        _icon.Icon = count > 0 ? AppIcon.WithBadge : AppIcon.Normal;
        _icon.ToolTipText = count > 0 ? $"OfficeChat — непрочитанных: {count}" : "OfficeChat";
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
