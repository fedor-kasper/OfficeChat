using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
            if (Program.Instance != null)
                Program.Instance.ShowRequested += () => Dispatcher.UIThread.Post(() => _mainWindow?.ShowFromTray());

            var settings = SettingsService.Load();
            Log.Info($"Настройки загружены: имя «{settings.DisplayName}», Id {settings.UserId}, автозапуск {settings.AutoStart}");
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
            // Сначала меню (оно копирует значок), потом автозапуск — чтобы и он ссылался на значок.
            DesktopIntegration.EnsureMenuEntry();
            DesktopIntegration.ApplyAutoStart(settings.AutoStart);

            Log.Info("Создаём главное окно");
            _mainWindow = new MainWindow(settings);
            desktop.MainWindow = _mainWindow;
            // При автозапуске окно не показываем — программа сразу работает в трее.
            if (!args.Contains(DesktopIntegration.TrayArgument))
                _mainWindow.Show();
            Log.Info("Запуск завершён");
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось запустить OfficeChat", ex);
            await Dialogs.Info(null, $"Не удалось запустить OfficeChat.\n\n{ex.Message}\n\nПодробности в логе:\n{Log.CurrentFile}");
            desktop.Shutdown(1);
        }
    }
}
