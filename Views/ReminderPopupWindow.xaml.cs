using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OfficeChat.Models;

namespace OfficeChat.Views;

/// <summary>
/// Сработавшее напоминание в стопке уведомлений. Цвет — по важности; критическое нельзя закрыть
/// крестиком — только «Готово» или «Отложить».
/// </summary>
public partial class ReminderPopupWindow : Window, IStackedPopup
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(200));

    public Reminder Reminder { get; }

    public bool IsClosing { get; private set; }

    public event Action<ReminderPopupWindow>? DoneRequested;
    public event Action<ReminderPopupWindow, FrameworkElement>? SnoozeRequested;
    public event Action<ReminderPopupWindow>? OpenRequested;
    public event Action<ReminderPopupWindow>? Dismissed;

    public ReminderPopupWindow(Reminder reminder)
    {
        InitializeComponent();
        Reminder = reminder;
        Refresh();
        reminder.PropertyChanged += OnReminderChanged;
        Closed += (_, _) => reminder.PropertyChanged -= OnReminderChanged;
        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeDuration));
    }

    private void OnReminderChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var color = (Color)ColorConverter.ConvertFromString(Reminder.Color);
        Frame.BorderBrush = new SolidColorBrush(color);
        Frame.BorderThickness = new Thickness(Reminder.Importance >= 3 ? 4 : 2);
        Header.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Reminder.Tint));
        TitleText.Text = $"Напоминание · {Reminder.ImportanceName}";
        SubtitleText.Text = string.Join(" · ", new[] { Reminder.DueText, Reminder.FromText }.Where(t => t.Length > 0));
        BodyText.Text = Reminder.Text;
        CloseButton.Visibility = Reminder.CanClosePopup ? Visibility.Visible : Visibility.Collapsed;
        Width = Reminder.Importance <= 1 ? 360 : 420;
        BodyText.FontSize = Reminder.Importance <= 1 ? 13 : 15;
    }

    public void FadeOutAndClose()
    {
        if (IsClosing) return;
        IsClosing = true;
        var fade = new DoubleAnimation(0, FadeDuration);
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void Done_Click(object sender, RoutedEventArgs e) => DoneRequested?.Invoke(this);

    private void Snooze_Click(object sender, RoutedEventArgs e) => SnoozeRequested?.Invoke(this, SnoozeButton);

    private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke(this);
        FadeOutAndClose();
    }
}
