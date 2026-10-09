using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Путь к файлу изображения → уменьшенная картинка для ленты (как превью в Telegram).
/// Файл читается целиком и сразу закрывается, чтобы его можно было удалить или переписать.
/// </summary>
public sealed class ImageThumbnailConverter : IValueConverter
{
    /// <summary>Ширина декодирования: хватает для чёткого превью на экранах с увеличенным масштабом.</summary>
    public int DecodeWidth { get; set; } = 640;

    // Небольшой кэш, чтобы при прокрутке и переключении переписок не декодировать заново.
    private static readonly Dictionary<string, BitmapImage> Cache = new();
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
    public static BitmapImage? Load(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return FromBytes(File.ReadAllBytes(path), decodeWidth);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось прочитать изображение {path}", ex);
            return null;
        }
    }

    public static BitmapImage? FromBytes(byte[] data, int decodeWidth)
    {
        try
        {
            // Узнаём исходную ширину, не декодируя картинку целиком:
            // маленькие изображения не растягиваем — декодируем не шире оригинала.
            int originalWidth;
            using (var header = new MemoryStream(data))
                originalWidth = BitmapFrame.Create(header, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(data);
            if (decodeWidth > 0 && originalWidth > decodeWidth) image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException
                                       or InvalidOperationException or IOException)
        {
            Log.Warn("Формат изображения не поддерживается", ex);
            return null;
        }
    }
}
