using Microsoft.Win32;

namespace OfficeChat.Services;

/// <summary>Автозапуск через HKCU\...\Run — не требует прав администратора.</summary>
public static class AutoStartService
{
    /// <summary>Аргумент командной строки: запуститься свёрнутым в трей.</summary>
    public const string TrayArgument = "--tray";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OfficeChat";

    /// <summary>
    /// Включает или выключает автозапуск. Вызывается при каждом старте,
    /// чтобы путь в реестре оставался верным, даже если программу перенесли в другую папку.
    /// </summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled && Environment.ProcessPath is { } exePath)
                key.SetValue(ValueName, $"\"{exePath}\" {TrayArgument}");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Политики компьютера могут запрещать запись — работаем без автозапуска.
        }
    }
}
