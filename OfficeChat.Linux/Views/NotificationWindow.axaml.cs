using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OfficeChat.Models;

namespace OfficeChat.Views;

/// <summary>
/// Всплывающее окно с сообщениями от одного человека. Не забирает фокус и висит, пока его не закроют.
/// Новые сообщения от того же человека добавляются сюда же, ответы видны со статусом доставки.
/// </summary>
public partial class NotificationWindow : Window, IStackedPopup
{
    private readonly ObservableCollection<ChatMessage> _messages = new();
    private int _incomingCount;

    public Contact Contact { get; }

    public bool IsClosing { get; private set; }

    public event Action<NotificationWindow>? OpenRequested;
    public event Action<NotificationWindow, string>? ReplyRequested;

    public NotificationWindow() : this(null!, null!) { }

    public NotificationWindow(Contact contact, ChatMessage message)
    {
        InitializeComponent();
        Contact = contact;
        if (contact == null) return; // конструктор для дизайнера

        MessagesList.ItemsSource = _messages;
        SenderText.Text = contact.Title;
        AvatarText.Text = contact.Title.Length > 0 ? char.ToUpper(contact.Title[0]).ToString() : "?";
        AddMessage(message);

        ReplyBox.AddHandler(KeyDownEvent, ReplyBox_KeyDown, RoutingStrategies.Tunnel);
        Popups.SetupFade(this);
    }

    public void AddMessage(ChatMessage message)
    {
        _messages.Add(message);
        if (!message.IsOutgoing)
        {
            _incomingCount++;
            CountText.Text = _incomingCount > 1 ? $"{_incomingCount} сообщ." : message.TimeText;
        }
        Dispatcher.UIThread.Post(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>Сообщение удалили — убираем его; если входящих не осталось, закрываем окно.</summary>
    public void RemoveMessage(ChatMessage message)
    {
        if (!_messages.Remove(message) || message.IsOutgoing) return;
        _incomingCount--;
        if (_incomingCount <= 0)
            FadeOutAndClose();
        else
            CountText.Text = _incomingCount > 1 ? $"{_incomingCount} сообщ." : _messages.Last(m => !m.IsOutgoing).TimeText;
    }

    public void FadeOutAndClose()
    {
        if (IsClosing) return;
        IsClosing = true;
        Popups.FadeOutAndClose(this);
    }

    private void Open_Click(object? sender, PointerReleasedEventArgs e) => OpenRequested?.Invoke(this);

    private void Close_Click(object? sender, RoutedEventArgs e) => FadeOutAndClose();

    private void Reply_Click(object? sender, RoutedEventArgs e) => SendReply();

    private void ReplyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SendReply();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            FadeOutAndClose();
        }
    }

    private void SendReply()
    {
        var text = ReplyBox.Text?.Trim() ?? "";
        if (text.Length > 0)
        {
            ReplyRequested?.Invoke(this, text);
            ReplyBox.Text = "";
        }
        ReplyBox.Focus();
    }
}

/// <summary>Общее для всплывающих окон: плавное появление и исчезновение.</summary>
internal static class Popups
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);

    public static void SetupFade(Window window)
    {
        window.Opacity = 0;
        window.Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = FadeDuration } };
        window.Opened += (_, _) => window.Opacity = 1;
    }

    public static void FadeOutAndClose(Window window)
    {
        window.Opacity = 0;
        DispatcherTimer.RunOnce(window.Close, FadeDuration);
    }
}
