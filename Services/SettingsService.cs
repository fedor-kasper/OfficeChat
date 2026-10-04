using System.IO;
using System.Text.Json;

namespace OfficeChat.Services;

public sealed class AppSettings
{
    /// <summary>Постоянный идентификатор этого компьютера в чате.</summary>
    public Guid UserId { get; set; } = Guid.NewGuid();

    /// <summary>Имя, которое видят остальные.</summary>
    public string DisplayName { get; set; } = "";
}

/// <summary>Хранит настройки в %AppData%\OfficeChat\settings.json.</summary>
public static class SettingsService
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OfficeChat");

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
        catch (Exception)
        {
            // Повреждённый файл — начинаем с чистых настроек.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
