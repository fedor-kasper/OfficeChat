using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using OfficeChat.Views;

namespace OfficeChat.Mobile;

/// <summary>
/// Миниатюра изображения из переписки, которая читается и декодируется в фоне: переписка открывается сразу,
/// а картинки появляются по мере готовности (на телефоне декодирование фото — самое долгое при открытии).
/// </summary>
public sealed class LazyImage : Image
{
    public static readonly StyledProperty<string?> PathProperty = AvaloniaProperty.Register<LazyImage, string?>(nameof(Path));

    /// <summary>Ширина, до которой уменьшать при декодировании.</summary>
    public int DecodeWidth { get; set; } = 640;

    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    private int _version;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PathProperty) Reload(change.GetNewValue<string?>());
    }

    private async void Reload(string? path)
    {
        var version = ++_version;
        if (string.IsNullOrEmpty(path))
        {
            Source = null;
            return;
        }
        if (ImageThumbnailConverter.TryGetCached(path, DecodeWidth) is { } cached)
        {
            Source = cached;
            return;
        }
        Source = null;
        var width = DecodeWidth;
        var image = await Task.Run(() => ImageThumbnailConverter.Load(path, width));
        if (image == null) return;
        ImageThumbnailConverter.Remember(path, width, image);
        // Пока грузили, сообщение в этом месте могло смениться.
        if (version == _version) await Dispatcher.UIThread.InvokeAsync(() => Source = image);
    }
}
