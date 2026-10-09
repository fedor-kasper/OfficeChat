using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
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
    public Bitmap? Thumbnail { get; private init; }

    public bool IsImage => Data != null;
    public bool IsFile => !IsImage;

    public string SizeText => FileStore.SizeText(Size);

    /// <summary>Значок типа файла (берём из модели сообщения, чтобы совпадал с лентой).</summary>
    public string Icon => new Models.ChatMessage
    {
        Id = Guid.Empty, IsOutgoing = true, Text = "", Timestamp = default, FileName = FileName,
    }.FileIcon;

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

    public static List<PendingAttachment> FromStorageItems(IEnumerable<IStorageItem> items, List<string> errors)
    {
        var result = new List<PendingAttachment>();
        foreach (var item in items)
        {
            if (item.TryGetLocalPath() is not { } path) continue;
            if (Directory.Exists(path))
            {
                errors.Add($"«{Path.GetFileName(path)}» — папка (папки отправлять нельзя, только файлы)");
                continue;
            }
            if (!File.Exists(path)) continue;
            if (FromFile(path, out var error) is { } attachment) result.Add(attachment);
            else if (error != null) errors.Add(error);
        }
        return result;
    }

    public static bool HasFiles(IDataTransfer data) => data.TryGetFiles() is { Length: > 0 };

    /// <summary>PNG как есть, байтами — без перекодирования (скриншоты, «Копировать изображение»).</summary>
    private static readonly DataFormat<byte[]> PngFormat = DataFormat.CreateBytesPlatformFormat("image/png");

    /// <summary>
    /// Вложения из буфера обмена: скопированные в файловом менеджере файлы или сама картинка (скриншот).
    /// null — вложений там нет (тогда вставляем текст как обычно).
    /// </summary>
    public static async Task<List<PendingAttachment>?> FromClipboardAsync(IClipboard clipboard, List<string> errors)
    {
        // Скопированные в файловом менеджере файлы (text/uri-list) — прямым запросом, это самый надёжный путь.
        if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files)
            return FromStorageItems(files, errors);

        var data = await clipboard.TryGetDataAsync();
        if (data == null) return null;

        // Если в буфере есть текст (офисные программы кладут рядом и картинку) — это вставка текста.
        if (data.Contains(DataFormat.Text))
            return null;

        var name = $"Снимок {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";
        if (data.Contains(PngFormat) && await data.TryGetValueAsync(PngFormat) is { Length: > 0 } png)
            return Single(FromImageBytes(png, name, out var error), error, errors);

        if (data.Contains(DataFormat.Bitmap) && await data.TryGetBitmapAsync() is { } bitmap)
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return Single(FromImageBytes(stream.ToArray(), name, out var error), error, errors);
        }
        return null;
    }

    private static List<PendingAttachment> Single(PendingAttachment? item, string? error, List<string> errors)
    {
        if (item != null) return new List<PendingAttachment> { item };
        if (error != null) errors.Add(error);
        return new List<PendingAttachment>();
    }
}
