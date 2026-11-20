using System.Windows;
using System.Windows.Controls;
using LandMark.Styling;

namespace LandMark.Controls;

/// <summary>A hex color box with a swatch that opens a palette of RDG brand and common colors.</summary>
public partial class ColorField : UserControl
{
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorField),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public sealed record Preset(string Label, string Hex);

    public sealed record PresetGroup(string Name, IReadOnlyList<Preset> Colors);

    /// <summary>RDG palettes from the Brand Guidelines (RGB values), plus a few neutrals and Office blues.</summary>
    public static IReadOnlyList<PresetGroup> Palette { get; } =
    [
        new("RDG primary",
        [
            new("True Black", DocumentStyle.TrueBlack), new("RDG Red", DocumentStyle.RdgRed), new("Rock Gray", DocumentStyle.RockGray),
            new("Cool Gray", DocumentStyle.CoolGray), new("Light Gray", DocumentStyle.LightGray), new("White", "FFFFFF"),
        ]),
        new("RDG limited",
        [
            new("Light Blue", "77B5DD"), new("Medium Blue", "2C88C4"), new("Dark Blue", "232D63"), new("Rich Black", "131313"),
            new("Orange", "E37C4A"), new("Yellow", "EDB959"), new("Green", "2E7742"), new("Purple", "9B567E"),
            new("Orange 20% tint", DocumentStyle.OrangeTint),
        ]),
        new("Other",
        [
            new("Black", "000000"), new("Charcoal", "222222"), new("Dark Gray", "595959"), new("Silver", "BFBFBF"),
            new("Pale Gray", "F2F2F2"), new("Navy", "1F3864"), new("Blue", "2F5496"), new("Link Blue", "0563C1"),
        ]),
    ];

    public ColorField()
    {
        InitializeComponent();
        PaletteGroups.ItemsSource = Palette;
    }

    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        Hex = (string)((Button)sender).Tag;
        PalettePopup.IsOpen = false;
    }
}
