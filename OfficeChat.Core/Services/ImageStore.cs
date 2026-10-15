using System.IO;

namespace OfficeChat.Services;

/// <summary>
/// Файлы изображений из переписки: %AppData%\OfficeChat\images\{Id сообщения}{расширение}.
/// У каждого сообщения свой файл (даже у копий рассылки «Всем»), поэтому удаление одной
/// переписки не трогает другие.
/// </summary>
public static class ImageStore
{
    /// <summary>Самое большое изображение, которое можно отправить.</summary>
    public const long MaxBytes = 20 * 1024 * 1024;

    public static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp" };

    public static string Folder { get; } = Path.Combine(SettingsService.DataFolder, "images");

    public static bool IsImageFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Где лежит изображение сообщения. Расширение берём из исходного имени файла.</summary>
    public static string PathFor(Guid messageId, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (!IsImageFile(fileName)) extension = ".png";
        return Path.Combine(Folder, messageId.ToString("N") + extension.ToLowerInvariant());
    }

    public static string Save(Guid messageId, string fileName, byte[] data)
    {
        Directory.CreateDirectory(Folder);
        var path = PathFor(messageId, fileName);
        File.WriteAllBytes(path, data);
        return path;
    }

    public static void Delete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось удалить изображение {path}", ex);
        }
    }
}
