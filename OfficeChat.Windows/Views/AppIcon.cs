using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OfficeChat.Views;

/// <summary>Значок приложения рисуется кодом, чтобы не хранить .ico в репозитории.</summary>
public static class AppIcon
{
    private static readonly System.Drawing.Color Accent = System.Drawing.Color.FromArgb(0x2F, 0x6F, 0xDE);
    private static readonly System.Drawing.Color Badge = System.Drawing.Color.FromArgb(0xE5, 0x3E, 0x3E);

    /// <summary>Текст на красном кружке: число непрочитанных, больше девяти — «9+».</summary>
    public static string BadgeText(int unread) => unread > 9 ? "9+" : unread.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Облачко чата; если есть непрочитанные — с красным кружком и их числом.
    /// Кружок крупный: в трее значок уменьшается до 16 точек, мелкую цифру там не разглядеть.
    /// </summary>
    public static Icon Create(int unread)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            using var accent = new SolidBrush(Accent);
            using (var bubble = RoundedRect(new RectangleF(1, 3, 29, 20), 6))
                g.FillPath(accent, bubble);
            g.FillPolygon(accent, new[] { new PointF(7, 21), new PointF(7, 29), new PointF(15, 21) });

            using var white = new SolidBrush(System.Drawing.Color.White);
            foreach (var x in new[] { 8.5f, 15.5f, 22.5f })
                g.FillEllipse(white, x - 2, 11, 4, 4);

            if (unread > 0)
            {
                using var red = new SolidBrush(Badge);
                using var outline = new System.Drawing.Pen(System.Drawing.Color.White, 2);
                var circle = new RectangleF(12, 11, 20, 20);
                g.FillEllipse(red, circle);
                g.DrawEllipse(outline, circle);

                var text = BadgeText(unread);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using var font = new Font("Segoe UI", text.Length > 1 ? 11 : 15, System.Drawing.FontStyle.Bold,
                    GraphicsUnit.Pixel);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                g.DrawString(text, font, white, new RectangleF(circle.X, circle.Y + 1, circle.Width, circle.Height), format);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Тот же значок для окон WPF.</summary>
    public static ImageSource CreateImageSource()
    {
        using var icon = Create(unread: 0);
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    /// <summary>Красный кружок с числом — накладывается на кнопку программы на панели задач.</summary>
    public static ImageSource CreateTaskbarOverlay(int unread)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var red = new SolidColorBrush(System.Windows.Media.Color.FromRgb(Badge.R, Badge.G, Badge.B));
            dc.DrawEllipse(red, new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 2),
                new System.Windows.Point(16, 16), 15, 15);
            var text = BadgeText(unread);
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold,
                    FontStretches.Normal),
                text.Length > 1 ? 16 : 20, System.Windows.Media.Brushes.White, 1.0);
            dc.DrawText(formatted, new System.Windows.Point(16 - formatted.Width / 2, 16 - formatted.Height / 2));
        }
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
