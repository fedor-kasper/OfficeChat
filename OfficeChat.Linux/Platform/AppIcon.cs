using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace OfficeChat.Platform;

/// <summary>Значок приложения рисуется кодом (как в Windows-версии): облачко чата, с красной точкой — есть непрочитанные.</summary>
public static class AppIcon
{
    private static readonly Color Accent = Color.FromRgb(0x2F, 0x6F, 0xDE);
    private static readonly Color Badge = Color.FromRgb(0xE5, 0x3E, 0x3E);

    public static Bitmap Render(bool withBadge, int size = 64)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var k = size / 32.0;
            var accent = new SolidColorBrush(Accent);
            ctx.DrawRectangle(accent, null, new RoundedRect(new Rect(1 * k, 3 * k, 29 * k, 20 * k), 6 * k));
            var tail = new StreamGeometry();
            using (var g = tail.Open())
            {
                g.BeginFigure(new Point(7 * k, 21 * k), true);
                g.LineTo(new Point(7 * k, 29 * k));
                g.LineTo(new Point(15 * k, 21 * k));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(accent, null, tail);
            foreach (var x in new[] { 8.5, 15.5, 22.5 })
                ctx.DrawEllipse(Brushes.White, null, new Point(x * k, 13 * k), 2 * k, 2 * k);
            if (withBadge)
                ctx.DrawEllipse(new SolidColorBrush(Badge), new Pen(Brushes.White, 2 * k), new Point(24.5 * k, 6.5 * k), 6.5 * k, 6.5 * k);
        }
        return bitmap;
    }

    public static WindowIcon Window { get; } = new(Render(false));

    public static WindowIcon Normal => Window;

    public static WindowIcon WithBadge { get; } = new(Render(true));

    /// <summary>PNG значка — для ярлыка AppImage и меню приложений.</summary>
    public static void SavePng(string path, int size)
    {
        using var bitmap = Render(false, size);
        bitmap.Save(path);
    }
}
