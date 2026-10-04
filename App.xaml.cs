using System.Windows;
using OfficeChat.Services;
using OfficeChat.Views;

namespace OfficeChat;

public partial class App : Application
{
    // Не даём запустить вторую копию на одном компьютере: обе заняли бы один и тот же UDP-порт.
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\OfficeChat.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("OfficeChat уже запущен.", "OfficeChat",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var settings = SettingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.DisplayName))
        {
            // Окно имени показывается до главного, поэтому временно не завершаем приложение при его закрытии.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var nameWindow = new NameWindow(Environment.UserName);
            if (nameWindow.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            settings.DisplayName = nameWindow.EnteredName;
            SettingsService.Save(settings);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        MainWindow = new MainWindow(settings);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
