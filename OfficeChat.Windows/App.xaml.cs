using System.Windows;
using System.Windows.Threading;
using OfficeChat.Client;
using OfficeChat.Services;
using OfficeChat.Views;

namespace OfficeChat;

public partial class App : Application
{
    // Local\ — в пределах сеанса Windows: у каждого вошедшего пользователя своя копия программы.
    private const string InstanceMutexName = @"Local\OfficeChat.SingleInstance";
    private const string ShowWindowEventName = @"Local\OfficeChat.ShowWindow";

    // Не даём запустить вторую копию (того же аккаунта) в одном сеансе.
    private Mutex? _singleInstance;
    private EventWaitHandle? _showWindowSignal;
    private RegisteredWaitHandle? _showWindowWait;
    private Views.MainWindow? _mainWindow;
    private bool _fatalShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Ловим всё, что не поймано: ошибки в окнах, в фоновых потоках и в забытых задачах.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error($"Необработанная ошибка (завершение: {args.IsTerminating})", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Необработанная ошибка в фоновой задаче", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
        Log.Startup(e.Args);

        try
        {
            Start(e);
        }
        catch (Exception ex)
        {
            ShowFatal("Не удалось запустить OfficeChat.", ex);
            Shutdown(1);
        }
    }

    private void Start(StartupEventArgs e)
    {
        // Перезапуск после обновления: ждём, пока закроется старая копия.
        UpdateService.FinishRestart(e.Args);

        // У каждого аккаунта своя копия программы. Окно входа (--login) — тоже отдельная копия.
        var account = AccountLauncher.SelectAccount(e.Args);
        var loginRequested = e.Args.Contains(AccountLauncher.LoginArgument);
        if (!ClaimInstance(loginRequested ? "login" : AccountLauncher.InstanceKey(account)))
        {
            Shutdown();
            return;
        }

        // Аккаунта нет — окно входа (если раньше не выбрали «работать без аккаунта»).
        if (account == null && (loginRequested || !SettingsService.Load().NoAccount))
        {
            Log.Info("Аккаунта нет — показываем вход");
            var login = new LoginWindow(null, allowNoAccount: !loginRequested);
            login.ShowDialog();
            if (login.Account != null)
            {
                account = login.Account;
                if (!ClaimInstance(AccountLauncher.InstanceKey(account)))
                {
                    Shutdown();
                    return;
                }
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
                Shutdown();
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
                // Ключ не расшифровался (например, профиль перенесли на другой компьютер) — входим заново.
                var again = new LoginWindow(account, allowNoAccount: false);
                again.ShowDialog();
                if (again.Account == null)
                {
                    Shutdown();
                    return;
                }
                account = again.Account;
                session = AccountLauncher.CreateSession(account)!;
            }
            OfferMigration(account, session);
            settings = AccountLauncher.Activate(account);
        }
        else
        {
            settings = SettingsService.Load();
            Log.Info($"Без аккаунта: имя «{settings.DisplayName}», Id {settings.UserId}, автозапуск {settings.AutoStart}");
            if (string.IsNullOrWhiteSpace(settings.DisplayName))
            {
                Log.Info("Первый запуск — спрашиваем имя");
                var settingsWindow = new SettingsWindow(Environment.UserName, settings.AutoStart);
                if (settingsWindow.ShowDialog() != true)
                {
                    Log.Info("Ввод имени отменён — выходим");
                    Shutdown();
                    return;
                }
                settings.DisplayName = settingsWindow.EnteredName;
                settings.AutoStart = settingsWindow.AutoStart;
                SettingsService.Save(settings);
                Log.Info($"Имя сохранено: «{settings.DisplayName}»");
            }
        }
        AutoStartService.Apply(settings.AutoStart);

        Log.Info("Создаём главное окно");
        _mainWindow = new Views.MainWindow(settings, session, account);
        MainWindow = _mainWindow;
        // При автозапуске с Windows окно не показываем — программа сразу работает в трее.
        var autostart = e.Args.Contains(AutoStartService.TrayArgument);
        if (!autostart)
            _mainWindow.Show();
        if (e.Args.Contains(UpdateService.UpdatedArgument))
            _mainWindow.NotifyUpdated();

        // Автозапуск: остальные аккаунты этого компьютера — каждый в своей копии.
        if (autostart && account != null && !e.Args.Contains(AccountLauncher.AccountArgument))
            foreach (var other in AccountStore.All().Where(a => a.Id != account.Id))
                AccountLauncher.StartCopy(AccountLauncher.AccountArgument, other.Id.ToString(), AutoStartService.TrayArgument);
        Log.Info("Запуск завершён");
    }

    /// <summary>Первый вход на этом компьютере: перенести переписку, которая велась без аккаунта.</summary>
    private static void OfferMigration(SavedAccount account, ServerSession session)
    {
        if (LegacyMigration.Offer(account) is not { } legacy) return;
        if (MessageBox.Show(LegacyMigration.Question(account, legacy), "OfficeChat — перенос переписки",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            LegacyMigration.Decline(account);
            return;
        }
        try
        {
            // Запуск ещё не закончен (окон нет) — ждём переноса здесь же.
            Task.Run(() => LegacyMigration.MigrateAsync(account, session, legacy)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось перенести переписку без аккаунта", ex);
            MessageBox.Show("Не удалось перенести переписку: " + LoginFlow.Describe(ex) +
                            "\n\nПопробуем снова при следующем запуске.", "OfficeChat");
        }
    }

    /// <summary>
    /// Занять «единственную копию» для ключа (аккаунта). false — такая копия уже работает: ей отправлена
    /// просьба показать окно.
    /// </summary>
    private bool ClaimInstance(string key)
    {
        // Ключ «main» — прежние имена: копия без аккаунта узнаёт и старую версию программы.
        var suffix = key == "main" ? "" : "." + key;
        var mutex = new Mutex(true, InstanceMutexName + suffix, out var isFirst);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName + suffix);
        if (!isFirst)
        {
            Log.Info("Эта копия уже запущена в этом сеансе — показываем её окно и выходим");
            signal.Set();
            signal.Dispose();
            mutex.Dispose();
            return false;
        }
        ReleaseInstance();
        _singleInstance = mutex;
        _showWindowSignal = signal;
        _showWindowWait = ThreadPool.RegisterWaitForSingleObject(signal,
            (_, _) => Dispatcher.BeginInvoke(() => _mainWindow?.ShowFromTray()),
            null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    /// <summary>Отпустить «единственную копию» (перед запуском этого же аккаунта в новой копии).</summary>
    public static void ReleaseInstance()
    {
        if (Current is not App app) return;
        app._showWindowWait?.Unregister(null);
        app._showWindowWait = null;
        app._showWindowSignal?.Dispose();
        app._showWindowSignal = null;
        if (app._singleInstance != null)
        {
            try
            {
                app._singleInstance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Мьютекс принадлежит другому потоку — закроется вместе с процессом.
            }
            app._singleInstance.Dispose();
            app._singleInstance = null;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal("В OfficeChat произошла ошибка.", e.Exception);
        // Окно запущено — пробуем продолжить работу; до запуска окна продолжать нечего.
        e.Handled = _mainWindow != null;
        if (!e.Handled) Shutdown(1);
    }

    private void ShowFatal(string title, Exception ex)
    {
        Log.Error(title, ex);
        if (_fatalShown) return;
        _fatalShown = true;
        MessageBox.Show(
            $"{title}\n\n{ex.Message}\n\nПодробности записаны в лог:\n{Log.CurrentFile}",
            "OfficeChat — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        _fatalShown = false;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Log.Info($"Завершение сеанса Windows ({e.ReasonSessionEnding})");
        // При выходе из Windows закрываемся по-настоящему, а не в трей.
        _mainWindow?.ExitApplication();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info($"Выход, код {e.ApplicationExitCode}");
        ReleaseInstance();
        base.OnExit(e);
    }
}
