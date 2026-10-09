using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OfficeChat.Models;
using OfficeChat.Platform;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Действия с файлом из переписки: открыть, сохранить как, показать в папке.</summary>
public static class FileActions
{
    /// <summary>Файлы, которые при открытии запускают программу, — перед открытием переспрашиваем.</summary>
    private static readonly string[] RunnableExtensions =
    {
        ".sh", ".run", ".bin", ".appimage", ".desktop", ".deb", ".py", ".pl", ".jar",
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".js",
    };

    public static async Task OpenAsync(ChatMessage message, Window owner)
    {
        if (!await ExistsAsync(message, owner)) return;

        var extension = Path.GetExtension(message.FilePath).ToLowerInvariant();
        if (!message.IsOutgoing && RunnableExtensions.Contains(extension) &&
            !await Dialogs.Confirm(owner,
                $"«{message.FileName}» — это программа или сценарий. Открывайте его, только если доверяете отправителю.\n\nОткрыть?"))
            return;

        Shell.Open(message.FilePath);
    }

    public static async Task SaveAsAsync(ChatMessage message, Window owner)
    {
        if (!await ExistsAsync(message, owner)) return;

        var extension = Path.GetExtension(message.FileName);
        var documents = await owner.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить файл",
            SuggestedFileName = message.FileName,
            DefaultExtension = extension.TrimStart('.'),
            SuggestedStartLocation = documents,
            FileTypeChoices = extension.Length > 0
                ? new[] { new FilePickerFileType($"Файл {extension}") { Patterns = new[] { "*" + extension } }, FilePickerFileTypes.All }
                : new[] { FilePickerFileTypes.All },
        });
        if (file == null) return;

        try
        {
            await using var target = await file.OpenWriteAsync();
            await using var source = File.OpenRead(message.FilePath);
            await source.CopyToAsync(target);
            Log.Info($"Файл сохранён в {file.Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Не удалось сохранить файл в {file.Path}", ex);
            await Dialogs.Info(owner, $"Не удалось сохранить файл:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Открывает папку с файлом в файловом менеджере. Сначала просим выделить сам файл
    /// (стандартный D-Bus интерфейс FileManager1 — Nemo, Nautilus, Caja, Dolphin), иначе просто открываем папку.
    /// </summary>
    public static async Task ShowInFolderAsync(ChatMessage message, Window owner)
    {
        if (!await ExistsAsync(message, owner)) return;
        try
        {
            var uri = new Uri(message.FilePath).AbsoluteUri;
            using var process = Process.Start(new ProcessStartInfo("dbus-send")
            {
                ArgumentList =
                {
                    "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
                    "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                    $"array:string:{uri}", "string:",
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process != null)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                if (process.ExitCode == 0) return;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            Log.Info($"FileManager1 недоступен ({ex.GetType().Name}) — открываем папку");
        }
        Shell.Open(Path.GetDirectoryName(message.FilePath)!);
    }

    private static async Task<bool> ExistsAsync(ChatMessage message, Window owner)
    {
        if (File.Exists(message.FilePath)) return true;
        await Dialogs.Info(owner, "Файл не найден — возможно, переписку очистили.");
        return false;
    }
}
