using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ChatService _chat;
    private readonly NotificationManager _notifications;
    private readonly TrayIcon _tray;
    private Contact? _current;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        Icon = AppIcon.CreateImageSource();
        _settings = settings;
        MyNameText.Text = settings.DisplayName;

        _chat = new ChatService(settings)
        {
            IsConversationVisible = contact =>
                contact == _current && IsVisible && IsActive && WindowState != WindowState.Minimized,
        };
        _chat.PresenceChanged += OnPresenceChanged;
        _chat.MessageReceived += OnMessageReceived;
        ContactsList.ItemsSource = _chat.Contacts;

        _notifications = new NotificationManager(_chat, OpenConversation);

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += ExitApplication;
        _chat.UnreadChanged += () => _tray.SetUnread(_chat.TotalUnread);

        _chat.Start();

        Activated += (_, _) =>
        {
            if (_current != null) _chat.MarkRead(_current);
        };

        OnPresenceChanged();
    }

    private void OnPresenceChanged()
    {
        var count = _chat.OnlineCount;
        OnlineCountText.Text = count == 0 ? "В СЕТИ НИКОГО НЕТ" : $"В СЕТИ: {count}";
        UpdateChatHeader();
    }

    private void OnMessageReceived(Contact contact, ChatMessage message)
    {
        // Если эта переписка уже открыта перед глазами — всплывать незачем.
        if (!_chat.IsConversationVisible(contact))
            _notifications.Show(contact, message);
    }

    // ---- Трей и всплывающие окна ----

    /// <summary>Показывает окно (в том числе из трея) и открывает переписку.</summary>
    public void OpenConversation(Contact contact)
    {
        ShowFromTray();
        ContactsList.SelectedItem = contact;
        ContactsList.ScrollIntoView(contact);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        if (_current != null)
            _notifications.CloseFor(_current);
    }

    /// <summary>Настоящий выход (из меню трея или при завершении работы Windows).</summary>
    public void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    // ---- Выбор собеседника ----

    private void ContactsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_current != null)
        {
            _current.Messages.CollectionChanged -= OnCurrentMessagesChanged;
            _current.PropertyChanged -= OnCurrentContactChanged;
        }

        _current = ContactsList.SelectedItem as Contact;
        NoticeText.Visibility = Visibility.Collapsed;

        if (_current == null)
        {
            ChatPanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        _current.Messages.CollectionChanged += OnCurrentMessagesChanged;
        _current.PropertyChanged += OnCurrentContactChanged;

        EmptyPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        MessagesList.ItemsSource = _current.Messages;

        UpdateChatHeader();
        UpdateMessagesHint();
        MessagesScroll.ScrollToEnd();
        _chat.MarkRead(_current);
        _notifications.CloseFor(_current);
        InputBox.Focus();
    }

    private void OnCurrentContactChanged(object? sender, PropertyChangedEventArgs e) => UpdateChatHeader();

    private void OnCurrentMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateMessagesHint();
        if (e.Action == NotifyCollectionChangedAction.Add)
            MessagesScroll.ScrollToEnd();
    }

    private void UpdateChatHeader()
    {
        if (_current == null) return;

        ChatTitleText.Text = _current.IsEveryone ? "Сообщение всем" : _current.Title;
        if (_current.IsEveryone)
        {
            var count = _chat.OnlineCount;
            ChatStatusText.Text = count == 0
                ? "Сейчас никого нет в сети"
                : $"Получат {count} {Plural(count, "человек", "человека", "человек")} в сети — " +
                  "сообщение появится в личной переписке с каждым";
        }
        else
        {
            ChatStatusText.Text = _current.IsOnline
                ? $"в сети · {_current.Peer!.Machine} · {_current.Peer.Address}"
                : "не в сети — сообщения будут доставлены, когда компьютер появится";
        }
    }

    private void UpdateMessagesHint()
    {
        if (_current == null) return;
        MessagesHintText.Text = _current.IsEveryone
            ? "Напишите сообщение — его получит каждый, кто сейчас в сети."
            : _current.Messages.Count == 0 ? "Сообщений пока нет." : "";
    }

    // ---- Отправка ----

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter — отправить, Shift+Enter — новая строка.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            SendCurrent();
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendCurrent();

    private void SendCurrent()
    {
        var text = InputBox.Text.Trim();
        if (_current == null || text.Length == 0) return;

        var recipients = _chat.Send(_current, text);

        if (_current.IsEveryone)
        {
            if (recipients == 0)
            {
                ShowNotice("Сейчас никого нет в сети — сообщение не отправлено.");
                return;
            }
            ShowNotice($"Отправлено {recipients} {Plural(recipients, "получателю", "получателям", "получателям")}.");
        }
        else if (!_current.IsOnline)
        {
            ShowNotice($"{_current.Title} сейчас не в сети. Сообщение будет доставлено, когда компьютер появится; " +
                       "до этого отправку можно отменить.");
        }
        else
        {
            NoticeText.Visibility = Visibility.Collapsed;
        }

        InputBox.Clear();
        InputBox.Focus();
    }

    private void CancelMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatMessage message && _chat.Cancel(message))
            ShowNotice("Отправка отменена.");
    }

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeText.Visibility = Visibility.Visible;
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14) return many;
        return mod10 switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }

    // ---- Прочее ----

    private void ChangeName_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NameWindow(_settings.DisplayName) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _settings.DisplayName = dialog.EnteredName;
        SettingsService.Save(_settings);
        MyNameText.Text = _settings.DisplayName;
        _chat.AnnounceNow();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_exiting) return;

        // Крестик не закрывает программу, а убирает её в трей — сообщения продолжают приходить.
        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowHint("OfficeChat работает в фоне",
                "Новые сообщения всплывут слева внизу. Чтобы выйти, нажмите на значок правой кнопкой → «Выход».");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _notifications.CloseAll();
        _chat.Dispose();
        _tray.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
