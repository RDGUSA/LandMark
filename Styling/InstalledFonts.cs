using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LandMark.Styling;

/// <summary>
/// Picks fonts from a style's comma-separated priority lists. Outlook only honors the first
/// font in a list (falling back to Times New Roman), so the first *installed* font goes first.
/// </summary>
public static partial class InstalledFonts
{
    private static readonly Lazy<HashSet<string>> Faces = new(LoadFaces);

    public static IReadOnlyList<string> Parse(string? fontList) =>
        (fontList ?? "").Split(',')
            .Select(Sanitize)
            .Where(name => name.Length > 0)
            .ToList();

    public static bool IsInstalled(string family) =>
        Faces.Value.Contains(family)
        || Faces.Value.Any(face => face.StartsWith(family + " ", StringComparison.OrdinalIgnoreCase));

    /// <summary>The font to use: the first installed one, else the first listed.</summary>
    public static string Resolve(string? fontList, string fallback = "Arial")
    {
        var fonts = Parse(fontList);
        return fonts.FirstOrDefault(IsInstalled) ?? fonts.FirstOrDefault() ?? fallback;
    }

    /// <summary>A CSS font-family value: installed fonts first, then the rest, then a generic family.</summary>
    public static string CssStack(string? fontList, string generic)
    {
        var fonts = Parse(fontList);
        var ordered = fonts.Where(IsInstalled).Concat(fonts.Where(font => !IsInstalled(font)));
        return string.Join(",", ordered.Select(font => $"'{font}'").Append(generic));
    }

    /// <summary>Keeps names safe inside HTML style attributes and XML.</summary>
    private static string Sanitize(string name) =>
        new string(name.Where(c => c >= ' ' && c < 127 && c is not ('\'' or '"' or ';' or '<' or '>' or '&')).ToArray()).Trim();

    /// <summary>Font face names from the registry, e.g. "Arial Bold (TrueType)" becomes "Arial Bold".</summary>
    private static HashSet<string> LoadFaces()
    {
        var faces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var fonts = root.OpenSubKey(key);
            foreach (var value in fonts?.GetValueNames() ?? [])
                foreach (var face in FontTypeSuffix().Replace(value, "").Split(" & "))
                    faces.Add(face.Trim());
        }
        return faces;
    }

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex FontTypeSuffix();
}
