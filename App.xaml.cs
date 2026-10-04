using System.Windows;
using OfficeChat.Services;
using OfficeChat.Views;

namespace OfficeChat;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\OfficeChat.SingleInstance";
    private const string ShowWindowEventName = @"Local\OfficeChat.ShowWindow";

    // Не даём запустить вторую копию на одном компьютере: обе заняли бы одни и те же порты.
    private static Mutex? _singleInstance;
    private EventWaitHandle? _showWindowSignal;
    private Views.MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, InstanceMutexName, out var isFirst);
        _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        if (!isFirst)
        {
            // Программа уже работает (возможно, свёрнута в трей) — просим её показать окно.
            _showWindowSignal.Set();
            Shutdown();
            return;
        }

        var settings = SettingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.DisplayName))
        {
            var nameWindow = new NameWindow(Environment.UserName);
            if (nameWindow.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            settings.DisplayName = nameWindow.EnteredName;
            SettingsService.Save(settings);
        }

        _mainWindow = new Views.MainWindow(settings);
        MainWindow = _mainWindow;
        _mainWindow.Show();

        ThreadPool.RegisterWaitForSingleObject(_showWindowSignal,
            (_, _) => Dispatcher.BeginInvoke(() => _mainWindow?.ShowFromTray()),
            null, Timeout.Infinite, executeOnlyOnce: false);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // При выходе из Windows закрываемся по-настоящему, а не в трей.
        _mainWindow?.ExitApplication();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWindowSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
