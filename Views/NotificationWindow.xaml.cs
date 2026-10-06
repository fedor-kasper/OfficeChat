using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using OfficeChat.Models;

namespace OfficeChat.Views;

/// <summary>
/// Всплывающее окно с сообщениями от одного человека. Не забирает фокус у текущей программы
/// и висит, пока его не закроют. Новые сообщения от того же человека добавляются в это же окно,
/// ответы из окна показываются в нём же со статусом доставки.
/// </summary>
public partial class NotificationWindow : Window, IStackedPopup
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(200));

    private readonly ObservableCollection<ChatMessage> _messages = new();
    private int _incomingCount;

    public Contact Contact { get; }

    public bool IsClosing { get; private set; }

    /// <summary>Нажали на сообщение — открыть переписку.</summary>
    public event Action<NotificationWindow>? OpenRequested;

    /// <summary>Отправлен быстрый ответ.</summary>
    public event Action<NotificationWindow, string>? ReplyRequested;

    public NotificationWindow(Contact contact, ChatMessage message)
    {
        InitializeComponent();
        Contact = contact;
        MessagesList.ItemsSource = _messages;

        SenderText.Text = contact.Title;
        AvatarText.Text = contact.Title.Length > 0 ? char.ToUpper(contact.Title[0]).ToString() : "?";
        AddMessage(message);

        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeDuration));
    }

    /// <summary>Добавляет в ленту входящее сообщение или ваш ответ.</summary>
    public void AddMessage(ChatMessage message)
    {
        _messages.Add(message);
        if (!message.IsOutgoing)
        {
            _incomingCount++;
            CountText.Text = _incomingCount > 1 ? $"{_incomingCount} сообщ." : message.TimeText;
        }

        // Прокручиваем к последнему, когда окно пересчитает размер.
        Dispatcher.BeginInvoke(() => MessagesScroll.ScrollToEnd(),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void FadeOutAndClose()
    {
        if (IsClosing) return;
        IsClosing = true;

        var fade = new DoubleAnimation(0, FadeDuration);
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void Open_Click(object sender, MouseButtonEventArgs e) => OpenRequested?.Invoke(this);

    private void Close_Click(object sender, RoutedEventArgs e) => FadeOutAndClose();

    private void Reply_Click(object sender, RoutedEventArgs e) => SendReply();

    private void ReplyBox_PreviewKeyDown(object sender, KeyEventArgs e)
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

    private void ReplyBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ReplyPlaceholder.Visibility = ReplyBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SendReply()
    {
        var text = ReplyBox.Text.Trim();
        if (text.Length > 0)
        {
            ReplyRequested?.Invoke(this, text);
            ReplyBox.Clear();
        }
        ReplyBox.Focus();
    }
}
