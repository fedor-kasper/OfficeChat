using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OfficeChat.Models;

namespace OfficeChat.Views;

/// <summary>
/// Всплывающее окно о новом сообщении. Не забирает фокус у текущей программы,
/// исчезает через несколько секунд, но ждёт, пока над ним мышь или набирается ответ.
/// </summary>
public partial class NotificationWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(7);
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(200));

    private readonly DispatcherTimer _lifetimeTimer;

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

        SenderText.Text = contact.Title;
        AvatarText.Text = contact.Title.Length > 0 ? char.ToUpper(contact.Title[0]).ToString() : "?";
        TimeText.Text = message.TimeText;
        MessageText.Text = message.Text;
        BroadcastText.Visibility = message.IsBroadcast ? Visibility.Visible : Visibility.Collapsed;

        _lifetimeTimer = new DispatcherTimer { Interval = Lifetime };
        _lifetimeTimer.Tick += (_, _) => FadeOutAndClose();

        MouseEnter += (_, _) => _lifetimeTimer.Stop();
        MouseLeave += (_, _) => RestartTimerIfIdle();
        ReplyBox.GotKeyboardFocus += (_, _) => _lifetimeTimer.Stop();
        ReplyBox.LostKeyboardFocus += (_, _) => RestartTimerIfIdle();

        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeDuration));
            _lifetimeTimer.Start();
        };
    }

    /// <summary>Человек сейчас взаимодействует с окном — закрывать нельзя.</summary>
    private bool IsBusy => IsMouseOver || ReplyBox.IsKeyboardFocusWithin || ReplyBox.Text.Length > 0;

    private void RestartTimerIfIdle()
    {
        if (IsClosing || IsBusy) return;
        _lifetimeTimer.Stop();
        _lifetimeTimer.Start();
    }

    public void FadeOutAndClose()
    {
        if (IsClosing) return;
        IsClosing = true;
        _lifetimeTimer.Stop();

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
        if (text.Length == 0)
        {
            ReplyBox.Focus();
            return;
        }
        ReplyRequested?.Invoke(this, text);
    }
}
