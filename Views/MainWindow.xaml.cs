using System.Collections.ObjectModel;
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
    private readonly UpdateService _updates;
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

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += ExitApplication;
        _chat.UnreadChanged += OnUnreadChanged;
        TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();

        AttachmentsList.ItemsSource = _attachments;
        _attachments.CollectionChanged += (_, _) =>
            AttachmentsBar.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Ctrl+V, Shift+Insert и «Вставить» из меню: если в буфере изображение — прикрепляем его.
        CommandManager.AddPreviewCanExecuteHandler(InputBox, InputBox_PreviewCanPaste);
        CommandManager.AddPreviewExecutedHandler(InputBox, InputBox_PreviewPaste);

        _chat.Start();
        _ = CheckFirewallAsync();

        // Обновление ставится, только когда окно свёрнуто в трей и ничего не прервётся.
        _updates = new UpdateService(_chat,
            () => !IsVisible && !_games.HasActiveGames && !_chat.HasTransfersInProgress,
            ExitApplication);

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

    /// <summary>Число непрочитанных — на значке в трее и на кнопке программы в панели задач.</summary>
    private void OnUnreadChanged()
    {
        var unread = _chat.TotalUnread;
        _tray.SetUnread(unread);
        TaskbarItemInfo.Overlay = unread > 0 ? AppIcon.CreateTaskbarOverlay(unread) : null;
        TaskbarItemInfo.Description = unread > 0 ? $"Непрочитанных: {unread}" : "";
        UpdateMuteButton();
    }

    // ---- Брандмауэр ----

    private async Task CheckFirewallAsync()
    {
        var port = _chat.MessagingPort;
        var state = await Task.Run(() => FirewallService.Check(port));
        FirewallBanner.Visibility = state == FirewallState.Blocked ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AllowFirewall_Click(object sender, RoutedEventArgs e)
    {
        FirewallButton.IsEnabled = false;
        try
        {
            var ok = await Task.Run(FirewallService.AllowIncoming);
            await CheckFirewallAsync();
            if (!ok)
                MessageBox.Show(this,
                    "Правило не добавлено: нужны права администратора.\n\n" +
                    "Если у вас их нет, попросите администратора разрешить OfficeChat в брандмауэре Windows " +
                    "(Панель управления → Брандмауэр Защитника Windows → Разрешение взаимодействия с приложением).",
                    "OfficeChat", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            FirewallButton.IsEnabled = true;
        }
    }

    // ---- Мини-игры ----

    /// <summary>Кнопка «Сыграть» — меню с играми.</summary>
    private void InviteToGame_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { CanInviteToGame: true } || sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var kind in new[] { GameKind.TicTacToe, GameKind.Checkers, GameKind.Battleship })
        {
            var item = new MenuItem { Header = BoardGame.TitleOf(kind) };
            item.Click += (_, _) => InviteToGame(kind);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void InviteToGame(GameKind kind)
    {
        if (_current is not { CanInviteToGame: true } contact) return;
        if (_games.ActiveGameWith(contact) != null)
        {
            ShowNotice("С этим собеседником уже идёт партия — сначала закончите её.");
            return;
        }
        _games.Invite(contact, kind);
        GameView.Show(_games.GameFor(contact));
    }

    private void OnGameInvite(BoardGame game)
    {
        // Переписка с пригласившим открыта на экране — кнопки «Принять / Отклонить» уже видны в панели.
        if (_chat.IsConversationVisible(game.Opponent)) return;
        _notifications.ShowGameInvite(game, AcceptGame, _games.Decline);
    }

    /// <summary>Приняли во всплывающем окне — открываем переписку, где идёт игра.</summary>
    private void AcceptGame(BoardGame game)
    {
        _games.Accept(game);
        OpenConversation(game.Opponent);
    }

    /// <summary>Соперник сходил, а переписка не на экране — напоминаем, что наш ход.</summary>
    private void OnOpponentMoved(BoardGame game)
    {
        if (_chat.IsConversationVisible(game.Opponent)) return;
        _notifications.Show(game.Opponent, new ChatMessage
        {
            Id = Guid.NewGuid(),
            IsOutgoing = false,
            Text = game is BattleshipGame ? "🎮 Морской бой: ваш выстрел" : $"🎮 {game.Title}: ваш ход",
            Timestamp = DateTime.Now,
            Kind = MessageKind.Game,
        });
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

    /// <summary>Программа только что обновилась по сети — сообщаем об этом в трее.</summary>
    public void NotifyUpdated() =>
        _tray.ShowHint("OfficeChat обновлён", $"Установлена версия {_updates.CurrentVersion}.");

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

        CancelCompose();
        _current = ContactsList.SelectedItem as Contact;
        NoticeText.Visibility = Visibility.Collapsed;
        // Прикреплённое к одной переписке не должно случайно уйти в другую.
        _attachments.Clear();

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
        GameView.Show(_games.GameFor(_current));
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
        MuteButton.ToolTip = contact.IsMuted
            ? "Уведомления выключены: сообщения приходят без всплывающих окон. Нажмите, чтобы включить"
            : "Выключить уведомления от этой переписки";
    }

    private void ToggleMute_Click(object sender, RoutedEventArgs e)
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

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter — отправить, Shift+Enter — новая строка.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            SendCurrent();
        }
        else if (e.Key == Key.Escape && ComposeBar.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            CancelCompose();
        }
        else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.None && InputBox.Text.Length == 0 &&
                 _editing == null && _current?.Messages.LastOrDefault(m => m.CanEdit) is { } last)
        {
            // Стрелка вверх в пустом поле — исправить своё последнее сообщение (как в Telegram).
            e.Handled = true;
            StartEdit(last);
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendCurrent();

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Сообщаем собеседнику «печатает…», только когда набирают текст, а не когда поле очистилось после отправки.
        if (_current != null && !_settingInput && InputBox.Text.Length > 0 && InputBox.IsKeyboardFocusWithin)
            _chat.NotifyTyping(_current);
    }

    private async void SendCurrent()
    {
        var text = InputBox.Text.Trim();
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
            // Одно вложение — текст становится подписью к нему (как в Telegram).
            // Несколько — уходят по очереди, а текст следом отдельным сообщением.
            var items = _attachments.ToList();
            var caption = items.Count == 1 ? text : "";
            _attachments.Clear();
            InputBox.Clear();
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
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
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
            NoticeText.Visibility = Visibility.Collapsed;
        }

        InputBox.Clear();
        InputBox.Focus();
    }

    // ---- Вложения: прикрепление ----

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var patterns = string.Join(";", ImageStore.Extensions.Select(x => "*" + x));
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите файлы или изображения",
            Filter = $"Все файлы (*.*)|*.*|Изображения ({patterns})|{patterns}",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var errors = new List<string>();
        AddAttachments(PendingAttachment.FromFiles(dialog.FileNames, errors), errors);
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

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PendingAttachment image)
            _attachments.Remove(image);
        if (_attachments.Count == 0) NoticeText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Вставлять ли из буфера как вложение. Если там есть текст (Word, Excel кладут ещё и картинку),
    /// вставляем текст как обычно; скопированные файлы и скриншоты — прикрепляем.
    /// </summary>
    private static bool ClipboardHasImageToAttach()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null || !PendingAttachment.ContainsAttachment(data)) return false;
            return data.GetDataPresent(DataFormats.FileDrop) || !data.GetDataPresent(DataFormats.UnicodeText);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private void InputBox_PreviewCanPaste(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !ClipboardHasImageToAttach()) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    private void InputBox_PreviewPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !ClipboardHasImageToAttach()) return;
        e.Handled = true;

        var errors = new List<string>();
        var data = Clipboard.GetDataObject();
        var images = data == null ? new List<PendingAttachment>() : PendingAttachment.FromDataObject(data, errors);
        AddAttachments(images, errors);
    }

    // ---- Изображения: перетаскивание файлов в окно ----

    private void ChatPanel_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (_current == null || !PendingAttachment.ContainsAttachment(e.Data)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void ChatPanel_PreviewDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave приходит и при переходе между дочерними элементами — прячем, только если курсор вышел из панели.
        var position = e.GetPosition(ChatPanel);
        if (position.X <= 0 || position.Y <= 0 || position.X >= ChatPanel.ActualWidth || position.Y >= ChatPanel.ActualHeight)
            DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void ChatPanel_PreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (_current == null || !PendingAttachment.ContainsAttachment(e.Data)) return;
        e.Handled = true;

        var errors = new List<string>();
        AddAttachments(PendingAttachment.FromDataObject(e.Data, errors), errors);
        Activate();
    }

    // ---- Изображения: просмотр и сохранение ----

    private static ChatMessage? MessageOf(object sender) => (sender as FrameworkElement)?.DataContext as ChatMessage;

    private void Image_Click(object sender, MouseButtonEventArgs e)
    {
        if (MessageOf(sender) is { } message) OpenImage(message);
    }

    private void ImageOpen_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message) OpenImage(message);
    }

    private void ImageSave_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message) ImageViewerWindow.SaveAs(message, this);
    }

    private void ImageCopy_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message)
        {
            ImageViewerWindow.Copy(message);
            ShowNotice("Изображение скопировано в буфер обмена.");
        }
    }

    private void ImageOpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message) ImageViewerWindow.OpenExternal(message);
    }

    // ---- Файлы в ленте ----

    private void FileOpen_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message) FileActions.Open(message, this);
    }

    private void FileSave_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message) FileActions.SaveAs(message, this);
    }

    private void FileShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } message) FileActions.ShowInFolder(message, this);
    }

    private void OpenImage(ChatMessage message)
    {
        var sender = message.IsOutgoing ? "Вы" : _current?.Title ?? "";
        new ImageViewerWindow(message, sender) { Owner = this }.Show();
    }

    // ---- Ответ, правка, удаление ----

    private void StartReply(ChatMessage message)
    {
        CancelCompose();
        _replyTo = message;
        ComposeTitle.Text = $"↩ Ответ · {(message.IsOutgoing ? ChatService.MyReplyAuthor : _current?.Title)}";
        ComposeText.Text = message.QuoteText;
        ComposeBar.Visibility = Visibility.Visible;
        InputBox.Focus();
    }

    private void StartEdit(ChatMessage message)
    {
        CancelCompose();
        _editing = message;
        _draftBeforeEdit = InputBox.Text;
        ComposeTitle.Text = message.IsFile || message.IsImage ? "✎ Изменение подписи" : "✎ Редактирование";
        ComposeText.Text = message.QuoteText;
        ComposeBar.Visibility = Visibility.Visible;
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
        ComposeBar.Visibility = Visibility.Collapsed;
    }

    private void SetInputText(string text)
    {
        _settingInput = true;
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        _settingInput = false;
    }

    private void CancelCompose_Click(object sender, RoutedEventArgs e)
    {
        CancelCompose();
        InputBox.Focus();
    }

    private void ReplyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { CanReply: true } message) StartReply(message);
    }

    private void EditMessage_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { CanEdit: true } message) StartEdit(message);
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is not { } message) return;
        // Меню открыли на тексте с выделенным куском — копируем его, иначе весь текст.
        var target = ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget as TextBox;
        var text = target is { SelectionLength: > 0 } ? target.SelectedText : message.Text;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Log.Warn("Буфер обмена занят другой программой", ex);
        }
    }

    private void DeleteForMe_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is not { } message || _current is not { } contact) return;
        var text = message.IsOutgoing && message.Status != MessageStatus.Queued
            ? "Удалить сообщение только у себя? У собеседника оно останется."
            : "Удалить сообщение?";
        if (MessageBox.Show(this, text, "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes)
            _chat.Delete(contact, message, forEveryone: false);
    }

    private void DeleteForAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is not { CanDeleteForEveryone: true } message || _current is not { } contact) return;
        var text = message.Status == MessageStatus.Queued
            ? "Сообщение ещё не отправлено — отменить его?"
            : "Удалить сообщение и у вас, и у собеседника?" +
              (contact.IsOnline ? "" : "\n\nСобеседник не в сети — у него сообщение исчезнет, когда он появится.");
        if (MessageBox.Show(this, text, "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes)
            _chat.Delete(contact, message, forEveryone: true);
    }

    /// <summary>Клик по цитате — прокрутить к сообщению, на которое ответили.</summary>
    private void Quote_Click(object sender, MouseButtonEventArgs e)
    {
        if (MessageOf(sender) is not { ReplyToId: { } id }) return;
        e.Handled = true;
        var original = _current?.Messages.FirstOrDefault(m => m.Id == id);
        if (original == null)
        {
            ShowNotice(_current?.HasOlderMessages == true
                ? "Это сообщение выше — нажмите «Показать более ранние сообщения»."
                : "Исходное сообщение удалено.");
            return;
        }
        (MessagesList.ItemContainerGenerator.ContainerFromItem(original) as FrameworkElement)?.BringIntoView();
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
        dialog.ShowVersion(_updates.CurrentVersion, _updates.CanShare);
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

    // ---- Группы ----

    private void CreateGroup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GroupWindow(null, _chat.People, _chat.IsPeerOnline, _chat.MyId) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        OpenConversation(_chat.CreateGroup(dialog.EnteredName, dialog.SelectedPeople));
    }

    private void GroupMembers_Click(object sender, RoutedEventArgs e)
    {
        if (_current is { IsGroup: true } contact) EditGroup(contact);
    }

    private void EditGroup(Contact contact)
    {
        var dialog = new GroupWindow(contact.Group, _chat.People, _chat.IsPeerOnline, _chat.MyId) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _chat.RenameGroup(contact, dialog.EnteredName);
        _chat.AddGroupMembers(contact, dialog.SelectedPeople);
    }

    private void LeaveGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_current is { IsGroup: true } contact) ConfirmLeave(contact);
    }

    private void ConfirmLeave(Contact contact)
    {
        var text = $"Выйти из группы «{contact.Title}»?\n\nПереписка группы удалится с этого компьютера. " +
                   "Вернуться можно, если кто-то из участников добавит вас снова.";
        if (MessageBox.Show(this, text, "Выход из группы", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes)
            _chat.LeaveGroup(contact);
    }

    private void ContactsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var contact = (e.OriginalSource as FrameworkElement)?.DataContext as Contact;
        if (contact == null || contact.IsEveryone)
        {
            e.Handled = true;
            return;
        }
        if (contact.IsGroup)
        {
            var members = new MenuItem { Header = "Участники…" };
            members.Click += (_, _) => EditGroup(contact);
            var groupMute = new MenuItem { Header = contact.IsMuted ? "Включить уведомления" : "Выключить уведомления" };
            groupMute.Click += (_, _) => _chat.SetMuted(contact, !contact.IsMuted);
            var groupClear = new MenuItem { Header = "Очистить переписку", IsEnabled = contact.Messages.Count > 0 };
            groupClear.Click += (_, _) => ConfirmClear(contact);
            var leave = new MenuItem { Header = "Выйти из группы" };
            leave.Click += (_, _) => ConfirmLeave(contact);
            ContactsList.ContextMenu = new ContextMenu { Items = { members, groupMute, new Separator(), groupClear, leave } };
            return;
        }

        var mute = new MenuItem { Header = contact.IsMuted ? "Включить уведомления" : "Выключить уведомления" };
        mute.Click += (_, _) => _chat.SetMuted(contact, !contact.IsMuted);

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

        ContactsList.ContextMenu = new ContextMenu { Items = { mute, new Separator(), clear, remove } };
    }

    private void ConfirmClear(Contact contact)
    {
        var hasQueued = contact.Messages.Any(m => m.CanCancel);
        var text = (contact.IsGroup
                       ? $"Удалить всю переписку группы «{contact.Title}» на этом компьютере?"
                       : $"Удалить всю переписку с «{contact.Title}» на этом компьютере?") +
                   (hasQueued ? "\n\nНеотправленные сообщения тоже будут отменены." : "") +
                   (contact.IsGroup ? "\n\nУ остальных участников переписка останется." : "\n\nУ собеседника переписка останется.");
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
        _updates.Dispose();
        _games.LeaveAll();
        _games.Dispose();
        _notifications.CloseAll();
        _chat.Dispose();
        _tray.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
