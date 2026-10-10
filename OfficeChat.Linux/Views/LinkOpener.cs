using Avalonia.Controls;
using OfficeChat.Platform;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Открывает ссылку из сообщения: веб-адрес — в браузере, сетевой путь Windows (\\сервер\папка) —
/// как smb://сервер/папка в файловом менеджере (Nemo, Nautilus, Caja понимают его сами).
/// </summary>
public static class LinkOpener
{
    /// <summary>По сетевому пути можно прислать программу — перед её запуском переспрашиваем.</summary>
    private static readonly string[] RunnableExtensions =
    {
        ".sh", ".run", ".bin", ".appimage", ".desktop", ".deb", ".py", ".pl", ".jar",
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".js",
    };

    public static async Task OpenAsync(string link, Window? owner)
    {
        var target = link;
        if (LinkParser.IsNetworkPath(link))
        {
            if (RunnableExtensions.Contains(Path.GetExtension(link).ToLowerInvariant()) &&
                !await Dialogs.Confirm(owner,
                    $"Ссылка ведёт на программу:\n{link}\n\nЗапускайте её, только если доверяете отправителю. Открыть?"))
                return;
            target = "smb:" + link.Replace('\\', '/');
        }

        Log.Info($"Открываем ссылку {target}");
        Shell.Open(target);
    }
}
