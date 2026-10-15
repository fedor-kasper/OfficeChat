using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Просмотр изображения из переписки на тёмном фоне: сохранить, скопировать, открыть в системной программе.</summary>
public partial class ImageViewerWindow : Window
{
    private readonly ChatMessage _message;

    public ImageViewerWindow(ChatMessage message, string senderName)
    {
        InitializeComponent();
        _message = message;

        var image = ImageThumbnailConverter.Load(message.ImagePath, 0);
        Picture.Source = image;
        Title = $"{message.FileName} — {senderName}";
        TitleText.Text = message.FileName;
        var size = File.Exists(message.ImagePath) ? new FileInfo(message.ImagePath).Length : 0;
        InfoText.Text = image == null
            ? "Не удалось открыть изображение"
            : $"{senderName} · {message.Timestamp:dd.MM.yyyy HH:mm} · {image.PixelWidth}×{image.PixelHeight} · {size / 1024} КБ";
        CaptionText.Text = message.Text;
        CaptionText.Visibility = message.HasText ? Visibility.Visible : Visibility.Collapsed;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) SaveAs(_message, this);
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) Copy(_message);
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveAs(_message, this);

    private void Copy_Click(object sender, RoutedEventArgs e) => Copy(_message);

    private void OpenExternal_Click(object sender, RoutedEventArgs e) => OpenExternal(_message);

    // ---- Действия, общие для окна просмотра и меню в ленте ----

    /// <summary>«Сохранить как…» с исходным именем файла.</summary>
    public static void SaveAs(ChatMessage message, Window owner)
    {
        if (!File.Exists(message.ImagePath))
        {
            MessageBox.Show(owner, "Файл изображения не найден.", "OfficeChat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var extension = Path.GetExtension(message.ImagePath);
        var dialog = new SaveFileDialog
        {
            FileName = string.IsNullOrWhiteSpace(message.FileName) ? "image" + extension : message.FileName,
            Filter = $"Изображение (*{extension})|*{extension}|Все файлы (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(owner) != true) return;

        try
        {
            File.Copy(message.ImagePath, dialog.FileName, overwrite: true);
            Log.Info($"Изображение сохранено в {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Не удалось сохранить изображение в {dialog.FileName}", ex);
            MessageBox.Show(owner, $"Не удалось сохранить файл:\n{ex.Message}", "OfficeChat",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Кладёт изображение в буфер обмена — можно вставить в Word, Paint, другой чат.</summary>
    public static void Copy(ChatMessage message)
    {
        var image = ImageThumbnailConverter.Load(message.ImagePath, 0);
        if (image == null) return;
        try
        {
            var data = new DataObject();
            data.SetImage(image);
            // Ещё и как файл — тогда в проводник или другой мессенджер вставится с исходным качеством.
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { message.ImagePath });
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Log.Warn("Буфер обмена занят другой программой", ex);
        }
    }

    /// <summary>Открывает файл в программе просмотра Windows по умолчанию.</summary>
    public static void OpenExternal(ChatMessage message)
    {
        if (!File.Exists(message.ImagePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(message.ImagePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Не удалось открыть {message.ImagePath} во внешней программе", ex);
        }
    }
}
