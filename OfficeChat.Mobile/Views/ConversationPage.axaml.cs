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
    private ChatMessage? _replyTo;
    private ChatMessage? _editing;
    private bool _settingInput;

    public ConversationPage() : this(Contact.Everyone()) { }

    public ConversationPage(Contact contact)
    {
        InitializeComponent();
        _contact = contact;
        DataContext = contact;
        MessagesList.ItemsSource = contact.Messages;
        InputBox.TextChanged += (_, _) =>
        {
            if (!_settingInput && !string.IsNullOrEmpty(InputBox.Text)) MobileSession.Chat?.NotifyTyping(_contact);
        };
        InputBox.KeyDown += (_, e) =>
        {
            // Физическая клавиатура: Enter — отправить, Shift+Enter — новая строка.
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                Send();
            }
        };
    }

    public override void OnAppearing()
    {
        MobileSession.Opened(_contact);
        _contact.Messages.CollectionChanged += OnMessagesChanged;
        _contact.PropertyChanged += OnContactChanged;
        if (MobileSession.Chat is { } chat) chat.PresenceChanged += UpdateStatus;
        UpdateStatus();
        GameButton.IsVisible = _contact.IsPerson;
        ScrollToEnd();
    }

    public override void OnDisappearing()
    {
        if (MobileSession.VisibleConversation == _contact) MobileSession.VisibleConversation = null;
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
        Dispatcher.UIThread.Post(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() => MessagesScroll.ScrollToEnd(), TimeSpan.FromMilliseconds(200));
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => Shell.Pop();

    private void LoadOlder_Click(object? sender, RoutedEventArgs e) => MobileSession.Chat?.LoadOlder(_contact);

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
                var name = file.Name;
                var size = (await file.GetBasicPropertiesAsync()).Size ?? 0;
                if (ImageStore.IsImageFile(name) && (long)size <= ImageStore.MaxBytes)
                {
                    await using var stream = await file.OpenReadAsync();
                    using var memory = new MemoryStream();
                    await stream.CopyToAsync(memory);
                    chat.SendImage(_contact, memory.ToArray(), name, "", replyTo);
                }
                else
                {
                    // Файл с телефона (content://) — сначала копия во временную папку программы.
                    var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + Path.GetExtension(name));
                    await using (var source = await file.OpenReadAsync())
                    await using (var target = File.Create(temp))
                        await source.CopyToAsync(target);
                    var copy = Path.Combine(Path.GetDirectoryName(temp)!, name);
                    if (File.Exists(copy)) copy = Path.Combine(Path.GetDirectoryName(temp)!, Guid.NewGuid().ToString("N")[..6] + "-" + name);
                    File.Move(temp, copy);
                    await chat.SendFileAsync(_contact, copy, "", replyTo);
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
        var menu = new MenuFlyout();
        menu.Items.Add(ChatsPage.Item("↩ Ответить", () => StartReply(message)));
        if (message.HasText)
            menu.Items.Add(ChatsPage.Item("📋 Копировать", async () =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(message.Text);
            }));
        menu.Items.Add(ChatsPage.Item("⏰ Напомнить об этом", () => Shell.Push(new ReminderEditPage(null, message.PreviewText, _contact))));
        if (message.CanEdit && !message.IsImage && !message.IsFile)
            menu.Items.Add(ChatsPage.Item("✏ Изменить", () => StartEdit(message)));
        if (message.CanCancel)
            menu.Items.Add(ChatsPage.Item("Отменить отправку", () => chat?.Delete(_contact, message, forEveryone: true)));
        else if (message.CanDeleteForEveryone)
            menu.Items.Add(ChatsPage.Item("🗑 Удалить у всех", async () =>
            {
                if (await Dialogs.ConfirmAsync(this, "Удалить сообщение у всех?", "Удалить"))
                    chat?.Delete(_contact, message, forEveryone: true);
            }));
        menu.Items.Add(ChatsPage.Item("Удалить у себя", () => chat?.Delete(_contact, message, forEveryone: false)));
        menu.ShowAt(control, true);
    }

    // ---- Меню переписки ----

    private void Menu_Click(object? sender, RoutedEventArgs e)
    {
        if (MobileSession.Chat is not { } chat) return;
        var menu = new MenuFlyout();
        if (_contact.IsGroup)
            menu.Items.Add(ChatsPage.Item("👥 Участники", () => Shell.Push(new GroupPage(_contact))));
        menu.Items.Add(ChatsPage.Item(_contact.IsMuted ? "🔔 Включить уведомления" : "🔕 Без уведомлений",
            () => chat.SetMuted(_contact, !_contact.IsMuted)));
        menu.Items.Add(ChatsPage.Item(_contact.IsPinned ? "Открепить" : "📌 Закрепить", () => chat.SetPinned(_contact, !_contact.IsPinned)));
        menu.Items.Add(ChatsPage.Item("Очистить переписку", async () =>
        {
            if (await Dialogs.ConfirmAsync(this, "Удалить всю переписку на этом телефоне? У собеседника она останется.", "Очистить"))
                chat.ClearConversation(_contact);
        }));
        if (_contact.IsGroup)
            menu.Items.Add(ChatsPage.Item("Выйти из группы", async () =>
            {
                if (!await Dialogs.ConfirmAsync(this, $"Выйти из группы «{_contact.Title}»?", "Выйти")) return;
                chat.LeaveGroup(_contact);
                Shell.Pop();
            }));
        menu.ShowAt(MenuButton);
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
        var menu = new MenuFlyout();
        foreach (var kind in Enum.GetValues<GameKind>())
            menu.Items.Add(ChatsPage.Item(BoardGame.TitleOf(kind), () => Shell.Push(new GamePage(games.Invite(_contact, kind)))));
        menu.ShowAt(GameButton);
    }
}
