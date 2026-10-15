using System.IO;
using System.Text.Json;

namespace OfficeChat.Services;

public sealed class AppSettings
{
    /// <summary>Постоянный идентификатор этого компьютера в чате.</summary>
    public Guid UserId { get; set; } = Guid.NewGuid();

    /// <summary>Имя, которое видят остальные.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Запускаться вместе с Windows (сразу в трей).</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Переписки, по которым не показывать всплывающие окна (по Id собеседника или группы).</summary>
    public List<Guid> MutedChats { get; set; } = new();

    /// <summary>Закреплённые переписки: всегда вверху списка (по Id собеседника или группы).</summary>
    public List<Guid> PinnedChats { get; set; } = new();

    /// <summary>Выбрали «работать без аккаунта» — окно входа при запуске не показываем.</summary>
    public bool NoAccount { get; set; }

    /// <summary>Недавно отправленные стикеры (по Id, последние — первыми).</summary>
    public List<string> RecentStickers { get; set; } = new();
}

/// <summary>
/// Хранит настройки в папке данных: без аккаунта — %AppData%\OfficeChat\settings.json (как раньше),
/// с аккаунтом — в его собственной папке %AppData%\OfficeChat\accounts\Id (история, файлы, настройки).
/// </summary>
public static class SettingsService
{
    /// <summary>Общая папка программы: журнал работы, список аккаунтов, данные без аккаунта.</summary>
    public static string RootFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create), "OfficeChat");

    /// <summary>Папка данных текущего аккаунта (без аккаунта — общая папка).</summary>
    public static string DataFolder { get; private set; } = RootFolder;

    /// <summary>
    /// Работать с данными в другой папке (папка аккаунта). Вызывать при запуске — до того,
    /// как что-либо прочитано или записано.
    /// </summary>
    public static void UseDataFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        DataFolder = folder;
    }

    private static string SettingsPath => Path.Combine(DataFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (settings != null) return settings;
            }
        }
        catch (Exception ex)
        {
            // Повреждённый файл — начинаем с чистых настроек.
            Log.Warn($"Не удалось прочитать {SettingsPath} — начинаем с чистых настроек", ex);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
