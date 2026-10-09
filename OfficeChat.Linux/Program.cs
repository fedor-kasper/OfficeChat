using Avalonia;
using OfficeChat.Platform;

namespace OfficeChat;

internal static class Program
{
    /// <summary>Сокет единственной копии — App подписывается на просьбы «покажи окно».</summary>
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Проверяем до запуска Avalonia: если программа уже работает (например, свёрнута в трей),
        // просим её показать окно и тихо выходим.
        Instance = new SingleInstance();
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
