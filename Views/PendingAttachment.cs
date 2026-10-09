using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Вложение, выбранное для отправки, — висит в полосе над полем ввода, пока не нажмут «Отправить».
/// Изображение до 20 МБ уходит как фото (с превью), всё остальное — как файл.
/// </summary>
public sealed class PendingAttachment
{
    /// <summary>Байты изображения (только для изображений; файлы не читаются в память).</summary>
    public byte[]? Data { get; private init; }

    /// <summary>Путь к исходному файлу (только для файлов).</summary>
    public string? SourcePath { get; private init; }

    public required string FileName { get; init; }
    public required long Size { get; init; }
    public BitmapImage? Thumbnail { get; private init; }

    public bool IsImage => Data != null;
    public bool IsFile => !IsImage;

    public string SizeText => FileStore.SizeText(Size);

    /// <summary>Значок типа файла (берём из модели сообщения, чтобы совпадал с лентой).</summary>
    public string Icon => new Models.ChatMessage
    {
        Id = Guid.Empty, IsOutgoing = true, Text = "", Timestamp = default, FileName = FileName,
    }.FileIcon;

    /// <summary>Из файла на диске. Возвращает null и причину, если файл не подходит.</summary>
    public static PendingAttachment? FromFile(string path, out string? error)
    {
        error = null;
        var name = Path.GetFileName(path);
        try
        {
            var size = new FileInfo(path).Length;
            if (size > FileStore.MaxBytes)
            {
                error = $"«{name}» больше {FileStore.SizeText(FileStore.MaxBytes)}";
                return null;
            }

            // Небольшие изображения отправляем как фото с превью.
            if (ImageStore.IsImageFile(path) && size <= ImageStore.MaxBytes)
            {
                var data = File.ReadAllBytes(path);
                if (ImageThumbnailConverter.FromBytes(data, 160) is { } thumbnail)
                    return new PendingAttachment { Data = data, FileName = name, Size = size, Thumbnail = thumbnail };
            }
            return new PendingAttachment { SourcePath = path, FileName = name, Size = size };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Не удалось прочитать «{name}»";
            Log.Warn(error, ex);
            return null;
        }
    }

    public static PendingAttachment? FromImageBytes(byte[] data, string fileName, out string? error)
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
        return new PendingAttachment { Data = data, FileName = fileName, Size = data.Length, Thumbnail = thumbnail };
    }

    /// <summary>Картинка без файла (скриншот из буфера обмена) — сохраняем как PNG.</summary>
    public static PendingAttachment? FromBitmap(BitmapSource bitmap, out string? error)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return FromImageBytes(stream.ToArray(), ScreenshotName(), out error);
    }

    private static string ScreenshotName() => $"Снимок {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";

    public static List<PendingAttachment> FromFiles(IEnumerable<string> paths, List<string> errors)
    {
        var result = new List<PendingAttachment>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                errors.Add($"«{Path.GetFileName(path)}» — папка (папки отправлять нельзя, только файлы)");
                continue;
            }
            if (!File.Exists(path)) continue;
            if (FromFile(path, out var error) is { } item) result.Add(item);
            else if (error != null) errors.Add(error);
        }
        return result;
    }

    /// <summary>
    /// Достаёт вложения из буфера обмена или перетаскивания: файлы любого типа, PNG (браузеры, «Ножницы»)
    /// или обычный рисунок (Print Screen).
    /// </summary>
    public static List<PendingAttachment> FromDataObject(IDataObject data, List<string> errors)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
                return FromFiles(files, errors);

            // PNG сохраняет прозрачность и не портит цвета, в отличие от устаревшего DIB.
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
                return Single(FromImageBytes(png.ToArray(), ScreenshotName(), out var error), error, errors);

            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
                return Single(FromBitmap(bitmap, out var error), error, errors);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                       or OutOfMemoryException or IOException)
        {
            // Буфер обмена занят другой программой или данные повреждены.
            Log.Warn("Не удалось получить вложение из буфера обмена/перетаскивания", ex);
            errors.Add("Не удалось получить файл");
        }
        return new List<PendingAttachment>();
    }

    private static List<PendingAttachment> Single(PendingAttachment? item, string? error, List<string> errors)
    {
        if (item != null) return new List<PendingAttachment> { item };
        if (error != null) errors.Add(error);
        return new List<PendingAttachment>();
    }

    /// <summary>Есть ли в данных файлы или изображение (для курсора при перетаскивании и для Ctrl+V).</summary>
    public static bool ContainsAttachment(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop))
                return data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };
            return data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }
}
