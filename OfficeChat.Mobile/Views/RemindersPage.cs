using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Mobile.Views;

/// <summary>Напоминания: активные (по важности и сроку) и выполненные; «+» — новое.</summary>
public sealed class RemindersPage : Page
{
    private readonly StackPanel _list = new() { Margin = new Avalonia.Thickness(12), Spacing = 10 };
    private readonly ToggleButton _activeTab = new() { Content = "Активные", IsChecked = true };
    private readonly ToggleButton _doneTab = new() { Content = "Выполненные" };
    private bool _showDone;

    public RemindersPage()
    {
        foreach (var tab in new[] { _activeTab, _doneTab })
        {
            tab.HorizontalAlignment = HorizontalAlignment.Stretch;
            tab.HorizontalContentAlignment = HorizontalAlignment.Center;
            tab.Padding = new Avalonia.Thickness(0, 8);
        }
        _activeTab.Click += (_, _) => ShowDone(false);
        _doneTab.Click += (_, _) => ShowDone(true);
        var tabs = new UniformGrid { Columns = 2, Margin = new Avalonia.Thickness(12, 10, 12, 0), Children = { _activeTab, _doneTab } };
        var body = new DockPanel();
        DockPanel.SetDock(tabs, Dock.Top);
        body.Children.Add(tabs);
        body.Children.Add(new ScrollViewer { Content = _list });
        Content = PageHeader.Wrap(this, "Напоминания", body,
            PageHeader.HeaderButton("+", () => Shell.Push(new ReminderEditPage(null, "", null))));
    }

    public override void OnAppearing()
    {
        if (MobileSession.Reminders is { } reminders) reminders.Changed += Refresh;
        Refresh();
    }

    public override void OnDisappearing()
    {
        if (MobileSession.Reminders is { } reminders) reminders.Changed -= Refresh;
    }

    private void ShowDone(bool done)
    {
        _showDone = done;
        _activeTab.IsChecked = !done;
        _doneTab.IsChecked = done;
        Refresh();
    }

    private void Refresh()
    {
        _list.Children.Clear();
        if (MobileSession.Reminders is not { } service) return;
        var items = _showDone ? service.Done : service.Active;
        _activeTab.Content = $"Активные ({service.Active.Count})";
        _doneTab.Content = $"Выполненные ({service.Done.Count})";
        if (items.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = _showDone ? "Выполненных пока нет." : "Напоминаний нет. Нажмите «+» вверху — или долгое нажатие " +
                                                            "на сообщение → «Напомнить об этом».",
                TextWrapping = TextWrapping.Wrap, Foreground = PageHeader.Resource("MutedTextBrush"),
                Margin = new Avalonia.Thickness(8, 24), TextAlignment = TextAlignment.Center,
            });
            return;
        }
        foreach (var reminder in items) _list.Children.Add(Card(service, reminder));
    }

    private Control Card(ReminderService service, Reminder reminder)
    {
        var info = new StackPanel { Spacing = 4 };
        info.Children.Add(new SelectableTextBlock { Text = reminder.Text, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var line = new WrapPanel();
        line.Children.Add(new Border
        {
            Background = SolidColorBrush.Parse(reminder.Color), CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(6, 1), Margin = new Avalonia.Thickness(0, 0, 8, 0),
            Child = new TextBlock { Text = reminder.ImportanceName, Foreground = Brushes.White, FontSize = 12 },
        });
        line.Children.Add(new TextBlock
        {
            Text = "⏰ " + reminder.DueText, FontSize = 13,
            Foreground = reminder.IsOverdue ? Brushes.Firebrick : PageHeader.Resource("MutedTextBrush"),
        });
        info.Children.Add(line);
        var extra = string.Join(" · ", new[] { reminder.RepeatText, reminder.HasParticipants ? "👥 " + reminder.ParticipantsText : "", reminder.FromText }
            .Where(t => t.Length > 0));
        if (extra.Length > 0)
            info.Children.Add(new TextBlock { Text = extra, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                Foreground = PageHeader.Resource("MutedTextBrush") });

        var buttons = new WrapPanel { Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        if (reminder.IsDone)
            buttons.Children.Add(Small("↺ Вернуть", () => service.Reopen(reminder)));
        else
        {
            buttons.Children.Add(Small("✓ Готово", () => service.Complete(reminder), "#16A34A"));
            var snooze = Small("⏱ Отложить", () => { });
            snooze.Click += (_, _) =>
            {
                var menu = new List<Dialogs.Action>();
                foreach (var (title, until) in ReminderService.SnoozeChoices())
                    menu.Add(new Dialogs.Action(title, () => service.Snooze(reminder, until)));
                Dialogs.Actions(this, null, menu);
            };
            buttons.Children.Add(snooze);
        }
        if (reminder.IsMine) buttons.Children.Add(Small("✏", () => Shell.Push(new ReminderEditPage(reminder, "", null))));
        buttons.Children.Add(Small("📋", async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(reminder.Text);
        }));
        buttons.Children.Add(Small("🗑", async () =>
        {
            if (await Dialogs.ConfirmAsync(this, reminder.IsMine && reminder.HasParticipants
                    ? "Удалить напоминание? У участников оно тоже пропадёт."
                    : "Удалить напоминание?", "Удалить"))
                service.Delete(reminder);
        }));
        info.Children.Add(buttons);

        return new Border
        {
            Background = SolidColorBrush.Parse(reminder.Tint), CornerRadius = new Avalonia.CornerRadius(10),
            BorderBrush = SolidColorBrush.Parse(reminder.Color), BorderThickness = new Avalonia.Thickness(4, 0, 0, 0),
            Padding = new Avalonia.Thickness(12, 10), Child = info, Opacity = reminder.IsDone ? 0.7 : 1,
        };
    }

    private static Button Small(string text, Action click, string? color = null)
    {
        var button = new Button { Content = text, Padding = new Avalonia.Thickness(12, 6), Margin = new Avalonia.Thickness(0, 0, 6, 6) };
        if (color != null)
        {
            button.Background = SolidColorBrush.Parse(color);
            button.Foreground = Brushes.White;
        }
        button.Click += (_, _) => click();
        return button;
    }
}
