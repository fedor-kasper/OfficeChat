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
        CompactAvatar.Text = AvatarText.Text;
        // Тихое сообщение всплывает маленькой полоской; обычное — полным окном с красной рамкой.
        SetCompact(message.IsQuiet && !message.IsOutgoing);
        AddMessage(message);

        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeDuration));
    }

    /// <summary>Маленькая полоска (тихие сообщения) или полное окно.</summary>
    public bool IsCompact { get; private set; }

    private void SetCompact(bool compact)
    {
        IsCompact = compact;
        CompactView.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        FullView.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Width = compact ? 360 : 440;
        Frame.BorderThickness = new Thickness(compact ? 1 : 3);
        Frame.BorderBrush = compact ? System.Windows.Media.Brushes.LightGray : RedBrush;
        Frame.CornerRadius = new CornerRadius(compact ? 8 : 12);
    }

    private static readonly System.Windows.Media.Brush RedBrush = GameBoards.Frozen(0xDC, 0x26, 0x26);

    /// <summary>Добавляет в ленту входящее сообщение или ваш ответ.</summary>
    public void AddMessage(ChatMessage message)
    {
        // Пришло обычное сообщение, а окно было маленьким (тихим) — разворачиваем.
        if (IsCompact && !message.IsOutgoing && !message.IsQuiet) SetCompact(false);
        var who = message.ShowSender ? $"{message.SenderName}: " : "";
        CompactText.Text = $"{Contact.Title} · {who}{message.PreviewText.ReplaceLineEndings(" ")}";
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
