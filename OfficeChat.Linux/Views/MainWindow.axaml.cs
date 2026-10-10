using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private readonly ObservableCollection<PendingAttachment> _attachments = new();
    private bool _exiting;
    // Сообщение, на которое отвечаем, и сообщение, которое редактируем (не больше одного из двух).
    private ChatMessage? _replyTo;
    private ChatMessage? _editing;
    // Что было в поле ввода до начала редактирования — вернём после.
    private string _draftBeforeEdit = "";
    // Текст в поле ввода меняет сама программа (начало и конец редактирования) — это не набор текста.
    private bool _settingInput;

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
        _chat.MessageRemoved += (_, message) =>
        {
            _notifications.Remove(message);
            if (message == _replyTo || message == _editing) CancelCompose();
        };

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
        _chat.UnreadChanged += () =>
        {
            _tray.SetUnread(_chat.TotalUnread);
            UpdateMuteButton();
        };

        AttachmentsList.ItemsSource = _attachments;
        _attachments.CollectionChanged += (_, _) => AttachmentsBar.IsVisible = _attachments.Count > 0;

        // Enter — отправить, Shift+Enter — новая строка, Ctrl+V — вставка изображения из буфера.
        InputBox.AddHandler(KeyDownEvent, InputBox_KeyDown, RoutingStrategies.Tunnel);
        // Набирают текст — сообщаем собеседнику «печатает…» (но не когда поле очистилось после отправки).
        InputBox.TextChanged += (_, _) =>
        {
            if (_current != null && !_settingInput && !string.IsNullOrEmpty(InputBox.Text) && InputBox.IsKeyboardFocusWithin)
                _chat.NotifyTyping(_current);
        };

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
        // Если эта переписка уже открыта перед глазами или уведомления по ней выключены — всплывать незачем.
        if (!_chat.IsConversationVisible(contact) && !contact.IsMuted)
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

        CancelCompose();
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

    /// <summary>
    /// Прокрутка к последнему сообщению. Повторяем чуть позже: высота нового блока (картинка, файл)
    /// досчитывается после первого прохода разметки, и одной прокрутки не хватает до самого низа.
    /// </summary>
    private void ScrollToEnd()
    {
        Dispatcher.UIThread.Post(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() => MessagesScroll.ScrollToEnd(), TimeSpan.FromMilliseconds(150));
    }

    private void UpdateChatHeader()
    {
        if (_current == null) return;

        ChatTitleText.Text = _current.IsEveryone ? "Сообщение всем" : _current.Title;
        UpdateMuteButton();
        if (_current.IsTyping)
        {
            ChatStatusText.Text = _current.TypingText;
            return;
        }
        if (_current.Group is { } group)
        {
            var others = group.Members.Where(m => m.Id != _chat.MyId).Select(m => m.Name);
            ChatStatusText.Text = $"{group.StatusText} · {string.Join(", ", others.Prepend("вы"))}";
        }
        else if (_current.IsEveryone)
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

    private void UpdateMuteButton()
    {
        if (_current is not { IsEveryone: false } contact) return;
        // Выключенные уведомления подписываем словами: значки 🔔 и 🔕 в мелком шрифте легко спутать.
        MuteButton.Content = contact.IsMuted ? "🔕 Уведомления выключены" : "🔔";
        ToolTip.SetTip(MuteButton, contact.IsMuted
            ? "Уведомления выключены: сообщения приходят без всплывающих окон. Нажмите, чтобы включить"
            : "Выключить уведомления от этой переписки");
    }

    private void ToggleMute_Click(object? sender, RoutedEventArgs e)
    {
        if (_current is { IsEveryone: false } contact)
            _chat.SetMuted(contact, !contact.IsMuted);
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
        else if (e.Key == Key.Escape && ComposeBar.IsVisible)
        {
            e.Handled = true;
            CancelCompose();
        }
        else if (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.None && string.IsNullOrEmpty(InputBox.Text) &&
                 _editing == null && _current?.Messages.LastOrDefault(m => m.CanEdit) is { } last)
        {
            // Стрелка вверх в пустом поле — исправить своё последнее сообщение (как в Telegram).
            e.Handled = true;
            StartEdit(last);
        }
        else if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
        {
            // Буфер обмена в Avalonia читается асинхронно — решаем сами, вставлять текст или прикреплять картинку.
            e.Handled = true;
            await PasteAsync();
        }
    }

    private void Send_Click(object? sender, RoutedEventArgs e) => SendCurrent();

    private async void SendCurrent()
    {
        var text = InputBox.Text?.Trim() ?? "";
        var contact = _current;
        if (contact != null && _editing != null)
        {
            FinishEdit(contact, text);
            return;
        }
        if (contact == null || (text.Length == 0 && _attachments.Count == 0)) return;

        // Ответ привязываем к первому, что уходит: к тексту или к первому вложению.
        var replyTo = _replyTo;
        CancelCompose();

        int recipients;
        if (_attachments.Count > 0)
        {
            // Одно вложение — текст становится подписью (как в Telegram); несколько — текст отдельным сообщением.
            var items = _attachments.ToList();
            var caption = items.Count == 1 ? text : "";
            _attachments.Clear();
            InputBox.Text = "";
            recipients = 0;
            foreach (var item in items)
            {
                if (item.IsImage)
                    recipients = _chat.SendImage(contact, item.Data!, item.FileName, caption, replyTo);
                else
                {
                    // Большой файл сначала копируется в хранилище программы — это может занять время.
                    if (item.Size > 50 * 1024 * 1024) ShowNotice($"Подготовка «{item.FileName}» к отправке…");
                    try
                    {
                        recipients = await _chat.SendFileAsync(contact, item.SourcePath!, caption, replyTo);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Error($"Не удалось подготовить файл {item.SourcePath} к отправке", ex);
                        ShowNotice($"Не удалось отправить «{item.FileName}»: {ex.Message}");
                        return;
                    }
                }
                replyTo = null;
            }
            if (items.Count > 1 && text.Length > 0)
                _chat.Send(contact, text);
        }
        else
        {
            recipients = _chat.Send(contact, text, replyTo);
        }
        if (contact != _current) return;

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

    // ---- Ответ, правка, удаление ----

    private void StartReply(ChatMessage message)
    {
        CancelCompose();
        _replyTo = message;
        ComposeTitle.Text = $"↩ Ответ · {(message.IsOutgoing ? ChatService.MyReplyAuthor : _current?.Title)}";
        ComposeText.Text = message.QuoteText;
        ComposeBar.IsVisible = true;
        InputBox.Focus();
    }

    private void StartEdit(ChatMessage message)
    {
        CancelCompose();
        _editing = message;
        _draftBeforeEdit = InputBox.Text ?? "";
        ComposeTitle.Text = message.IsFile || message.IsImage ? "✎ Изменение подписи" : "✎ Редактирование";
        ComposeText.Text = message.QuoteText;
        ComposeBar.IsVisible = true;
        SetInputText(message.Text);
        InputBox.Focus();
    }

    /// <summary>Отправка в режиме редактирования — сохранить новый текст.</summary>
    private void FinishEdit(Contact contact, string text)
    {
        var message = _editing!;
        if (text.Length == 0 && message.Kind == MessageKind.Text)
        {
            ShowNotice("Сообщение не может быть пустым. Чтобы убрать его, нажмите на него правой кнопкой → «Удалить».");
            return;
        }
        _chat.Edit(contact, message, text);
        CancelCompose();
    }

    /// <summary>Выйти из режима ответа или редактирования (после редактирования вернуть прежний черновик).</summary>
    private void CancelCompose()
    {
        if (_editing != null)
        {
            _editing = null;
            SetInputText(_draftBeforeEdit);
            _draftBeforeEdit = "";
        }
        _replyTo = null;
        ComposeBar.IsVisible = false;
    }

    private void SetInputText(string text)
    {
        _settingInput = true;
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        _settingInput = false;
    }

    private void CancelCompose_Click(object? sender, RoutedEventArgs e)
    {
        CancelCompose();
        InputBox.Focus();
    }

    private void ReplyMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { CanReply: true } message) StartReply(message);
    }

    private void EditMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { CanEdit: true } message) StartEdit(message);
    }

    private async void CopyMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is not { } message || Clipboard == null) return;
        // Если в сообщении выделен кусок текста — копируем его, иначе весь текст.
        var bubble = ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget;
        var selected = bubble?.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Select(t => t.SelectedText).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        await Clipboard.SetTextAsync(selected ?? message.Text);
    }

    private async void DeleteForMe_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is not { } message || _current is not { } contact) return;
        var text = message.IsOutgoing && message.Status != MessageStatus.Queued
            ? "Удалить сообщение только у себя? У собеседника оно останется."
            : "Удалить сообщение?";
        if (await Dialogs.Confirm(this, text, "Удаление"))
            _chat.Delete(contact, message, forEveryone: false);
    }

    private async void DeleteForAll_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is not { CanDeleteForEveryone: true } message || _current is not { } contact) return;
        var text = message.Status == MessageStatus.Queued
            ? "Сообщение ещё не отправлено — отменить его?"
            : "Удалить сообщение и у вас, и у собеседника?" +
              (contact.IsOnline ? "" : "\n\nСобеседник не в сети — у него сообщение исчезнет, когда он появится.");
        if (await Dialogs.Confirm(this, text, "Удаление"))
            _chat.Delete(contact, message, forEveryone: true);
    }

    /// <summary>Клик по цитате — прокрутить к сообщению, на которое ответили.</summary>
    private void Quote_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || MessageOf(sender) is not { ReplyToId: { } id }) return;
        e.Handled = true;
        var original = _current?.Messages.FirstOrDefault(m => m.Id == id);
        if (original == null)
        {
            ShowNotice(_current?.HasOlderMessages == true
                ? "Это сообщение выше — нажмите «Показать более ранние сообщения»."
                : "Исходное сообщение удалено.");
            return;
        }
        MessagesList.ContainerFromItem(original)?.BringIntoView();
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

    // ---- Вложения: прикрепление ----

    private async void Attach_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите файлы или изображения",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                FilePickerFileTypes.All,
                new FilePickerFileType("Изображения")
                {
                    Patterns = ImageStore.Extensions.SelectMany(x => new[] { "*" + x, "*" + x.ToUpperInvariant() }).ToArray(),
                    MimeTypes = new[] { "image/*" },
                },
            },
        });
        if (files.Count == 0) return;

        var errors = new List<string>();
        AddAttachments(PendingAttachment.FromStorageItems(files, errors), errors);
    }

    private void AddAttachments(List<PendingAttachment> images, List<string> errors)
    {
        foreach (var image in images)
        {
            // Тот же файл дважды не прикрепляем (например, Ctrl+V нажали повторно).
            if (image.SourcePath != null && _attachments.Any(a => a.SourcePath == image.SourcePath)) continue;
            _attachments.Add(image);
        }

        if (errors.Count > 0)
            ShowNotice("Не прикреплено: " + string.Join("; ", errors));
        else if (images.Count > 0)
            ShowNotice(_attachments.Count == 1
                ? $"{(_attachments[0].IsImage ? "Изображение" : "Файл")} прикреплён(о). Напишите подпись (необязательно) и нажмите «Отправить»."
                : $"Прикреплено: {_attachments.Count}. Нажмите «Отправить».");
        InputBox.Focus();
    }

    private void RemoveAttachment_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PendingAttachment image)
            _attachments.Remove(image);
        if (_attachments.Count == 0) NoticeText.IsVisible = false;
    }

    private async Task PasteAsync()
    {
        if (Clipboard == null) return;
        try
        {
            var errors = new List<string>();
            var attachments = await PendingAttachment.FromClipboardAsync(Clipboard, errors);
            if (attachments != null)
            {
                AddAttachments(attachments, errors);
                return;
            }
            // В буфере текст. Стандартная вставка TextBox после перехвата Ctrl+V не срабатывает —
            // вставляем сами: в позицию курсора, заменяя выделенное.
            if (await Clipboard.TryGetTextAsync() is { Length: > 0 } text)
                InsertIntoInput(text);
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось прочитать буфер обмена", ex);
        }
    }

    private void InsertIntoInput(string paste)
    {
        var current = InputBox.Text ?? "";
        var start = Math.Clamp(Math.Min(InputBox.SelectionStart, InputBox.SelectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(InputBox.SelectionStart, InputBox.SelectionEnd), 0, current.Length);
        paste = paste.Replace("\r\n", "\n");
        if (InputBox.MaxLength > 0)
        {
            var room = InputBox.MaxLength - (current.Length - (end - start));
            if (room <= 0) return;
            if (paste.Length > room) paste = paste[..room];
        }
        InputBox.Text = current[..start] + paste + current[end..];
        InputBox.SelectionStart = InputBox.SelectionEnd = InputBox.CaretIndex = start + paste.Length;
    }

    // ---- Изображения: перетаскивание файлов в окно ----

    private void ChatPanel_DragOver(object? sender, DragEventArgs e)
    {
        if (_current == null || !PendingAttachment.HasFiles(e.DataTransfer))
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
        AddAttachments(PendingAttachment.FromStorageItems(files, errors), errors);
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

    // ---- Файлы в ленте ----

    private async void FileOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message) await FileActions.OpenAsync(message, this);
    }

    private async void FileSave_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message) await FileActions.SaveAsAsync(message, this);
    }

    private async void FileShowInFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message) await FileActions.ShowInFolderAsync(message, this);
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

    // ---- Группы ----

    private async void CreateGroup_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new GroupWindow(null, _chat.People, _chat.IsPeerOnline, _chat.MyId);
        await dialog.ShowDialog(this);
        if (!dialog.Saved) return;
        OpenConversation(_chat.CreateGroup(dialog.EnteredName, dialog.SelectedPeople));
    }

    private async void GroupMembers_Click(object? sender, RoutedEventArgs e)
    {
        if (_current is { IsGroup: true } contact) await EditGroup(contact);
    }

    private async Task EditGroup(Contact contact)
    {
        var dialog = new GroupWindow(contact.Group, _chat.People, _chat.IsPeerOnline, _chat.MyId);
        await dialog.ShowDialog(this);
        if (!dialog.Saved) return;
        _chat.RenameGroup(contact, dialog.EnteredName);
        _chat.AddGroupMembers(contact, dialog.SelectedPeople);
    }

    private async void LeaveGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (_current is { IsGroup: true } contact) await ConfirmLeave(contact);
    }

    private async Task ConfirmLeave(Contact contact)
    {
        var text = $"Выйти из группы «{contact.Title}»?\n\nПереписка группы удалится с этого компьютера. " +
                   "Вернуться можно, если кто-то из участников добавит вас снова.";
        if (await Dialogs.Confirm(this, text, "Выход из группы"))
            _chat.LeaveGroup(contact);
    }

    private void ContactsList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var contact = (e.Source as Control)?.DataContext as Contact;
        if (contact == null || contact.IsEveryone)
        {
            e.Handled = true;
            return;
        }
        if (contact.IsGroup)
        {
            var members = new MenuItem { Header = "Участники…" };
            members.Click += async (_, _) => await EditGroup(contact);
            var groupMute = new MenuItem { Header = contact.IsMuted ? "Включить уведомления" : "Выключить уведомления" };
            groupMute.Click += (_, _) => _chat.SetMuted(contact, !contact.IsMuted);
            var groupClear = new MenuItem { Header = "Очистить переписку", IsEnabled = contact.Messages.Count > 0 };
            groupClear.Click += async (_, _) => await ConfirmClear(contact);
            var leave = new MenuItem { Header = "Выйти из группы" };
            leave.Click += async (_, _) => await ConfirmLeave(contact);
            new ContextMenu { ItemsSource = new Control[] { members, groupMute, new Separator(), groupClear, leave } }
                .Open(ContactsList);
            e.Handled = true;
            return;
        }

        var mute = new MenuItem { Header = contact.IsMuted ? "Включить уведомления" : "Выключить уведомления" };
        mute.Click += (_, _) => _chat.SetMuted(contact, !contact.IsMuted);
        var clear = new MenuItem { Header = "Очистить переписку", IsEnabled = contact.Messages.Count > 0 };
        clear.Click += async (_, _) => await ConfirmClear(contact);
        var remove = new MenuItem { Header = "Удалить из списка", IsEnabled = contact.CanRemove };
        remove.Click += async (_, _) => await ConfirmRemove(contact);

        var menu = new ContextMenu { ItemsSource = new Control[] { mute, new Separator(), clear, remove } };
        menu.Open(ContactsList);
        e.Handled = true;
    }

    private async Task ConfirmClear(Contact contact)
    {
        var hasQueued = contact.Messages.Any(m => m.CanCancel);
        var text = (contact.IsGroup
                       ? $"Удалить всю переписку группы «{contact.Title}» на этом компьютере?"
                       : $"Удалить всю переписку с «{contact.Title}» на этом компьютере?") +
                   (hasQueued ? "\n\nНеотправленные сообщения тоже будут отменены." : "") +
                   (contact.IsGroup ? "\n\nУ остальных участников переписка останется." : "\n\nУ собеседника переписка останется.");
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
