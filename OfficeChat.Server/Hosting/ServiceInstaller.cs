using System.Diagnostics;
using OfficeChat.Protocol;

namespace OfficeChat.Server.Hosting;

/// <summary>
/// Установка сервера службой: на Windows — служба «OfficeChat Server» и правила брандмауэра,
/// на Linux — пользователь officechat, программа в /opt/officechat-server и systemd-сервис.
/// Сервер после этого запускается сам при включении компьютера.
/// </summary>
public static class ServiceInstaller
{
    private const string LinuxUnit = "officechat-server";
    private const string LinuxUser = "officechat";
    private const string LinuxProgramFolder = "/opt/officechat-server";
    private const string LinuxDataFolder = "/var/lib/officechat-server";

    public static int Install(ServerOptions options)
    {
        var exe = Environment.ProcessPath;
        if (exe == null || Path.GetFileNameWithoutExtension(exe) is "dotnet")
        {
            Console.Error.WriteLine("Установка службы работает только из собранного файла officechat-server.");
            return 1;
        }
        return OperatingSystem.IsWindows() ? InstallWindows(exe, options) : InstallLinux(exe, options);
    }

    public static int Uninstall() => OperatingSystem.IsWindows() ? UninstallWindows() : UninstallLinux();

    // ---- Windows ----

    private static int InstallWindows(string exe, ServerOptions options)
    {
        var binPath = $"\"{exe}\" run --data \"{options.DataFolder}\" --port {options.Port}";
        if (Run("sc.exe", "create", ServerApp.ServiceName, "binPath=", binPath, "start=", "auto",
                "DisplayName=", "OfficeChat Server") != 0)
        {
            Console.Error.WriteLine("Не удалось создать службу. Запустите командную строку от имени администратора.");
            return 1;
        }
        Run("sc.exe", "description", ServerApp.ServiceName, "Сервер офисного чата OfficeChat: аккаунты, переписка, синхронизация устройств.");
        Run("sc.exe", "failure", ServerApp.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000");
        Run("netsh", "advfirewall", "firewall", "add", "rule", "name=OfficeChat Server", "dir=in", "action=allow",
            "protocol=TCP", $"localport={options.Port}");
        Run("netsh", "advfirewall", "firewall", "add", "rule", "name=OfficeChat Server (поиск)", "dir=in", "action=allow",
            "protocol=UDP", $"localport={ServerProtocol.DiscoveryPort}");
        Run("sc.exe", "start", ServerApp.ServiceName);
        Console.WriteLine($"""
            Служба «OfficeChat Server» установлена и запущена.
              Программа: {exe} (не перемещайте этот файл — служба запускает его отсюда)
              Данные:    {options.DataFolder}
              Порт:      {options.Port}
            """);
        return 0;
    }

    private static int UninstallWindows()
    {
        Run("sc.exe", "stop", ServerApp.ServiceName);
        var result = Run("sc.exe", "delete", ServerApp.ServiceName);
        Run("netsh", "advfirewall", "firewall", "delete", "rule", "name=OfficeChat Server");
        Run("netsh", "advfirewall", "firewall", "delete", "rule", "name=OfficeChat Server (поиск)");
        Console.WriteLine(result == 0 ? "Служба удалена. Данные сервера не тронуты." : "Не удалось удалить службу (нужны права администратора?).");
        return result;
    }

    // ---- Linux ----

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static int InstallLinux(string exe, ServerOptions options)
    {
        if (Environment.UserName != "root")
        {
            Console.Error.WriteLine("Установка сервиса — от root: sudo ./officechat-server install");
            return 1;
        }
        var dataFolder = options.DataFolder == ServerOptions.DefaultDataFolder() ? LinuxDataFolder : options.DataFolder;

        if (Run("id", "-u", LinuxUser) != 0 &&
            Run("useradd", "--system", "--no-create-home", "--shell", "/usr/sbin/nologin", LinuxUser) != 0)
        {
            Console.Error.WriteLine($"Не удалось создать пользователя {LinuxUser}.");
            return 1;
        }

        Directory.CreateDirectory(LinuxProgramFolder);
        var target = Path.Combine(LinuxProgramFolder, "officechat-server");
        if (!string.Equals(Path.GetFullPath(exe), target, StringComparison.Ordinal))
        {
            // Через временный файл: работающую программу не перезаписать, а заменить можно.
            File.Copy(exe, target + ".new", overwrite: true);
            File.Move(target + ".new", target, overwrite: true);
        }
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                     UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                     UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        // Папка программы — сервису: обновляясь, он сам подменяет свой файл (только подписанной сборкой).
        Run("chown", "-R", $"{LinuxUser}:{LinuxUser}", LinuxProgramFolder);
        Directory.CreateDirectory(dataFolder);
        Run("chown", "-R", $"{LinuxUser}:{LinuxUser}", dataFolder);

        File.WriteAllText($"/etc/systemd/system/{LinuxUnit}.service", $"""
            [Unit]
            Description=OfficeChat Server
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=notify
            ExecStart={target} run --data {dataFolder} --port {options.Port}
            User={LinuxUser}
            Group={LinuxUser}
            Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR={dataFolder}/.runtime
            # После обновления сервер выходит с кодом 75 — systemd запускает новую версию.
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=multi-user.target
            """);
        Run("systemctl", "daemon-reload");
        // restart, а не start: при повторной установке поверх работающего сервиса запустится новая версия.
        if (Run("systemctl", "enable", LinuxUnit) != 0 || Run("systemctl", "restart", LinuxUnit) != 0)
        {
            Console.Error.WriteLine($"Сервис не запустился. Подробности: journalctl -u {LinuxUnit}");
            return 1;
        }
        Console.WriteLine($"""
            Сервис {LinuxUnit} установлен и запущен.
              Программа: {target}
              Данные:    {dataFolder}
              Порт:      {options.Port} (TCP), поиск в сети — {ServerProtocol.DiscoveryPort} (UDP)
            Если включён брандмауэр: sudo ufw allow {options.Port}/tcp && sudo ufw allow {ServerProtocol.DiscoveryPort}/udp
            Команды администратора: sudo {target} admin ЛОГИН --data {dataFolder}
            """);
        return 0;
    }

    private static int UninstallLinux()
    {
        if (Environment.UserName != "root")
        {
            Console.Error.WriteLine("Удаление сервиса — от root: sudo officechat-server uninstall");
            return 1;
        }
        Run("systemctl", "disable", "--now", LinuxUnit);
        File.Delete($"/etc/systemd/system/{LinuxUnit}.service");
        Run("systemctl", "daemon-reload");
        Console.WriteLine($"Сервис удалён. Данные остались в {LinuxDataFolder}, программа — в {LinuxProgramFolder}.");
        return 0;
    }

    private static int Run(string file, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }
}
