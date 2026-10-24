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

    /// <summary>Переписка без аккаунта перенесена в этот аккаунт (в настройках общей папки).</summary>
    public Guid? MigratedTo { get; set; }

    /// <summary>Перенести переписку без аккаунта уже предлагали (в настройках аккаунта) — больше не спрашиваем.</summary>
    public bool LegacyOffered { get; set; }

    /// <summary>Режим «Касса»: слева список поставщиков с балансом.</summary>
    public bool CashMode { get; set; }

    /// <summary>Код кассы: компьютеры с одинаковым кодом ведут общий журнал (по сети, без сервера).</summary>
    public string CashCode { get; set; } = "";

    /// <summary>Этот компьютер в журнале кассы (у каждой копии программы свой).</summary>
    public Guid CashDeviceId { get; set; } = Guid.NewGuid();
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

    private static string SettingsPath => PathIn(DataFolder);

    private static string PathIn(string folder) => Path.Combine(folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load() => LoadFrom(DataFolder);

    /// <summary>Настройки из другой папки (например, общей — при переносе переписки в аккаунт).</summary>
    public static AppSettings LoadFrom(string folder)
    {
        var path = PathIn(folder);
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (settings != null) return settings;
            }
        }
        catch (Exception ex)
        {
            // Повреждённый файл — начинаем с чистых настроек.
            Log.Warn($"Не удалось прочитать {path} — начинаем с чистых настроек", ex);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings) => SaveTo(DataFolder, settings);

    public static void SaveTo(string folder, AppSettings settings)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(PathIn(folder), JsonSerializer.Serialize(settings, JsonOptions));
    }
}
