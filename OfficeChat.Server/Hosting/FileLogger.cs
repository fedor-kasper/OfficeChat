using System.Collections.Concurrent;

namespace OfficeChat.Server.Hosting;

/// <summary>
/// Журнал работы в файлы logs/server-ГГГГ-ММ-ДД.log (хранятся 30 дней): у службы нет консоли,
/// а разбираться, что случилось, нужно.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _folder;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var old in Directory.GetFiles(folder, "server-*.log")
                         .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)))
                File.Delete(old);
        }
        catch (IOException)
        {
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(this, ShortName(name)));

    private static string ShortName(string category) => category[(category.LastIndexOf('.') + 1)..];

    internal void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(Path.Combine(_folder, $"server-{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Журнал не должен ронять сервер.
            }
        }
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Warning => "WARN ",
                LogLevel.Error or LogLevel.Critical => "ERROR",
                _ => "INFO ",
            };
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{category}] {formatter(state, exception)}";
            if (exception != null) line += Environment.NewLine + exception;
            provider.Write(line);
        }
    }
}
