using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OfficeChat.Client;
using OfficeChat.Platform;
using OfficeChat.Services;
using OfficeChat.Views;

namespace OfficeChat;

public partial class App : Application
{
    private MainWindow? _mainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error($"Необработанная ошибка (завершение: {args.IsTerminating})", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Необработанная ошибка в фоновой задаче", args.Exception);
            args.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            Log.Error("В OfficeChat произошла ошибка", args.Exception);
            // Окно уже работает — продолжаем; ошибку показываем, чтобы её можно было прислать.
            args.Handled = _mainWindow != null;
            _ = Dialogs.Info(_mainWindow, $"В OfficeChat произошла ошибка.\n\n{args.Exception.Message}\n\nПодробности в логе:\n{Log.CurrentFile}");
        };

        var args = desktop.Args ?? Array.Empty<string>();
        Log.Startup(args);
        desktop.Exit += (_, e) =>
        {
            Log.Info($"Выход, код {e.ApplicationExitCode}");
        };

        _ = StartAsync(desktop, args);
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, string[] args)
    {
        try
        {
            ListenForShowRequests();
            var account = Program.Account;
            var loginRequested = args.Contains(AccountLauncher.LoginArgument);

            // Аккаунта нет — окно входа (если раньше не выбрали «работать без аккаунта»).
            if (account == null && (loginRequested || !SettingsService.Load().NoAccount))
            {
                Log.Info("Аккаунта нет — показываем вход");
                var login = await ShowLoginAsync(null, allowNoAccount: !loginRequested);
                if (login.Account != null)
                {
                    account = login.Account;
                    if (!Program.ClaimAccount(account))
                    {
                        Log.Info("Этот аккаунт уже открыт в другой копии — показали её окно");
                        desktop.Shutdown();
                        return;
                    }
                    ListenForShowRequests();
                }
                else if (login.ChoseNoAccount)
                {
                    var root = SettingsService.Load();
                    root.NoAccount = true;
                    SettingsService.Save(root);
                    Log.Info("Выбрана работа без аккаунта");
                }
                else
                {
                    Log.Info("Окно входа закрыто — выходим");
                    desktop.Shutdown();
                    return;
                }
            }

            AppSettings settings;
            ServerSession? session = null;
            if (account != null)
            {
                session = AccountLauncher.CreateSession(account);
                if (session == null)
                {
                    // Ключ не расшифровался (например, перенесли профиль) — входим заново.
                    var again = await ShowLoginAsync(account, allowNoAccount: false);
                    if (again.Account == null)
                    {
                        desktop.Shutdown();
                        return;
                    }
                    account = again.Account;
                    session = AccountLauncher.CreateSession(account)!;
                }
                await OfferMigrationAsync(account, session);
                settings = AccountLauncher.Activate(account);
            }
            else
            {
                settings = SettingsService.Load();
                Log.Info($"Без аккаунта: имя «{settings.DisplayName}», Id {settings.UserId}, автозапуск {settings.AutoStart}");
                if (string.IsNullOrWhiteSpace(settings.DisplayName))
                {
                    Log.Info("Первый запуск — спрашиваем имя");
                    var dialog = new SettingsWindow(Environment.UserName, settings.AutoStart);
                    var closed = new TaskCompletionSource();
                    dialog.Closed += (_, _) => closed.TrySetResult();
                    dialog.Show();
                    await closed.Task;
                    if (!dialog.Saved)
                    {
                        Log.Info("Ввод имени отменён — выходим");
                        desktop.Shutdown();
                        return;
                    }
                    settings.DisplayName = dialog.EnteredName;
                    settings.AutoStart = dialog.AutoStart;
                    SettingsService.Save(settings);
                    Log.Info($"Имя сохранено: «{settings.DisplayName}»");
                }
            }
            // Сначала меню (оно копирует значок), потом автозапуск — чтобы и он ссылался на значок.
            DesktopIntegration.EnsureMenuEntry();
            DesktopIntegration.ApplyAutoStart(settings.AutoStart);

            Log.Info("Создаём главное окно");
            _mainWindow = new MainWindow(settings, session, account);
            // desktop.MainWindow не задаём: если назначить его до запуска цикла событий, Avalonia сама покажет
            // окно — и автозапуск «сразу в трей» открывал бы его. Программа и так живёт до явного выхода.
            // При автозапуске окно не показываем — программа сразу работает в трее.
            var autostart = args.Contains(DesktopIntegration.TrayArgument);
            if (!autostart)
                _mainWindow.Show();

            // Автозапуск: остальные аккаунты этого компьютера — каждый в своей копии.
            if (autostart && account != null && !args.Contains(AccountLauncher.AccountArgument))
                foreach (var other in AccountStore.All().Where(a => a.Id != account.Id))
                    AccountLauncher.StartCopy(AccountLauncher.AccountArgument, other.Id.ToString(), DesktopIntegration.TrayArgument);
            Log.Info("Запуск завершён");
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось запустить OfficeChat", ex);
            await Dialogs.Info(null, $"Не удалось запустить OfficeChat.\n\n{ex.Message}\n\nПодробности в логе:\n{Log.CurrentFile}");
            desktop.Shutdown(1);
        }
    }

    /// <summary>Первый вход на этом компьютере: перенести переписку, которая велась без аккаунта.</summary>
    private static async Task OfferMigrationAsync(SavedAccount account, ServerSession session)
    {
        if (LegacyMigration.Offer(account) is not { } legacy) return;
        if (!await Dialogs.Confirm(null, LegacyMigration.Question(account, legacy), "OfficeChat — перенос переписки"))
        {
            LegacyMigration.Decline(account);
            return;
        }
        try
        {
            await Task.Run(() => LegacyMigration.MigrateAsync(account, session, legacy));
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось перенести переписку без аккаунта", ex);
            await Dialogs.Info(null, "Не удалось перенести переписку: " + LoginFlow.Describe(ex) +
                                     "\n\nПопробуем снова при следующем запуске.");
        }
    }

    private void ListenForShowRequests()
    {
        if (Program.Instance != null)
            Program.Instance.ShowRequested += () => Dispatcher.UIThread.Post(() => _mainWindow?.ShowFromTray());
    }

    private static async Task<LoginWindow> ShowLoginAsync(SavedAccount? previous, bool allowNoAccount)
    {
        var login = new LoginWindow(previous, allowNoAccount);
        var closed = new TaskCompletionSource();
        login.Closed += (_, _) => closed.TrySetResult();
        login.Show();
        await closed.Task;
        return login;
    }
}
