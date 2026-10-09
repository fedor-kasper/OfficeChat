using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OfficeChat.Models;
using OfficeChat.Platform;
using OfficeChat.Services;

namespace OfficeChat.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ChatService _chat;
    private readonly NotificationManager _notifications;
    private readonly GameService _games;
    private readonly Tray _tray;
    private Contact? _current;
    // Изображения, выбранные для отправки (полоса над полем ввода).
    private readonly ObservableCollection<PendingImage> _attachments = new();
    private bool _exiting;

    public MainWindow() : this(new AppSettings { DisplayName = "Дизайнер" }) { }

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        Icon = AppIcon.Window;
        _settings = settings;
        MyNameText.Text = settings.DisplayName;
        Title = $"OfficeChat — {settings.DisplayName}";

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
        _games.InviteReceived += OnGameInvite;
        _games.OpponentMoved += OnOpponentMoved;
        _games.GameChanged += contact =>
        {
            if (_current?.Peer != null && contact.Peer?.Id == _current.Peer.Id)
                GameView.Show(_games.GameFor(_current));
        };
        GameView.Attach(_games);

        _tray = new Tray();
        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += ExitApplication;
        _chat.UnreadChanged += () => _tray.SetUnread(_chat.TotalUnread);

        AttachmentsList.ItemsSource = _attachments;
        _attachments.CollectionChanged += (_, _) => AttachmentsBar.IsVisible = _attachments.Count > 0;

        // Enter — отправить, Shift+Enter — новая строка, Ctrl+V — вставка изображения из буфера.
        InputBox.AddHandler(KeyDownEvent, InputBox_KeyDown, RoutingStrategies.Tunnel);

        ChatPanel.AddHandler(DragDrop.DragOverEvent, ChatPanel_DragOver);
        ChatPanel.AddHandler(DragDrop.DragLeaveEvent, ChatPanel_DragLeave);
        ChatPanel.AddHandler(DragDrop.DropEvent, ChatPanel_Drop);

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

    private void InviteToGame_Click(object? sender, RoutedEventArgs e)
    {
        if (_current is not { IsEveryone: false, IsOnline: true }) return;
        _games.Invite(_current);
        GameView.Show(_games.GameFor(_current));
    }

    private void OnGameInvite(TicTacToeGame game)
    {
        if (_chat.IsConversationVisible(game.Opponent)) return;
        _notifications.ShowGameInvite(game, AcceptGame, _games.Decline);
    }

    private void AcceptGame(TicTacToeGame game)
    {
        _games.Accept(game);
        OpenConversation(game.Opponent);
    }

    private void OnOpponentMoved(TicTacToeGame game)
    {
        if (_chat.IsConversationVisible(game.Opponent)) return;
        _notifications.Show(game.Opponent, new ChatMessage
        {
            Id = Guid.NewGuid(),
            IsOutgoing = false,
            Text = "🎮 Ваш ход в крестики-нолики",
            Timestamp = DateTime.Now,
            Kind = MessageKind.Game,
        });
    }

    // ---- Трей и всплывающие окна ----

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

    public void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    // ---- Выбор собеседника ----

    private void ContactsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_current != null)
        {
            _current.Messages.CollectionChanged -= OnCurrentMessagesChanged;
            _current.PropertyChanged -= OnCurrentContactChanged;
        }

        _current = ContactsList.SelectedItem as Contact;
        NoticeText.IsVisible = false;
        // Прикреплённое к одной переписке не должно случайно уйти в другую.
        _attachments.Clear();

        if (_current == null)
        {
            ChatPanel.IsVisible = false;
            EmptyPanel.IsVisible = true;
            return;
        }

        _current.Messages.CollectionChanged += OnCurrentMessagesChanged;
        _current.PropertyChanged += OnCurrentContactChanged;

        EmptyPanel.IsVisible = false;
        ChatPanel.IsVisible = true;
        ChatPanel.DataContext = _current;
        MessagesList.ItemsSource = _current.Messages;

        UpdateChatHeader();
        UpdateMessagesHint();
        ScrollToEnd();
        _chat.MarkRead(_current);
        _notifications.CloseFor(_current);
        GameView.Show(_games.GameFor(_current));
        InputBox.Focus();
    }

    private void OnCurrentContactChanged(object? sender, PropertyChangedEventArgs e) => UpdateChatHeader();

    private void OnCurrentMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateMessagesHint();
        // Новое сообщение в конце — прокручиваем вниз; подгрузка старых вставляет в начало и не прокручивает.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == _current!.Messages.Count - 1)
            ScrollToEnd();
    }

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);

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

    private async void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            SendCurrent();
        }
        else if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
        {
            // Буфер обмена в Avalonia читается асинхронно — решаем сами, вставлять текст или прикреплять картинку.
            e.Handled = true;
            await PasteAsync();
        }
    }

    private void Send_Click(object? sender, RoutedEventArgs e) => SendCurrent();

    private void SendCurrent()
    {
        var text = InputBox.Text?.Trim() ?? "";
        if (_current == null || (text.Length == 0 && _attachments.Count == 0)) return;

        int recipients;
        if (_attachments.Count > 0)
        {
            // Одно изображение — текст становится подписью (как в Telegram); несколько — текст отдельным сообщением.
            var images = _attachments.ToList();
            var caption = images.Count == 1 ? text : "";
            recipients = 0;
            foreach (var image in images)
                recipients = _chat.SendImage(_current, image.Data, image.FileName, caption);
            if (images.Count > 1 && text.Length > 0)
                _chat.Send(_current, text);
            _attachments.Clear();
        }
        else
        {
            recipients = _chat.Send(_current, text);
        }

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
            NoticeText.IsVisible = false;
        }

        InputBox.Text = "";
        InputBox.Focus();
    }

    private void CancelMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message && _chat.Cancel(message))
            ShowNotice("Отправка отменена.");
    }

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeText.IsVisible = true;
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14) return many;
        return mod10 switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }

    // ---- Изображения: прикрепление ----

    private async void Attach_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите изображения",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Изображения")
                {
                    Patterns = ImageStore.Extensions.SelectMany(x => new[] { "*" + x, "*" + x.ToUpperInvariant() }).ToArray(),
                    MimeTypes = new[] { "image/*" },
                },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count == 0) return;

        var errors = new List<string>();
        AddAttachments(PendingImage.FromStorageItems(files, errors), errors);
    }

    private void AddAttachments(List<PendingImage> images, List<string> errors)
    {
        foreach (var image in images)
            _attachments.Add(image);

        if (errors.Count > 0)
            ShowNotice("Не прикреплено: " + string.Join("; ", errors));
        else if (images.Count > 0)
            ShowNotice(_attachments.Count == 1
                ? "Изображение прикреплено. Напишите подпись (необязательно) и нажмите «Отправить»."
                : $"Прикреплено изображений: {_attachments.Count}. Нажмите «Отправить».");
        InputBox.Focus();
    }

    private void RemoveAttachment_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PendingImage image)
            _attachments.Remove(image);
        if (_attachments.Count == 0) NoticeText.IsVisible = false;
    }

    private async Task PasteAsync()
    {
        if (Clipboard == null) return;
        try
        {
            var errors = new List<string>();
            var images = await PendingImage.FromClipboardAsync(Clipboard, errors);
            if (images == null)
            {
                InputBox.Paste(); // в буфере текст — вставляем как обычно
                return;
            }
            AddAttachments(images, errors);
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось прочитать буфер обмена", ex);
            InputBox.Paste();
        }
    }

    // ---- Изображения: перетаскивание файлов в окно ----

    private void ChatPanel_DragOver(object? sender, DragEventArgs e)
    {
        if (_current == null || !PendingImage.HasImageFiles(e.DataTransfer))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        DropOverlay.IsVisible = true;
    }

    private void ChatPanel_DragLeave(object? sender, DragEventArgs e) => DropOverlay.IsVisible = false;

    private void ChatPanel_Drop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (_current == null || e.DataTransfer.TryGetFiles() is not { } files) return;

        var errors = new List<string>();
        AddAttachments(PendingImage.FromStorageItems(files, errors), errors);
        Activate();
    }

    // ---- Изображения: просмотр и сохранение ----

    private static ChatMessage? MessageOf(object? sender) => (sender as Control)?.DataContext as ChatMessage;

    private void Image_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && MessageOf(sender) is { } message)
            OpenImage(message);
    }

    private void ImageOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message) OpenImage(message);
    }

    private async void ImageSave_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message) await ImageViewerWindow.SaveAsAsync(message, this);
    }

    private async void ImageCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is not { } message) return;
        await ImageViewerWindow.CopyAsync(message, this);
        ShowNotice("Изображение скопировано в буфер обмена.");
    }

    private void ImageOpenExternal_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message) Shell.Open(message.ImagePath);
    }

    private void OpenImage(ChatMessage message)
    {
        var sender = message.IsOutgoing ? "Вы" : _current?.Title ?? "";
        new ImageViewerWindow(message, sender).Show(this);
    }

    // ---- Настройки ----

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings.DisplayName, _settings.AutoStart);
        await dialog.ShowDialog(this);
        if (!dialog.Saved) return;

        _settings.DisplayName = dialog.EnteredName;
        _settings.AutoStart = dialog.AutoStart;
        SettingsService.Save(_settings);
        DesktopIntegration.ApplyAutoStart(_settings.AutoStart);
        MyNameText.Text = _settings.DisplayName;
        Title = $"OfficeChat — {_settings.DisplayName}";
        _chat.AnnounceNow();
    }

    // ---- История и управление контактами ----

    private void LoadOlder_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null) return;

        // Сохраняем позицию, чтобы после вставки сверху остаться на том же сообщении.
        var distanceFromBottom = MessagesScroll.Extent.Height - MessagesScroll.Offset.Y;
        _chat.LoadOlder(_current);
        MessagesScroll.UpdateLayout();
        MessagesScroll.Offset = new Vector(0, MessagesScroll.Extent.Height - distanceFromBottom);
    }

    private async void ClearConversation_Click(object? sender, RoutedEventArgs e)
    {
        if (_current != null) await ConfirmClear(_current);
    }

    private async void RemoveContact_Click(object? sender, RoutedEventArgs e)
    {
        if (_current != null) await ConfirmRemove(_current);
    }

    private void ContactsList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var contact = (e.Source as Control)?.DataContext as Contact;
        if (contact == null || contact.IsEveryone)
        {
            e.Handled = true;
            return;
        }

        var clear = new MenuItem { Header = "Очистить переписку", IsEnabled = contact.Messages.Count > 0 };
        clear.Click += async (_, _) => await ConfirmClear(contact);
        var remove = new MenuItem { Header = "Удалить из списка", IsEnabled = contact.CanRemove };
        remove.Click += async (_, _) => await ConfirmRemove(contact);

        var menu = new ContextMenu { ItemsSource = new[] { clear, remove } };
        menu.Open(ContactsList);
        e.Handled = true;
    }

    private async Task ConfirmClear(Contact contact)
    {
        var hasQueued = contact.Messages.Any(m => m.CanCancel);
        var text = $"Удалить всю переписку с «{contact.Title}» на этом компьютере?" +
                   (hasQueued ? "\n\nНеотправленные сообщения тоже будут отменены." : "") +
                   "\n\nУ собеседника переписка останется.";
        if (await Dialogs.Confirm(this, text, "Очистить переписку"))
            _chat.ClearConversation(contact);
    }

    private async Task ConfirmRemove(Contact contact)
    {
        if (!contact.CanRemove) return;
        var text = $"Убрать «{contact.Title}» из списка вместе со всей перепиской?\n\n" +
                   "Если этот компьютер снова появится в сети, он вернётся в список.";
        if (await Dialogs.Confirm(this, text, "Удалить из списка"))
            _chat.RemoveContact(contact);
    }

    // ---- Закрытие ----

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_exiting || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;

        // Крестик не закрывает программу, а убирает её в трей — сообщения продолжают приходить.
        e.Cancel = true;
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _games.LeaveAll();
        _games.Dispose();
        _notifications.CloseAll();
        _chat.Dispose();
        _tray.Dispose();
        base.OnClosed(e);
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
