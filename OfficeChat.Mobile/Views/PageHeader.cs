using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OfficeChat.Mobile.Views;

/// <summary>Синяя полоса вверху страницы: «←», заголовок, кнопки справа.</summary>
public static class PageHeader
{
    public static IBrush Resource(string key) => (IBrush)Avalonia.Application.Current!.FindResource(key)!;

    public static DockPanel Wrap(Page page, string title, Control content, params Control[] right)
    {
        var back = new Button { Content = "←", Classes = { "header" } };
        back.Click += (_, _) => page.Shell.Pop();
        var bar = new DockPanel();
        DockPanel.SetDock(back, Dock.Left);
        bar.Children.Add(back);
        foreach (var button in right)
        {
            DockPanel.SetDock(button, Dock.Right);
            bar.Children.Add(button);
        }
        bar.Children.Add(new TextBlock
        {
            Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var header = new Border { Background = Resource("HeaderBrush"), Padding = new Avalonia.Thickness(2, 8, 4, 8), Child = bar };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(content);
        return dock;
    }

    public static Button HeaderButton(string text, Action click)
    {
        var button = new Button { Content = text, Classes = { "header" } };
        button.Click += (_, _) => click();
        return button;
    }
}
