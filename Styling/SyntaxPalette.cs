using System.Windows;
using System.Windows.Media;
using LandMark.Markdown;

namespace LandMark.Styling;

/// <summary>
/// The editor's Markdown colors, one per syntax group, in a light and a dark variant (after VS Code's Light+ and
/// Dark+ themes, adjusted for the Fluent card backgrounds). Fixed on purpose: these color the source, not the
/// document, so they don't belong in a document style.
/// </summary>
public sealed class SyntaxPalette
{
    public static SyntaxPalette Light { get; } = new(new()
    {
        [SyntaxKind.Heading] = "0451A5",
        [SyntaxKind.ListMarker] = "AF00DB",
        [SyntaxKind.Bold] = "795E26",
        [SyntaxKind.Italic] = "795E26",
        [SyntaxKind.Strikethrough] = "795E26",
        [SyntaxKind.Code] = "A31515",
        [SyntaxKind.Link] = "267F99",
        [SyntaxKind.Url] = "0070C1",
        [SyntaxKind.Quote] = "008000",
        [SyntaxKind.Table] = "767676",
        [SyntaxKind.Rule] = "767676",
        [SyntaxKind.Html] = "767676",
        [SyntaxKind.Escape] = "CD3131",
    });

    public static SyntaxPalette Dark { get; } = new(new()
    {
        [SyntaxKind.Heading] = "569CD6",
        [SyntaxKind.ListMarker] = "C586C0",
        [SyntaxKind.Bold] = "DCDCAA",
        [SyntaxKind.Italic] = "DCDCAA",
        [SyntaxKind.Strikethrough] = "DCDCAA",
        [SyntaxKind.Code] = "CE9178",
        [SyntaxKind.Link] = "4EC9B0",
        [SyntaxKind.Url] = "9CDCFE",
        [SyntaxKind.Quote] = "6A9955",
        [SyntaxKind.Table] = "8C8C8C",
        [SyntaxKind.Rule] = "8C8C8C",
        [SyntaxKind.Html] = "8C8C8C",
        [SyntaxKind.Escape] = "D7BA7D",
    });

    private readonly Brush[] _brushes;

    private SyntaxPalette(Dictionary<SyntaxKind, string> colors)
    {
        _brushes = new Brush[Enum.GetValues<SyntaxKind>().Length];
        foreach (var (kind, hex) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + hex));
            brush.Freeze();
            _brushes[(int)kind] = brush;
        }
    }

    public Brush BrushFor(SyntaxKind kind) => _brushes[(int)kind];

    /// <summary>Weight for a group; monospace fonts keep the same character width in bold, so the text stays aligned.</summary>
    public static FontWeight? WeightFor(SyntaxKind kind) => kind is SyntaxKind.Heading or SyntaxKind.Bold ? FontWeights.Bold : null;

    public static FontStyle? StyleFor(SyntaxKind kind) => kind is SyntaxKind.Italic or SyntaxKind.Quote ? FontStyles.Italic : null;

    public static TextDecorationCollection? DecorationsFor(SyntaxKind kind) => kind == SyntaxKind.Strikethrough ? TextDecorations.Strikethrough : null;

    /// <summary>The dark variant for light text (a dark theme), the light one otherwise.</summary>
    public static SyntaxPalette ForTextColor(Color text) =>
        0.299 * text.R + 0.587 * text.G + 0.114 * text.B > 128 ? Dark : Light;
}
