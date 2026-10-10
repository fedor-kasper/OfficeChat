using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OfficeChat.Views;

/// <summary>Выбор даты и времени, до которого отложить напоминание.</summary>
public partial class TimePickWindow : Window
{
    /// <summary>Выбранное время; null — отменили.</summary>
    public DateTime? Picked { get; private set; }

    public TimePickWindow() : this(DateTime.Now.AddHours(1)) { }

    public TimePickWindow(DateTime initial)
    {
        InitializeComponent();
        Icon = Platform.AppIcon.Window;
        DateBox.SelectedDate = initial.Date;
        TimeBox.Text = initial.ToString("HH:mm");
        Opened += (_, _) => TimeBox.Focus();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (DateBox.SelectedDate is not { } date || OfficeChat.Models.Reminder.ParseTimeOfDay(TimeBox.Text) is not { } time)
        {
            ErrorText.Text = "Укажите дату и время, например 14:30.";
            ErrorText.IsVisible = true;
            return;
        }
        var picked = date.Date + time;
        if (picked <= DateTime.Now)
        {
            ErrorText.Text = "Это время уже прошло.";
            ErrorText.IsVisible = true;
            return;
        }
        Picked = picked;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
