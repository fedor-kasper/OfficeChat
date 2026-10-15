using Avalonia;
using OfficeChat.Mobile;

namespace OfficeChat.Mobile.Desktop;

/// <summary>Интерфейс телефона в окне на компьютере — для проверки без телефона.</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<MobileApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
}
