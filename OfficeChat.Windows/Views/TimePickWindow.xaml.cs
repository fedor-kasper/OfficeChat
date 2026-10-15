using System.Windows;
using OfficeChat.Models;

namespace OfficeChat.Views;

/// <summary>Выбор даты и времени, до которого отложить напоминание.</summary>
public partial class TimePickWindow : Window
{
    /// <summary>Выбранное время; null — отменили.</summary>
    public DateTime? Picked { get; private set; }

    public TimePickWindow(DateTime initial)
    {
        InitializeComponent();
        DateBox.SelectedDate = initial.Date;
        TimeBox.Text = initial.ToString("HH:mm");
        TimeBox.SelectAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DateBox.SelectedDate is not { } date || Reminder.ParseTimeOfDay(TimeBox.Text) is not { } time)
        {
            ShowError("Укажите дату и время, например 14:30.");
            return;
        }
        var picked = date.Date + time;
        if (picked <= DateTime.Now)
        {
            ShowError("Это время уже прошло.");
            return;
        }
        Picked = picked;
        DialogResult = true;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
