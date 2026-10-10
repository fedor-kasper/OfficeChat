using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace OfficeChat.Views;

/// <summary>
/// Значок в области уведомлений: открыть окно, выйти. Пока есть непрочитанные — показывает их число
/// и мигает, чтобы о сообщении нельзя было забыть, даже закрыв всплывающее окно.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(600);

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _normalIcon = AppIcon.Create(unread: 0);
    // Значки с числом создаются по мере надобности (их всего десять: 1…9 и «9+»).
    private readonly Dictionary<string, Icon> _badgeIcons = new();
    private readonly DispatcherTimer _blinkTimer;
    private int _unread;
    private bool _badgeVisible;

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
        _notifyIcon.Text = count > 0 ? $"OfficeChat — непрочитанных: {count}" : "OfficeChat";
        _badgeVisible = count > 0;
        UpdateIcon();
        if (count > 0) _blinkTimer.Start();
        else _blinkTimer.Stop();
    }

    private void UpdateIcon()
    {
        if (!_badgeVisible || _unread == 0)
        {
            _notifyIcon.Icon = _normalIcon;
            return;
        }
        var text = AppIcon.BadgeText(_unread);
        if (!_badgeIcons.TryGetValue(text, out var icon))
            _badgeIcons[text] = icon = AppIcon.Create(_unread);
        _notifyIcon.Icon = icon;
    }

    public void ShowHint(string title, string text) =>
        _notifyIcon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _blinkTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _normalIcon.Dispose();
        foreach (var icon in _badgeIcons.Values)
            icon.Dispose();
    }
}
