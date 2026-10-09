using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Изображение, выбранное для отправки, — висит в полосе над полем ввода, пока не нажмут «Отправить».</summary>
public sealed class PendingImage
{
    public required byte[] Data { get; init; }
    public required string FileName { get; init; }
    public required BitmapImage Thumbnail { get; init; }

    public string SizeText => Data.Length >= 1024 * 1024
        ? $"{Data.Length / 1024.0 / 1024.0:0.#} МБ"
        : $"{Math.Max(1, Data.Length / 1024)} КБ";

    /// <summary>Из файла на диске. Возвращает null и причину, если файл не подходит.</summary>
    public static PendingImage? FromFile(string path, out string? error)
    {
        error = null;
        var name = Path.GetFileName(path);
        if (!ImageStore.IsImageFile(path))
        {
            error = $"«{name}» — не изображение";
            return null;
        }
        try
        {
            var info = new FileInfo(path);
            if (info.Length > ImageStore.MaxBytes)
            {
                error = $"«{name}» больше {ImageStore.MaxBytes / 1024 / 1024} МБ";
                return null;
            }
            return FromBytes(File.ReadAllBytes(path), name, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Не удалось прочитать «{name}»";
            Log.Warn(error, ex);
            return null;
        }
    }

    public static PendingImage? FromBytes(byte[] data, string fileName, out string? error)
    {
        error = null;
        if (data.Length > ImageStore.MaxBytes)
        {
            error = $"Изображение больше {ImageStore.MaxBytes / 1024 / 1024} МБ";
            return null;
        }
        var thumbnail = ImageThumbnailConverter.FromBytes(data, 160);
        if (thumbnail == null)
        {
            error = $"«{fileName}» не удалось открыть как изображение";
            return null;
        }
        return new PendingImage { Data = data, FileName = fileName, Thumbnail = thumbnail };
    }

    /// <summary>Картинка без файла (скриншот из буфера обмена) — сохраняем как PNG.</summary>
    public static PendingImage? FromBitmap(BitmapSource bitmap, out string? error)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return FromBytes(stream.ToArray(), $"Снимок {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png", out error);
    }

    /// <summary>
    /// Достаёт изображения из буфера обмена или перетаскивания: файлы, PNG (браузеры, «Ножницы»)
    /// или обычный рисунок (Print Screen). Пусто — изображений там нет.
    /// </summary>
    public static List<PendingImage> FromDataObject(IDataObject data, List<string> errors)
    {
        var result = new List<PendingImage>();
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var file in files.Where(f => File.Exists(f)))
                {
                    if (FromFile(file, out var error) is { } image) result.Add(image);
                    else if (error != null) errors.Add(error);
                }
                return result;
            }

            // PNG сохраняет прозрачность и не портит цвета, в отличие от устаревшего DIB.
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
            {
                if (FromBytes(png.ToArray(), $"Снимок {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png", out var error) is { } image)
                    result.Add(image);
                else if (error != null) errors.Add(error);
                return result;
            }

            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                if (FromBitmap(bitmap, out var error) is { } image) result.Add(image);
                else if (error != null) errors.Add(error);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                       or OutOfMemoryException or IOException)
        {
            // Буфер обмена занят другой программой или данные повреждены.
            Log.Warn("Не удалось получить изображение из буфера обмена/перетаскивания", ex);
            errors.Add("Не удалось получить изображение");
        }
        return result;
    }

    /// <summary>Есть ли в данных что-то похожее на изображение (для курсора при перетаскивании и для Ctrl+V).</summary>
    public static bool ContainsImage(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop))
                return data.GetData(DataFormats.FileDrop) is string[] files && files.Any(ImageStore.IsImageFile);
            return data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }
}
