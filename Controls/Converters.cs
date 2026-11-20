using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LandMark.Styling;

namespace LandMark.Controls;

/// <summary>"DC3F35" → a brush of that color; invalid text → transparent.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string hex = DocumentStyle.Hex(value as string, fallback: "");
        if (hex.Length != 6) return Brushes.Transparent;

        var brush = new SolidColorBrush(Color.FromRgb(
            byte.Parse(hex[..2], NumberStyles.HexNumber),
            byte.Parse(hex[2..4], NumberStyles.HexNumber),
            byte.Parse(hex[4..], NumberStyles.HexNumber)));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Explains which font from a priority list this PC will actually use.</summary>
public sealed class FontInUseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fonts = InstalledFonts.Parse(value as string);
        if (fonts.Count == 0) return "Enter one or more fonts, separated by commas.";

        var installed = fonts.FirstOrDefault(InstalledFonts.IsInstalled);
        return installed != null
            ? $"Uses {installed} on this PC."
            : $"None of these fonts are installed on this PC; {fonts[0]} will be requested.";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Heading level → left indent for the outline panel (14px per level below 1).</summary>
public sealed class OutlineIndentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new System.Windows.Thickness(value is int level ? Math.Max(0, level - 1) * 14 : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
