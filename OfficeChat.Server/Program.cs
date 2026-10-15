using OfficeChat.Server.Hosting;

namespace OfficeChat.Server;

/// <summary>
/// officechat-server [команда] [параметры]
///
///   run (по умолчанию)        — запустить сервер
///   admin ЛОГИН               — выдать права администратора
///   unadmin ЛОГИН             — снять права администратора
///   reset-password ЛОГИН      — выдать временный пароль
///   users                     — список аккаунтов
///   install / uninstall       — установить/удалить службу (Windows — служба, Linux — systemd)
///
///   --data ПАПКА   где хранить данные      --port ПОРТ   порт HTTPS (45680)
///   --name ИМЯ     имя сервера в сети      --no-discovery  не отвечать на поиск в сети
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = new ServerOptions();
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data" when i + 1 < args.Length:
                    options.DataFolder = Path.GetFullPath(args[++i]);
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var port):
                    options.Port = port;
                    i++;
                    break;
                case "--name" when i + 1 < args.Length:
                    options.Name = args[++i];
                    break;
                case "--no-discovery":
                    options.Discovery = false;
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        var command = rest.FirstOrDefault(a => !a.StartsWith('-')) ?? "run";
        var argument = rest.Skip(1).FirstOrDefault();
        try
        {
            switch (command)
            {
                case "run":
                    await ServerApp.Build(options, rest.ToArray()).RunAsync();
                    return 0;
                case "admin":
                case "unadmin":
                case "reset-password":
                case "users":
                    return Commands.Accounts(options, command, argument);
                case "install":
                    return ServiceInstaller.Install(options);
                case "uninstall":
                    return ServiceInstaller.Uninstall();
                case "help":
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"Неизвестная команда «{command}».");
                    PrintHelp();
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp() => Console.WriteLine("""
        OfficeChat Server — сервер офисного чата.

        Использование: officechat-server [команда] [параметры]

        Команды:
          run                    запустить сервер (по умолчанию)
          admin ЛОГИН            выдать права администратора аккаунту
          unadmin ЛОГИН          снять права администратора
          reset-password ЛОГИН   выдать временный пароль (при входе попросит сменить)
          users                  список аккаунтов
          install                установить как службу (Windows) или systemd-сервис (Linux) и запустить
          uninstall              удалить службу

        Параметры:
          --data ПАПКА           где хранить данные
          --port ПОРТ            порт HTTPS (по умолчанию 45680)
          --name ИМЯ             имя сервера, которое видят приложения
          --no-discovery         не отвечать на поиск сервера в сети
        """);
}
