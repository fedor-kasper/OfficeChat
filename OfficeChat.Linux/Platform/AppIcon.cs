using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace OfficeChat.Platform;

/// <summary>Значок приложения рисуется кодом (как в Windows-версии): облачко чата, с красным кружком и числом — есть непрочитанные.</summary>
public static class AppIcon
{
    private static readonly Color Accent = Color.FromRgb(0x2F, 0x6F, 0xDE);
    private static readonly Color Badge = Color.FromRgb(0xE5, 0x3E, 0x3E);

    /// <summary>Текст на красном кружке: число непрочитанных, больше девяти — «9+».</summary>
    public static string BadgeText(int unread) => unread > 9 ? "9+" : unread.ToString(CultureInfo.InvariantCulture);

    /// <summary>Кружок крупный: в трее значок уменьшается до 22–24 точек, мелкую цифру там не разглядеть.</summary>
    public static Bitmap Render(int unread, int size = 64)
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
            if (unread > 0)
            {
                var center = new Point(22 * k, 21 * k);
                ctx.DrawEllipse(new SolidColorBrush(Badge), new Pen(Brushes.White, 2 * k), center, 10 * k, 10 * k);
                var text = BadgeText(unread);
                var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                    (text.Length > 1 ? 11 : 15) * k, Brushes.White);
                ctx.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
            }
        }
        return bitmap;
    }

    public static WindowIcon Window { get; } = new(Render(0));

    public static WindowIcon Normal => Window;

    private static readonly Dictionary<string, WindowIcon> Badges = new();

    /// <summary>Значок с числом непрочитанных (создаётся один раз на каждое число).</summary>
    public static WindowIcon WithBadge(int unread)
    {
        var text = BadgeText(unread);
        if (!Badges.TryGetValue(text, out var icon))
            Badges[text] = icon = new WindowIcon(Render(unread));
        return icon;
    }

    /// <summary>PNG значка — для ярлыка AppImage и меню приложений.</summary>
    public static void SavePng(string path, int size)
    {
        using var bitmap = Render(0, size);
        bitmap.Save(path);
    }
}
