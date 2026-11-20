using System.IO;
using System.Text.Json;

namespace LandMark;

/// <summary>App preferences other than document styles. Saved to %AppData%\LandMark\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string FilePath { get; } = Path.Combine(AppDataFolder.Path, "settings.json");

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Page margins for PDF and DOCX export: "Wide" (1") or "Narrow" (0.25").</summary>
    public string PageMargins { get; set; } = "Wide";

    /// <summary>Color the Markdown syntax in the editor (View › Syntax Colors).</summary>
    public bool SyntaxColors { get; set; } = true;

    /// <summary>Full paths of the files opened or saved most recently, newest first (File › Open Recent).</summary>
    public List<string> RecentFiles { get; set; } = [];

    public const int MaxRecentFiles = 7;

    public double MarginInches => PageMargins == "Narrow" ? 0.25 : 1.0;

    /// <summary>Picks up the list from disk, where another LandMark window may have added to it.</summary>
    public void RefreshRecentFiles() => RecentFiles = Load().RecentFiles;

    /// <summary>Moves the file to the top of the recent list and saves. Failing to save is not worth interrupting for.</summary>
    public void AddRecentFile(string path) => ChangeRecentFiles(path, add: true);

    public void RemoveRecentFile(string path) => ChangeRecentFiles(path, add: false);

    public void ClearRecentFiles()
    {
        RecentFiles = [];
        TrySave();
    }

    private void ChangeRecentFiles(string path, bool add)
    {
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        RefreshRecentFiles();
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (add) RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        TrySave();
    }

    private void TrySave()
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The list still works for this session.
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) is { } settings)
                return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable settings: fall back to the defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
