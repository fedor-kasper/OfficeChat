using OfficeChat.Protocol;

namespace OfficeChat.Server.Hosting;

/// <summary>Где сервер хранит данные и на каком порту работает.</summary>
public sealed class ServerOptions
{
    public string DataFolder { get; set; } = DefaultDataFolder();
    public int Port { get; set; } = ServerProtocol.DefaultPort;
    /// <summary>Имя, которое видят клиенты при поиске сервера.</summary>
    public string Name { get; set; } = Environment.MachineName;
    /// <summary>Отвечать на поиск сервера в локальной сети (UDP).</summary>
    public bool Discovery { get; set; } = true;
    /// <summary>Сам проверять выпуски на GitHub и обновляться.</summary>
    public bool AutoUpdate { get; set; } = true;

    public string DatabasePath => Path.Combine(DataFolder, "server.db");
    public string CertificatePath => Path.Combine(DataFolder, "server.pfx");
    public string FilesFolder => Path.Combine(DataFolder, "files");
    public string LogsFolder => Path.Combine(DataFolder, "logs");

    /// <summary>
    /// Windows — %ProgramData%\OfficeChat Server; Linux — /var/lib/officechat-server (служба или root),
    /// иначе ~/.local/share/officechat-server. Можно переопределить переменной OFFICECHAT_SERVER_DATA или --data.
    /// </summary>
    public static string DefaultDataFolder()
    {
        if (Environment.GetEnvironmentVariable("OFFICECHAT_SERVER_DATA") is { Length: > 0 } fromEnv) return fromEnv;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OfficeChat Server");
        const string system = "/var/lib/officechat-server";
        if (Directory.Exists(system) || Environment.UserName == "root") return system;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "officechat-server");
    }
}
