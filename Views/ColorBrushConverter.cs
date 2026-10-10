using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace OfficeChat.Views;

/// <summary>Цвет строкой («#EF4444») → кисть: цвет напоминания зависит от важности.</summary>
public sealed class ColorBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text) return null;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
