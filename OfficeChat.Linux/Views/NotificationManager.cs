using Avalonia;
using Avalonia.Controls;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Складывает всплывающие окна стопкой снизу вверх в правом нижнем углу экрана.</summary>
public sealed class NotificationManager
{
    /// <summary>
    /// Окна висят, пока их не закроют, поэтому на каждого собеседника — одно окно.
    /// Если одновременно пишут больше людей, самое старое окно убирается
    /// (сообщения при этом остаются непрочитанными в главном окне).
    /// </summary>
    private const int MaxVisible = 5;

    private readonly ChatService _chat;
    private readonly Action<Contact> _openConversation;

    // [0] — нижнее (самое старое) окно.
    private readonly List<Window> _windows = new();

    public NotificationManager(ChatService chat, Action<Contact> openConversation)
    {
        _chat = chat;
        _openConversation = openConversation;
    }

    public void Show(Contact contact, ChatMessage message)
    {
        // Этот человек уже висит на экране — дописываем в его окно.
        var existing = _windows.OfType<NotificationWindow>().FirstOrDefault(w => w.Contact == contact && !w.IsClosing);
        if (existing != null)
        {
            existing.AddMessage(message);
            return;
        }

        var window = new NotificationWindow(contact, message);
        window.OpenRequested += w =>
        {
            w.FadeOutAndClose();
            _openConversation(w.Contact);
        };
        window.ReplyRequested += (w, text) => w.AddMessage(_chat.Reply(w.Contact, text));
        AddToStack(window);
    }

    /// <summary>Приглашение в игру с кнопками «Принять / Отклонить».</summary>
    public void ShowGameInvite(BoardGame game, Action<BoardGame> accept, Action<BoardGame> decline)
    {
        var window = new GameInviteWindow(game);
        window.Accepted += w => accept(w.Game);
        window.Declined += w => decline(w.Game);
        AddToStack(window);
    }

    /// <summary>Сработавшее напоминание. Если его окно уже на экране — новое не открываем.</summary>
    public void ShowReminder(Reminder reminder, ReminderService reminders, Action openBoard)
    {
        if (_windows.OfType<ReminderPopupWindow>().Any(w => w.Reminder == reminder && !w.IsClosing)) return;
        var window = new ReminderPopupWindow(reminder);
        window.DoneRequested += w => reminders.Complete(w.Reminder);
        window.SnoozeRequested += (w, anchor) => SnoozeMenu.Show(anchor, w.Reminder, reminders, null);
        window.OpenRequested += w =>
        {
            w.FadeOutAndClose();
            openBoard();
        };
        window.Dismissed += w => reminders.Dismiss(w.Reminder);
        AddToStack(window);
    }

    /// <summary>Закрыть окна напоминаний, на которые уже ответили (выполнено, отложено, удалено).</summary>
    public void CloseAnsweredReminders(ReminderService reminders)
    {
        foreach (var window in _windows.OfType<ReminderPopupWindow>()
                     .Where(w => !w.IsClosing && (!w.Reminder.IsAlerting || w.Reminder.IsDone ||
                                                  !reminders.Reminders.Contains(w.Reminder)))
                     .ToList())
            window.FadeOutAndClose();
    }

    private void AddToStack<T>(T window) where T : Window, IStackedPopup
    {
        // Высота окна устанавливается по содержимому уже после показа — пересчитываем позицию при каждом изменении.
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty || e.Property == TopLevel.ClientSizeProperty)
                Layout();
        };
        window.Opened += (_, _) => Layout();
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            Layout();
        };

        _windows.Add(window);

        // Приглашения не вытесняем — на них нужно ответить.
        foreach (var old in _windows.OfType<NotificationWindow>().Where(w => !w.IsClosing).SkipLast(MaxVisible).ToList())
            old.FadeOutAndClose();

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Show();
        Layout();
    }

    /// <summary>Сообщение удалили — убираем его из всплывающих окон.</summary>
    public void Remove(ChatMessage message)
    {
        foreach (var window in _windows.OfType<NotificationWindow>().ToList())
            window.RemoveMessage(message);
    }

    public void CloseFor(Contact contact)
    {
        foreach (var window in _windows.OfType<NotificationWindow>().Where(w => w.Contact == contact).ToList())
            window.FadeOutAndClose();
    }

    public void CloseAll()
    {
        foreach (var window in _windows.ToList())
            window.Close();
    }

    /// <summary>Раскладывает окна снизу вверх от правого нижнего угла рабочей области (без панели задач).</summary>
    private void Layout()
    {
        // Основной монитор; если система его не отметила (бывает с несколькими мониторами) — первый попавшийся.
        var screens = _windows.FirstOrDefault()?.Screens;
        var screen = screens?.Primary ?? screens?.All.FirstOrDefault();
        if (screen == null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var bottom = area.Bottom;
        foreach (var window in _windows)
        {
            var width = (int)Math.Ceiling(window.ClientSize.Width * scale);
            var height = (int)Math.Ceiling(window.ClientSize.Height * scale);
            if (width == 0 || height == 0) continue;
            window.Position = new PixelPoint(area.Right - width, bottom - height);
            bottom -= height;
        }
    }
}
