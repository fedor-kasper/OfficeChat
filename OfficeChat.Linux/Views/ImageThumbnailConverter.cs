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

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        var key = $"{DecodeWidth}|{path}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var image = Load(path, DecodeWidth);
        if (image == null) return null;
        Cache[key] = image;
        CacheOrder.Enqueue(key);
        while (CacheOrder.Count > CacheSize)
            Cache.Remove(CacheOrder.Dequeue());
        return image;
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
            var full = new Bitmap(new MemoryStream(data));
            // Маленькие изображения не растягиваем: уменьшаем только то, что шире нужного.
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
