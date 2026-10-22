using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Путь к файлу изображения → уменьшенная картинка для ленты (файл сразу закрывается).</summary>
public sealed class ImageThumbnailConverter : IValueConverter
{
    public int DecodeWidth { get; set; } = 640;

    private static readonly Dictionary<string, Bitmap> Cache = new();
    private static readonly Queue<string> CacheOrder = new();
    private const int CacheSize = 150;

    // Файлы меньше этого — стикеры и значки: их проще прочитать целиком.
    private const int SmallFileBytes = 160 * 1024;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        if (TryGetCached(path, DecodeWidth) is { } cached) return cached;
        var image = Load(path, DecodeWidth);
        if (image != null) Remember(path, DecodeWidth, image);
        return image;
    }

    /// <summary>Уже загруженная миниатюра (без чтения диска) — или null.</summary>
    public static Bitmap? TryGetCached(string path, int decodeWidth)
    {
        lock (Cache) return Cache.GetValueOrDefault($"{decodeWidth}|{path}");
    }

    public static void Remember(string path, int decodeWidth, Bitmap image)
    {
        var key = $"{decodeWidth}|{path}";
        lock (Cache)
        {
            if (!Cache.TryAdd(key, image)) return;
            CacheOrder.Enqueue(key);
            while (CacheOrder.Count > CacheSize)
                Cache.Remove(CacheOrder.Dequeue());
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Загружает изображение с диска; 0 — в полном размере. null — файла нет или формат не поддерживается.</summary>
    public static Bitmap? Load(string path, int decodeWidth)
    {
        try
        {
            return File.Exists(path) ? FromBytes(File.ReadAllBytes(path), decodeWidth) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось прочитать изображение {path}", ex);
            return null;
        }
    }

    public static Bitmap? FromBytes(byte[] data, int decodeWidth)
    {
        try
        {
            // Большой файл — это фото или снимок экрана: сразу уменьшенное (без декодирования в полном размере —
            // это и было медленным при открытии переписки с картинками). Маленький (стикер, значок) — как есть,
            // а уменьшаем, только если он всё-таки шире нужного.
            if (decodeWidth > 0 && data.Length > SmallFileBytes)
                return Bitmap.DecodeToWidth(new MemoryStream(data), decodeWidth, BitmapInterpolationMode.HighQuality);
            var full = new Bitmap(new MemoryStream(data));
            if (decodeWidth <= 0 || full.PixelSize.Width <= decodeWidth) return full;
            full.Dispose();
            return Bitmap.DecodeToWidth(new MemoryStream(data), decodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex)
        {
            Log.Warn("Формат изображения не поддерживается", ex);
            return null;
        }
    }
}
