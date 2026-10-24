using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OfficeChat.Views;

/// <summary>Окна режима «Касса»: ввод строки и настройки кассы.</summary>
public static class CashDialogs
{
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

    /// <summary>Ввод строки. null — отменили (или оставили пустым, когда <paramref name="required"/>).</summary>
    public static string? Input(Window? owner, string title, string prompt, string initial, string okText, bool required = false)
    {
        string? result = null;
        var window = NewWindow(owner, title, 420);
        var box = new TextBox { Text = initial, MaxLength = 500, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 10, 0, 0) };
        var (buttons, ok) = Buttons(okText, window, () => result = box.Text.Trim());
        void Update() => ok.IsEnabled = !required || !string.IsNullOrWhiteSpace(box.Text);
        box.TextChanged += (_, _) => Update();
        Update();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, FontSize = 14 }, box, buttons },
        };
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        window.ShowDialog();
        return required && string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>Режим «Касса»: включить и код кассы. null — отменили.</summary>
    public static (bool Enabled, string Code)? Settings(Window? owner, bool enabled, string code, string status)
    {
        (bool, string)? result = null;
        var window = NewWindow(owner, "Режим «Касса»", 480);
        TextBlock Hint(string text) => new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 6, 0, 0),
        };
        var enable = new CheckBox { Content = "Включить режим «Касса»", IsChecked = enabled, FontSize = 14 };
        var codeBox = new TextBox { Text = code, MaxLength = 100, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 4, 0, 0) };
        var (buttons, _) = Buttons("Сохранить", window, () => result = (enable.IsChecked == true, codeBox.Text.Trim()));
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                enable,
                Hint("Слева появится список поставщиков с балансом: поставки (сколько по накладной и сколько выдали), " +
                     "возвраты и погашения. Излишки от округления копятся на балансе поставщика."),
                new TextBlock { Text = "Код кассы", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) },
                codeBox,
                Hint("Компьютеры с одинаковым кодом ведут общий журнал — по локальной сети, без сервера. Код — как пароль: " +
                     "без него чужие компьютеры записи не прочитают и не подделают. Без кода касса только на этом компьютере."),
            },
        };
        if (status.Length > 0) panel.Children.Add(Hint(status));
        panel.Children.Add(buttons);
        window.Content = panel;
        window.ShowDialog();
        return result;
    }

    private static Window NewWindow(Window? owner, string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
        Owner = owner is { IsVisible: true } ? owner : null,
        WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
    };

    private static (StackPanel Panel, Button Ok) Buttons(string okText, Window window, Action onOk)
    {
        var ok = new Button { Content = okText, MinWidth = 100, Padding = new Thickness(10, 4, 10, 4), IsDefault = true };
        var cancel = new Button
        {
            Content = "Отмена", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), IsCancel = true, Margin = new Thickness(8, 0, 0, 0),
        };
        ok.Click += (_, _) =>
        {
            onOk();
            window.Close();
        };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0),
            Children = { ok, cancel },
        };
        return (panel, ok);
    }
}
