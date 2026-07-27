using System.Globalization;
using Avalonia.Data.Converters;

namespace NoteManager.Desktop;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
        => value is not true;

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
        => value is not true;
}
