using System.Diagnostics;
using System.IO;
using System.Windows;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Открывает ссылку из сообщения: веб-адрес — в браузере, сетевой путь — в Проводнике.</summary>
public static class LinkOpener
{
    /// <summary>По сетевому пути можно прислать программу — перед её запуском переспрашиваем.</summary>
    private static readonly string[] RunnableExtensions =
    {
        ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".pif", ".ps1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".lnk", ".reg", ".jar",
    };

    public static void Open(string link, Window? owner)
    {
        if (LinkParser.IsNetworkPath(link) &&
            RunnableExtensions.Contains(Path.GetExtension(link).ToLowerInvariant()) &&
            MessageBox.Show(owner ?? Application.Current.MainWindow!,
                $"Ссылка ведёт на программу:\n{link}\n\nЗапускайте её, только если доверяете отправителю. Открыть?",
                "OfficeChat", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            Log.Info($"Открыта ссылка {link}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Не удалось открыть ссылку {link}", ex);
            MessageBox.Show(owner ?? Application.Current.MainWindow!, $"Не удалось открыть:\n{link}\n\n{ex.Message}",
                "OfficeChat", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
