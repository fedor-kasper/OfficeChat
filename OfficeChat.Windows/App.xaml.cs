using System.Windows;
using System.Windows.Threading;
using OfficeChat.Services;
using OfficeChat.Views;

namespace OfficeChat;

public partial class App : Application
{
    // Local\ — в пределах сеанса Windows: у каждого вошедшего пользователя своя копия программы.
    private const string InstanceMutexName = @"Local\OfficeChat.SingleInstance";
    private const string ShowWindowEventName = @"Local\OfficeChat.ShowWindow";

    // Не даём запустить вторую копию в одном сеансе.
    private static Mutex? _singleInstance;
    private EventWaitHandle? _showWindowSignal;
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
        _singleInstance = new Mutex(true, InstanceMutexName, out var isFirst);
        _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        if (!isFirst)
        {
            // Программа уже работает в этом сеансе (возможно, свёрнута в трей) — просим её показать окно.
            Log.Info("Копия уже запущена в этом сеансе — показываем её окно и выходим");
            _showWindowSignal.Set();
            Shutdown();
            return;
        }

        var settings = SettingsService.Load();
        Log.Info($"Настройки загружены: имя «{settings.DisplayName}», Id {settings.UserId}, автозапуск {settings.AutoStart}");
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
        AutoStartService.Apply(settings.AutoStart);

        Log.Info("Создаём главное окно");
        _mainWindow = new Views.MainWindow(settings);
        MainWindow = _mainWindow;
        // При автозапуске с Windows окно не показываем — программа сразу работает в трее.
        if (!e.Args.Contains(AutoStartService.TrayArgument))
            _mainWindow.Show();
        if (e.Args.Contains(UpdateService.UpdatedArgument))
            _mainWindow.NotifyUpdated();
        Log.Info("Запуск завершён");

        ThreadPool.RegisterWaitForSingleObject(_showWindowSignal,
            (_, _) => Dispatcher.BeginInvoke(() => _mainWindow?.ShowFromTray()),
            null, Timeout.Infinite, executeOnlyOnce: false);
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
        _showWindowSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
