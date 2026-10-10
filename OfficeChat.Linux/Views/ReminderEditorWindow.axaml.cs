using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using OfficeChat.Models;

namespace OfficeChat.Views;

/// <summary>
/// Создание и правка напоминания: текст, срок (или без срока), повтор, важность ползунком
/// (цвет и поведение видны сразу) и кому ещё отправить.
/// </summary>
public partial class ReminderEditorWindow : Window
{
    private static readonly (string Title, ReminderRepeat Value)[] RepeatChoices =
    {
        ("Не повторять", ReminderRepeat.None),
        ("Каждый день", ReminderRepeat.Daily),
        ("По будням", ReminderRepeat.Weekdays),
        ("Каждую неделю", ReminderRepeat.Weekly),
        ("Каждый месяц", ReminderRepeat.Monthly),
    };

    private readonly List<(CheckBox Box, Contact Person)> _people = new();

    public bool Saved { get; private set; }
    public string EnteredText => TextBox.Text?.Trim() ?? "";
    public DateTime? DueAt { get; private set; }
    public int Importance => (int)Math.Round(ImportanceSlider.Value);
    public ReminderRepeat Repeat => RepeatChoices[Math.Max(0, RepeatBox.SelectedIndex)].Value;
    public List<Contact> SelectedPeople => _people.Where(p => p.Box.IsChecked == true).Select(p => p.Person).ToList();

    public ReminderEditorWindow() : this(null, Array.Empty<Contact>(), "") { }

    /// <param name="reminder">null — новое напоминание.</param>
    public ReminderEditorWindow(Reminder? reminder, IEnumerable<Contact> people, string text)
    {
        InitializeComponent();
        Icon = Platform.AppIcon.Window;
        HeaderText.Text = reminder == null ? "Новое напоминание" : "Напоминание";
        OkButton.Content = reminder == null ? "Создать" : "Сохранить";
        TextBox.Text = text;

        RepeatBox.ItemsSource = RepeatChoices.Select(c => c.Title).ToList();
        RepeatBox.SelectedIndex = Array.FindIndex(RepeatChoices, c => c.Value == (reminder?.Repeat ?? ReminderRepeat.None));

        var due = reminder?.DueAt ?? DefaultTime();
        HasTimeBox.IsChecked = reminder == null || reminder.DueAt != null;
        DateBox.SelectedDate = due.Date;
        TimeBox.Text = due.ToString("HH:mm");
        HasTimeBox.IsCheckedChanged += (_, _) => TimePanel.IsEnabled = HasTimeBox.IsChecked == true;
        TimePanel.IsEnabled = HasTimeBox.IsChecked == true;

        ImportanceSlider.Value = reminder?.Importance ?? 2;
        ImportanceSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) ShowImportance();
        };
        ShowImportance();

        var chosen = reminder?.Participants.Select(p => p.Id).ToHashSet() ?? new HashSet<Guid>();
        foreach (var person in people)
        {
            var box = new CheckBox
            {
                Content = $"{person.Title} · {(person.IsOnline ? "в сети" : "не в сети")}",
                IsChecked = chosen.Contains(person.Key),
            };
            _people.Add((box, person));
            PeopleList.Children.Add(box);
        }
        PeopleLabel.IsVisible = PeopleBorder.IsVisible = _people.Count > 0;

        Opened += (_, _) => TextBox.Focus();
    }

    /// <summary>По умолчанию — через час, на ровные полчаса.</summary>
    private static DateTime DefaultTime()
    {
        var time = DateTime.Now.AddHours(1);
        return new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute < 30 ? 30 : 0, 0)
            .AddHours(time.Minute < 30 ? 0 : 1);
    }

    private void ShowImportance()
    {
        var importance = Importance;
        var color = Color.Parse(Reminder.ColorOf(importance));
        ImportanceText.Text = Reminder.NameOf(importance);
        ImportanceChip.Background = new SolidColorBrush(color);
        Preview.BorderBrush = new SolidColorBrush(color);
        Preview.Background = new SolidColorBrush(Color.Parse(Reminder.TintOf(importance)));
        BehaviorText.Text = Reminder.BehaviorOf(importance);
    }

    private void Quick_Click(object? sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        var time = ((sender as Button)?.Tag as string) switch
        {
            "15" => now.AddMinutes(15),
            "60" => now.AddHours(1),
            "today17" => DateTime.Today.AddHours(17) > now ? DateTime.Today.AddHours(17) : DateTime.Today.AddDays(1).AddHours(17),
            _ => DateTime.Today.AddDays(1).AddHours(9),
        };
        HasTimeBox.IsChecked = true;
        DateBox.SelectedDate = time.Date;
        TimeBox.Text = time.ToString("HH:mm");
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (EnteredText.Length == 0)
        {
            ShowError("Напишите, о чём напомнить.");
            return;
        }
        if (HasTimeBox.IsChecked == true)
        {
            if (DateBox.SelectedDate is not { } date || Reminder.ParseTimeOfDay(TimeBox.Text) is not { } time)
            {
                ShowError("Укажите дату и время, например 14:30.");
                return;
            }
            DueAt = date.Date + time;
        }
        else
        {
            DueAt = null;
        }
        Saved = true;
        Close();
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
