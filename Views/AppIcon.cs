using System.Drawing;
using System.Drawing.Drawing2D;
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

    /// <summary>Облачко чата; с красной точкой — когда есть непрочитанные.</summary>
    public static Icon Create(bool withBadge)
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

            if (withBadge)
            {
                using var red = new SolidBrush(Badge);
                using var outline = new System.Drawing.Pen(System.Drawing.Color.White, 2);
                g.FillEllipse(red, 18, 0, 13, 13);
                g.DrawEllipse(outline, 18, 0, 13, 13);
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
        using var icon = Create(withBadge: false);
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
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
