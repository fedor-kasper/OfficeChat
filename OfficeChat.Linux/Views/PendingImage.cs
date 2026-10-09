using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Изображение, выбранное для отправки, — висит в полосе над полем ввода, пока не нажмут «Отправить».</summary>
public sealed class PendingImage
{
    public required byte[] Data { get; init; }
    public required string FileName { get; init; }
    public required Bitmap Thumbnail { get; init; }

    public string SizeText => Data.Length >= 1024 * 1024
        ? $"{Data.Length / 1024.0 / 1024.0:0.#} МБ"
        : $"{Math.Max(1, Data.Length / 1024)} КБ";

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
            if (new FileInfo(path).Length > ImageStore.MaxBytes)
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

    public static List<PendingImage> FromStorageItems(IEnumerable<IStorageItem> items, List<string> errors)
    {
        var result = new List<PendingImage>();
        foreach (var item in items)
        {
            if (item.TryGetLocalPath() is not { } path || !File.Exists(path)) continue;
            if (FromFile(path, out var error) is { } image) result.Add(image);
            else if (error != null) errors.Add(error);
        }
        return result;
    }

    public static bool HasImageFiles(IDataTransfer data) =>
        data.TryGetFiles()?.Any(f => f.TryGetLocalPath() is { } p && ImageStore.IsImageFile(p)) == true;

    /// <summary>PNG как есть, байтами — без перекодирования (скриншоты, «Копировать изображение»).</summary>
    private static readonly DataFormat<byte[]> PngFormat = DataFormat.CreateBytesPlatformFormat("image/png");

    /// <summary>
    /// Изображения из буфера обмена: скопированные файлы или сама картинка (скриншот).
    /// null — изображений там нет (тогда вставляем текст как обычно).
    /// </summary>
    public static async Task<List<PendingImage>?> FromClipboardAsync(IClipboard clipboard, List<string> errors)
    {
        var data = await clipboard.TryGetDataAsync();
        if (data == null) return null;

        if (data.Contains(DataFormat.File) && await data.TryGetFilesAsync() is { } files &&
            files.Any(f => f.TryGetLocalPath() is { } p && ImageStore.IsImageFile(p)))
            return FromStorageItems(files, errors);

        // Если в буфере есть текст (офисные программы кладут рядом и картинку) — это вставка текста.
        if (data.Contains(DataFormat.Text))
            return null;

        var name = $"Снимок {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";
        if (data.Contains(PngFormat) && await data.TryGetValueAsync(PngFormat) is { Length: > 0 } png)
            return Single(FromBytes(png, name, out var error), error, errors);

        if (data.Contains(DataFormat.Bitmap) && await data.TryGetBitmapAsync() is { } bitmap)
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return Single(FromBytes(stream.ToArray(), name, out var error), error, errors);
        }
        return null;
    }

    private static List<PendingImage> Single(PendingImage? image, string? error, List<string> errors)
    {
        if (image != null) return new List<PendingImage> { image };
        if (error != null) errors.Add(error);
        return new List<PendingImage>();
    }
}
