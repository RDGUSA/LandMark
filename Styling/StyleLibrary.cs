using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LandMark.Styling;

/// <summary>
/// The built-in styles plus the user's saved ones, the default style, and which style each
/// document uses. Saved to %AppData%\LandMark\styles.json.
/// </summary>
public sealed class StyleLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string FilePath { get; } = Path.Combine(AppDataFolder.Path, "styles.json");

    /// <summary>Where styles were saved when the app was called MD Render.</summary>
    private static string LegacyFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MD Render", "styles.json");

    [JsonIgnore]
    public static IReadOnlyList<DocumentStyle> BuiltIn { get; } =
        [DocumentStyle.CreateRdgBrand(), DocumentStyle.CreateClassic(), DocumentStyle.CreateNeutral()];

    public string DefaultStyleId { get; set; } = BuiltIn[0].Id;

    public List<DocumentStyle> CustomStyles { get; set; } = [];

    /// <summary>Full document path → style Id, for documents given a style explicitly.</summary>
    public Dictionary<string, string> DocumentStyles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public IEnumerable<DocumentStyle> All => BuiltIn.Concat(CustomStyles);

    [JsonIgnore]
    public DocumentStyle Default => Find(DefaultStyleId) ?? BuiltIn[0];

    public DocumentStyle? Find(string? id) => All.FirstOrDefault(style => style.Id == id);

    /// <summary>The style remembered for a document, or the default style.</summary>
    public DocumentStyle StyleFor(string? path) =>
        path != null && DocumentStyles.TryGetValue(Path.GetFullPath(path), out var id) && Find(id) is { } style
            ? style
            : Default;

    public void Remember(string path, DocumentStyle style) => DocumentStyles[Path.GetFullPath(path)] = style.Id;

    public static StyleLibrary Load()
    {
        try
        {
            if (!File.Exists(FilePath) && File.Exists(LegacyFilePath))
            {
                // Copy rather than move, so the old settings stay as a backup.
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(LegacyFilePath, FilePath);
            }

            if (File.Exists(FilePath) && JsonSerializer.Deserialize<StyleLibrary>(File.ReadAllText(FilePath), JsonOptions) is { } library)
            {
                // The deserializer doesn't keep the case-insensitive comparer.
                library.DocumentStyles = new(library.DocumentStyles, StringComparer.OrdinalIgnoreCase);
                return library;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable settings: start with the built-in styles rather than failing to open.
        }
        return new StyleLibrary();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
