using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Доска напоминаний: активные (сначала важные, затем по сроку) и выполненные.
/// Создание, правка, «Готово», «Отложить», удаление.
/// </summary>
public partial class RemindersPanel : UserControl
{
    private static readonly Brush SelectedTabBrush = GameBoards.Frozen(0xDB, 0xEA, 0xFE);

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
        ActiveTab.Background = _showDone ? Brushes.Transparent : SelectedTabBrush;
        DoneTab.Background = _showDone ? SelectedTabBrush : Brushes.Transparent;
        ActiveTab.FontWeight = _showDone ? FontWeights.Normal : FontWeights.SemiBold;
        DoneTab.FontWeight = _showDone ? FontWeights.SemiBold : FontWeights.Normal;
        var items = _showDone ? done : active;
        List.ItemsSource = items;
        EmptyText.Text = items.Count > 0 ? ""
            : _showDone ? "Выполненных пока нет."
            : "Напоминаний нет. Нажмите «＋ Новое напоминание» — или правой кнопкой по сообщению → «Напомнить об этом».";
    }

    private Window? Owner => Window.GetWindow(this);

    private static Reminder? ReminderOf(object sender) => (sender as FrameworkElement)?.DataContext as Reminder;

    private void ActiveTab_Click(object sender, RoutedEventArgs e)
    {
        _showDone = false;
        Refresh();
    }

    private void DoneTab_Click(object sender, RoutedEventArgs e)
    {
        _showDone = true;
        Refresh();
    }

    private void New_Click(object sender, RoutedEventArgs e) => Create("");

    /// <summary>Новое напоминание (с готовым текстом — например, из сообщения).</summary>
    public void Create(string text)
    {
        if (_reminders == null) return;
        var editor = new ReminderEditorWindow(null, _people(), text) { Owner = Owner };
        if (editor.ShowDialog() != true) return;
        _reminders.Create(editor.EnteredText, editor.DueAt, editor.Importance, editor.Repeat, editor.SelectedPeople);
        _showDone = false;
        Refresh();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_reminders == null || ReminderOf(sender) is not { IsMine: true } reminder) return;
        var editor = new ReminderEditorWindow(reminder, _people(), reminder.Text) { Owner = Owner };
        if (editor.ShowDialog() == true)
            _reminders.Update(reminder, editor.EnteredText, editor.DueAt, editor.Importance, editor.Repeat, editor.SelectedPeople);
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderOf(sender) is { } reminder) _reminders?.Complete(reminder);
    }

    private void Reopen_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderOf(sender) is { } reminder) _reminders?.Reopen(reminder);
    }

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        if (_reminders != null && ReminderOf(sender) is { } reminder && sender is FrameworkElement button)
            SnoozeMenu.Show(button, reminder, _reminders);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_reminders == null || ReminderOf(sender) is not { } reminder) return;
        var text = reminder.IsMine && reminder.HasParticipants
            ? "Удалить напоминание? Оно удалится и у участников."
            : "Удалить напоминание?";
        if (MessageBox.Show(Owner!, text, "Напоминание", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes)
            _reminders.Delete(reminder);
    }
}

/// <summary>Меню «Отложить»: готовые варианты и «Выбрать время…».</summary>
public static class SnoozeMenu
{
    public static void Show(FrameworkElement anchor, Reminder reminder, ReminderService reminders)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var (title, until) in ReminderService.SnoozeChoices())
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => reminders.Snooze(reminder, until);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var pick = new MenuItem { Header = "Выбрать время…" };
        pick.Click += (_, _) =>
        {
            var initial = reminder.EffectiveTime is { } t && t > DateTime.Now ? t : DateTime.Now.AddHours(1);
            var dialog = new TimePickWindow(initial);
            if (dialog.ShowDialog() == true && dialog.Picked is { } time)
                reminders.Snooze(reminder, time);
        };
        menu.Items.Add(pick);
        menu.IsOpen = true;
    }
}
