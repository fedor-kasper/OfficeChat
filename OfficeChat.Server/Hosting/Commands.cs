using System.Diagnostics;
using OfficeChat.Server.Data;
using OfficeChat.Server.Security;

namespace OfficeChat.Server.Hosting;

/// <summary>Команды администратора, которые выполняются на самом сервере (без входа в приложение).</summary>
public static class Commands
{
    public static int Accounts(ServerOptions options, string command, string? login)
    {
        if (!File.Exists(options.DatabasePath))
        {
            Console.Error.WriteLine($"База сервера не найдена: {options.DatabasePath}. Сервер ещё не запускался или указана не та папка (--data).");
            return 1;
        }
        var db = new ServerDb(options.DatabasePath);
        try
        {
            if (command == "users")
            {
                var accounts = db.Accounts();
                if (accounts.Count == 0) Console.WriteLine("Аккаунтов пока нет.");
                foreach (var a in accounts)
                    Console.WriteLine($"{a.Login,-20} {a.DisplayName,-30}{(a.IsAdmin ? " [администратор]" : "")}{(a.IsBlocked ? " [заблокирован]" : "")}");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(login))
            {
                Console.Error.WriteLine($"Укажите логин: officechat-server {command} ЛОГИН");
                return 2;
            }
            var account = db.FindAccountByLogin(login);
            if (account == null)
            {
                Console.Error.WriteLine($"Аккаунт «{login}» не найден. Сначала зарегистрируйтесь в приложении.");
                return 1;
            }
            switch (command)
            {
                case "admin":
                    db.SetAdmin(account.Id, true);
                    Console.WriteLine($"Аккаунт «{account.Login}» ({account.DisplayName}) теперь администратор.");
                    break;
                case "unadmin":
                    db.SetAdmin(account.Id, false);
                    Console.WriteLine($"У аккаунта «{account.Login}» больше нет прав администратора.");
                    break;
                case "reset-password":
                    var temporary = Passwords.Temporary();
                    db.SetPassword(account.Id, Passwords.Hash(temporary), mustChange: true);
                    db.RevokeAllDevices(account.Id);
                    Console.WriteLine($"Временный пароль для «{account.Login}»: {temporary}");
                    Console.WriteLine("При входе приложение попросит сменить его. Все устройства аккаунта отключены.");
                    break;
            }
            return 0;
        }
        finally
        {
            FixOwnership(options.DataFolder);
        }
    }

    /// <summary>
    /// На Linux служба работает от своего пользователя. Если команду выполнили от root, файлы базы
    /// возвращаем владельцу папки данных — иначе служба не сможет в них писать.
    /// </summary>
    private static void FixOwnership(string folder)
    {
        if (OperatingSystem.IsWindows() || Environment.UserName != "root") return;
        try
        {
            using var chown = Process.Start(new ProcessStartInfo("chown", ["-R", "--reference=" + folder, folder])
            {
                UseShellExecute = false,
            });
            chown?.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
