using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OfficeChat.Models;
using OfficeChat.Platform;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Просмотр изображения из переписки: сохранить, скопировать, открыть в системной программе.</summary>
public partial class ImageViewerWindow : Window
{
    private readonly ChatMessage _message;

    public ImageViewerWindow() : this(null!, "") { }

    public ImageViewerWindow(ChatMessage message, string senderName)
    {
        InitializeComponent();
        Icon = AppIcon.Window;
        _message = message;
        if (message == null) return; // конструктор для дизайнера

        var image = ImageThumbnailConverter.Load(message.ImagePath, 0);
        Picture.Source = image;
        Title = $"{message.FileName} — {senderName}";
        TitleText.Text = message.FileName;
        var size = File.Exists(message.ImagePath) ? new FileInfo(message.ImagePath).Length : 0;
        InfoText.Text = image == null
            ? "Не удалось открыть изображение"
            : $"{senderName} · {message.Timestamp:dd.MM.yyyy HH:mm} · {image.PixelSize.Width}×{image.PixelSize.Height} · {size / 1024} КБ";
        CaptionText.Text = message.Text;
        CaptionText.IsVisible = message.HasText;

        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control) await SaveAsAsync(_message, this);
            else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control) await CopyAsync(_message, this);
        };
    }

    private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveAsAsync(_message, this);

    private async void Copy_Click(object? sender, RoutedEventArgs e) => await CopyAsync(_message, this);

    private void OpenExternal_Click(object? sender, RoutedEventArgs e) => Shell.Open(_message.ImagePath);

    /// <summary>«Сохранить как…» с исходным именем файла.</summary>
    public static async Task SaveAsAsync(ChatMessage message, Window owner)
    {
        if (!File.Exists(message.ImagePath))
        {
            await Dialogs.Info(owner, "Файл изображения не найден.");
            return;
        }

        var extension = Path.GetExtension(message.ImagePath);
        var pictures = await owner.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Pictures);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить изображение",
            SuggestedFileName = string.IsNullOrWhiteSpace(message.FileName) ? "image" + extension : message.FileName,
            DefaultExtension = extension.TrimStart('.'),
            SuggestedStartLocation = pictures,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Изображение") { Patterns = new[] { "*" + extension } },
                FilePickerFileTypes.All,
            },
        });
        if (file == null) return;

        try
        {
            await using var target = await file.OpenWriteAsync();
            await using var source = File.OpenRead(message.ImagePath);
            await source.CopyToAsync(target);
            Log.Info($"Изображение сохранено в {file.Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Не удалось сохранить изображение в {file.Path}", ex);
            await Dialogs.Info(owner, $"Не удалось сохранить файл:\n{ex.Message}");
        }
    }

    /// <summary>Кладёт изображение в буфер обмена как картинку (вставляется в GIMP, LibreOffice, Telegram).</summary>
    public static async Task CopyAsync(ChatMessage message, TopLevel owner)
    {
        if (owner.Clipboard == null || !File.Exists(message.ImagePath)) return;
        try
        {
            var image = ImageThumbnailConverter.Load(message.ImagePath, 0);
            if (image == null) return;
            await owner.Clipboard.SetBitmapAsync(image);
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось скопировать изображение в буфер обмена", ex);
        }
    }
}
