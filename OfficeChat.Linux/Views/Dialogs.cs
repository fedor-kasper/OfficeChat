using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OfficeChat.Views;

/// <summary>Простые окна-сообщения (в Avalonia нет MessageBox).</summary>
public static class Dialogs
{
    /// <summary>Сообщение с кнопкой «ОК».</summary>
    public static Task Info(Window? owner, string text, string title = "OfficeChat") =>
        Show(owner, text, title, yesNo: false);

    /// <summary>Вопрос «Да / Нет». true — «Да».</summary>
    public static Task<bool> Confirm(Window? owner, string text, string title = "OfficeChat") =>
        Show(owner, text, title, yesNo: true);

    private static async Task<bool> Show(Window? owner, string text, string title, bool yesNo)
    {
        var result = false;
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Icon = Platform.AppIcon.Window,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var ok = new Button { Content = yesNo ? "Да" : "ОК", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        ok.Click += (_, _) => { result = true; window.Close(); };
        buttons.Children.Add(ok);
        if (yesNo)
        {
            var no = new Button { Content = "Нет", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
            no.Click += (_, _) => window.Close();
            buttons.Children.Add(no);
        }

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 },
                buttons,
            },
        };

        if (owner is { IsVisible: true })
            await window.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }
        return result;
    }
}
