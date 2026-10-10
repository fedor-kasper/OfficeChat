using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OfficeChat.Views;

/// <summary>Цвет строкой («#EF4444») → кисть: цвет напоминания зависит от важности.</summary>
public sealed class ColorBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && Color.TryParse(text, out var color) ? new SolidColorBrush(color) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
