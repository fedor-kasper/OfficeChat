using Avalonia.Controls;
using Avalonia.Interactivity;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Доска напоминаний: активные (сначала важные, затем по сроку) и выполненные.
/// Создание, правка, «Готово», «Отложить», удаление.
/// </summary>
public partial class RemindersPanel : UserControl
{
    private ReminderService? _reminders;
    private Func<IEnumerable<Contact>> _people = Array.Empty<Contact>;
    private bool _showDone;

    public RemindersPanel()
    {
        InitializeComponent();
    }

    public void Attach(ReminderService reminders, Func<IEnumerable<Contact>> people)
    {
        _reminders = reminders;
        _people = people;
        reminders.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        if (_reminders == null) return;
        var active = _reminders.Active;
        var done = _reminders.Done;
        ActiveTab.Content = $"Активные ({active.Count})";
        DoneTab.Content = $"Выполненные ({done.Count})";
        ActiveTab.Classes.Set("selected", !_showDone);
        DoneTab.Classes.Set("selected", _showDone);
        var items = _showDone ? done : active;
        List.ItemsSource = items;
        EmptyText.Text = items.Count > 0 ? ""
            : _showDone ? "Выполненных пока нет."
            : "Напоминаний нет. Нажмите «＋ Новое напоминание» — или правой кнопкой по сообщению → «Напомнить об этом».";
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private static Reminder? ReminderOf(object? sender) => (sender as Control)?.DataContext as Reminder;

    private void ActiveTab_Click(object? sender, RoutedEventArgs e)
    {
        _showDone = false;
        Refresh();
    }

    private void DoneTab_Click(object? sender, RoutedEventArgs e)
    {
        _showDone = true;
        Refresh();
    }

    private async void New_Click(object? sender, RoutedEventArgs e) => await CreateAsync("");

    /// <summary>Новое напоминание (с готовым текстом — например, из сообщения).</summary>
    public async Task CreateAsync(string text)
    {
        if (_reminders == null || Owner is not { } owner) return;
        var editor = new ReminderEditorWindow(null, _people(), text);
        await editor.ShowDialog(owner);
        if (!editor.Saved) return;
        _reminders.Create(editor.EnteredText, editor.DueAt, editor.Importance, editor.Repeat, editor.SelectedPeople);
        _showDone = false;
        Refresh();
    }

    private async void Edit_Click(object? sender, RoutedEventArgs e)
    {
        if (_reminders == null || ReminderOf(sender) is not { IsMine: true } reminder || Owner is not { } owner) return;
        var editor = new ReminderEditorWindow(reminder, _people(), reminder.Text);
        await editor.ShowDialog(owner);
        if (editor.Saved)
            _reminders.Update(reminder, editor.EnteredText, editor.DueAt, editor.Importance, editor.Repeat, editor.SelectedPeople);
    }

    private void Done_Click(object? sender, RoutedEventArgs e)
    {
        if (ReminderOf(sender) is { } reminder) _reminders?.Complete(reminder);
    }

    private void Reopen_Click(object? sender, RoutedEventArgs e)
    {
        if (ReminderOf(sender) is { } reminder) _reminders?.Reopen(reminder);
    }

    private void Snooze_Click(object? sender, RoutedEventArgs e)
    {
        if (_reminders == null || ReminderOf(sender) is not { } reminder || sender is not Control button) return;
        SnoozeMenu.Show(button, reminder, _reminders, Owner);
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (_reminders == null || ReminderOf(sender) is not { } reminder) return;
        var text = reminder.IsMine && reminder.HasParticipants
            ? "Удалить напоминание? Оно удалится и у участников."
            : "Удалить напоминание?";
        if (await Dialogs.Confirm(Owner, text, "Напоминание"))
            _reminders.Delete(reminder);
    }
}

/// <summary>Меню «Отложить»: готовые варианты и «Выбрать время…».</summary>
public static class SnoozeMenu
{
    public static void Show(Control anchor, Reminder reminder, ReminderService reminders, Window? owner)
    {
        var items = new List<Control>();
        foreach (var (title, until) in ReminderService.SnoozeChoices())
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => reminders.Snooze(reminder, until);
            items.Add(item);
        }
        items.Add(new Separator());
        var pick = new MenuItem { Header = "Выбрать время…" };
        pick.Click += async (_, _) =>
        {
            var dialog = new TimePickWindow(reminder.EffectiveTime is { } t && t > DateTime.Now ? t : DateTime.Now.AddHours(1));
            if (owner != null) await dialog.ShowDialog(owner);
            else
            {
                var closed = new TaskCompletionSource();
                dialog.Closed += (_, _) => closed.TrySetResult();
                dialog.Show();
                await closed.Task;
            }
            if (dialog.Picked is { } time) reminders.Snooze(reminder, time);
        };
        items.Add(pick);
        new ContextMenu { ItemsSource = items }.Open(anchor);
    }
}
