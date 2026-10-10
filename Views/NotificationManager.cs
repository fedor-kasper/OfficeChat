using System.Windows;
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

    private void AddToStack<T>(T window) where T : Window, IStackedPopup
    {
        window.SizeChanged += (_, _) => Layout();
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            Layout();
        };

        _windows.Add(window);

        // Приглашения не вытесняем — на них нужно ответить.
        foreach (var old in Popups.OfType<NotificationWindow>().Where(w => !w.IsClosing)
                     .SkipLast(MaxVisible).ToList())
            old.FadeOutAndClose();

        var area = SystemParameters.WorkArea;
        window.Left = area.Right - window.Width;
        window.Top = area.Bottom;
        window.Show();
        Layout();
    }

    private IEnumerable<IStackedPopup> Popups => _windows.Cast<IStackedPopup>();

    /// <summary>Сообщение удалили — убираем его из всплывающих окон.</summary>
    public void Remove(ChatMessage message)
    {
        foreach (var window in Popups.OfType<NotificationWindow>().ToList())
            window.RemoveMessage(message);
    }

    /// <summary>Закрывает окна о переписке, которую открыли в главном окне.</summary>
    public void CloseFor(Contact contact)
    {
        foreach (var window in Popups.OfType<NotificationWindow>().Where(w => w.Contact == contact).ToList())
            window.FadeOutAndClose();
    }

    public void CloseAll()
    {
        foreach (var window in _windows.ToList())
            window.Close();
    }

    private void Layout()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom;
        foreach (var window in _windows)
        {
            window.Left = area.Right - window.ActualWidth;
            window.Top = bottom - window.ActualHeight;
            bottom = window.Top;
        }
    }
}
