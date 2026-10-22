using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OfficeChat.Models;
using OfficeChat.Services;
using OfficeChat.Views;

namespace OfficeChat.Mobile.Views;

/// <summary>Переписка с человеком или группой: сообщения, ответы, правка, вложения, стикеры.</summary>
public partial class ConversationPage : Page
{
    private readonly Contact _contact;

    public Contact Contact => _contact;
    private ChatMessage? _replyTo;
    private ChatMessage? _editing;
    private bool _settingInput;

    public ConversationPage() : this(Contact.Everyone()) { }

    public ConversationPage(Contact contact)
    {
        InitializeComponent();
        // Внизу переписки — остаёмся внизу, даже когда сообщения становятся выше (догрузились картинки);
        // прокрутили вверх — не мешаем читать.
        MessagesScroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0 && _stickToBottom)
                MessagesScroll.Offset = new Avalonia.Vector(MessagesScroll.Offset.X, double.PositiveInfinity);
            else if (e.OffsetDelta.Y != 0 || e.ViewportDelta.Y != 0)
                _stickToBottom = MessagesScroll.Offset.Y >= MessagesScroll.Extent.Height - MessagesScroll.Viewport.Height - 48;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _window?.Dispose();
            _window = null;
        };
        _contact = contact;
        DataContext = contact;
        // Enter на телефоне — новая строка (отправка — кнопкой ➤); списки продолжаются сами, как в Markdown.
        InputBox.TextChanged += (_, _) =>
        {
            if (_settingInput) return;
            var text = InputBox.Text ?? "";
            if (text.Length > 0) MobileSession.Chat?.NotifyTyping(_contact);
            ContinueList(_previousInput, text);
            _previousInput = InputBox.Text ?? "";
        };
    }

    private string _previousInput = "";
    private bool _stickToBottom = true;

    /// <summary>Набрали перевод строки (ровно один символ «\n») — продолжить или закончить список.</summary>
    private void ContinueList(string before, string after)
    {
        if (after.Length != before.Length + 1) return;
        var at = 0;
        while (at < before.Length && before[at] == after[at]) at++;
        if (after[at] != '\n' || after.Remove(at, 1) != before) return;
        if (ListContinuation.AfterNewLine(after, at + 1) is not { } result) return;
        _settingInput = true;
        InputBox.Text = result.Text;
        InputBox.CaretIndex = result.Caret;
        _settingInput = false;
    }

    // Сообщения на экране: при открытии — только последние (большая история открывается быстро).
    private MessageWindow? _window;

    public override void OnAppearing()
    {
        if (_window == null)
        {
            _window = new MessageWindow(_contact);
            _window.HasMoreChanged += UpdateLoadOlder;
            MessagesList.ItemsSource = _window.Items;
        }
        UpdateLoadOlder();
        MobileSession.Opened(_contact);
        _contact.Messages.CollectionChanged += OnMessagesChanged;
        _contact.PropertyChanged += OnContactChanged;
        if (MobileSession.Chat is { } chat) chat.PresenceChanged += UpdateStatus;
        UpdateStatus();
        GameButton.IsVisible = _contact.IsPerson;
        CameraButton.IsVisible = MobileSession.Platform.CanTakePhoto;
        Shell.KeyboardChanged += OnKeyboardChanged;
        ScrollToEnd();
    }

    // Клавиатура открылась — последние сообщения остаются видны над полем ввода.
    private void OnKeyboardChanged(bool open)
    {
        if (open) ScrollToEnd();
    }

    public override void OnDisappearing()
    {
        if (MobileSession.VisibleConversation == _contact) MobileSession.VisibleConversation = null;
        Shell.KeyboardChanged -= OnKeyboardChanged;
        _contact.Messages.CollectionChanged -= OnMessagesChanged;
        _contact.PropertyChanged -= OnContactChanged;
        if (MobileSession.Chat is { } chat) chat.PresenceChanged -= UpdateStatus;
    }

    public override bool HandleBack()
    {
        if (_replyTo == null && _editing == null) return false;
        CancelCompose();
        return true;
    }

    private void OnContactChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Contact.Subtitle) or nameof(Contact.IsOnline) or nameof(Contact.IsTyping)) UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText.Text = _contact.IsTyping ? _contact.TypingText
            : _contact.IsGroup ? _contact.Group!.StatusText
            : _contact.IsOnline ? "в сети"
            : _contact.Peer?.HasAccount == true ? "не в сети — получит, когда включит OfficeChat"
            : "не в сети";
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Новое внизу — прокручиваем к нему; подгрузка старых (вставка в начало) — не трогаем.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex >= _contact.Messages.Count - e.NewItems!.Count)
        {
            ScrollToEnd();
            if (MobileSession.IsForeground) MobileSession.Chat?.MarkRead(_contact);
        }
    }

    private void ScrollToEnd()
    {
        _stickToBottom = true;
        Dispatcher.UIThread.Post(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() => MessagesScroll.ScrollToEnd(), TimeSpan.FromMilliseconds(200));
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => Shell.Pop();

    private void UpdateLoadOlder() => LoadOlderButton.IsVisible = _window?.HasMore == true;

    private void LoadOlder_Click(object? sender, RoutedEventArgs e)
    {
        if (_window == null || MobileSession.Chat is not { } chat) return;
        // Подгрузили сверху — то, что было на экране, остаётся на месте.
        var fromBottom = MessagesScroll.Extent.Height - MessagesScroll.Offset.Y;
        _stickToBottom = false;
        _window.ShowMore(chat);
        MessagesScroll.UpdateLayout();
        MessagesScroll.Offset = new Avalonia.Vector(0, MessagesScroll.Extent.Height - fromBottom);
    }

    // ---- Отправка ----

    private void Send_Click(object? sender, RoutedEventArgs e) => Send();

    private void Send()
    {
        var chat = MobileSession.Chat;
        var text = InputBox.Text?.Trim() ?? "";
        if (chat == null || text.Length == 0) return;
        if (_editing != null)
        {
            chat.Edit(_contact, _editing, text);
        }
        else
        {
            chat.Send(_contact, text, _replyTo);
        }
        CancelCompose();
        SetInput("");
    }

    private void SetInput(string text)
    {
        _settingInput = true;
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        _previousInput = text;
        _settingInput = false;
    }

    private void StartReply(ChatMessage message)
    {
        _editing = null;
        _replyTo = message;
        ComposeTitle.Text = "Ответ: " + (message.IsOutgoing ? "вам" : message.SenderName.Length > 0 ? message.SenderName : _contact.Title);
        ComposeText.Text = message.PreviewText;
        ComposeBanner.IsVisible = true;
        InputBox.Focus();
    }

    private void StartEdit(ChatMessage message)
    {
        _replyTo = null;
        _editing = message;
        ComposeTitle.Text = "Изменение сообщения";
        ComposeText.Text = message.Text;
        ComposeBanner.IsVisible = true;
        SetInput(message.Text);
        InputBox.Focus();
    }

    private void CancelCompose()
    {
        if (_editing != null) SetInput("");
        _replyTo = null;
        _editing = null;
        ComposeBanner.IsVisible = false;
    }

    private void CancelCompose_Click(object? sender, RoutedEventArgs e) => CancelCompose();

    // ---- Вложения и стикеры ----

    private async void Attach_Click(object? sender, RoutedEventArgs e)
    {
        var chat = MobileSession.Chat;
        if (chat == null || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Отправить файл или изображение",
            AllowMultiple = true,
        });
        var replyTo = _editing == null ? _replyTo : null;
        CancelCompose();
        foreach (var file in files)
        {
            try
            {
                var name = FileStore.SafeFileName(file.Name);
                // Файл с телефона (content://) — сначала копия в папку программы, по ней и размер.
                var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                var copy = Path.Combine(folder, name);
                try
                {
                    await using (var source = await file.OpenReadAsync())
                    await using (var target = File.Create(copy))
                        await source.CopyToAsync(target);
                    if (ImageStore.IsImageFile(name) && new FileInfo(copy).Length <= ImageStore.MaxBytes)
                        chat.SendImage(_contact, await File.ReadAllBytesAsync(copy), name, "", replyTo);
                    else
                        await chat.SendFileAsync(_contact, copy, "", replyTo);
                }
                finally
                {
                    // SendFileAsync уже скопировал файл в хранилище программы.
                    try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
                }
                replyTo = null;
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось отправить «{file.Name}»", ex);
                await Dialogs.InfoAsync(this, $"Не удалось отправить «{file.Name}»: {ex.Message}");
            }
        }
    }

    /// <summary>Снимок камерой телефона — сразу в переписку.</summary>
    private async void Camera_Click(object? sender, RoutedEventArgs e)
    {
        if (MobileSession.Chat is not { } chat) return;
        byte[]? photo;
        try
        {
            photo = await MobileSession.Platform.TakePhotoAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось сделать снимок", ex);
            await Dialogs.InfoAsync(this, $"Не удалось сделать снимок: {ex.Message}");
            return;
        }
        if (photo == null) return; // передумали
        var replyTo = _editing == null ? _replyTo : null;
        if (_editing == null) CancelCompose();
        chat.SendImage(_contact, photo, $"Фото {DateTime.Now:yyyy-MM-dd HH-mm-ss}.jpg", "", replyTo);
    }

    private Flyout? _stickerFlyout;

    private void Stickers_Click(object? sender, RoutedEventArgs e)
    {
        if (MobileSession.Settings is not { } settings) return;
        if (_stickerFlyout == null)
        {
            var picker = new StickerPicker(settings);
            picker.StickerChosen += SendSticker;
            _stickerFlyout = new Flyout { Content = picker, Placement = PlacementMode.TopEdgeAlignedLeft };
        }
        ((StickerPicker)_stickerFlyout.Content!).Reload();
        _stickerFlyout.ShowAt(StickersButton);
    }

    private void SendSticker(Sticker sticker)
    {
        _stickerFlyout?.Hide();
        if (MobileSession.Chat is not { } chat || MobileSession.Settings is not { } settings) return;
        try
        {
            var data = sticker.Load();
            var extension = sticker.Id.StartsWith("builtin/", StringComparison.Ordinal) ? ".png" : Path.GetExtension(sticker.Id);
            chat.SendImage(_contact, data, StickerPicker.PrettyName(sticker.Name) + extension, "",
                _editing == null ? _replyTo : null, sticker: true);
            StickerStore.MarkUsed(settings, sticker);
            if (_editing == null) CancelCompose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Не удалось прочитать стикер", ex);
        }
    }

    private void Image_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ChatMessage { ImagePath.Length: > 0 } message)
            Shell.Push(new ImagePage(message));
    }

    private async void File_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ChatMessage message || TopLevel.GetTopLevel(this) is not { } top) return;
        if (!File.Exists(message.FilePath))
        {
            await Dialogs.InfoAsync(this, "Файл ещё не получен или удалён.");
            return;
        }
        try
        {
            await MobileSession.Platform.OpenFileAsync(top, message.FilePath);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, $"Не удалось открыть файл: {ex.Message}");
        }
    }

    // ---- Действия с сообщением (долгое нажатие) ----

    private void Message_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not ChatMessage message || message.IsSystem) return;
        e.Handled = true;
        var chat = MobileSession.Chat;
        var menu = new List<Dialogs.Action>();
        menu.Add(new Dialogs.Action("↩ Ответить", () => StartReply(message)));
        if (message.HasText)
            menu.Add(new Dialogs.Action("📋 Копировать", async () =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(message.Text);
            }));
        menu.Add(new Dialogs.Action("⏰ Напомнить об этом", () => Shell.Push(new ReminderEditPage(null, message.PreviewText, _contact))));
        if (message.CanEdit && !message.IsImage && !message.IsFile)
            menu.Add(new Dialogs.Action("✏ Изменить", () => StartEdit(message)));
        if (message.CanCancel)
            menu.Add(new Dialogs.Action("Отменить отправку", () => chat?.Delete(_contact, message, forEveryone: true)));
        else if (message.CanDeleteForEveryone)
            menu.Add(new Dialogs.Action("🗑 Удалить у всех", async () =>
            {
                if (await Dialogs.ConfirmAsync(this, "Удалить сообщение у всех?", "Удалить"))
                    chat?.Delete(_contact, message, forEveryone: true);
            }));
        menu.Add(new Dialogs.Action("Удалить у себя", () => chat?.Delete(_contact, message, forEveryone: false)));
        Dialogs.Actions(this, null, menu);
    }

    // ---- Меню переписки ----

    private void Menu_Click(object? sender, RoutedEventArgs e)
    {
        if (MobileSession.Chat is not { } chat) return;
        var menu = new List<Dialogs.Action>();
        if (_contact.IsGroup)
            menu.Add(new Dialogs.Action("👥 Участники", () => Shell.Push(new GroupPage(_contact))));
        menu.Add(new Dialogs.Action(_contact.IsMuted ? "🔔 Включить уведомления" : "🔕 Без уведомлений",
            () => chat.SetMuted(_contact, !_contact.IsMuted)));
        menu.Add(new Dialogs.Action(_contact.IsPinned ? "Открепить" : "📌 Закрепить", () => chat.SetPinned(_contact, !_contact.IsPinned)));
        menu.Add(new Dialogs.Action("Очистить переписку", async () =>
        {
            if (await Dialogs.ConfirmAsync(this, "Удалить всю переписку на этом телефоне? У собеседника она останется.", "Очистить"))
                chat.ClearConversation(_contact);
        }));
        if (_contact.IsGroup)
            menu.Add(new Dialogs.Action("Выйти из группы", async () =>
            {
                if (!await Dialogs.ConfirmAsync(this, $"Выйти из группы «{_contact.Title}»?", "Выйти")) return;
                chat.LeaveGroup(_contact);
                Shell.Pop();
            }));
        Dialogs.Actions(this, null, menu);
    }

    private void Game_Click(object? sender, RoutedEventArgs e)
    {
        if (MobileSession.Games is not { } games) return;
        if (games.GameFor(_contact) is { } game)
        {
            Shell.Push(new GamePage(game));
            return;
        }
        if (!_contact.CanInviteToGame)
        {
            _ = Dialogs.InfoAsync(this, "Сыграть можно с тем, кто сейчас в сети.");
            return;
        }
        var menu = new List<Dialogs.Action>();
        foreach (var kind in Enum.GetValues<GameKind>())
            menu.Add(new Dialogs.Action(BoardGame.TitleOf(kind), () => Shell.Push(new GamePage(games.Invite(_contact, kind)))));
        Dialogs.Actions(this, null, menu);
    }
}
