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
    private readonly List<NotificationWindow> _windows = new();

    public NotificationManager(ChatService chat, Action<Contact> openConversation)
    {
        _chat = chat;
        _openConversation = openConversation;
    }

    public void Show(Contact contact, ChatMessage message)
    {
        // Этот человек уже висит на экране — дописываем в его окно.
        var existing = _windows.FirstOrDefault(w => w.Contact == contact && !w.IsClosing);
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
        window.SizeChanged += (_, _) => Layout();
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            Layout();
        };

        _windows.Add(window);

        foreach (var old in _windows.Where(w => !w.IsClosing).SkipLast(MaxVisible).ToList())
            old.FadeOutAndClose();

        var area = SystemParameters.WorkArea;
        window.Left = area.Right - window.Width;
        window.Top = area.Bottom;
        window.Show();
        Layout();
    }

    /// <summary>Закрывает окна о переписке, которую открыли в главном окне.</summary>
    public void CloseFor(Contact contact)
    {
        foreach (var window in _windows.Where(w => w.Contact == contact).ToList())
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
