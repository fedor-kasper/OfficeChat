using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Текст сообщения с кликабельными ссылками: &lt;local:LinkTextBlock LinkedText="{Binding Text}" /&gt;.
/// Остаётся выделяемым (как SelectableTextBlock); ссылки синие с подчёркиванием, над ними курсор-рука,
/// клик (без выделения текста) открывает ссылку.
/// </summary>
public class LinkTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string?> LinkedTextProperty =
        AvaloniaProperty.Register<LinkTextBlock, string?>(nameof(LinkedText));

    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    // Где в тексте ссылки: [начало, конец) в символах и что открывать.
    private readonly List<(int Start, int End, string Link)> _links = new();

    /// <summary>Внешний вид и поведение — как у обычного SelectableTextBlock.</summary>
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public string? LinkedText
    {
        get => GetValue(LinkedTextProperty);
        set => SetValue(LinkedTextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LinkedTextProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        _links.Clear();
        var inlines = new InlineCollection();
        var position = 0;
        foreach (var segment in LinkParser.Split(LinkedText ?? ""))
        {
            var run = new Run(segment.Text);
            if (segment.IsLink)
            {
                run.Foreground = LinkBrush;
                run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                _links.Add((position, position + segment.Text.Length, segment.Link!));
            }
            inlines.Add(run);
            position += segment.Text.Length;
        }
        Inlines = inlines;
        ToolTip.SetTip(this, null);
    }

    /// <summary>Ссылка под точкой (в координатах элемента) или null.</summary>
    private string? LinkAt(Point point)
    {
        if (_links.Count == 0) return null;
        // Проверяем прямоугольники, которые занимает каждая ссылка (на перенесённой строке их несколько).
        // Флагу IsInside из HitTestPoint не доверяем: на второй и следующих строках он бывает ложным.
        var local = point - new Point(Padding.Left, Padding.Top);
        foreach (var (start, end, link) in _links)
            foreach (var rect in TextLayout.HitTestTextRange(start, end - start))
                if (rect.Contains(local))
                    return link;
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var link = LinkAt(e.GetPosition(this));
        Cursor = link != null ? HandCursor : TextCursor;
        ToolTip.SetTip(this, link);
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // Если человек выделял текст — это не клик по ссылке.
        if (e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd) return;
        if (LinkAt(e.GetPosition(this)) is { } link)
            await LinkOpener.OpenAsync(link, TopLevel.GetTopLevel(this) as Window);
    }
}
