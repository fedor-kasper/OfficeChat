using System.Diagnostics;
using OfficeChat.Protocol;
using OfficeChat.Services;

namespace OfficeChat.Client;

/// <summary>
/// Запуск с аккаунтом. Каждый аккаунт работает в своей копии программы со своей папкой данных —
/// так на одном компьютере (и даже под одной учётной записью) могут одновременно работать несколько человек.
/// </summary>
public static class AccountLauncher
{
    /// <summary>--account Id — запустить этот аккаунт.</summary>
    public const string AccountArgument = "--account";

    /// <summary>--login — показать окно входа (добавить аккаунт).</summary>
    public const string LoginArgument = "--login";

    /// <summary>
    /// С каким аккаунтом работать: из --account, иначе последний использованный. null — нужен вход
    /// (аккаунтов нет или просили окно входа).
    /// </summary>
    public static SavedAccount? SelectAccount(string[] args)
    {
        if (args.Contains(LoginArgument)) return null;
        var index = Array.IndexOf(args, AccountArgument);
        if (index >= 0 && index + 1 < args.Length && Guid.TryParse(args[index + 1], out var id))
            return AccountStore.Find(id);
        return AccountStore.LastUsed();
    }

    /// <summary>Ключ «одной копии»: у каждого аккаунта — своя копия программы.</summary>
    public static string InstanceKey(SavedAccount? account) => account == null ? "main" : account.Id.ToString("N")[..12];

    /// <summary>Переключиться на данные аккаунта: его папка, его имя и Id в сети.</summary>
    public static AppSettings Activate(SavedAccount account)
    {
        SettingsService.UseDataFolder(AccountStore.FolderOf(account.Id));
        var settings = SettingsService.Load();
        settings.UserId = account.Id;
        settings.DisplayName = account.DisplayName;
        SettingsService.Save(settings);
        AccountStore.MarkUsed(account.Id);
        UpdateService.RestartArguments = [AccountArgument, account.Id.ToString()];
        Log.Info($"Аккаунт «{account.Login}» ({account.DisplayName}), сервер {account.ServerHost}:{account.ServerPort}, " +
                 $"данные — {SettingsService.DataFolder}");
        return settings;
    }

    /// <summary>Сеанс на сервере по сохранённому ключу. null — ключа нет (нужно войти заново).</summary>
    public static ServerSession? CreateSession(SavedAccount account)
    {
        var token = AccountStore.Unprotect(account.ProtectedToken);
        if (string.IsNullOrEmpty(token)) return null;
        return new ServerSession(new ServerApi(account.ServerHost, account.ServerPort, account.ServerFingerprint, token),
            account.Id, account.Login);
    }

    /// <summary>Запомнить вход: аккаунт, сервер и (зашифрованный) ключ устройства.</summary>
    public static SavedAccount Remember(ServerApi api, string serverName, AuthResponse auth)
    {
        var account = AccountStore.Find(auth.Account.Id) ?? new SavedAccount();
        account.Id = auth.Account.Id;
        account.Login = auth.Account.Login;
        account.DisplayName = auth.Account.DisplayName;
        account.IsAdmin = auth.Account.IsAdmin;
        account.ServerHost = api.Host;
        account.ServerPort = api.Port;
        account.ServerName = serverName;
        account.ServerFingerprint = api.Fingerprint ?? "";
        account.ProtectedToken = AccountStore.Protect(auth.Token);
        AccountStore.Save(account);
        return account;
    }

    /// <summary>Своё название устройства (телефон: модель вместо имени компьютера).</summary>
    public static DeviceInfo? DeviceOverride { get; set; }

    /// <summary>Как устройство называется на сервере (видно в списке устройств).</summary>
    public static DeviceInfo ThisDevice() => DeviceOverride ?? new(
        Environment.MachineName + (Environment.UserName is { Length: > 0 } user ? $" ({user})" : ""),
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other");

    /// <summary>Запустить ещё одну копию программы (другой аккаунт или окно входа).</summary>
    public static void StartCopy(params string[] args)
    {
        // AppImage: запускаем сам образ, а не распакованный файл внутри него.
        var exe = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath;
        if (exe == null) return;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        // Запуск из «dotnet officechat.dll» (разработка) — передаём и саму программу.
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet" && Environment.GetCommandLineArgs().FirstOrDefault() is { } dll)
            start.ArgumentList.Add(dll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try
        {
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Error("Не удалось запустить копию программы", ex);
        }
    }

    /// <summary>Выйти из аккаунта на этом компьютере: ключ на сервере отзывается, аккаунт убирается из списка.</summary>
    public static async Task SignOutAsync(SavedAccount account, ServerSession? session)
    {
        if (session != null)
        {
            try
            {
                await session.Api.LogoutAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or ServerApiException or TaskCanceledException)
            {
                Log.Warn("Не удалось сообщить серверу о выходе — ключ отключится, когда его отзовут вручную", ex);
            }
        }
        AccountStore.Remove(account.Id);
        Log.Info($"Выход из аккаунта «{account.Login}»");
    }
}
