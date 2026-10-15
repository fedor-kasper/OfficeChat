using Avalonia.Controls;
using Avalonia.Interactivity;
using OfficeChat.Models;

namespace OfficeChat.Mobile.Views;

/// <summary>Главный экран телефона: переписки (закреплённые и свежие — вверху), напоминания, меню.</summary>
public partial class ChatsPage : Page
{
    public ChatsPage()
    {
        InitializeComponent();
    }

    public override void OnAppearing()
    {
        var chat = MobileSession.Chat;
        if (chat == null) return;
        MobileSession.VisibleConversation = null;
        ContactsList.ItemsSource = chat.Contacts;
        MyNameText.Text = MobileSession.Settings?.DisplayName ?? "";
        chat.ServerStateChanged += UpdateStatus;
        chat.Contacts.CollectionChanged += OnContactsChanged;
        if (MobileSession.Reminders is { } reminders) reminders.Changed += UpdateReminders;
        UpdateStatus();
        UpdateReminders();
        OnContactsChanged(null, null!);
    }

    public override void OnDisappearing()
    {
        if (MobileSession.Chat is { } chat)
        {
            chat.ServerStateChanged -= UpdateStatus;
            chat.Contacts.CollectionChanged -= OnContactsChanged;
        }
        if (MobileSession.Reminders is { } reminders) reminders.Changed -= UpdateReminders;
    }

    private void OnContactsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        EmptyText.IsVisible = MobileSession.Chat?.Contacts.All(c => !c.IsConversation) != false;

    private void UpdateStatus()
    {
        var connected = MobileSession.Chat?.IsServerConnected == true;
        ServerStatusText.Text = connected ? "● на связи" : "○ нет связи с сервером — подключаемся…";
    }

    private void UpdateReminders()
    {
        var count = MobileSession.Reminders?.OverdueCount ?? 0;
        RemindersBadge.IsVisible = count > 0;
        RemindersBadgeText.Text = count.ToString();
    }

    private void Contact_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is Contact contact) Shell.Push(new ConversationPage(contact));
    }

    /// <summary>Долгое нажатие на переписку: закрепить, без уведомлений, удалить.</summary>
    private void Contact_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not Contact contact || MobileSession.Chat is not { } chat)
            return;
        e.Handled = true;
        var menu = new List<Dialogs.Action>();
        menu.Add(new Dialogs.Action(contact.IsPinned ? "Открепить" : "📌 Закрепить", () => chat.SetPinned(contact, !contact.IsPinned)));
        menu.Add(new Dialogs.Action(contact.IsMuted ? "🔔 Включить уведомления" : "🔕 Без уведомлений",
            () => chat.SetMuted(contact, !contact.IsMuted)));
        if (contact.CanRemove)
            menu.Add(new Dialogs.Action("Удалить из списка", async () =>
            {
                if (await Dialogs.ConfirmAsync(this, $"Удалить «{contact.Title}» из списка вместе с перепиской?"))
                    chat.RemoveContact(contact);
            }));
        Dialogs.Actions(this, null, menu);
    }


    private void Reminders_Click(object? sender, RoutedEventArgs e) => Shell.Push(new RemindersPage());

    private void NewGroup_Click(object? sender, RoutedEventArgs e) => Shell.Push(new GroupPage(null));

    private void Menu_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new List<Dialogs.Action>();
        menu.Add(new Dialogs.Action("Новая группа", () => Shell.Push(new GroupPage(null))));
        menu.Add(new Dialogs.Action("Напоминания", () => Shell.Push(new RemindersPage())));
        menu.Add(new Dialogs.Action("Аккаунт и настройки", () => Shell.Push(new SettingsPage())));
        Dialogs.Actions(this, null, menu);
    }
}
