using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OfficeChat.Services;

namespace OfficeChat.Mobile;

/// <summary>Приложение для телефона: один экран (<see cref="ShellView"/>), внутри — переходы между страницами.</summary>
public partial class MobileApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        switch (ApplicationLifetime)
        {
            case ISingleViewApplicationLifetime single:
                // Android: окно приложения может пересоздаваться (поворот, возврат из фона), сеанс — остаётся.
                single.MainView = new ShellView();
                break;
            case IClassicDesktopStyleApplicationLifetime desktop:
                // Проверка на компьютере: окно размером с телефон.
                desktop.MainWindow = new Window
                {
                    Title = "OfficeChat — телефон",
                    Width = 400,
                    Height = 780,
                    Content = new ShellView(),
                };
                desktop.Exit += (_, _) => MobileSession.Stop();
                break;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
