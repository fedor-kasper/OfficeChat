using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OfficeChat.Mobile;

/// <summary>Цвет кружка с буквой — по Id собеседника (у каждого свой, всегда один и тот же).</summary>
public sealed class AvatarBrushConverter : IValueConverter
{
    private static readonly IBrush[] Palette =
    {
        Brush("#3B82F6"), Brush("#10B981"), Brush("#F59E0B"), Brush("#EF4444"), Brush("#8B5CF6"),
        Brush("#EC4899"), Brush("#14B8A6"), Brush("#F97316"), Brush("#6366F1"), Brush("#84CC16"),
    };

    private static IBrush Brush(string color) => new ImmutableSolidColorBrush(Color.Parse(color));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Guid id ? Palette[(int)((uint)id.GetHashCode() % Palette.Length)] : Palette[0];

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Первая буква имени — для кружка.</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } text ? text.TrimStart()[..1].ToUpperInvariant() : "?";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
