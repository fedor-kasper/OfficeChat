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

    /// <summary>Ввод строки. null — отменили (или оставили пустым, когда <paramref name="required"/>).</summary>
    public static async Task<string?> Input(Window owner, string title, string prompt, string initial, string okText,
        bool required = false)
    {
        string? result = null;
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Platform.AppIcon.Window,
        };
        var box = new TextBox { Text = initial, MaxLength = 500 };
        var ok = new Button { Content = okText, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        void Update() => ok.IsEnabled = !required || !string.IsNullOrWhiteSpace(box.Text);
        box.TextChanged += (_, _) => Update();
        Update();
        ok.Click += (_, _) =>
        {
            result = (box.Text ?? "").Trim();
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, FontSize = 14 },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0), Children = { ok, cancel },
                },
            },
        };
        window.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        await window.ShowDialog(owner);
        return required && string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>Режим «Касса»: включить и код кассы. null — отменили.</summary>
    public static async Task<(bool Enabled, string Code)?> CashSettings(Window owner, bool enabled, string code, string status)
    {
        (bool, string)? result = null;
        var window = new Window
        {
            Title = "Режим «Касса»",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Platform.AppIcon.Window,
        };
        TextBlock Hint(string text) => new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
        };
        var enable = new CheckBox { Content = "Включить режим «Касса»", IsChecked = enabled, FontSize = 14 };
        var codeBox = new TextBox { Text = code, MaxLength = 100, Watermark = "например: магазин-ленина-5-касса" };
        var ok = new Button { Content = "Сохранить", MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        ok.Click += (_, _) =>
        {
            result = (enable.IsChecked == true, (codeBox.Text ?? "").Trim());
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children =
            {
                enable,
                Hint("Слева появится список поставщиков с балансом: поставки (сколько по накладной и сколько выдали), " +
                     "возвраты и погашения. Излишки от округления копятся на балансе поставщика."),
                new TextBlock { Text = "Код кассы", FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                codeBox,
                Hint("Компьютеры с одинаковым кодом ведут общий журнал — по локальной сети, без сервера. Код — как пароль: " +
                     "без него чужие компьютеры записи не прочитают и не подделают. Без кода касса только на этом компьютере."),
            },
        };
        if (status.Length > 0) panel.Children.Add(Hint(status));
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0), Children = { ok, cancel },
        });
        window.Content = panel;
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
