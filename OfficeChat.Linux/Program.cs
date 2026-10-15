using Avalonia;
using OfficeChat.Client;
using OfficeChat.Platform;
using OfficeChat.Services;

namespace OfficeChat;

internal static class Program
{
    /// <summary>Сокет единственной копии — App подписывается на просьбы «покажи окно».</summary>
    public static SingleInstance? Instance { get; private set; }

    /// <summary>Аккаунт, с которым запущена эта копия (null — вход или работа без аккаунта).</summary>
    public static SavedAccount? Account { get; private set; }

    /// <summary>
    /// Занять «единственную копию» для аккаунта (после входа). false — этот аккаунт уже открыт в другой копии
    /// (ей отправлена просьба показать окно).
    /// </summary>
    public static bool ClaimAccount(SavedAccount account)
    {
        var instance = new SingleInstance(AccountLauncher.InstanceKey(account));
        if (!instance.TryBecomePrimary()) return false;
        Instance?.Dispose();
        Instance = instance;
        Account = account;
        return true;
    }

    /// <summary>Отпустить «единственную копию» (перед запуском этого же аккаунта заново).</summary>
    public static void ReleaseInstance()
    {
        Instance?.Dispose();
        Instance = null;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // Перезапуск после обновления: ждём, пока закроется старая копия.
        UpdateService.FinishRestart(args);

        // Проверяем до запуска Avalonia: если программа уже работает (например, свёрнута в трей),
        // просим её показать окно и тихо выходим.
        // У каждого аккаунта своя копия программы. Окно входа (--login) — тоже отдельная копия.
        Account = AccountLauncher.SelectAccount(args);
        Instance = new SingleInstance(args.Contains(AccountLauncher.LoginArgument) ? "login" : AccountLauncher.InstanceKey(Account));
        if (!Instance.TryBecomePrimary())
            return 0;

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            Instance.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
