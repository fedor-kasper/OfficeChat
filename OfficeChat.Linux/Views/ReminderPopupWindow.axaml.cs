using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using OfficeChat.Models;

namespace OfficeChat.Views;

/// <summary>
/// Сработавшее напоминание в стопке уведомлений. Цвет — по важности; критическое нельзя закрыть
/// крестиком — только «Готово» или «Отложить».
/// </summary>
public partial class ReminderPopupWindow : Window, IStackedPopup
{
    public Reminder Reminder { get; }

    public bool IsClosing { get; private set; }

    public event Action<ReminderPopupWindow>? DoneRequested;
    public event Action<ReminderPopupWindow, Control>? SnoozeRequested;
    public event Action<ReminderPopupWindow>? OpenRequested;
    public event Action<ReminderPopupWindow>? Dismissed;

    public ReminderPopupWindow() : this(null!) { }

    public ReminderPopupWindow(Reminder reminder)
    {
        InitializeComponent();
        Reminder = reminder;
        if (reminder == null) return; // конструктор для дизайнера

        Refresh();
        reminder.PropertyChanged += OnReminderChanged;
        Closed += (_, _) => reminder.PropertyChanged -= OnReminderChanged;
        Popups.SetupFade(this);
    }

    private void OnReminderChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var color = new SolidColorBrush(Color.Parse(Reminder.Color));
        Frame.BorderBrush = color;
        Frame.BorderThickness = new Thickness(Reminder.Importance >= 3 ? 4 : 2);
        Header.Background = new SolidColorBrush(Color.Parse(Reminder.Tint));
        TitleText.Text = $"Напоминание · {Reminder.ImportanceName}";
        SubtitleText.Text = string.Join(" · ", new[] { Reminder.DueText, Reminder.FromText }.Where(t => t.Length > 0));
        BodyText.Text = Reminder.Text;
        CloseButton.IsVisible = Reminder.CanClosePopup;
        Width = Reminder.Importance <= 1 ? 340 : 400;
        BodyText.FontSize = Reminder.Importance <= 1 ? 13 : 15;
    }

    public void FadeOutAndClose()
    {
        if (IsClosing) return;
        IsClosing = true;
        Popups.FadeOutAndClose(this);
    }

    private void Done_Click(object? sender, RoutedEventArgs e) => DoneRequested?.Invoke(this);

    private void Snooze_Click(object? sender, RoutedEventArgs e) => SnoozeRequested?.Invoke(this, SnoozeButton);

    private void Open_Click(object? sender, RoutedEventArgs e) => OpenRequested?.Invoke(this);

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke(this);
        FadeOutAndClose();
    }
}
