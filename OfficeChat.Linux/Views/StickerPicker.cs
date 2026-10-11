using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Выбор стикера: вкладки наборов («Недавние», встроенные, свои, «＋» — добавить из папки)
/// и сетка картинок. Клик по стикеру — сразу отправка.
/// </summary>
public sealed class StickerPicker : UserControl
{
    private const string RecentId = "recent";
    private static readonly Dictionary<string, Bitmap?> Thumbnails = new();

    private readonly AppSettings _settings;
    private readonly WrapPanel _tabs = new() { Margin = new Thickness(0, 0, 0, 6), ItemSpacing = 3, LineSpacing = 3 };
    private readonly WrapPanel _grid = new();
    private readonly ScrollViewer _gridScroll;
    private readonly TextBlock _empty = new()
    {
        FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#6B7280")), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(12), IsVisible = false,
    };
    private List<StickerPack> _packs = new();
    private string _selected = "";

    public event Action<Sticker>? StickerChosen;
    public event Action? AddPackRequested;
    public event Action<StickerPack>? DeletePackRequested;

    public StickerPicker(AppSettings settings)
    {
        _settings = settings;
        Width = 372;
        Height = 330;
        _gridScroll = new ScrollViewer { Content = _grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        DockPanel.SetDock(_tabs, Dock.Top);
        Content = new DockPanel { Children = { _tabs, new Panel { Children = { _gridScroll, _empty } } } };
    }

    /// <summary>Перечитать наборы (перед каждым открытием: могли добавить или сохранить стикер).</summary>
    public void Reload()
    {
        _packs = StickerStore.LoadPacks();
        var hasRecent = StickerStore.Recent(_settings, _packs).Count > 0;
        if (_selected.Length == 0 || (_selected != RecentId && _packs.All(p => p.Id != _selected)) ||
            (_selected == RecentId && !hasRecent))
            _selected = hasRecent ? RecentId : _packs.FirstOrDefault()?.Id ?? "";

        _tabs.Children.Clear();
        if (hasRecent) _tabs.Children.Add(Tab(RecentId, "🕘", "Недавние", null));
        foreach (var pack in _packs)
            _tabs.Children.Add(Tab(pack.Id, pack.Title, pack.IsBuiltIn ? pack.Title : pack.Title + " — правый клик, чтобы удалить", pack));

        var add = new Button { Content = "＋", Padding = new Thickness(10, 3), FontSize = 13 };
        ToolTip.SetTip(add, "Добавить свой набор: папка с картинками (PNG, JPG, GIF, WEBP)");
        add.Click += (_, _) => AddPackRequested?.Invoke();
        _tabs.Children.Add(add);
        ShowSelected();
    }

    private ToggleButton Tab(string id, string text, string tip, StickerPack? pack)
    {
        var tab = new ToggleButton { Content = text, Padding = new Thickness(10, 3), FontSize = 12, IsChecked = id == _selected, Tag = id };
        ToolTip.SetTip(tab, tip);
        tab.Click += (_, _) =>
        {
            _selected = id;
            foreach (var other in _tabs.Children.OfType<ToggleButton>())
                other.IsChecked = (string?)other.Tag == id;
            ShowSelected();
        };
        if (pack is { IsBuiltIn: false })
        {
            var delete = new MenuItem { Header = $"Удалить набор «{pack.Title}»" };
            delete.Click += (_, _) => DeletePackRequested?.Invoke(pack);
            tab.ContextMenu = new ContextMenu { Items = { delete } };
        }
        return tab;
    }

    private void ShowSelected()
    {
        var stickers = _selected == RecentId
            ? StickerStore.Recent(_settings, _packs)
            : _packs.FirstOrDefault(p => p.Id == _selected)?.Stickers ?? (IReadOnlyList<Sticker>)Array.Empty<Sticker>();
        _grid.Children.Clear();
        foreach (var sticker in stickers)
        {
            var button = new Button
            {
                Width = 84, Height = 84, Padding = new Thickness(6), Margin = new Thickness(1),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = new Cursor(StandardCursorType.Hand),
                Content = new Image { Source = Thumbnail(sticker), Stretch = Stretch.Uniform },
            };
            ToolTip.SetTip(button, PrettyName(sticker.Name));
            button.Click += (_, _) => StickerChosen?.Invoke(sticker);
            _grid.Children.Add(button);
        }
        _empty.IsVisible = stickers.Count == 0;
        _empty.Text = _packs.Count == 0
            ? "Наборов пока нет. Нажмите «＋», чтобы добавить папку с картинками."
            : "Здесь пусто.";
        _gridScroll.Offset = default;
    }

    private static Bitmap? Thumbnail(Sticker sticker)
    {
        if (Thumbnails.TryGetValue(sticker.Id, out var cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            bitmap = ImageThumbnailConverter.FromBytes(sticker.Load(), 144);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось прочитать стикер {sticker.Id}", ex);
        }
        Thumbnails[sticker.Id] = bitmap;
        return bitmap;
    }

    /// <summary>«07_face_with_tears_of_joy» → «face with tears of joy».</summary>
    public static string PrettyName(string name)
    {
        var underscore = name.IndexOf('_');
        if (underscore > 0 && name[..underscore].All(char.IsDigit)) name = name[(underscore + 1)..];
        return name.Replace('_', ' ');
    }
}
