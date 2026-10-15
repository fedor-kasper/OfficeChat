using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Выбор стикера: вкладки наборов («Недавние», встроенные, свои, «＋» — добавить из папки)
/// и сетка картинок. Клик по стикеру — сразу отправка.
/// </summary>
public sealed class StickerPicker : UserControl
{
    private const string RecentId = "recent";
    private static readonly Dictionary<string, BitmapImage?> Thumbnails = new();

    private readonly AppSettings _settings;
    private readonly WrapPanel _tabs = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly WrapPanel _grid = new();
    private readonly ScrollViewer _gridScroll;
    private readonly TextBlock _empty = new()
    {
        FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(12), Visibility = Visibility.Collapsed,
    };
    private List<StickerPack> _packs = new();
    private string _selected = "";

    public event Action<Sticker>? StickerChosen;
    public event Action? AddPackRequested;
    public event Action<StickerPack>? DeletePackRequested;

    public StickerPicker(AppSettings settings)
    {
        _settings = settings;
        _gridScroll = new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(_tabs, Dock.Top);
        Content = new Border
        {
            Width = 384, Height = 340, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8), Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.25 },
            Child = new DockPanel { Children = { _tabs, new Grid { Children = { _gridScroll, _empty } } } },
        };
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

        var add = new Button
        {
            Content = "＋", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 3, 3), FontSize = 12,
            ToolTip = "Добавить свой набор: папка с картинками (PNG, JPG, GIF, WEBP)",
        };
        add.Click += (_, _) => AddPackRequested?.Invoke();
        _tabs.Children.Add(add);
        ShowSelected();
    }

    private ToggleButton Tab(string id, string text, string tip, StickerPack? pack)
    {
        var tab = new ToggleButton
        {
            Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 3, 3), FontSize = 12,
            IsChecked = id == _selected, Tag = id, ToolTip = tip,
        };
        if (id == RecentId) tab.FontFamily = new FontFamily("Segoe UI Emoji");
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
            var image = new Image { Source = Thumbnail(sticker), Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var button = new Button
            {
                Width = 84, Height = 84, Padding = new Thickness(6), Margin = new Thickness(1),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                Content = image, ToolTip = PrettyName(sticker.Name),
            };
            button.Click += (_, _) => StickerChosen?.Invoke(sticker);
            _grid.Children.Add(button);
        }
        _empty.Visibility = stickers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = _packs.Count == 0
            ? "Наборов пока нет. Нажмите «＋», чтобы добавить папку с картинками."
            : "Здесь пусто.";
        _gridScroll.ScrollToTop();
    }

    private static BitmapImage? Thumbnail(Sticker sticker)
    {
        if (Thumbnails.TryGetValue(sticker.Id, out var cached)) return cached;
        BitmapImage? bitmap = null;
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
