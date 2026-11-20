using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace LandMark.Styling;

/// <summary>
/// A named group of formatting settings applied to the preview, Copy for Email and DOCX export.
/// Colors are hex (RRGGBB), sizes are points, and each font is a comma-separated priority list:
/// the first font installed on this PC is used.
/// </summary>
/// <remarks>Property defaults are the RDG style's values, so a saved style missing a setting falls back to the brand.</remarks>
public sealed class DocumentStyle : INotifyPropertyChanged
{
    // RDG brand colors (Brand Guidelines, April 2026). The RGB values are used because the
    // guidelines specify RGB for digital files; some of the printed hex codes differ slightly.
    public const string TrueBlack = "282829";
    public const string RdgRed = "DC3F35";
    public const string RockGray = "8C8A88";
    public const string CoolGray = "BBBCBC";
    public const string LightGray = "F0F1F1";
    // 20% tint of the limited-palette Orange. The guidelines (p.17) don't allow tints of RDG Red,
    // so this is the warm tint used to make code stand out.
    public const string OrangeTint = "F9E5DB";

    private string _name = "RDG";
    private string _bodyFont = "FranklinGothicITC Book, Franklin Gothic Book, Arial";
    private double _bodySize = 11;
    private string _textColor = TrueBlack;
    private string _headingFont = "FranklinGothicITC Demi, Franklin Gothic Demi, Franklin Gothic Medium, Arial";
    private string _headingColor = TrueBlack;
    private double _heading1Size = 22;
    private double _heading2Size = 16;
    private string _subheadingFont = "FranklinGothicITC Medium, Franklin Gothic Medium, Arial";
    private string _subheadingColor = TrueBlack;
    private double _heading3Size = 13;
    private bool _boldHeadings;
    private string _linkColor = TrueBlack;
    private string _accentColor = RdgRed;
    private string _quoteTextColor = TrueBlack;
    private string _tableHeaderBackground = LightGray;
    private string _borderColor = CoolGray;
    private string _codeFont = "Consolas, Courier New";
    private string _codeBackground = OrangeTint;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonIgnore]
    public bool IsBuiltIn { get; private set; }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string BodyFont { get => _bodyFont; set => Set(ref _bodyFont, value); }
    public double BodySize { get => _bodySize; set => Set(ref _bodySize, value); }
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }
    public string HeadingFont { get => _headingFont; set => Set(ref _headingFont, value); }
    public string HeadingColor { get => _headingColor; set => Set(ref _headingColor, value); }
    public double Heading1Size { get => _heading1Size; set => Set(ref _heading1Size, value); }
    public double Heading2Size { get => _heading2Size; set => Set(ref _heading2Size, value); }
    public string SubheadingFont { get => _subheadingFont; set => Set(ref _subheadingFont, value); }
    public string SubheadingColor { get => _subheadingColor; set => Set(ref _subheadingColor, value); }
    public double Heading3Size { get => _heading3Size; set => Set(ref _heading3Size, value); }
    public bool BoldHeadings { get => _boldHeadings; set => Set(ref _boldHeadings, value); }
    public string LinkColor { get => _linkColor; set => Set(ref _linkColor, value); }
    public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }
    public string QuoteTextColor { get => _quoteTextColor; set => Set(ref _quoteTextColor, value); }
    public string TableHeaderBackground { get => _tableHeaderBackground; set => Set(ref _tableHeaderBackground, value); }
    public string BorderColor { get => _borderColor; set => Set(ref _borderColor, value); }
    public string CodeFont { get => _codeFont; set => Set(ref _codeFont, value); }
    public string CodeBackground { get => _codeBackground; set => Set(ref _codeBackground, value); }

    // ---- Built-in styles ----

    public static DocumentStyle CreateRdgBrand() => new() { Id = "builtin-rdg", IsBuiltIn = true };

    /// <summary>The original look: Calibri with Word-style blue headings.</summary>
    public static DocumentStyle CreateClassic() => new()
    {
        Id = "builtin-classic",
        IsBuiltIn = true,
        Name = "Classic",
        BodyFont = "Calibri, Arial",
        TextColor = "222222",
        HeadingFont = "Calibri, Arial",
        HeadingColor = "1F3864",
        Heading1Size = 20,
        Heading2Size = 16,
        SubheadingFont = "Calibri, Arial",
        SubheadingColor = "2F5496",
        BoldHeadings = true,
        LinkColor = "0563C1",
        AccentColor = "BFBFBF",
        QuoteTextColor = "595959",
        TableHeaderBackground = "F2F2F2",
        BorderColor = "BFBFBF",
        CodeBackground = "F3F3F3",
    };

    /// <summary>Black and gray only; safe for any business email.</summary>
    public static DocumentStyle CreateNeutral() => new()
    {
        Id = "builtin-neutral",
        IsBuiltIn = true,
        Name = "Neutral",
        BodyFont = "Calibri, Arial",
        TextColor = "222222",
        HeadingFont = "Calibri, Arial",
        HeadingColor = "111111",
        Heading1Size = 18,
        Heading2Size = 15,
        SubheadingFont = "Calibri, Arial",
        SubheadingColor = "333333",
        Heading3Size = 12,
        BoldHeadings = true,
        LinkColor = "222222",
        AccentColor = "AAAAAA",
        QuoteTextColor = "555555",
        TableHeaderBackground = "F2F2F2",
        BorderColor = "CCCCCC",
        CodeBackground = "F3F3F3",
    };

    // ---- Copies ----

    /// <summary>An identical copy (same Id), used as a working copy while editing.</summary>
    public DocumentStyle Clone()
    {
        var copy = (DocumentStyle)MemberwiseClone();
        copy.PropertyChanged = null;
        return copy;
    }

    /// <summary>A new, editable style with these settings.</summary>
    public DocumentStyle Duplicate(string name)
    {
        var copy = Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.IsBuiltIn = false;
        copy.Name = name;
        return copy;
    }

    // ---- Helpers for the converters ----

    /// <summary>Normalizes "#dc3f35" or "DC3F35" to "DC3F35"; anything else becomes the fallback.</summary>
    public static string Hex(string? value, string fallback = "000000")
    {
        var hex = (value ?? "").Trim().TrimStart('#');
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : fallback;
    }

    public static string Points(double size, double min = 6, double max = 72) =>
        Math.Clamp(double.IsFinite(size) ? size : 11, min, max).ToString("0.#", CultureInfo.InvariantCulture);

    public static int HalfPoints(double size, double min = 6, double max = 72) =>
        (int)Math.Round(Math.Clamp(double.IsFinite(size) ? size : 11, min, max) * 2);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
