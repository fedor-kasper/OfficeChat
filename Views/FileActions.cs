using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Действия с файлом из переписки: открыть, сохранить как, показать в папке.</summary>
public static class FileActions
{
    /// <summary>Файлы, которые при открытии запускают программу, — перед открытием переспрашиваем.</summary>
    private static readonly string[] RunnableExtensions =
    {
        ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".pif", ".ps1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".lnk", ".reg", ".jar",
    };

    public static void Open(ChatMessage message, Window owner)
    {
        if (!Exists(message, owner)) return;

        var extension = Path.GetExtension(message.FilePath).ToLowerInvariant();
        if (!message.IsOutgoing && RunnableExtensions.Contains(extension) &&
            MessageBox.Show(owner,
                $"«{message.FileName}» — это программа или сценарий. Открывайте его, только если доверяете отправителю.\n\nОткрыть?",
                "OfficeChat", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(message.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Не удалось открыть {message.FilePath}", ex);
            MessageBox.Show(owner, $"Не удалось открыть файл:\n{ex.Message}", "OfficeChat",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void SaveAs(ChatMessage message, Window owner)
    {
        if (!Exists(message, owner)) return;

        var extension = Path.GetExtension(message.FileName);
        var dialog = new SaveFileDialog
        {
            FileName = message.FileName,
            Filter = extension.Length > 0
                ? $"Файл {extension} (*{extension})|*{extension}|Все файлы (*.*)|*.*"
                : "Все файлы (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(owner) != true) return;

        try
        {
            File.Copy(message.FilePath, dialog.FileName, overwrite: true);
            Log.Info($"Файл сохранён в {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Не удалось сохранить файл в {dialog.FileName}", ex);
            MessageBox.Show(owner, $"Не удалось сохранить файл:\n{ex.Message}", "OfficeChat",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Открывает Проводник с выделенным файлом.</summary>
    public static void ShowInFolder(ChatMessage message, Window owner)
    {
        if (!Exists(message, owner)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{message.FilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Не удалось показать {message.FilePath} в папке", ex);
        }
    }

    private static bool Exists(ChatMessage message, Window owner)
    {
        if (File.Exists(message.FilePath)) return true;
        MessageBox.Show(owner, "Файл не найден — возможно, переписку очистили.", "OfficeChat",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
