using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Mobile.Views;

/// <summary>Изображение из переписки на весь экран (тёмный фон, «Назад» — закрыть).</summary>
public sealed class ImagePage : Page
{
    public ImagePage(ChatMessage message)
    {
        Background = Brushes.Black;
        var back = new Button { Content = "←", Classes = { "header" }, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Avalonia.Thickness(4) };
        back.Click += (_, _) => Shell.Pop();
        Control image;
        try
        {
            image = new Image { Source = new Bitmap(message.ImagePath), Stretch = Stretch.Uniform };
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось открыть изображение {message.ImagePath}", ex);
            image = new TextBlock { Text = "Не удалось открыть изображение", Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        var caption = new TextBlock
        {
            Text = message.Text, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(16),
            VerticalAlignment = VerticalAlignment.Bottom, IsVisible = message.HasText,
        };
        Content = new Panel { Children = { image, caption, back } };
    }
}
