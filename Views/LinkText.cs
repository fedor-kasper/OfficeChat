using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Текст сообщения с кликабельными ссылками для TextBlock:
/// &lt;TextBlock local:LinkText.Source="{Binding Text}" /&gt;.
/// У ссылки — своё меню (открыть, копировать); у остального текста — меню сообщения (там есть «Копировать текст»).
/// </summary>
public static class LinkText
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(LinkText), new PropertyMetadata(null, OnSourceChanged));

    public static string? GetSource(DependencyObject element) => (string?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, string? value) => element.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        var text = e.NewValue as string ?? "";

        block.Inlines.Clear();
        foreach (var segment in LinkParser.Split(text))
        {
            if (!segment.IsLink)
            {
                block.Inlines.Add(new Run(segment.Text));
                continue;
            }

            var link = segment.Link!;
            var hyperlink = new Hyperlink(new Run(segment.Text))
            {
                ToolTip = link,
                Foreground = LinkBrush,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            hyperlink.Click += (_, _) => LinkOpener.Open(link, Window.GetWindow(block));
            hyperlink.ContextMenu = Menu(
                ("Открыть ссылку", () => LinkOpener.Open(link, Window.GetWindow(block))),
                ("Копировать ссылку", () => CopyText(segment.Text)));
            block.Inlines.Add(hyperlink);
        }
    }

    private static readonly System.Windows.Media.Brush LinkBrush = CreateLinkBrush();

    private static System.Windows.Media.Brush CreateLinkBrush()
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x4E, 0xD8));
        brush.Freeze();
        return brush;
    }

    private static ContextMenu Menu(params (string Header, Action Action)[] items)
    {
        var menu = new ContextMenu();
        foreach (var (header, action) in items)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        return menu;
    }

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Log.Warn("Буфер обмена занят другой программой", ex);
        }
    }
}
