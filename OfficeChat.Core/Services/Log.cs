using System.Diagnostics;
using System.IO;
using System.Text;

namespace OfficeChat.Services;

/// <summary>
/// Простой лог в файл: %AppData%\OfficeChat\logs\officechat-ГГГГ-ММ-ДД.log (у каждого пользователя Windows свой).
/// Потокобезопасен, никогда не бросает исключений. Хранит логи за последние <see cref="KeepDays"/> дней.
/// </summary>
public static class Log
{
    private const int KeepDays = 14;

    private static readonly object Sync = new();

    public static string Folder { get; } = ResolveFolder();

    /// <summary>Файл сегодняшнего лога — его путь показываем пользователю при ошибке.</summary>
    public static string CurrentFile => Path.Combine(Folder, $"officechat-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>Шапка при запуске: кто, где и с какой версией — чтобы по логу было понятно окружение.</summary>
    public static void Startup(string[] args)
    {
        var process = Process.GetCurrentProcess();
        Info(new string('=', 70));
        Info($"Запуск OfficeChat {typeof(Log).Assembly.GetName().Version}");
        Info($"Пользователь: {Environment.UserDomainName}\\{Environment.UserName}, сеанс Windows {process.SessionId}");
        Info($"Компьютер: {Environment.MachineName}, ОС: {Environment.OSVersion}, .NET {Environment.Version}");
        Info($"Программа: {Environment.ProcessPath}, PID {process.Id}, аргументы: [{string.Join(' ', args)}]");
        Info($"Папка данных: {SettingsService.DataFolder}");
        CleanupOldLogs();
    }

    private static void Write(string level, string message, Exception? ex = null)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
                .Append(level).Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
                .Append(message);
            if (ex != null)
                line.AppendLine().Append(ex);
            line.AppendLine();

            lock (Sync)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(CurrentFile, line.ToString(), Encoding.UTF8);
            }
            Debug.Write(line.ToString());
        }
        catch
        {
            // Лог не должен ронять программу, даже если диск недоступен.
        }
    }

    private static string ResolveFolder()
    {
        try
        {
            return Path.Combine(SettingsService.DataFolder, "logs");
        }
        catch
        {
            // Если профиль пользователя недоступен — пишем хотя бы во временную папку.
            return Path.Combine(Path.GetTempPath(), "OfficeChat", "logs");
        }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var threshold = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in Directory.EnumerateFiles(Folder, "officechat-*.log"))
            {
                if (File.GetLastWriteTime(file) < threshold)
                    File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Warn("Не удалось удалить старые логи", ex);
        }
    }
}
