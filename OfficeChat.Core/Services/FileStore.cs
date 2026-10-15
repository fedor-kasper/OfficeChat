using System.IO;

namespace OfficeChat.Services;

/// <summary>
/// Файлы из переписки: %AppData%\OfficeChat\files\{Id}\{исходное имя}.
/// Своя папка на каждый файл — чтобы сохранить исходное имя (и программа для открытия выбиралась по расширению),
/// даже если два разных файла называются одинаково.
/// </summary>
public static class FileStore
{
    /// <summary>Самый большой файл, который можно отправить.</summary>
    public const long MaxBytes = 1024L * 1024 * 1024;

    /// <summary>Сколько места оставлять свободным на диске при приёме.</summary>
    private const long ReserveBytes = 200L * 1024 * 1024;

    public static string Folder { get; } = Path.Combine(SettingsService.DataFolder, "files");

    private static string IncomingFolder => Path.Combine(Folder, "incoming");

    /// <summary>Новое место для файла с исходным именем.</summary>
    public static string NewPath(Guid id, string fileName)
    {
        var dir = Path.Combine(Folder, id.ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, SafeFileName(fileName));
    }

    /// <summary>Временный файл для принимаемого вложения. Бросает IOException, если на диске не хватает места.</summary>
    public static string NewIncomingTempFile(long size)
    {
        Directory.CreateDirectory(IncomingFolder);
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(IncomingFolder))!).AvailableFreeSpace;
            if (free < size + ReserveBytes)
                throw new IOException($"Недостаточно места на диске для приёма файла ({size / 1024 / 1024} МБ, свободно {free / 1024 / 1024} МБ).");
        }
        catch (ArgumentException)
        {
            // Не удалось определить диск — принимаем как есть.
        }
        return Path.Combine(IncomingFolder, Guid.NewGuid().ToString("N") + ".part");
    }

    /// <summary>Убирает из имени то, что недопустимо в имени файла (в Windows и Linux).</summary>
    public static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }))
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "file" : name;
    }

    /// <summary>Удаляет файл и его папку (если она из files\ и опустела).</summary>
    public static void Delete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        DeleteQuietly(path);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Path.GetDirectoryName(dir) == Folder && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось удалить папку файла {path}", ex);
        }
    }

    public static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось удалить {path}", ex);
        }
    }

    public static string SizeText(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.##} ГБ",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} МБ",
        _ => $"{Math.Max(1, bytes / 1024)} КБ",
    };
}
