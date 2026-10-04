using System.Drawing;
using Forms = System.Windows.Forms;

namespace OfficeChat.Views;

/// <summary>Значок в области уведомлений: открыть окно, выйти, признак непрочитанных.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _normalIcon = AppIcon.Create(withBadge: false);
    private readonly Icon _unreadIcon = AppIcon.Create(withBadge: true);

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = menu.Items.Add("Открыть", null, (_, _) => OpenRequested?.Invoke());
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _normalIcon,
            Text = "OfficeChat",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                OpenRequested?.Invoke();
        };
    }

    public void SetUnread(int count)
    {
        _notifyIcon.Icon = count > 0 ? _unreadIcon : _normalIcon;
        _notifyIcon.Text = count > 0 ? $"OfficeChat — непрочитанных: {count}" : "OfficeChat";
    }

    public void ShowHint(string title, string text) =>
        _notifyIcon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _normalIcon.Dispose();
        _unreadIcon.Dispose();
    }
}
