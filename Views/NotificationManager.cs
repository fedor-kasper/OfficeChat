using System.Windows;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Складывает всплывающие окна стопкой снизу вверх в левом нижнем углу экрана.</summary>
public sealed class NotificationManager
{
    private const int MaxVisible = 4;

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
        var window = new NotificationWindow(contact, message);
        window.OpenRequested += w =>
        {
            w.FadeOutAndClose();
            _openConversation(w.Contact);
        };
        window.ReplyRequested += (w, text) =>
        {
            _chat.Send(w.Contact, text);
            // Раз ответил — значит, прочитал.
            _chat.MarkRead(w.Contact);
            w.FadeOutAndClose();
        };
        window.SizeChanged += (_, _) => Layout();
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            Layout();
        };

        _windows.Add(window);

        // Лишние старые окна убираем, чтобы стопка не росла бесконечно.
        foreach (var old in _windows.Where(w => !w.IsClosing).SkipLast(MaxVisible).ToList())
            old.FadeOutAndClose();

        var area = SystemParameters.WorkArea;
        window.Left = area.Left;
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
            window.Left = area.Left;
            window.Top = bottom - window.ActualHeight;
            bottom = window.Top;
        }
    }
}
