using OfficeChat.Services;

namespace OfficeChat.Platform;

/// <summary>
/// Интеграция с рабочим столом по стандартам freedesktop (Cinnamon, MATE, Xfce, GNOME, KDE):
/// пункт в меню приложений, значок и автозапуск при входе в систему.
/// </summary>
public static class DesktopIntegration
{
    public const string TrayArgument = "--tray";

    private static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    private static string ConfigHome => Environment.GetFolderPath(
        Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);

    private static string AutostartFile => Path.Combine(ConfigHome, "autostart", "officechat.desktop");
    private static string MenuFile => Path.Combine(DataHome, "applications", "officechat.desktop");
    private static string IconFile => Path.Combine(DataHome, "icons", "hicolor", "256x256", "apps", "officechat.png");

    /// <summary>
    /// Что запускать. Внутри AppImage программа работает из временной папки,
    /// а запускать нужно сам файл .AppImage — его путь даёт переменная APPIMAGE.
    /// </summary>
    public static string? ExecutablePath =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath;

    /// <summary>Включает или выключает автозапуск. Вызывается при каждом старте — путь к программе мог смениться.</summary>
    public static void ApplyAutoStart(bool enabled)
    {
        try
        {
            if (enabled && ExecutablePath is { } exe)
                WriteDesktopFile(AutostartFile, exe, TrayArgument, autostart: true);
            else if (File.Exists(AutostartFile))
                File.Delete(AutostartFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Не удалось настроить автозапуск", ex);
        }
    }

    /// <summary>
    /// При запуске из AppImage добавляет программу в меню приложений со значком,
    /// чтобы дальше запускать её оттуда, а не искать файл.
    /// </summary>
    public static void EnsureMenuEntry()
    {
        if (Environment.GetEnvironmentVariable("APPIMAGE") is not { Length: > 0 } appImage) return;
        try
        {
            if (Environment.GetEnvironmentVariable("APPDIR") is { Length: > 0 } appDir &&
                File.Exists(Path.Combine(appDir, "officechat.png")))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(IconFile)!);
                File.Copy(Path.Combine(appDir, "officechat.png"), IconFile, overwrite: true);
            }
            WriteDesktopFile(MenuFile, appImage, "", autostart: false);
            Log.Info($"Пункт меню приложений: {MenuFile}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Не удалось добавить программу в меню приложений", ex);
        }
    }

    private static void WriteDesktopFile(string path, string exe, string arguments, bool autostart)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var icon = File.Exists(IconFile) ? IconFile : "officechat";
        var exec = $"\"{exe}\" {arguments}".TrimEnd();
        File.WriteAllText(path, $"""
            [Desktop Entry]
            Type=Application
            Name=OfficeChat
            Comment=Локальный чат для офиса
            Exec={exec}
            Icon={icon}
            Terminal=false
            Categories=Network;Chat;
            StartupWMClass=officechat
            {(autostart ? "X-GNOME-Autostart-enabled=true" : "")}
            """);
    }
}
