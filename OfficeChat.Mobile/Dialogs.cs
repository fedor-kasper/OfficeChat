using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace OfficeChat.Mobile;

/// <summary>Вопросы и ввод текста поверх экрана (на телефоне нет отдельных окон).</summary>
public static class Dialogs
{
    private static ShellView? ShellOf(Control control) =>
        control as ShellView ?? (control as Page)?.Shell ?? control.FindAncestorOfType<ShellView>();

    public static Task InfoAsync(Control owner, string text) => ShowAsync(owner, text, "OK", null, null).ContinueWith(_ => { });

    public static async Task<bool> ConfirmAsync(Control owner, string text, string yes = "Да", string no = "Отмена") =>
        (await ShowAsync(owner, text, yes, no, null)).Ok;

    /// <summary>Ввод строки. null — отменили.</summary>
    public static async Task<string?> InputAsync(Control owner, string text, string initial = "", string ok = "Сохранить",
        string watermark = "", bool password = false)
    {
        var box = new TextBox { Text = initial, Watermark = watermark, PasswordChar = password ? '•' : default };
        var result = await ShowAsync(owner, text, ok, "Отмена", box);
        return result.Ok ? box.Text ?? "" : null;
    }

    /// <summary>Своё содержимое (несколько полей) с кнопками «OK» и «Отмена».</summary>
    public static async Task<bool> FormAsync(Control owner, string text, Control content, string ok = "Сохранить") =>
        (await ShowAsync(owner, text, ok, "Отмена", content)).Ok;

    private static Task<(bool Ok, object? _)> ShowAsync(Control owner, string text, string ok, string? cancel, Control? content)
    {
        var shell = ShellOf(owner) ?? throw new InvalidOperationException("Нет экрана для диалога");
        var done = new TaskCompletionSource<(bool, object?)>();
        var panel = new StackPanel { Spacing = 14 };
        if (text.Length > 0) panel.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 16 });
        if (content != null) panel.Children.Add(content);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(buttons);

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)),
            Child = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(20),
                Margin = new Thickness(20),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new ScrollViewer { Content = panel, MaxHeight = 640 },
            },
        };
        void Close(bool result)
        {
            shell.CloseOverlay(overlay);
            done.TrySetResult((result, null));
        }
        if (cancel != null)
        {
            var no = new Button { Content = cancel, Padding = new Thickness(16, 10) };
            no.Click += (_, _) => Close(false);
            buttons.Children.Add(no);
        }
        var yes = new Button
        {
            Content = ok, Padding = new Thickness(16, 10), Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.Parse("#2563EB")), IsDefault = true,
        };
        yes.Click += (_, _) => Close(true);
        buttons.Children.Add(yes);
        shell.ShowOverlay(overlay, () => Close(false));
        (content as TextBox)?.Focus();
        return done.Task;
    }
}
