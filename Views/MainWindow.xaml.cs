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
    private readonly GameService _games;
    private readonly Dictionary<Guid, GameWindow> _gameWindows = new();
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

        _games = new GameService(_chat);
        _games.InviteReceived += game => _notifications.ShowGameInvite(game, AcceptGame, _games.Decline);

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

    // ---- Крестики-нолики ----

    private void InviteToGame_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { IsEveryone: false, IsOnline: true }) return;
        OpenGameWindow(_games.Invite(_current));
    }

    private void AcceptGame(TicTacToeGame game)
    {
        _games.Accept(game);
        OpenGameWindow(game);
    }

    /// <summary>Отдельное окно партии; если оно уже открыто — просто выводим его вперёд.</summary>
    private void OpenGameWindow(TicTacToeGame game)
    {
        if (!_gameWindows.TryGetValue(game.Id, out var window))
        {
            window = new GameWindow(_games, game);
            window.Closed += (_, _) => _gameWindows.Remove(game.Id);
            _gameWindows.Add(game.Id, window);
            window.Show();
        }
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
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
        ChatPanel.DataContext = _current;
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
        // Новое сообщение в конце — прокручиваем вниз; подгрузка старых вставляет в начало и не прокручивает.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == _current!.Messages.Count - 1)
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

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings.DisplayName, _settings.AutoStart) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _settings.DisplayName = dialog.EnteredName;
        _settings.AutoStart = dialog.AutoStart;
        SettingsService.Save(_settings);
        AutoStartService.Apply(_settings.AutoStart);
        MyNameText.Text = _settings.DisplayName;
        _chat.AnnounceNow();
    }

    // ---- История и управление контактами ----

    private void LoadOlder_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;

        // Сохраняем позицию, чтобы после вставки сверху остаться на том же сообщении.
        var distanceFromBottom = MessagesScroll.ExtentHeight - MessagesScroll.VerticalOffset;
        _chat.LoadOlder(_current);
        MessagesScroll.UpdateLayout();
        MessagesScroll.ScrollToVerticalOffset(MessagesScroll.ExtentHeight - distanceFromBottom);
    }

    private void ClearConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) ConfirmClear(_current);
    }

    private void RemoveContact_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) ConfirmRemove(_current);
    }

    private void ContactsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var contact = (e.OriginalSource as FrameworkElement)?.DataContext as Contact;
        if (contact == null || contact.IsEveryone)
        {
            e.Handled = true;
            return;
        }

        var clear = new MenuItem { Header = "Очистить переписку", IsEnabled = contact.Messages.Count > 0 };
        clear.Click += (_, _) => ConfirmClear(contact);

        var remove = new MenuItem
        {
            Header = "Удалить из списка",
            IsEnabled = contact.CanRemove,
            ToolTip = contact.CanRemove ? null : "Можно удалить только того, кто не в сети",
        };
        ToolTipService.SetShowOnDisabled(remove, true);
        remove.Click += (_, _) => ConfirmRemove(contact);

        ContactsList.ContextMenu = new ContextMenu { Items = { clear, remove } };
    }

    private void ConfirmClear(Contact contact)
    {
        var hasQueued = contact.Messages.Any(m => m.CanCancel);
        var text = $"Удалить всю переписку с «{contact.Title}» на этом компьютере?" +
                   (hasQueued ? "\n\nНеотправленные сообщения тоже будут отменены." : "") +
                   "\n\nУ собеседника переписка останется.";
        if (MessageBox.Show(this, text, "Очистить переписку", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            _chat.ClearConversation(contact);
    }

    private void ConfirmRemove(Contact contact)
    {
        if (!contact.CanRemove) return;
        var text = $"Убрать «{contact.Title}» из списка вместе со всей перепиской?\n\n" +
                   "Если этот компьютер снова появится в сети, он вернётся в список.";
        if (MessageBox.Show(this, text, "Удалить из списка", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            _chat.RemoveContact(contact);
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
                "Новые сообщения всплывут справа внизу. Чтобы выйти, нажмите на значок правой кнопкой → «Выход».");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var window in _gameWindows.Values.ToList())
            window.ForceClose();
        _games.Dispose();
        _notifications.CloseAll();
        _chat.Dispose();
        _tray.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
