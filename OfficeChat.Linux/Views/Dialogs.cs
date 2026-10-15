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

    /// <summary>Смена пароля: (текущий, новый) или (null, null), если отменили.</summary>
    public static async Task<(string? Old, string? New)> ChangePassword(Window owner)
    {
        (string?, string?) result = (null, null);
        var window = new Window
        {
            Title = "Смена пароля",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Platform.AppIcon.Window,
        };
        TextBox Field() => new() { PasswordChar = '•' };
        TextBlock Label(string text) => new() { Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) };
        var current = Field();
        var fresh = Field();
        var repeat = Field();
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var ok = new Button { Content = "Сменить", MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        ok.Click += (_, _) =>
        {
            var newPassword = fresh.Text ?? "";
            error.IsVisible = true;
            if (newPassword.Length < Protocol.ServerProtocol.MinPasswordLength)
                error.Text = $"Новый пароль — не короче {Protocol.ServerProtocol.MinPasswordLength} символов";
            else if (newPassword != repeat.Text)
                error.Text = "Пароли не совпадают";
            else
            {
                result = (current.Text ?? "", newPassword);
                window.Close();
            }
        };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                Label("Текущий пароль"), current, Label("Новый пароль"), fresh, Label("Новый пароль ещё раз"), repeat,
                error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel },
                },
            },
        };
        window.Opened += (_, _) => current.Focus();
        await window.ShowDialog(owner);
        return result;
    }

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
