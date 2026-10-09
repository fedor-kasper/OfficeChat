using System.Diagnostics;
using OfficeChat.Services;

namespace OfficeChat.Platform;

public static class Shell
{
    /// <summary>Открывает файл в программе по умолчанию (xdg-open).</summary>
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось открыть {path}", ex);
        }
    }
}
