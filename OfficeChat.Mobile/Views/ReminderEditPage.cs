using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OfficeChat.Models;

namespace OfficeChat.Mobile.Views;

/// <summary>Новое напоминание или правка своего: текст, срок, важность, повтор, кому ещё напомнить.</summary>
public sealed class ReminderEditPage : Page
{
    private static readonly (ReminderRepeat Value, string Title)[] Repeats =
    {
        (ReminderRepeat.None, "не повторять"), (ReminderRepeat.Daily, "каждый день"), (ReminderRepeat.Weekdays, "по будням"),
        (ReminderRepeat.Weekly, "каждую неделю"), (ReminderRepeat.Monthly, "каждый месяц"),
    };

    private readonly Reminder? _reminder;
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxLength = 2000 };
    private readonly CheckBox _hasTime = new() { Content = "Напомнить в срок", FontSize = 16, IsChecked = true };
    private readonly DatePicker _date = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TimePicker _time = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ClockIdentifier = "24HourClock" };
    private readonly ComboBox _importance = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _repeat = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _people = new() { Spacing = 2 };
    private readonly List<(CheckBox Box, Contact Person)> _checks = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, IsVisible = false, TextWrapping = TextWrapping.Wrap };

    /// <param name="reminder">Своё напоминание для правки (null — новое).</param>
    /// <param name="text">Текст нового (например, из сообщения).</param>
    /// <param name="person">Кому ещё напомнить (переписка, из которой создают).</param>
    public ReminderEditPage(Reminder? reminder, string text, Contact? person)
    {
        _reminder = reminder;
        foreach (var level in Enumerable.Range(Reminder.MinImportance, Reminder.MaxImportance))
            _importance.Items.Add(new ComboBoxItem { Content = Reminder.NameOf(level), Tag = level });
        foreach (var (value, title) in Repeats) _repeat.Items.Add(new ComboBoxItem { Content = title, Tag = value });

        var due = reminder?.DueAt ?? DateTime.Now.AddHours(1);
        _text.Text = reminder?.Text ?? text;
        _hasTime.IsChecked = reminder == null || reminder.DueAt != null;
        _date.SelectedDate = due.Date;
        _time.SelectedTime = new TimeSpan(due.Hour, due.Minute, 0);
        _importance.SelectedIndex = (reminder?.Importance ?? 2) - Reminder.MinImportance;
        _repeat.SelectedIndex = Array.FindIndex(Repeats, r => r.Value == (reminder?.Repeat ?? ReminderRepeat.None));
        _hasTime.IsCheckedChanged += (_, _) => _date.IsEnabled = _time.IsEnabled = _repeat.IsEnabled = _hasTime.IsChecked == true;

        var chat = MobileSession.Chat;
        var chosen = reminder?.Participants.Select(p => p.Id).ToHashSet() ?? new HashSet<Guid>();
        if (person is { IsPerson: true }) chosen.Add(person.Key);
        if (person is { IsGroup: true }) chosen.UnionWith(person.Group!.Members.Select(m => m.Id));
        foreach (var contact in chat?.Contacts.Where(c => c.IsPerson).OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
                                ?? Enumerable.Empty<Contact>())
        {
            var box = new CheckBox { Content = contact.Title, IsChecked = chosen.Contains(contact.Key), FontSize = 16,
                Padding = new Avalonia.Thickness(6, 6) };
            _checks.Add((box, contact));
            _people.Children.Add(box);
        }

        var body = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 4 };
        void Label(string title) => body.Children.Add(new TextBlock { Text = title, Classes = { "label" } });
        Label("О чём напомнить");
        body.Children.Add(_text);
        body.Children.Add(new Border { Height = 8 });
        body.Children.Add(_hasTime);
        body.Children.Add(_date);
        body.Children.Add(_time);
        Label("Важность");
        body.Children.Add(_importance);
        Label("Повтор");
        body.Children.Add(_repeat);
        Label("Напомнить ещё (у них тоже появится)");
        body.Children.Add(_people);
        body.Children.Add(_error);
        var save = new Button { Content = reminder == null ? "Создать" : "Сохранить", Classes = { "primary" },
            Margin = new Avalonia.Thickness(0, 16, 0, 0) };
        save.Click += (_, _) => Save();
        body.Children.Add(save);
        Content = PageHeader.Wrap(this, reminder == null ? "Новое напоминание" : "Напоминание", new ScrollViewer { Content = body });
    }

    private void Save()
    {
        if (MobileSession.Reminders is not { } service) return;
        var text = _text.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            _error.Text = "Напишите, о чём напомнить";
            _error.IsVisible = true;
            return;
        }
        DateTime? due = null;
        if (_hasTime.IsChecked == true && _date.SelectedDate is { } date)
            due = date.Date + (_time.SelectedTime ?? TimeSpan.FromHours(9));
        var importance = (_importance.SelectedItem as ComboBoxItem)?.Tag is int level ? level : 2;
        var repeat = (_repeat.SelectedItem as ComboBoxItem)?.Tag is ReminderRepeat r ? r : ReminderRepeat.None;
        var people = _checks.Where(c => c.Box.IsChecked == true).Select(c => c.Person).ToList();
        if (_reminder == null) service.Create(text, due, importance, repeat, people);
        else service.Update(_reminder, text, due, importance, repeat, people);
        Shell.Pop();
    }
}
